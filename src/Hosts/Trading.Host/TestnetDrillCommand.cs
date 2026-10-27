using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Exchange.Binance;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Trading.Host;

/// <summary>
/// <c>testnet-drill [--symbol BTCUSDT] [--rounds 4] [--capital 20000]</c> (Gate C, m6-protocol §8.3), run by the user
/// in the trading-host container with its secrets (scripts/m6-testnet-drill.ps1). Writes the operational JSON report to
/// stdout. Exit codes: 0 PASS, 1 FAIL, 2 not run (Testnet key missing, or a Testnet run is active: the drill's trades
/// would break the account reconciliation; run it before the activation, or rebaseline afterwards).
/// </summary>
public static class TestnetDrillCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args.Where(a => a.Contains('=', StringComparison.Ordinal)).ToArray()).Build();
        string Option(string name, string fallback) => args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault() ?? fallback;
        var symbol = Option("symbol", "BTCUSDT");
        var rounds = int.Parse(Option("rounds", "4"), System.Globalization.CultureInfo.InvariantCulture);
        var capital = decimal.Parse(Option("capital", "20000"), System.Globalization.CultureInfo.InvariantCulture);

        var enabled = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?> { ["Trading:Testnet"] = "enabled" }).Build();
        if (TradingHost.TestnetCredentialsFrom(enabled) is not { } credentials)
        {
            await Console.Error.WriteLineAsync("The Testnet key is missing (secrets/testnet_api_key and testnet_api_secret: scripts/m6-testnet-key.ps1).");
            return 2;
        }

        IReadOnlyList<string> manifestSymbols = [];
        if (configuration.GetConnectionString("Trading") is { Length: > 0 } connection)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
            if (string.IsNullOrEmpty(builder.Password) && configuration["Trading:SecretsDirectory"] is { } secrets && builder.Username is { } user && File.Exists(Path.Combine(secrets, user)))
            {
                builder.Password = (File.ReadAllText(Path.Combine(secrets, user)).Trim());
            }

            var store = new PaperStore(builder.ConnectionString);
            var closed = await store.ClosedRunsAsync(CancellationToken.None);
            if ((await store.RunsAsync(CancellationToken.None)).Any(r => r.Config.IsTestnet && !closed.Contains(r.Id)))
            {
                await Console.Error.WriteLineAsync("A Testnet run is active: the drill would change the account balance. Run the drill before the activation, or rebaseline after it.");
                return 2;
            }

            manifestSymbols = (await store.FrozenAsync(CancellationToken.None)).SelectMany(f => CohortManifestReader.Read(f.Manifest).Pairs.Select(p => p.Symbol))
                .Distinct(StringComparer.Ordinal).ToList();
        }

        var clock = new ExchangeClock(TimeProvider.System);
        var allowed = manifestSymbols.Append(symbol).ToHashSet(StringComparer.Ordinal);
        using var http = BinanceTestnetAdapter.CreateClient(credentials, clock, allowed, TestnetGuardHandler.CreateNetworkHandler());
        var drill = new TestnetDrill(new BinanceTestnetAdapter(http, clock), clock, (wait, ct) => Task.Delay(wait, ct));
        var report = await drill.RunAsync(symbol, manifestSymbols, rounds, capital, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return report.Pass ? 0 : 1;
    }
}
