using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;

// M1 market data command line (docs/m1-data-policy.md §3, §9).
//   plan                  — prints the plan; no network, no database writes (the "dry run").
//   check-cutoff          — H1: counts stored candles closing after C (must be 0).
//   run --allow-network   — Gate 2 only: real archive access after an interactive confirmation.
// Output never contains candle rows; reports go to the cache directory outside the repository.
const string ConfirmationWord = "EVET";

var command = args.FirstOrDefault() ?? "plan";
var repositoryRoot = FindRepositoryRoot();
var cache = new ArchiveCacheDirectory(Environment.GetEnvironmentVariable("PI_ARCHIVE_CACHE") ?? ArchiveCacheDirectory.DefaultRoot, repositoryRoot);

switch (command)
{
    case "plan":
        PrintPlan(cache);
        return 0;

    case "check-cutoff":
    {
        var store = new CandleStore(RequireConnectionString());
        var violations = await store.CountCutoffViolationsAsync(CancellationToken.None);
        Console.WriteLine($"H1: candles closing after {ResearchCutoff.C:O}: {violations}");
        return violations == 0 ? 0 : 2;
    }

    case "run":
    {
        if (!args.Contains("--allow-network"))
        {
            Console.Error.WriteLine("Real archive access requires --allow-network (Gate 2).");
            return 1;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Real archive access requires an interactive terminal; refused.");
            return 1;
        }

        PrintPlan(cache);
        Console.Write($"Gate 2: {ArchiveRequestPolicy.FileHost} and {ArchiveRequestPolicy.ListingHost} will be contacted. Type {ConfirmationWord} to continue: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), ConfirmationWord, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Not confirmed; nothing was requested.");
            return 1;
        }

        var store = new CandleStore(RequireConnectionString());
        using var network = ArchiveGuardHandler.CreateNetworkHandler();
        var incidents = new IncidentLog(cache.IncidentLogPath);
        var pipeline = new M1Pipeline(
            policy => new ArchiveClient(new ArchiveGuardHandler(policy, network)),
            client => new ArchiveImporter(client, cache, store, incidents),
            store,
            ExclusionListLoader.LoadDefault(),
            new M1PipelineOptions());

        var result = await pipeline.RunAsync(CancellationToken.None);
        var (markdown, json) = M1ReportWriter.Write(result, cache.ReportsDirectory);
        Console.WriteLine($"Selected {result.Selection.Selected.Count} of {CoreSelector.CoreSize}; complete: {result.Selection.Complete}.");
        Console.WriteLine($"Snapshot: {result.SnapshotHash}");
        Console.WriteLine($"Report: {markdown}");
        Console.WriteLine($"JSON:   {json}");
        return result.Selection.Complete ? 0 : 3;
    }

    default:
        Console.Error.WriteLine("Usage: plan | check-cutoff | run --allow-network");
        return 1;
}

static void PrintPlan(ArchiveCacheDirectory cache)
{
    Console.WriteLine($"Policy: {M1PipelineOptions.PolicyVersion}");
    Console.WriteLine($"Files: {ArchiveRequestPolicy.FileHost}; listing: {ArchiveRequestPolicy.ListingHost}/{ArchiveRequestPolicy.ListingBucket} (HTTPS only, redirects rejected)");
    Console.WriteLine($"Cache: {cache.Root}");
    Console.WriteLine($"Research cutoff C: {ResearchCutoff.C:O}; universe date U: {ResearchCutoff.U:O}");
    Console.WriteLine($"Ranking window W: {ResearchCutoff.RankingWindow}; research window R: {ResearchCutoff.ResearchWindow}");
    Console.WriteLine("Steps:");
    Console.WriteLine("  1. Symbol-level listing of data/spot/monthly/klines/ (names only; raw response not stored).");
    Console.WriteLine("  2. First 1h month per USDT symbol via year prefixes 2017–2021 (max-keys=1).");
    Console.WriteLine("  3. 1d files 2021-07 … 2021-12 for each pool symbol; frozen ranking.");
    Console.WriteLine("  4. 1h and 4h files 2021-07 … 2025-12 per candidate in ranking order until 20 pass the quality gate.");
    Console.WriteLine("  5. E1 pre-candidates via year prefixes 2022–2024 (listing only, no import).");
    var sample = MonthlyFilePlanner.Plan("BTCUSDT", CandleInterval.OneHour, new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C));
    Console.WriteLine($"Per candidate: {sample.Count} 1h files + {sample.Count} 4h files; last planned month {sample[^1].Year:D4}-{sample[^1].Month:D2}.");
    Console.WriteLine("No request has been made.");
}

static string RequireConnectionString() =>
    Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
    ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m1-import.ps1 sets it).");

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PortfolioIntelligence.slnx")))
        {
            return directory.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
