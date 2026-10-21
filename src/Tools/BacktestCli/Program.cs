using System.Globalization;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Evaluation;

// M2: run-baselines runs the two pre-registered baselines (policy and no-halt) and the benchmarks on the 20 core
// pairs (docs/m2-protocol.md). M3: parity-m3 re-runs them and their DSL equivalents without recording anything and
// compares the result hashes; calibrate runs the synthetic gate calibration (docs/m3-protocol.md §7, no database).
// M4: research-m4 runs the pre-registered LLM-less research cycle (docs/m4-protocol.md) through the Researcher tools.
// M5: freeze-import, parity-m5 (L1/L2 parity replay) and p1-analysis (docs/m5-protocol.md §4.5, §5, §9).
// M5b: cohort-manifest builds a cohort manifest from a spec of pre-registered M4 points (ADR-0019).
// Reads market.candles from the local database; no network. Output: metrics only.
const string ProtocolVersion = "m2-protocol v1";
const string ProtocolCommit = "296757f";

var command = args.FirstOrDefault() ?? "run-baselines";
switch (command)
{
    case "run-baselines":
        break;
    case "parity-m3":
        return await ParityM3();
    case "calibrate":
        return Calibrate(args.Skip(1).ToArray());
    case "cohort0":
    {
        var root = FindRepositoryRoot();
        var ops = Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
            ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m3-cohort0.ps1 sets it).");
        return await Cohort0.RunAsync(ops, root, GitHead(root), Path.Combine(root, "docs", "cohorts", "cohort-0.json"));
    }
    case "research-m4":
    {
        var root = FindRepositoryRoot();
        var ops = Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
            ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m4-llmless.ps1 sets it).");
        // Uncommitted engine: the script passes HEAD plus the git tree hash of src/ (verifiable after the commit).
        return await ResearchM4.RunAsync(ops, root, Environment.GetEnvironmentVariable("PI_ENGINE_SOURCE") ?? GitHead(root));
    }
    case "freeze-import" when args.Length >= 3:
    {
        var root = FindRepositoryRoot();
        return await ParityM5.FreezeImportAsync(OpsConnection(), Path.GetFullPath(args[1]), long.Parse(args[2], CultureInfo.InvariantCulture), GitHead(root));
    }
    case "parity-m5" when args.Length >= 3:
    {
        var root = FindRepositoryRoot();
        return await ParityM5.ParityAsync(OpsConnection(), Path.GetFullPath(args[1]), long.Parse(args[2], CultureInfo.InvariantCulture), args.Contains("--record"),
            Environment.GetEnvironmentVariable("PI_ENGINE_SOURCE") ?? GitHead(root));
    }
    case "cohort-manifest" when args.Length >= 2:
    {
        // cohort-manifest <spec.json> --image-digest <digest> --engine-sha <sha> [--out <path>] (ADR-0019).
        var root = FindRepositoryRoot();
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (Option("--image-digest") is not { } digest || Option("--engine-sha") is not { } sha)
        {
            Console.Error.WriteLine("cohort-manifest needs --image-digest and --engine-sha (the registered evaluator release).");
            return 1;
        }

        return await CohortManifestCommand.RunAsync(OpsConnection(), root, Path.GetFullPath(args[1]), digest, sha,
            Option("--out") ?? Path.Combine(root, "docs", "cohorts", "cohort-1.json"));
    }
    case "p1-analysis" when args.Length >= 2:
    {
        var root = FindRepositoryRoot();
        return await P1Analysis.RunAsync(OpsConnection(), Path.GetFullPath(args[1]), Environment.GetEnvironmentVariable("PI_ENGINE_SOURCE") ?? GitHead(root));
    }
    default:
        Console.Error.WriteLine("Usage: run-baselines | parity-m3 | calibrate [--quick] [--out <path>] | cohort0 | research-m4 | " +
                                "freeze-import <manifest> <cohortId> | parity-m5 <manifest> <cohortId> [--record] | p1-analysis <manifest> | " +
                                "cohort-manifest <spec> --image-digest <d> --engine-sha <s> [--out <path>]");
        return 1;
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
    ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m2-backtest.ps1 sets it).");
var repositoryRoot = FindRepositoryRoot();
var universe = CoreUniverse.LoadDefault();
var store = new CandleStore(connectionString);
var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);

var candles = new Dictionary<string, IReadOnlyList<Candle>>();
foreach (var symbol in universe.Ranks.Keys.Order(StringComparer.Ordinal))
{
    candles[symbol] = await store.ReadAsync(symbol, CandleInterval.FourHours, range, CancellationToken.None);
    Console.WriteLine($"Loaded {symbol}: {candles[symbol].Count} 4h candles");
}

var input = new BacktestInput(candles, universe.Ranks, ResearchCutoff.U, ResearchCutoff.C);
var engineSha = GitHead(repositoryRoot);
var experiments = new ExperimentStore(connectionString);
var results = new List<(BacktestResult Result, PerformanceMetrics Metrics)>();
var years = (ResearchCutoff.C - ResearchCutoff.U).TotalDays / 365.25;

