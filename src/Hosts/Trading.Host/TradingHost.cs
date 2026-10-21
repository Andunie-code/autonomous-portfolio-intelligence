using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Radar;
using PortfolioIntelligence.Infrastructure.Logging;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.MarketData.Live;

namespace PortfolioIntelligence.Trading.Host;

/// <summary>
/// Trading.Host (M5): the hourly paper-trading worker and the Ops API. Configuration:
///   ConnectionStrings:Trading   ops database as trading_app
///   ConnectionStrings:OpsApi    ops database as ops_api (Ops API writes only)
///   OpsApi:TokenFile / OpsApi:Token   bearer token of the Ops API (Docker secret file in the container)
///   Trading:LiveData = enabled  fetch live candles (set only by the user's m5-paper-start.ps1; off by default)
///   Trading:SecretsDirectory    Docker secret files: login passwords by user name (connection strings carry none)
///   Trading:Worker = disabled   Ops API only (tests)
///   Radar:WatchSymbols          comma-separated watch-only symbols of the signal radar (ADR-0020 §5; no run, no order)
///   OpsApi:AllowedHosts         loopback host names accepted by the Ops API (default 127.0.0.1;localhost;[::1])
/// </summary>
public static class TradingHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddPortfolioLogging(builder.Configuration);
        var configuration = builder.Configuration;
        var watch = WatchSymbols(configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TradingHealth>();
        builder.Services.AddSingleton(_ => new PaperStore(Required(configuration, "Trading")));
        builder.Services.AddSingleton(_ => new OpsService(Required(configuration, "OpsApi")));
        builder.Services.AddSingleton(_ => new DashboardQueries(Required(configuration, "OpsApi")));
        builder.Services.AddSingleton(_ => new RadarService(Required(configuration, "OpsApi"), watch));
        builder.Services.AddSingleton(sp => new LiveCandleStore(Required(configuration, "Trading")));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<PaperStore>();
            return new PaperTrader(store, new LiveCandleSource(sp.GetRequiredService<LiveCandleStore>()), CostModel.V1);
        });
        builder.Services.AddSingleton(sp =>
        {
            LiveCandleFeed? feed = null;
            if (string.Equals(configuration["Trading:LiveData"], "enabled", StringComparison.Ordinal))
            {
                // The guard allows only the pairs of the frozen strategies and the configured radar watch list (read once
                // at start; a new cohort needs a restart).
                var symbols = sp.GetRequiredService<PaperStore>().FrozenAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .SelectMany(f => CohortManifestReader.Read(f.Manifest).Pairs.Select(p => p.Symbol)).Concat(watch).ToHashSet(StringComparer.Ordinal);
                var http = new HttpClient(new LiveKlineGuardHandler(symbols.ToHashSet(), LiveKlineGuardHandler.CreateNetworkHandler())) { Timeout = TimeSpan.FromSeconds(30) };
                feed = new LiveCandleFeed(new LiveKlineClient(http), sp.GetRequiredService<LiveCandleStore>());
            }

            return new TradingCycle(sp.GetRequiredService<PaperStore>(), sp.GetRequiredService<PaperTrader>(), feed, sp.GetRequiredService<TradingHealth>(), sp.GetRequiredService<TimeProvider>(), watch);
        });
        builder.Services.AddSingleton(_ => OpsToken.Load(configuration));
        if (!string.Equals(configuration["Trading:Worker"], "disabled", StringComparison.Ordinal))
        {
            builder.Services.AddHostedService<TradingWorker>();
        }

        return builder;
    }

    public static WebApplication Build(string[] args)
    {
        var app = CreateBuilder(args).Build();
        OpsApi.Map(app);
        return app;
    }

    /// <summary>Watch-only radar symbols: upper-case USDT spot symbols only (anything else is ignored).</summary>
    public static IReadOnlyList<string> WatchSymbols(IConfiguration configuration) =>
        (configuration["Radar:WatchSymbols"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length is > 4 and <= 20 && s.EndsWith("USDT", StringComparison.Ordinal) && s.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static string Required(IConfiguration configuration, string name)
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString(name) ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not set."));
        if (string.IsNullOrEmpty(connection.Password) && configuration["Trading:SecretsDirectory"] is { } secrets && connection.Username is { } user
            && File.Exists(Path.Combine(secrets, user)))
        {
            connection.Password = (File.ReadAllText(Path.Combine(secrets, user)).Trim());
        }

        return connection.ConnectionString;
    }
}

/// <summary>Runs <see cref="TradingCycle"/> at HH:00:15 UTC every hour (m5-protocol §2.1).</summary>
public sealed class TradingWorker(TradingCycle cycle, TimeProvider clock, ILogger<TradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First cycle immediately at start (restart: reconciliation, then missed bars in order).
        var scheduled = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await cycle.RunAsync(scheduled, stoppingToken);
            logger.LogInformation("Cycle {Scheduled:O}: {Bars} bars, degraded [{Degraded}]", scheduled, outcome.BarsProcessed, string.Join(", ", outcome.Degraded));
            scheduled = TradingCycle.NextSchedule(clock.GetUtcNow());
            var wait = scheduled - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, stoppingToken);
            }
        }
    }
}
