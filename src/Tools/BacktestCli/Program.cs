using System.Globalization;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research;

// M2: runs the two pre-registered baselines (policy and no-halt) and the benchmarks on the 20 core pairs
// (docs/m2-protocol.md). Reads market.candles from the local database; no network. Output: metrics only.
const string ProtocolVersion = "m2-protocol v1";
const string ProtocolCommit = "296757f";

var command = args.FirstOrDefault() ?? "run-baselines";
if (command != "run-baselines")
{
    Console.Error.WriteLine("Usage: run-baselines");
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
