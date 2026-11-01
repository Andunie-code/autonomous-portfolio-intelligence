using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Ai.Gemini;
using PortfolioIntelligence.Ai.Persistence;
using PortfolioIntelligence.Allocator;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Exchange.Binance;
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
///   Trading:Testnet = enabled   Binance Spot Testnet execution (M6, ADR-0021; off by default). Needs the Docker
///                               secrets testnet_api_key and testnet_api_secret in Trading:SecretsDirectory (D33)
///   Allocator (M8, ADR-0023)    the mode is the user's (Ops API); a model is called only with the Docker secret
///                               gemini_runtime_api_key in Trading:SecretsDirectory, otherwise every decision is T11
/// </summary>
public static class TradingHost
{
    public const string TestnetKeyFile = "testnet_api_key";
    public const string TestnetSecretFile = "testnet_api_secret";
    public const string GeminiKeyFile = "gemini_runtime_api_key";

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;
        var credentials = TestnetCredentialsFrom(configuration);
        // Docker secret files never enter the configuration: their values are masked as literals (m6-protocol §8.2).
        builder.Logging.AddPortfolioLogging(builder.Configuration, FileSecrets(configuration));
        var watch = WatchSymbols(configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TradingHealth>();
        builder.Services.AddSingleton<MaintenanceSignal>();
        builder.Services.AddSingleton(_ => new PaperStore(Required(configuration, "Trading")));
        builder.Services.AddSingleton(sp => new OpsService(Required(configuration, "OpsApi"), sp.GetRequiredService<TimeProvider>(), credentials is not null));
        builder.Services.AddSingleton(sp => new TestnetRuntime(credentials is null ? null : CreateExecutor(sp, credentials)));
        builder.Services.AddSingleton(_ => new DashboardQueries(Required(configuration, "OpsApi")));
        builder.Services.AddSingleton(_ => Budget(AiConfiguration.LoadDefault()));
        builder.Services.AddSingleton(_ => new RadarService(Required(configuration, "OpsApi"), watch));
        builder.Services.AddSingleton(sp => new LiveCandleStore(Required(configuration, "Trading")));
        builder.Services.AddSingleton(_ => new AllocatorStore(Required(configuration, "Trading")));
        builder.Services.AddSingleton(sp => CreateAllocator(configuration, Required(configuration, "Trading"), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<PaperStore>();
            return new PaperTrader(store, new LiveCandleSource(sp.GetRequiredService<LiveCandleStore>()), CostModel.V1, testnet: sp.GetRequiredService<TestnetRuntime>().Executor,
                allocator: sp.GetRequiredService<IAllocationAdvisor>(), allocatorStore: sp.GetRequiredService<AllocatorStore>());
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

    /// <summary>
    /// The Testnet key from the Docker secret files (D33): only with Trading:Testnet = enabled and both files present
    /// and non-empty; otherwise Testnet stays off.
    /// </summary>
    public static TestnetCredentials? TestnetCredentialsFrom(IConfiguration configuration)
    {
        if (!string.Equals(configuration["Trading:Testnet"], "enabled", StringComparison.Ordinal) || configuration["Trading:SecretsDirectory"] is not { Length: > 0 } secrets)
        {
            return null;
        }

        var key = ReadSecret(Path.Combine(secrets, TestnetKeyFile));
        var secret = ReadSecret(Path.Combine(secrets, TestnetSecretFile));
        return key is { Length: > 0 } && secret is { Length: > 0 } ? new TestnetCredentials(key, secret) : null;
    }

    /// <summary>Values of the secret files the host reads (Ops API token, login passwords, Testnet key), for log redaction.</summary>
    public static IReadOnlyList<string> FileSecrets(IConfiguration configuration)
    {
        var files = new List<string>();
        if (configuration["OpsApi:TokenFile"] is { Length: > 0 } token)
        {
            files.Add(token);
        }

        if (configuration["Trading:SecretsDirectory"] is { Length: > 0 } secrets)
        {
            files.AddRange(new[] { TestnetKeyFile, TestnetSecretFile, GeminiKeyFile, "trading_app", "ops_api" }.Select(f => Path.Combine(secrets, f)));
        }

        return files.Select(ReadSecret).OfType<string>().Where(v => v.Length > 0).ToList();
    }

    private static string? ReadSecret(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    /// <summary>
    /// The allocator (ADR-0023): ledger → AI kill switch → Gemini (its own host guard) → call guard → router → advisor,
    /// all on the trading_app connection. Without the runtime key file no model can be called: every decision is T11.
    /// </summary>
    /// <summary>The AI caps as plain values for the dashboard and the alarms (Execution stays AI-free; ADR-0024 §1).</summary>
    public static AiBudget Budget(AiConfiguration ai) =>
        new(ai.Spend.MonthlyCapUsd, ai.Spend.JobShares.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value, StringComparer.Ordinal));

    public static IAllocationAdvisor CreateAllocator(IConfiguration configuration, string connection, TimeProvider time)
    {
        if (configuration["Trading:SecretsDirectory"] is not { Length: > 0 } secrets || ReadSecret(Path.Combine(secrets, GeminiKeyFile)) is not { Length: > 0 } key)
        {
            return new DeterministicAllocationAdvisor();
        }

        var ai = AiConfiguration.LoadDefault();
        var ledger = new NpgsqlCostLedger(connection);
        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(connection), new NpgsqlAiAudit(connection), time);
        var guard = new AiCallGuard(new GeminiProvider(key, ai.Prices, GeminiGuardHandler.CreateNetworkHandler()), ai, ledger, killSwitch, time);
        var protocols = new NpgsqlQualificationStore(connection);
        var router = new ModelRouter(ai, guard, new NpgsqlDecisionTraceStore(connection), new QualificationRegistry(protocols), time);
        return new AllocationAdvisor(router, protocols, ledger, time);
    }

    /// <summary>
    /// Testnet execution: HTTP client (HMAC signing, then the Testnet guard as the last handler; no redirects), the
    /// adapter, the outbox store as trading_app and the executor. The guard allows the frozen strategies' pairs (read
    /// once at start; a new cohort needs a restart).
    /// </summary>
    private static TestnetExecutor CreateExecutor(IServiceProvider sp, TestnetCredentials credentials)
    {
        var store = sp.GetRequiredService<PaperStore>();
        var symbols = store.FrozenAsync(CancellationToken.None).GetAwaiter().GetResult()
            .SelectMany(f => CohortManifestReader.Read(f.Manifest).Pairs.Select(p => p.Symbol)).ToHashSet(StringComparer.Ordinal);
        var clock = new ExchangeClock(sp.GetRequiredService<TimeProvider>());
        var http = BinanceTestnetAdapter.CreateClient(credentials, clock, symbols, TestnetGuardHandler.CreateNetworkHandler());
        return new TestnetExecutor(new BinanceTestnetAdapter(http, clock), new ExchangeStore(store.ConnectionString), clock,
            (e, payload, ct) => store.AuditAsync("trading", e, payload, ct));
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

/// <summary>Testnet execution of this host, or none when Testnet is off.</summary>
public sealed record TestnetRuntime(TestnetExecutor? Executor);

/// <summary>
/// Runs <see cref="TradingCycle"/> at HH:00:15 UTC every hour (m5-protocol §2.1); in between, a maintenance request
/// (kill switch engaged, m6-protocol §5.5) runs the exchange maintenance at once.
/// </summary>
public sealed class TradingWorker(TradingCycle cycle, TimeProvider clock, MaintenanceSignal signal, ILogger<TradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First cycle immediately at start (restart: reconciliation, then missed bars in order).
        var scheduled = clock.GetUtcNow();
        Task? request = null; // one pending read of the signal at a time, so no request is lost
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await cycle.RunAsync(scheduled, stoppingToken);
            logger.LogInformation("Cycle {Scheduled:O}: {Bars} bars, degraded [{Degraded}]", scheduled, outcome.BarsProcessed, string.Join(", ", outcome.Degraded));
            scheduled = TradingCycle.NextSchedule(clock.GetUtcNow());
            for (var wait = scheduled - clock.GetUtcNow(); wait > TimeSpan.Zero; wait = scheduled - clock.GetUtcNow())
            {
                var due = Task.Delay(wait, clock, stoppingToken);
                request ??= signal.WaitAsync(stoppingToken);
                if (await Task.WhenAny(due, request) != request)
                {
                    await due;
                    break;
                }

                await request;
                request = null;
                await cycle.MaintainAsync(stoppingToken);
                logger.LogInformation("Exchange maintenance on request");
            }
        }
    }
}
