using System.Text.Json;

namespace PortfolioIntelligence.MarketData;

/// <summary>
/// Append-only incident log in the cache directory (docs/m1-data-policy.md §3 item 9). Entries hold metadata only
/// (time, rule, archive path), never candle contents.
/// </summary>
public sealed class IncidentLog(string path)
{
    public void Append(string rule, string archivePath, string detail)
    {
        var entry = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, rule, archivePath, detail });
        File.AppendAllLines(path, [entry]);
    }
}
