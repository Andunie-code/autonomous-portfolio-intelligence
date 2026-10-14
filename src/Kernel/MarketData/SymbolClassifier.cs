namespace PortfolioIntelligence.Kernel.MarketData;

public enum ExclusionCategory
{
    Stablecoin,
    Fiat,
    Leveraged,
    Wrapped,
    Commodity,
}

public enum ClassificationOutcome
{
    Include,
    Exclude,
    ReviewPending,
}

public sealed record SymbolClassification(string Symbol, string BaseAsset, ClassificationOutcome Outcome, ExclusionCategory? Category, string? Reason);

/// <summary>Versioned exclusion lists (docs/m1-data-policy.md §5 step 1). Changing them requires a new policy version.</summary>
public sealed record ExclusionList(
    string Version,
    IReadOnlySet<string> Stablecoins,
    IReadOnlySet<string> Fiat,
    IReadOnlySet<string> Wrapped,
    IReadOnlySet<string> Commodities,
    IReadOnlyList<string> LeveragedSuffixes);

/// <summary>
/// Classifies USDT spot symbols by base asset. Leveraged-token suffixes are only applied structurally: the base
/// must be another listed base asset plus the suffix (e.g. BTCUP when BTC is listed); otherwise the symbol is
/// marked for review instead of being excluded or included silently. Review-pending symbols are kept out of the
/// core and reported.
/// </summary>
public static class SymbolClassifier
{
    public const string Quote = "USDT";

    public static IReadOnlyList<SymbolClassification> Classify(IEnumerable<string> symbols, ExclusionList list)
    {
        var usdt = symbols.Where(s => s.EndsWith(Quote, StringComparison.Ordinal) && s.Length > Quote.Length).Distinct().ToList();
        var bases = usdt.Select(BaseOf).ToHashSet(StringComparer.Ordinal);
        return usdt.OrderBy(s => s, StringComparer.Ordinal).Select(s => ClassifyOne(s, bases, list)).ToList();
    }

    public static string BaseOf(string symbol) => symbol[..^Quote.Length];

    private static SymbolClassification ClassifyOne(string symbol, IReadOnlySet<string> bases, ExclusionList list)
    {
        var baseAsset = BaseOf(symbol);
        if (list.Stablecoins.Contains(baseAsset))
        {
            return Excluded(symbol, baseAsset, ExclusionCategory.Stablecoin);
        }

        if (list.Fiat.Contains(baseAsset))
        {
            return Excluded(symbol, baseAsset, ExclusionCategory.Fiat);
        }

        if (list.Wrapped.Contains(baseAsset))
        {
            return Excluded(symbol, baseAsset, ExclusionCategory.Wrapped);
        }

        if (list.Commodities.Contains(baseAsset))
        {
            return Excluded(symbol, baseAsset, ExclusionCategory.Commodity);
        }

        foreach (var suffix in list.LeveragedSuffixes)
        {
            if (baseAsset.Length <= suffix.Length || !baseAsset.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var underlying = baseAsset[..^suffix.Length];
            return bases.Contains(underlying)
                ? Excluded(symbol, baseAsset, ExclusionCategory.Leveraged)
                : new SymbolClassification(symbol, baseAsset, ClassificationOutcome.ReviewPending, null,
                    $"ends with '{suffix}' but '{underlying}' is not a listed base asset");
        }

        return new SymbolClassification(symbol, baseAsset, ClassificationOutcome.Include, null, null);
    }

    private static SymbolClassification Excluded(string symbol, string baseAsset, ExclusionCategory category) =>
        new(symbol, baseAsset, ClassificationOutcome.Exclude, category, null);
}
