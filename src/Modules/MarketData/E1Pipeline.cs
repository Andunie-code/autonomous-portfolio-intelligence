using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;

namespace PortfolioIntelligence.MarketData;

public sealed record E1PipelineOptions
{
    public const string PolicyVersion = "m3-e1 v1";

    /// <summary>Number of E1 pairs to import (m3-protocol §11, D28: 5).</summary>
    public int Limit { get; init; } = 5;

    /// <summary>Days after U0 whose median daily quote volume ranks the candidates.</summary>
    public int RankingDays { get; init; } = 180;

    public int MaxConcurrency { get; init; } = 6;

    public Action<string>? Progress { get; init; }
}

public sealed record E1Candidate(string Symbol, DateTimeOffset FirstOpen, DateTimeOffset U0, decimal? MedianQuoteVolume, int Rank);

public sealed record E1Assessment(E1Candidate Candidate, QualityAssessment Quality)
{
    public PairCategory? Category => PairCategories.Categorize(Candidate.FirstOpen, Quality.Passes);
}

public sealed record E1Result(
    int ListedSymbols,
    IReadOnlyList<E1Candidate> Candidates,
    IReadOnlyList<E1Assessment> Selected,
    IReadOnlyList<E1Assessment> Eliminated,
    IReadOnlyList<FileImportResult> Files,
    string SnapshotHash,
    bool Complete);

