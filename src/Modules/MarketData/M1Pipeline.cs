using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;

namespace PortfolioIntelligence.MarketData;

public sealed record M1PipelineOptions
{
    public const string PolicyVersion = "m1-data-policy v1.2";

    public int CoreSize { get; init; } = CoreSelector.CoreSize;

    public int FirstListingYear { get; init; } = 2017;

    /// <summary>Concurrent archive requests; kept low so the archive is not pressed (T).</summary>
    public int MaxConcurrency { get; init; } = 6;

    /// <summary>Progress lines (counts and step names only; never candle data).</summary>
    public Action<string>? Progress { get; init; }
}

public sealed record PoolMember(string Symbol, ArchiveFileKey FirstMonth, DateTimeOffset FirstOpenUpperBound, bool FirstOpenExact);

public sealed record E1PreCandidate(string Symbol, int FirstYear, int FirstMonth, bool FirstOpenVerified);

public sealed record M1Result(
    int ListedSymbols,
    int UnsupportedSymbolNames,
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
    private readonly Lock _filesLock = new();

    public async Task<M1Result> RunAsync(CancellationToken cancellationToken)
    {
        var core = clientFor(ArchiveRequestPolicy.CorePhase());
        var importer = importerFor(core);

        // 1. Discovery and classification (names only). Directory names that are not plain upper-case ASCII
        //    symbols cannot be archive keys; they are skipped and only counted, never printed.
        var listed = await core.ListSymbolsAsync(cancellationToken);
        var supported = listed.Where(ArchiveFileKey.IsValidSymbol).ToList();
        var classification = SymbolClassifier.Classify(supported, exclusions);
        var included = classification.Where(c => c.Outcome == ClassificationOutcome.Include).Select(c => c.Symbol).ToList();
        Report($"[Step 1/5] Listed {listed.Count} symbol directories ({listed.Count - supported.Count} unsupported names skipped); {classification.Count} USDT symbols, {included.Count} included after exclusions.");

        // 2. Core pool: first 1h month in 2017–2021, exact first open where it matters.
        var years = Enumerable.Range(options.FirstListingYear, 2021 - options.FirstListingYear + 1).ToList();
        var checkedCount = 0;
        var firsts = await ForEachAsync(included, async (symbol, ct) =>
        {
            var first = await core.FindFirstMonthAsync(symbol, years, ct);
            var member = first is null ? null : await ResolveFirstOpenAsync(importer, first, ct);
            var done = Interlocked.Increment(ref checkedCount);
            if (done % 50 == 0 || done == included.Count)
            {
                Report($"[Step 2/5] Checking first month: {done}/{included.Count}");
            }

            return (Symbol: symbol, First: first, Member: member);
        }, cancellationToken);

        var pool = new List<PoolMember>();
        var withoutEarlyData = new List<string>();
        var lateListed = new List<E1PreCandidate>();
        foreach (var (symbol, first, member) in firsts.OrderBy(f => f.Symbol, StringComparer.Ordinal))
        {
            if (first is null)
            {
                withoutEarlyData.Add(symbol);
            }
            else if (member is not null && member.FirstOpenUpperBound <= ResearchCutoff.CorePoolLatestFirstOpen)
            {
                pool.Add(member);
            }
            else if (member is not null)
            {
                // Listed in December 2021 after the core deadline: not core, but an E1 pre-candidate (U0 ≤ 2025-01-01).
                lateListed.Add(new E1PreCandidate(symbol, first.Year, first.Month, FirstOpenVerified: true));
            }
        }

        Report($"[Step 2/5] Core pool: {pool.Count} symbols.");

        // 3. Ranking data (1d, window W) and frozen ranking.
        var rankedCount = 0;
        var stats = await ForEachAsync(pool, async (member, ct) =>
        {
            foreach (var key in MonthlyFilePlanner.Plan(member.Symbol, CandleInterval.OneDay, ResearchCutoff.RankingWindow))
            {
                Track(await importer.ImportAsync(key, ct));
            }

            var daily = await store.ReadAsync(member.Symbol, CandleInterval.OneDay, ResearchCutoff.RankingWindow, ct);
            var done = Interlocked.Increment(ref rankedCount);
            if (done % 25 == 0 || done == pool.Count)
            {
                Report($"[Step 3/5] Ranking data (1d): {done}/{pool.Count}");
            }

            return CoreRanking.ComputeStats(member.Symbol, daily, member.FirstOpenUpperBound);
        }, cancellationToken);

        var ranking = CoreRanking.Rank(stats);
        Report($"[Step 3/5] Ranking frozen: {ranking.Count(r => r.Status == RankingStatus.Ranked)} ranked, {ranking.Count(r => r.Status != RankingStatus.Ranked)} unranked.");

        // 4. Quality gate with replacement, in ranking order.
        var selection = await SelectAsync(ranking, importer, cancellationToken);
        Report($"[Step 4/5] Selected {selection.Selected.Count}/{options.CoreSize}; eliminated {selection.Eliminated.Count}.");

        // 5. E1 pre-candidates: listed only, never imported.
        var e1 = lateListed.Concat(await ListE1Async(withoutEarlyData, cancellationToken)).ToList();
        Report($"[Step 5/5] E1 pre-candidates: {e1.Count}.");

        var hash = SnapshotHash(classification, ranking, selection);
        await store.SaveSnapshotAsync(M1PipelineOptions.PolicyVersion, hash, Manifest(classification, ranking, selection, hash), cancellationToken);
        return new M1Result(listed.Count, listed.Count - supported.Count, classification, pool, ranking, selection, Files(), e1, hash);
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
            Report($"[Step 4/5] Importing candidate #{candidate.Rank} {candidate.Symbol} (1h and 4h)...");
            var keys = new[] { CandleInterval.OneHour, CandleInterval.FourHours }
                .SelectMany(interval => MonthlyFilePlanner.Plan(candidate.Symbol, interval, dataRange)).ToList();
            var results = await ForEachAsync(keys, async (key, token) => Track(await importer.ImportAsync(key, token)), ct);
            var integrityFailure = results.Any(r => r.Status == FileStatus.Rejected && r.Key.MonthEnd > ResearchCutoff.U);

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
        var found = await ForEachAsync(symbols.ToList(), async (symbol, ct) =>
            (Symbol: symbol, First: await e1Client.FindFirstMonthAsync(symbol, [2022, 2023, 2024], ct)), cancellationToken);

        // U0 ≤ 2025-01-01 needs first open ≤ 2024-12-02; a December 2024 start cannot be confirmed without data.
        var candidates = found.Where(f => f.First is not null)
            .OrderBy(f => f.Symbol, StringComparer.Ordinal)
            .Select(f => new E1PreCandidate(f.Symbol, f.First!.Year, f.First.Month, FirstOpenVerified: f.First.Year < 2024 || f.First.Month < 12))
            .ToList();

        return candidates;
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

    /// <summary>Runs <paramref name="action"/> for every item with bounded concurrency; results keep input order.</summary>
    private async Task<IReadOnlyList<TResult>> ForEachAsync<TItem, TResult>(
        IReadOnlyList<TItem> items, Func<TItem, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        var results = new TResult[items.Count];
        await Parallel.ForAsync(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await action(items[i], ct));
        return results;
    }

    private string SnapshotHash(IReadOnlyList<SymbolClassification> classification, IReadOnlyList<RankedSymbol> ranking, CoreSelection selection)
    {
        var canonical = new StringBuilder();
        canonical.Append(M1PipelineOptions.PolicyVersion).Append('|').Append(exclusions.Version).Append('\n');
        foreach (var file in Files().Where(f => f.Sha256 is not null).DistinctBy(f => f.Key.Path).OrderBy(f => f.Key.Path, StringComparer.Ordinal))
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
            files = Files().Where(f => f.Sha256 is not null).DistinctBy(f => f.Key.Path).Select(f => new { path = f.Key.Path, sha256 = f.Sha256 }),
            excluded = classification.Count(c => c.Outcome != ClassificationOutcome.Include),
            ranking = ranking.Select(r => new { r.Symbol, r.Rank, status = r.Status.ToString() }),
            selected = selection.Selected.Select(s => s.Symbol),
            eliminated = selection.Eliminated.Select(e => new { e.Symbol, e.Rank, reason = e.Reason.ToString() }),
            complete = selection.Complete,
        });
}
