using System.Text.Json;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Exchange.Binance;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// The user's Gate C drill against the fake Testnet (m6-protocol §8.3): at least 20 order lifecycles, every scenario
/// passes, the account ends flat, an injected client timeout is resolved by the client order id (found, or
/// NeverAccepted after the horizon), and the report carries no price, quantity, balance or PnL field.
/// </summary>
public class TestnetDrillTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private static async Task<(DrillReport Report, FakeBinance Fake)> RunAsync(TimeSpan latency)
    {
        var time = new ManualTime(Now);
        var fake = new FakeBinance(time) { Price = s => s == "BTCUSDT" ? 64_000m : 3_000m };
        fake.Free["BTC"] = 0.5m;
        if (latency > TimeSpan.Zero)
        {
            fake.Latency = latency;
        }

        var clock = new ExchangeClock(time);
        var adapter = new BinanceTestnetAdapter(fake.Client(new HashSet<string> { "BTCUSDT", "ETHUSDT" }, clock), clock);
        var drill = new TestnetDrill(adapter, clock, (wait, _) => { time.Now += wait; return Task.CompletedTask; });
        return (await drill.RunAsync("BTCUSDT", ["BTCUSDT", "ETHUSDT"], rounds: 4, requiredCapital: 20_000m, default), fake);
    }

    [Fact]
    public async Task The_drill_runs_at_least_twenty_lifecycles_and_ends_flat()
    {
        var (report, fake) = await RunAsync(TimeSpan.Zero);

        Assert.True(report.Pass, string.Join("; ", report.Scenarios.Where(s => s.Result != "PASS").Select(s => $"{s.Name}:{s.Reason}")));
        Assert.All(report.Scenarios, s => Assert.Equal("PASS", s.Result));
        Assert.True(report.Orders >= 20);
        Assert.True(report.CompletedLifecycles >= 20);
        Assert.Equal(0, report.DuplicateOrders);
        Assert.Equal(1, report.ErrorCodes["-2010"]); // the duplicate client order id was refused by the exchange
        Assert.Equal(2, report.LocalRejectionsWithoutRequest);
        Assert.Equal((1, 1), (report.UnknownInjected, report.UnknownResolved));
        Assert.Equal(2, report.StopLossSupported);
        Assert.True(report.CanTrade && report.CapitalSufficient);
        Assert.DoesNotContain(fake.Orders, o => o.ClientOrderId.StartsWith("drill-", StringComparison.Ordinal) && o.Status == "NEW");
        Assert.All(fake.Orders, o => Assert.StartsWith(ClientOrderIds.DrillPrefix, o.ClientOrderId));
    }

    [Fact]
    public async Task A_request_the_exchange_never_saw_becomes_never_accepted_after_the_horizon()
    {
        var (report, _) = await RunAsync(TimeSpan.FromMilliseconds(40)); // the 1 ms client timeout fires before the exchange applies it

        Assert.Equal(1, report.NeverAccepted);
        Assert.Equal("PASS", report.Scenarios.Single(s => s.Name == "unknown-recovery").Result);
        Assert.True(report.Pass);
    }

    [Fact]
    public void The_capital_check_expects_the_exposure_limit_of_the_scaled_testnet_runs()
    {
        // A-4 v1.1: cohorts × 5,000 USDT × 80 % (D7) — two cohorts need 8,000 USDT free, within the ≈ 10,000 allowance.
        Assert.Equal(8_000m, PortfolioIntelligence.Trading.Host.TestnetDrillCommand.RequiredCapital(2));
        Assert.Equal(4_000m, PortfolioIntelligence.Trading.Host.TestnetDrillCommand.RequiredCapital(1));
    }

    [Fact]
    public async Task The_report_has_operational_fields_only()
    {
        var (report, _) = await RunAsync(TimeSpan.Zero);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

        string[] allowed =
        [
            "kind", "startedAt", "finishedAt", "rounds", "scenarios", "orders", "completedLifecycles", "ordersByTypeAndStatus", "errorCodes", "latencyMedianMs",
            "latencyP95Ms", "unknownInjected", "unknownResolved", "neverAccepted", "duplicateOrders", "localRejectionsWithoutRequest", "stopLossSupported",
            "symbolsChecked", "canTrade", "clockOffsetMs", "capitalSufficient", "rulesHash", "pass",
        ];
        Assert.Equal(allowed.Order(StringComparer.Ordinal), json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(json.GetProperty("scenarios").EnumerateArray(), s => Assert.Equal(["name", "reason", "result"], s.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        Assert.DoesNotMatch("(?i)price|quantity|qty|balance|pnl|amount|notional", json.GetRawText());
    }
}
