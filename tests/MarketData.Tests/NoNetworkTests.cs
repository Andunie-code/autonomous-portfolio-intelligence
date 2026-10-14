namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>H2c: the test suite never builds a real network path to the archive (roadmap M1 criterion 5).</summary>
public class NoNetworkTests
{
    [Fact]
    public void Tests_only_use_in_memory_handlers()
    {
        var directory = Path.Combine(TimeSemanticsTests.RepositoryRootPath(), "tests", "MarketData.Tests");
        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.EndsWith(nameof(NoNetworkTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => l.Text.Contains("new HttpClient()", StringComparison.Ordinal)
                        || l.Text.Contains("new SocketsHttpHandler", StringComparison.Ordinal)
                        || l.Text.Contains("new HttpClientHandler", StringComparison.Ordinal)
                        || (l.Text.Contains("CreateNetworkHandler()", StringComparison.Ordinal) && !l.Text.Contains("AllowAutoRedirect", StringComparison.Ordinal)
                            && !l.Text.Contains("using var handler = (SocketsHttpHandler)", StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.Empty(offenders);
    }
}
