using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Research;
using Testcontainers.PostgreSql;
using static PortfolioIntelligence.Backtest.Tests.Support.Synthetic;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>The append-only experiment registry (protocol §12); requires Docker.</summary>
public class ExperimentRegistryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(_postgres.GetConnectionString(), SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly), CancellationToken.None);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Experiments_are_recorded_and_cannot_be_changed()
    {
        var store = new ExperimentStore(_postgres.GetConnectionString());
        var metrics = new PerformanceMetrics(0.1m, 0.02, 0.05m, 0.5, 3, 0.5, 0.3, 2, 10m, 5m);
        var id = await store.AddAsync(new ExperimentRecord(new EmaTrendStrategy(), "policy", "m2-protocol v1", "296757f",
            new string('a', 64), "deadbeef", new string('b', 64), metrics), CancellationToken.None);

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var update = new NpgsqlCommand($"UPDATE research.experiments SET variant = 'x' WHERE id = {id}", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }
}

/// <summary>Benchmarks against analytic values (protocol §10) and the core universe configuration.</summary>
public class BenchmarkTests
{
    [Fact]
    public void Buy_and_hold_matches_the_analytic_value()
    {
        var candles = Flat("BTCUSDT", 100, 100m, i => i == 99 ? 150m : 100m);
        var costs = CostModel.V1;
        var result = Benchmarks.BuyAndHold("BTCUSDT", candles, Start, At(100), costs, 10_000m);

        var expected = 10_000m / (1 + costs.FeeRate) / (100m * (1 + costs.OpenSlippage)) * 150m * (1 - costs.OpenSlippage) * (1 - costs.FeeRate);
        Assert.Equal(Math.Round(expected, 10), Math.Round(result.FinalEquity, 10));

        // Turnover counts each leg once: entry and exit notional over the average equity (about 2.4 here).
        var flat = Benchmarks.BuyAndHold("BTCUSDT", Flat("BTCUSDT", 100, 100m), Start, At(100), costs, 10_000m);
        Assert.InRange(Metrics.Compute(flat, 1).Turnover, 1.99, 2.01);
    }

    [Fact]
    public void Equal_weight_basket_on_constant_prices_loses_only_costs_and_rebalances_monthly_and_cash_is_constant()
    {
        var candles = Enumerable.Range(1, 4).ToDictionary(i => $"S{i}USDT", i => (IReadOnlyList<PortfolioIntelligence.Kernel.MarketData.Candle>)Flat($"S{i}USDT", 600, 10m * i));
        var basket = Benchmarks.Basket("equal-weight", candles, Start, At(600), CostModel.V1, 10_000m);
        var cash = Benchmarks.Cash(Start, At(600), TimeSpan.FromHours(4), 10_000m);

        // Constant prices: the basket only loses costs (entry, small monthly rebalances back to equal weight, exit).
        var oneWay = (1 + CostModel.V1.FeeRate) * (1 + CostModel.V1.OpenSlippage);
        Assert.InRange(basket.FinalEquity, 10_000m / oneWay / oneWay * 0.999m, 10_000m / oneWay / oneWay * 1.0001m);
        Assert.Contains(basket.Trades, t => t.EntryTime.Day == 1 && t.EntryTime.Month == 2 && t.EntryTime.Hour == 0);
        Assert.All(cash.Equity, p => Assert.Equal(10_000m, p.Equity));
        Assert.Equal(600, cash.Equity.Count);
    }

    [Fact]
    public void Core_universe_config_matches_the_accepted_list()
    {
        var universe = CoreUniverse.LoadDefault();

        Assert.Equal(20, universe.Ranks.Count);
        Assert.Equal("a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82", universe.SnapshotHash);
        Assert.Equal(1, universe.Ranks["BTCUSDT"]);
        Assert.DoesNotContain("LUNAUSDT", universe.Ranks.Keys);
    }
}
