namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// The Trading test suite never builds a real network path (m6-protocol §9, D33): exchange and market data clients
/// are built on in-memory handlers (the fake Testnet, stubs); an HttpClient without a handler is allowed only for the
/// loopback test server of the Ops API.
/// </summary>
public class NoNetworkTests
{
    [Fact]
    public void Tests_only_use_in_memory_handlers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PortfolioIntelligence.slnx")))
        {
            root = root.Parent;
        }

        var directory = Path.Combine(root!.FullName, "tests", "Trading.Tests");
        var lines = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.EndsWith(nameof(NoNetworkTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .ToList();

        var offenders = lines.Where(l =>
                l.Text.Contains("new HttpClient()", StringComparison.Ordinal)
                || l.Text.Contains("new SocketsHttpHandler", StringComparison.Ordinal)
                || l.Text.Contains("new HttpClientHandler", StringComparison.Ordinal)
                || (l.Text.Contains("CreateNetworkHandler()", StringComparison.Ordinal) && !l.Text.Contains("(SocketsHttpHandler)", StringComparison.Ordinal))
                || (l.Text.Contains("new HttpClient {", StringComparison.Ordinal) && !l.Text.Contains("app.Urls", StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.Empty(offenders);
        Assert.Contains(lines, l => l.Text.Contains("BinanceTestnetAdapter.CreateClient(new TestnetCredentials(ApiKey, SigningKey), clock, symbols, this)", StringComparison.Ordinal));
    }
}