foreach (var strategy in new IStrategy[] { new EmaTrendStrategy(), new SupertrendStrategy() })
{
    foreach (var risk in new[] { RiskSettings.Policy, RiskSettings.NoHalt })
    {
        var result = new BacktestEngine(strategy, CostModel.V1, risk).Run(input);
        var metrics = Metrics.Compute(result, years);
        var hash = result.Hash();
        await experiments.AddAsync(new ExperimentRecord(strategy, risk.Variant, ProtocolVersion, ProtocolCommit, universe.SnapshotHash, engineSha, hash, metrics), CancellationToken.None);
        results.Add((result, metrics));
        Console.WriteLine($"{strategy.Name} {risk.Variant}: return {metrics.TotalReturn:P2}, trades {metrics.Trades}, result {hash[..12]}");
    }
}

var tradingCandles = candles.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value.Where(c => c.OpenTime >= ResearchCutoff.U).ToList());
foreach (var benchmark in new[]
{
    Benchmarks.BuyAndHold("BTCUSDT", tradingCandles["BTCUSDT"], ResearchCutoff.U, ResearchCutoff.C, CostModel.V1, input.InitialCapital),
    Benchmarks.Basket("equal-weight-20", tradingCandles, ResearchCutoff.U, ResearchCutoff.C, CostModel.V1, input.InitialCapital),
    Benchmarks.Cash(ResearchCutoff.U, ResearchCutoff.C, TimeSpan.FromHours(4), input.InitialCapital),
})
{
    results.Add((benchmark, Metrics.Compute(benchmark, years)));
}

var reports = Directory.CreateDirectory(Path.Combine(ArchiveCacheDirectory.DefaultRoot, "reports")).FullName;
var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
var markdownPath = Path.Combine(reports, $"m2-baselines-{stamp}.md");
File.WriteAllText(markdownPath, Markdown(results, engineSha, universe.SnapshotHash), Encoding.UTF8);
File.WriteAllText(Path.ChangeExtension(markdownPath, ".json"),
    JsonSerializer.Serialize(results.Select(r => new { r.Result.Strategy, r.Result.Variant, hash = r.Result.Hash(), r.Metrics, haltedAt = r.Result.HaltedAt }),
        new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
Console.WriteLine($"Report: {markdownPath}");
return 0;

static async Task<int> ParityM3()
{
    // Result hashes recorded in docs/m2-baseline-results.md (engine 73a6b60 and 452fabd).
    var expected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ema-trend policy"] = "756c8fe803a9b0663be248408bb801e6c74f01e003ac141d942f2dc9e4c3e82a",
        ["ema-trend no-halt"] = "12a38d32eee2047af51853f96c6d186ebc66281f96cf0c7bf0e52374788683b2",
        ["supertrend policy"] = "ff8d0a951f32a7afe806061ffb7725bcf55c2216519304abaeebbd5b0668ad18",
        ["supertrend no-halt"] = "48c935fc1c9a4e150d0c7d3eaca974fe91105cf75918381e77d53d8c4482adb7",
    };
    var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
        ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m3-parity.ps1 sets it).");
    var universe = CoreUniverse.LoadDefault();
    var store = new CandleStore(connection);
    var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
    var candles = new Dictionary<string, IReadOnlyList<Candle>>();
    foreach (var symbol in universe.Ranks.Keys.Order(StringComparer.Ordinal))
    {
        candles[symbol] = await store.ReadAsync(symbol, CandleInterval.FourHours, range, CancellationToken.None);
    }

    var input = new BacktestInput(candles, universe.Ranks, ResearchCutoff.U, ResearchCutoff.C);
    var ok = true;
    foreach (var (coded, dsl) in new (IStrategy, IStrategy)[] { (new EmaTrendStrategy(), DslBaselines.EmaTrend()), (new SupertrendStrategy(), DslBaselines.Supertrend()) })
    {
        foreach (var risk in new[] { RiskSettings.Policy, RiskSettings.NoHalt })
        {
            var key = $"{coded.Name} {risk.Variant}";
            var codedHash = new BacktestEngine(coded, CostModel.V1, risk).Run(input).Hash();
            var dslHash = new BacktestEngine(dsl, CostModel.V1, risk).Run(input).Hash();
            var match = codedHash == expected[key] && dslHash == expected[key];
            ok &= match;
            Console.WriteLine($"{key}: m2 {expected[key][..12]} coded {codedHash[..12]} dsl {dslHash[..12]} -> {(match ? "OK" : "MISMATCH")}");
        }
    }

    Console.WriteLine(ok ? "Parity: all four M2 result hashes reproduced by the coded and the DSL baselines." : "Parity FAILED.");
    return ok ? 0 : 2;
}

