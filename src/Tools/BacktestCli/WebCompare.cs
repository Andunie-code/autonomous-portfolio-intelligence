using System.Globalization;
using System.Text;
using Npgsql;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Ai.Persistence;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Evaluation;
using PortfolioIntelligence.Research.Families;
using PortfolioIntelligence.Research.Hypotheses;
using PortfolioIntelligence.Research.Sources;
using PortfolioIntelligence.Researcher;
using PortfolioIntelligence.SourceGateway;

/// <summary>
/// web-compare (m7-protocol §9, Gate C6): the web arm (human-approved hypotheses written from verified findings) and the
/// no-web arm (approved or grid hypotheses) through the same research cycle, budget and gates. Every point is a trial
/// (T18). While FindingExtraction is not qualified the web arm is not run and the report says web research is closed.
/// </summary>
internal static class WebCompare
{
    private const string ProtocolVersion = "m7-protocol v1";

    public static async Task<int> RunAsync(string connection, string repositoryRoot, string engineSha, string? webList, string? webHash, string noWebList, string noWebHash, string author)
    {
        var started = DateTimeOffset.UtcNow;
        var ai = AiConfiguration.LoadDefault();
        var open = await new WebResearchGate(ai, new QualificationRegistry(new NpgsqlQualificationStore(connection))).IsOpenAsync(CancellationToken.None);
        var web = open && webList is not null ? HypothesisList.Load(await File.ReadAllTextAsync(webList), webHash ?? "") : null;
        var noWeb = HypothesisList.Load(await File.ReadAllTextAsync(noWebList), noWebHash);
        if (web is not null && (web.Hypotheses.Count > 5 || web.Hypotheses.Any(h => h.SourceFindingIds is not { Count: > 0 })))
        {
            Console.Error.WriteLine("The web arm has at most 5 hypotheses, each naming its source findings (m7-protocol §9).");
            return 1;
        }

        if (web is not null && noWeb.Hypotheses.Count != web.Hypotheses.Count)
        {
            Console.Error.WriteLine("Both arms need the same number of hypotheses (m7-protocol §9).");
            return 1;
        }

        var universe = CoreUniverse.LoadDefault();
        var store = new CandleStore(connection);
        var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
        var candles = new Dictionary<string, IReadOnlyList<Candle>>();
        foreach (var symbol in universe.Ranks.Keys.Order(StringComparer.Ordinal))
        {
            candles[symbol] = await store.ReadAsync(symbol, CandleInterval.FourHours, range, CancellationToken.None);
        }

        var input = new BacktestInput(candles, universe.Ranks, ResearchCutoff.U, ResearchCutoff.C, Reference: candles["BTCUSDT"]);
        var trials = new TrialRegistry(new NpgsqlFamilyStore(connection), universe.SnapshotHash);
        var before = (int)await trials.CountAsync(CancellationToken.None);
        var planned = (web?.TrialCount ?? 0) + noWeb.TrialCount;
        // G6 uses the global counter after this run (every point is a trial, T18); the cap leaves no room beyond the plan.
        var settings = new ResearchCycleSettings(ProtocolVersion, GitHead(repositoryRoot), engineSha, universe.SnapshotHash, before + planned,
            Math.Max(100, before + planned), new GateThresholds(100, 0.75, 0.80, 0.90));
        var audit = new AuditLog(connection);
        var sources = new NpgsqlSourceStore(connection);
        var backend = new ResearchToolBackend(input, settings, trials, new ExperimentStore(connection), new ExperimentRequestGuard(audit),
            new DatasetCatalog(connection), new HypothesisStore(connection, sources));
        var safety = new InMemorySafetyLog();
        var cycle = new ResearchCycle(new ResearcherToolbox(backend, safety), backend, safety);
        var provenance = new HypothesisProvenance("human", "grid", author, null, false, $"user (M7 Gate C6, {DateTimeOffset.UtcNow:yyyy-MM-dd})");
        await audit.RecordAsync(ResearchToolBackend.Actor, "web-compare-started", new { protocol = ProtocolVersion, open, before, planned }, CancellationToken.None);

        void Progress(string message) => Console.WriteLine($"[{(DateTimeOffset.UtcNow - started).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}] {message}");
        var webReport = web is null ? null : await cycle.RunAsync(new ApprovedHypothesisSource(web, provenance), Progress, CancellationToken.None);
        var noWebReport = await cycle.RunAsync(new ApprovedHypothesisSource(noWeb, provenance with { Origin = "dev-assisted" }), Progress, CancellationToken.None);
        var after = (int)await trials.CountAsync(CancellationToken.None);
        var extractorCost = await ExtractorCostAsync(connection);

        var markdown = WebComparison.Markdown(
            new ComparisonArm("web'li (bulgulardan)", web?.Hypotheses ?? [], webReport, extractorCost),
            new ComparisonArm("web'siz", noWeb.Hypotheses, noWebReport, 0), open, ProtocolVersion, engineSha, before, after);
        var output = Path.Combine(repositoryRoot, "docs", "m7-web-comparison.md");
        await File.WriteAllTextAsync(output, markdown, new UTF8Encoding(false));
        await audit.RecordAsync(ResearchToolBackend.Actor, "web-compare-finished", new { after }, CancellationToken.None);
        Console.WriteLine($"Report: {output}. Global trial counter {before} → {after}.");
        return (webReport?.SafetyViolations.Count ?? 0) + noWebReport.SafetyViolations.Count == 0 ? 0 : 4;
    }

    private static async Task<decimal> ExtractorCostAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(sum(amount_usd), 0) FROM ai.cost_ledger WHERE task_class = 'FindingExtraction' AND entry_kind = 'Settlement' AND job_id LIKE 'extract-%'", db);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static string GitHead(string root)
    {
        var head = File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim();
        return head.StartsWith("ref: ", StringComparison.Ordinal) ? File.ReadAllText(Path.Combine(root, ".git", head[5..])).Trim()[..7] : head[..7];
    }
}
