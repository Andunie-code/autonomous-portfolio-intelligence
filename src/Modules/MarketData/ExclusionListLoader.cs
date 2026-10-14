using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData;

public static class ExclusionListLoader
{
    private const string ResourceSuffix = "Config.m1-exclusions.v1.json";

    public static ExclusionList LoadDefault()
    {
        var assembly = typeof(ExclusionListLoader).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return Parse(stream);
    }

    public static ExclusionList Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ExclusionList(
            root.GetProperty("version").GetString()!,
            Set(root, "stablecoins"),
            Set(root, "fiat"),
            Set(root, "wrapped"),
            Set(root, "commodities"),
            root.GetProperty("leveragedSuffixes").EnumerateArray().Select(e => e.GetString()!).ToList());
    }

    private static HashSet<string> Set(JsonElement root, string name) =>
        root.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
}
