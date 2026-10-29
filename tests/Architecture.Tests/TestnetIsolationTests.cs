namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>
/// D33 and m6-protocol §8: the Testnet key exists only for Trading.Host. Its secret names appear only in
/// Trading.Host, the trading-host service of the compose file and the user's scripts that write or check them; the
/// exchange code names only the Testnet host and never a production Binance trading host; the Research host has no
/// Testnet secret.
/// </summary>
public class TestnetIsolationTests
{
    private static readonly string Root = RepositoryRoot.Path;
    private static readonly string[] SecretNames = ["testnet_api_key", "testnet_api_secret"];
    private static readonly string[] Extensions = [".cs", ".csproj", ".props", ".json", ".yml", ".yaml", ".sql", ".ps1", ".sh", ".example", ".env", ".js", ".html"];

    private static IEnumerable<string> SourceFiles() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
        .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
        .Where(f => !f.StartsWith("docs/", StringComparison.Ordinal) && !f.StartsWith("tests/", StringComparison.Ordinal) && !f.StartsWith(".git/", StringComparison.Ordinal)
                    && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal) && !f.StartsWith("secrets/", StringComparison.Ordinal));

    [Fact]
    public void Testnet_secret_names_appear_only_for_trading_host()
    {
        static bool Allowed(string f) => f.StartsWith("src/Hosts/Trading.Host/", StringComparison.Ordinal) || f == "docker-compose.yml"
            || f.StartsWith("scripts/m6-", StringComparison.Ordinal) || f == "scripts/m5-paper-start.ps1";

        var hits = SourceFiles().Where(f => SecretNames.Any(n => File.ReadAllText(Path.Combine(Root, f)).Contains(n, StringComparison.Ordinal))).ToList();

        Assert.DoesNotContain(hits, f => !Allowed(f));
        Assert.Contains("src/Hosts/Trading.Host/TradingHost.cs", hits);
        Assert.Contains("docker-compose.yml", hits);
    }

    [Fact]
    public void Exchange_code_targets_only_the_testnet_host()
    {
        var execution = SourceFiles().Where(f => f.StartsWith("src/Modules/Execution/", StringComparison.Ordinal) || f.StartsWith("src/Hosts/Trading.Host/", StringComparison.Ordinal)).ToList();
        foreach (var file in execution)
        {
            var text = File.ReadAllText(Path.Combine(Root, file));
            Assert.DoesNotContain("api.binance.com", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/sapi/", text, StringComparison.Ordinal);
        }

        var guard = File.ReadAllText(Path.Combine(Root, "src/Modules/Execution/Exchange/Binance/TestnetGuardHandler.cs"));
        Assert.Contains("public const string Host = \"testnet.binance.vision\";", guard);
        Assert.Contains("AllowAutoRedirect = false", guard);
        // The base address is a constant, not configuration (D33).
        Assert.Contains("public const string BaseAddress = \"https://testnet.binance.vision\";", File.ReadAllText(Path.Combine(Root, "src/Modules/Execution/Exchange/Binance/BinanceTestnetAdapter.cs")));
        Assert.DoesNotContain(execution, f => File.ReadAllText(Path.Combine(Root, f)).Contains("Testnet:BaseAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_trading_host_mounts_the_testnet_key_and_testnet_is_off_by_default()
    {
        var compose = File.ReadAllLines(Path.Combine(Root, "docker-compose.yml"));
        var research = Block(compose, "research-host");
        var trading = Block(compose, "trading-host");

        Assert.DoesNotContain(research, l => l.Contains("testnet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("secrets: [trading_app, ops_api, ops_api_token, testnet_api_key, testnet_api_secret, gemini_runtime_api_key]", trading);
        // M8 (ADR-0023): the allocator's Gemini runtime key is mounted only into Trading.Host.
        Assert.DoesNotContain(research, l => l.Contains("gemini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Block(compose, "postgres"), l => l.Contains("gemini", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Trading__Testnet: ${PI_TRADING_TESTNET:-disabled}", trading);
        Assert.DoesNotContain(Block(compose, "postgres"), l => l.Contains("testnet", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Block(string[] lines, string service)
    {
        var start = Array.FindIndex(lines, l => l == $"  {service}:");
        return lines.Skip(start + 1).TakeWhile(l => l.Length == 0 || l.StartsWith("    ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
    }
}
