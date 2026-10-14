using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;

namespace PortfolioIntelligence.MarketData;

public sealed record M1PipelineOptions
{
    public const string PolicyVersion = "m1-data-policy v1.0";

    public int CoreSize { get; init; } = CoreSelector.CoreSize;

    public int FirstListingYear { get; init; } = 2017;
}

public sealed record PoolMember(string Symbol, ArchiveFileKey FirstMonth, DateTimeOffset FirstOpenUpperBound, bool FirstOpenExact);

public sealed record E1PreCandidate(string Symbol, int FirstYear, int FirstMonth, bool FirstOpenVerified);

public sealed record M1Result(
    int ListedSymbols,
    IReadOnlyList<SymbolClassification> Classification,
    IReadOnlyList<PoolMember> Pool,
    IReadOnlyList<RankedSymbol> Ranking,
    CoreSelection Selection,
    IReadOnlyList<FileImportResult> Files,
    IReadOnlyList<E1PreCandidate> E1,
    string SnapshotHash);

/// <summary>
/// The M1 core pair selection pipeline of docs/m1-data-policy.md §4–§8, in the fixed order: discovery and
/// classification, ranking data, frozen ranking, quality gate with replacement, E1 listing, snapshot.
/// Rules of each step are fixed in code before the data of the next step is read.
/// </summary>
public sealed class M1Pipeline(
    Func<ArchiveRequestPolicy, ArchiveClient> clientFor,
    Func<ArchiveClient, ArchiveImporter> importerFor,
    CandleStore store,
    ExclusionList exclusions,
    M1PipelineOptions options)
{
    private static readonly DateTimeOffset LatestRankingFirstMonth = new(2021, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LatestWarmupFreeMonth = new(2021, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<FileImportResult> _files = [];

    public async Task<M1Result> RunAsync(CancellationToken cancellationToken)
    {
        var core = clientFor(ArchiveRequestPolicy.CorePhase());
        var importer = importerFor(core);

        // 1. Discovery and classification (names only).
        var listed = await core.ListSymbolsAsync(cancellationToken);
        var classification = SymbolClassifier.Classify(listed, exclusions);
        var included = classification.Where(c => c.Outcome == ClassificationOutcome.Include).Select(c => c.Symbol).ToList();

        // 2. Core pool: first 1h month in 2017–2021, exact first open where it matters.
        var pool = new List<PoolMember>();
        var withoutEarlyData = new List<string>();
        var lateListed = new List<E1PreCandidate>();
        foreach (var symbol in included)
        {
            var years = Enumerable.Range(options.FirstListingYear, 2021 - options.FirstListingYear + 1);
            var first = await core.FindFirstMonthAsync(symbol, years, cancellationToken);
            if (first is null)
            {
                withoutEarlyData.Add(symbol);
                continue;
            }

            var member = await ResolveFirstOpenAsync(importer, first, cancellationToken);
            if (member is not null && member.FirstOpenUpperBound <= ResearchCutoff.CorePoolLatestFirstOpen)
            {
                pool.Add(member);
            }
            else if (member is not null)
            {
                // Listed in December 2021 after the core deadline: not core, but an E1 pre-candidate (U0 ≤ 2025-01-01).
                lateListed.Add(new E1PreCandidate(symbol, first.Year, first.Month, FirstOpenVerified: true));
            }
        }

        // 3. Ranking data (1d, window W) and frozen ranking.
        var stats = new List<RankingStats>();
        foreach (var member in pool)
        {
            foreach (var key in MonthlyFilePlanner.Plan(member.Symbol, CandleInterval.OneDay, ResearchCutoff.RankingWindow))
            {
                Track(await importer.ImportAsync(key, cancellationToken));
            }

            var daily = await store.ReadAsync(member.Symbol, CandleInterval.OneDay, ResearchCutoff.RankingWindow, cancellationToken);
            stats.Add(CoreRanking.ComputeStats(member.Symbol, daily, member.FirstOpenUpperBound));
        }

        var ranking = CoreRanking.Rank(stats);

        // 4. Quality gate with replacement, in ranking order.
        var selection = await SelectAsync(ranking, importer, cancellationToken);

        // 5. E1 pre-candidates: listed only, never imported.
        var e1 = lateListed.Concat(await ListE1Async(withoutEarlyData, cancellationToken)).ToList();

        var hash = SnapshotHash(classification, ranking, selection);
        await store.SaveSnapshotAsync(M1PipelineOptions.PolicyVersion, hash, Manifest(classification, ranking, selection, hash), cancellationToken);
        return new M1Result(listed.Count, classification, pool, ranking, selection, _files, e1, hash);
    }

    private async Task<PoolMember?> ResolveFirstOpenAsync(ArchiveImporter importer, ArchiveFileKey first, CancellationToken cancellationToken)
    {
        if (first.MonthStart <= LatestWarmupFreeMonth)
        {
            // First month ends before W starts: every W day is expected; the exact first open is not needed.
            return new PoolMember(first.Symbol, first, first.MonthEnd.AddHours(-1), false);
        }

        if (first.MonthStart > LatestRankingFirstMonth)
        {
            return null;
        }

        var result = Track(await importer.ImportAsync(first, cancellationToken));
        if (result.Status is not (FileStatus.Imported or FileStatus.AlreadyImported))
        {
            return null;
        }

        var candles = await store.ReadAsync(first.Symbol, CandleInterval.OneHour, new TimeRange(first.MonthStart, first.MonthEnd), cancellationToken);
        return candles.Count == 0 ? null : new PoolMember(first.Symbol, first, candles.Min(c => c.OpenTime), true);
    }

    private Task<CoreSelection> SelectAsync(IReadOnlyList<RankedSymbol> ranking, ArchiveImporter importer, CancellationToken cancellationToken)
    {
        var dataRange = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
        return CoreSelector.SelectAsync(ranking, async (candidate, ct) =>
        {
            var integrityFailure = false;
            foreach (var interval in new[] { CandleInterval.OneHour, CandleInterval.FourHours })
            {
                foreach (var key in MonthlyFilePlanner.Plan(candidate.Symbol, interval, dataRange))
                {
                    var result = Track(await importer.ImportAsync(key, ct));
                    integrityFailure |= result.Status == FileStatus.Rejected && key.MonthEnd > ResearchCutoff.U;
                }
            }

            var hourly = await store.ReadAsync(candidate.Symbol, CandleInterval.OneHour, ResearchCutoff.ResearchWindow, ct);
            var fourHour = await store.ReadAsync(candidate.Symbol, CandleInterval.FourHours, ResearchCutoff.ResearchWindow, ct);
            return new QualityAssessment(
                candidate.Symbol,
                CoverageAnalyzer.Analyze(hourly.Select(c => c.OpenTime), CandleInterval.OneHour, ResearchCutoff.ResearchWindow),
                CoverageAnalyzer.Analyze(fourHour.Select(c => c.OpenTime), CandleInterval.FourHours, ResearchCutoff.ResearchWindow),
                integrityFailure,
                IntervalConsistency.Check(hourly, fourHour));
        }, options.CoreSize, cancellationToken);
    }

    private async Task<IReadOnlyList<E1PreCandidate>> ListE1Async(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        var e1Client = clientFor(ArchiveRequestPolicy.E1Phase());
        var candidates = new List<E1PreCandidate>();
        foreach (var symbol in symbols)
        {
            var first = await e1Client.FindFirstMonthAsync(symbol, [2022, 2023, 2024], cancellationToken);
            if (first is null)
            {
                continue;
            }

            // U0 ≤ 2025-01-01 needs first open ≤ 2024-12-02; a December 2024 start cannot be confirmed without data.
            candidates.Add(new E1PreCandidate(symbol, first.Year, first.Month, FirstOpenVerified: first.Year < 2024 || first.Month < 12));
        }

        return candidates;
    }

    private FileImportResult Track(FileImportResult result)
    {
        _files.Add(result);
        return result;
    }

    private string SnapshotHash(IReadOnlyList<SymbolClassification> classification, IReadOnlyList<RankedSymbol> ranking, CoreSelection selection)
    {
        var canonical = new StringBuilder();
        canonical.Append(M1PipelineOptions.PolicyVersion).Append('|').Append(exclusions.Version).Append('\n');
        foreach (var file in _files.Where(f => f.Sha256 is not null).DistinctBy(f => f.Key.Path).OrderBy(f => f.Key.Path, StringComparer.Ordinal))
        {
            canonical.Append(file.Key.Path).Append(' ').Append(file.Sha256).Append('\n');
        }

        foreach (var c in classification)
        {
            canonical.Append(c.Symbol).Append(' ').Append(c.Outcome).Append(' ').Append(c.Category).Append('\n');
        }

        foreach (var r in ranking)
        {
            canonical.Append(r.Symbol).Append(' ').Append(r.Rank).Append(' ').Append(r.Status).Append('\n');
        }

        canonical.Append(string.Join(',', selection.Selected.Select(s => s.Symbol)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private string Manifest(IReadOnlyList<SymbolClassification> classification, IReadOnlyList<RankedSymbol> ranking, CoreSelection selection, string hash) =>
        JsonSerializer.Serialize(new
        {
            policy = M1PipelineOptions.PolicyVersion,
            exclusions = exclusions.Version,
            hash,
            files = _files.Where(f => f.Sha256 is not null).DistinctBy(f => f.Key.Path).Select(f => new { path = f.Key.Path, sha256 = f.Sha256 }),
            excluded = classification.Count(c => c.Outcome != ClassificationOutcome.Include),
            ranking = ranking.Select(r => new { r.Symbol, r.Rank, status = r.Status.ToString() }),
            selected = selection.Selected.Select(s => s.Symbol),
            eliminated = selection.Eliminated.Select(e => new { e.Symbol, e.Rank, reason = e.Reason.ToString() }),
            complete = selection.Complete,
        });
}