/// <summary>
/// M3 E1 subset (docs/m3-protocol.md §11, D28): discover non-core USDT pairs whose first archive open lies after the
/// core deadline with U0 ≤ 2025-01-01, rank them by the median daily quote volume of their first 180 days after U0,
/// then import 1h and 4h data from the first month up to C in rank order until the limit passes the quality rules
/// on [U0, C). Failing pairs are eliminated and replaced, never demoted. Requests are checked by the archive
/// policy (listing years ≤ 2024; no file closing after C). Pipeline validation only, not a research universe.
/// </summary>
public sealed class E1Pipeline(
    Func<ArchiveRequestPolicy, ArchiveClient> clientFor,
    Func<ArchiveClient, ArchiveImporter> importerFor,
    CandleStore store,
    ExclusionList exclusions,
    IReadOnlySet<string> coreSymbols,
    E1PipelineOptions options)
{
    private static readonly DateTimeOffset EarliestFirstMonth = new(2021, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<FileImportResult> _files = [];
    private readonly Lock _filesLock = new();

    public async Task<E1Result> RunAsync(CancellationToken cancellationToken)
    {
        var client = clientFor(ArchiveRequestPolicy.E1Phase());
        var importer = importerFor(client);

        var listed = await client.ListSymbolsAsync(cancellationToken);
        var included = SymbolClassifier.Classify(listed.Where(ArchiveFileKey.IsValidSymbol), exclusions)
            .Where(c => c.Outcome == ClassificationOutcome.Include && !coreSymbols.Contains(c.Symbol))
            .Select(c => c.Symbol).ToList();
        Report($"[Step 1/4] Listed {listed.Count} symbol directories; {included.Count} non-core USDT symbols after exclusions.");

        // First month among 2021–2024 year prefixes; only a first month from 2021-12 can belong to an E1 pair.
        var firsts = await ForEachAsync(included, async (symbol, ct) => (Symbol: symbol, First: await client.FindFirstMonthAsync(symbol, [2021, 2022, 2023, 2024], ct)), cancellationToken);
        var opens = await ForEachAsync(firsts.Where(f => f.First is { } k && k.MonthStart >= EarliestFirstMonth).ToList(), async (f, ct) =>
        {
            Track(await importer.ImportAsync(f.First!, ct));
            var hourly = await store.ReadAsync(f.Symbol, CandleInterval.OneHour, new TimeRange(f.First!.MonthStart, f.First.MonthEnd), ct);
            return (f.Symbol, First: f.First, Open: hourly.Count == 0 ? (DateTimeOffset?)null : hourly.Min(c => c.OpenTime));
        }, cancellationToken);
        var eligible = opens.Where(o => o.Open is { } open && PairCategories.IsE1Candidate(open)).ToList();
        Report($"[Step 2/4] E1 candidates (first open after {ResearchCutoff.CorePoolLatestFirstOpen:yyyy-MM-dd}, U0 ≤ {PairCategories.E1LatestU0:yyyy-MM-dd}): {eligible.Count}.");

        var stats = await ForEachAsync(eligible, async (e, ct) =>
        {
            var u0 = PairCategories.U0(e.Open!.Value);
            var range = new TimeRange(u0, u0.AddDays(options.RankingDays));
            foreach (var key in MonthlyFilePlanner.Plan(e.Symbol, CandleInterval.OneDay, range))
            {
                Track(await importer.ImportAsync(key, ct));
            }

            var daily = await store.ReadAsync(e.Symbol, CandleInterval.OneDay, range, ct);
            return (e.Symbol, Open: e.Open.Value, U0: u0, Median: CoreRanking.Median(daily.Select(c => c.QuoteVolume).ToList()));
        }, cancellationToken);
        var candidates = stats
            .OrderByDescending(s => s.Median ?? -1m)
            .ThenBy(s => s.Symbol, StringComparer.Ordinal)
            .Select((s, i) => new E1Candidate(s.Symbol, s.Open, s.U0, s.Median, i + 1))
            .ToList();
        Report($"[Step 3/4] Ranking frozen: {candidates.Count} candidates.");

        var selected = new List<E1Assessment>();
        var eliminated = new List<E1Assessment>();
        foreach (var candidate in candidates)
        {
            if (selected.Count >= options.Limit)
            {
                break;
            }

            Report($"[Step 4/4] Importing E1 candidate #{candidate.Rank} {candidate.Symbol} (1h and 4h)...");
            var assessment = new E1Assessment(candidate, await AssessAsync(candidate, importer, cancellationToken));
            (assessment.Category == PairCategory.E1 ? selected : eliminated).Add(assessment);
        }

        Report($"[Step 4/4] Selected {selected.Count}/{options.Limit}; eliminated {eliminated.Count}.");
        var hash = SnapshotHash(candidates, selected);
        await store.SaveSnapshotAsync(E1PipelineOptions.PolicyVersion, hash, Manifest(candidates, selected, eliminated, hash), cancellationToken);
        return new E1Result(listed.Count, candidates, selected, eliminated, Files(), hash, selected.Count == options.Limit);
    }

    private async Task<QualityAssessment> AssessAsync(E1Candidate candidate, ArchiveImporter importer, CancellationToken cancellationToken)
    {
        var firstMonth = new DateTimeOffset(candidate.FirstOpen.Year, candidate.FirstOpen.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var dataRange = new TimeRange(firstMonth, ResearchCutoff.C);
        var window = new TimeRange(candidate.U0, ResearchCutoff.C);
        var keys = new[] { CandleInterval.OneHour, CandleInterval.FourHours }.SelectMany(i => MonthlyFilePlanner.Plan(candidate.Symbol, i, dataRange)).ToList();
        var results = await ForEachAsync(keys, async (key, ct) => Track(await importer.ImportAsync(key, ct)), cancellationToken);
        var integrityFailure = results.Any(r => r.Status == FileStatus.Rejected && r.Key.MonthEnd > candidate.U0);
        var hourly = await store.ReadAsync(candidate.Symbol, CandleInterval.OneHour, window, cancellationToken);
        var fourHour = await store.ReadAsync(candidate.Symbol, CandleInterval.FourHours, window, cancellationToken);
        return new QualityAssessment(
            candidate.Symbol,
            CoverageAnalyzer.Analyze(hourly.Select(c => c.OpenTime), CandleInterval.OneHour, window),
            CoverageAnalyzer.Analyze(fourHour.Select(c => c.OpenTime), CandleInterval.FourHours, window),
            integrityFailure,
            IntervalConsistency.Check(hourly, fourHour));
    }

    private FileImportResult Track(FileImportResult result)
    {
        lock (_filesLock)
        {
            _files.Add(result);
        }

        return result;
    }

    private List<FileImportResult> Files()
    {
        lock (_filesLock)
        {
            return _files.OrderBy(f => f.Key.Path, StringComparer.Ordinal).ToList();
        }
    }

    private void Report(string line) => options.Progress?.Invoke(line);

    private async Task<IReadOnlyList<TResult>> ForEachAsync<TItem, TResult>(
        IReadOnlyList<TItem> items, Func<TItem, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        var results = new TResult[items.Count];
        await Parallel.ForAsync(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await action(items[i], ct));
        return results;
    }

    private string SnapshotHash(IReadOnlyList<E1Candidate> candidates, IReadOnlyList<E1Assessment> selected)
    {
        var canonical = new StringBuilder();
        canonical.Append(E1PipelineOptions.PolicyVersion).Append('|').Append(exclusions.Version).Append('\n');
        foreach (var file in Files().Where(f => f.Sha256 is not null).DistinctBy(f => f.Key.Path).OrderBy(f => f.Key.Path, StringComparer.Ordinal))
        {
            canonical.Append(file.Key.Path).Append(' ').Append(file.Sha256).Append('\n');
        }

        foreach (var c in candidates)
        {
            canonical.Append(c.Symbol).Append(' ').Append(c.Rank).Append(' ').Append(c.FirstOpen.ToString("O")).Append('\n');
        }

        canonical.Append(string.Join(',', selected.Select(s => s.Candidate.Symbol)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private string Manifest(IReadOnlyList<E1Candidate> candidates, IReadOnlyList<E1Assessment> selected, IReadOnlyList<E1Assessment> eliminated, string hash) =>
        JsonSerializer.Serialize(new
        {
            policy = E1PipelineOptions.PolicyVersion,
            exclusions = exclusions.Version,
            hash,
            files = Files().Where(f => f.Sha256 is not null).DistinctBy(f => f.Key.Path).Select(f => new { path = f.Key.Path, sha256 = f.Sha256 }),
            candidates = candidates.Select(c => new { c.Symbol, c.Rank, firstOpen = c.FirstOpen, u0 = c.U0 }),
            selected = selected.Select(s => new { s.Candidate.Symbol, label = PairCategories.EvidenceLabel(PairCategory.E1, s.Candidate.FirstOpen) }),
            eliminated = eliminated.Select(e => new { e.Candidate.Symbol, reason = e.Quality.Reason?.ToString() }),
            complete = selected.Count == options.Limit,
        });
}