static int Calibrate(string[] options)
{
    var quick = options.Contains("--quick");
    var outIndex = Array.IndexOf(options, "--out");
    var output = outIndex >= 0 && outIndex + 1 < options.Length ? options[outIndex + 1] : null;
    var settings = quick
        ? new CalibrationOptions(NullStrategies: 40, NullUniverses: 4, TruePositiveUniverses: 4, MaxParallelism: Environment.ProcessorCount)
        : new CalibrationOptions(MaxParallelism: Math.Max(1, Environment.ProcessorCount - 2));
    // Exploration of the planted-edge generator only (m3-protocol §7 fixes its target effect, ≈ 15 %, not its parameters).
    string? Option(string name) => Array.IndexOf(options, name) is var i and >= 0 && i + 1 < options.Length ? options[i + 1] : null;
    if (Option("--kappa") is { } kappa)
    {
        settings = settings with { TrendKappa = double.Parse(kappa, CultureInfo.InvariantCulture) };
    }

    if (Option("--regime-bars") is { } regimeBars)
    {
        settings = settings with { TrendMeanBars = int.Parse(regimeBars, CultureInfo.InvariantCulture) };
    }

    if (Option("--up-share") is { } upShare)
    {
        settings = settings with { TrendUpShare = double.Parse(upShare, CultureInfo.InvariantCulture) };
    }

    if (Option("--tp") is { } tp)
    {
        settings = settings with { TruePositiveUniverses = int.Parse(tp, CultureInfo.InvariantCulture) };
    }
    var started = DateTimeOffset.UtcNow;
    // --v2: protocol v2 (gates on the no-halt run, DSR estimator variance, κ search). Without it, v1; --no-halt
    // with v1 is a diagnostic only (v1 §6 evaluates the gates on the policy run).
    if (options.Contains("--v2"))
    {
        settings = settings with { ProtocolVersion = 2 };
    }

    var risk = options.Contains("--v2") || options.Contains("--no-halt") ? RiskSettings.NoHalt : RiskSettings.Policy;
    var report = new CalibrationRunner(CostModel.V1, risk, quick ? 1000 : 10_000)
        .Run(settings, message => Console.WriteLine($"[{(DateTimeOffset.UtcNow - started).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}] {message}"));
    var markdown = CalibrationMarkdown.Render(report, DateTimeOffset.UtcNow - started);
    if (options.Contains("--verbose"))
    {
        foreach (var x in report.TruePositive)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"TP M={x.PrimaryMetric:P1} trades={x.Trades} lower={x.Bounds.Lower:P1} wf={x.WalkForwardShare:0.00} nb={x.NeighbourhoodShare:0.00} multi={x.MultiAsset} sharpe={x.PeriodSharpe:0.0000} dsr={x.DeflatedSharpeProbability:0.000} halt={x.HaltedAt:yyyy-MM-dd}"));
        }
    }

    Console.WriteLine(markdown);
    if (output is not null)
    {
        File.WriteAllText(output, markdown, new UTF8Encoding(false));
        Console.WriteLine($"Report: {output}");
    }

    return report.Selected is null ? 3 : 0;
}

static string Markdown(List<(BacktestResult Result, PerformanceMetrics Metrics)> results, string engineSha, string snapshot)
{
    var s = new StringBuilder();
    s.AppendLine($"# M2 baseline raporu ({ProtocolVersion}, ön kayıt {ProtocolCommit})");
    s.AppendLine();
    s.AppendLine($"- Engine: `{engineSha}` · Veri snapshot'ı: `{snapshot}` · Evren: `core-4y-S` (survivor-conditioned) · 4h · R = [2022-01-01, 2026-01-01)");
    s.AppendLine("- Maliyetler düşülmüştür (cost-model v1). Kapı eşikleri M3'e aittir; bu rapor yalnızca betimleyicidir.");
    s.AppendLine();
    s.AppendLine("| Koşu | Toplam getiri | CAGR | Maks. DD | Sharpe | İşlem | Kazanma | Ort. maruziyet | Devir | Ücret | Slippage | Halt |");
    s.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (var (r, m) in results)
    {
        s.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {r.Strategy} ({r.Variant}) | {m.TotalReturn:P2} | {m.Cagr:P2} | {m.MaxDrawdown:P2} | {m.Sharpe:F2} | {m.Trades} | {m.WinRate:P1} | {m.AverageExposure:P1} | {m.Turnover:F1} | {m.FeesPaid:N0} | {m.SlippagePaid:N0} | {(r.HaltedAt is { } h ? h.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—")} |"));
    }

    s.AppendLine();
    s.AppendLine("Sonuç hash'leri:");
    foreach (var (r, _) in results)
    {
        s.AppendLine($"- {r.Strategy} ({r.Variant}): `{r.Hash()}`");
    }

    return s.ToString();
}

static string OpsConnection() => Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
    ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m5-research.ps1 sets it).");

static string GitHead(string root)
{
    var gitDir = Path.Combine(root, ".git");
    var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
    if (!head.StartsWith("ref: ", StringComparison.Ordinal))
    {
        return head;
    }

    var reference = head[5..];
    var loose = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(loose))
    {
        return File.ReadAllText(loose).Trim();
    }

    var packed = File.ReadLines(Path.Combine(gitDir, "packed-refs")).FirstOrDefault(l => l.EndsWith(" " + reference, StringComparison.Ordinal));
    return packed?.Split(' ')[0] ?? "unknown";
}

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
