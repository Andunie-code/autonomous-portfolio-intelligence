namespace PortfolioIntelligence.Kernel.MarketData;

public enum PairCategory
{
    Core,
    E1,
    E2,
}

/// <summary>
/// Pair categories of ADR-0012 and docs/m3-protocol.md §11. U0 = first archive open + 30 days (quarantine).
/// E1: not core, U0 ≤ 2025-01-01 and the quality rules of m1-data-policy §6 hold on [U0, C) (no outage exemption).
/// A pair that fails quality is eliminated and reported, never moved down to E2. Data before U0 is warm-up only.
/// </summary>
public static class PairCategories
{
    public static readonly TimeSpan Quarantine = TimeSpan.FromDays(30);

    public static readonly DateTimeOffset E1LatestU0 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan MinimumE1History = TimeSpan.FromDays(365);

    public static DateTimeOffset U0(DateTimeOffset firstOpen) => firstOpen + Quarantine;

    /// <summary>Research window of an E1 pair: [U0, C).</summary>
    public static TimeRange ResearchWindow(DateTimeOffset firstOpen) => new(U0(firstOpen), ResearchCutoff.C);

    /// <summary>Listed after the core deadline and early enough for E1 (before quality is known).</summary>
    public static bool IsE1Candidate(DateTimeOffset firstOpen) =>
        firstOpen > ResearchCutoff.CorePoolLatestFirstOpen && U0(firstOpen) <= E1LatestU0 && ResearchCutoff.C - U0(firstOpen) >= MinimumE1History;

    /// <summary>The category of a non-core pair, or null when it fails quality (eliminated).</summary>
    public static PairCategory? Categorize(DateTimeOffset firstOpen, bool qualityPasses) =>
        !IsE1Candidate(firstOpen) ? (firstOpen <= ResearchCutoff.CorePoolLatestFirstOpen ? null : PairCategory.E2)
        : qualityPasses ? PairCategory.E1 : null;

    /// <summary>Evidence label (ADR-0012): core-4y, E1-&lt;n&gt;y (full years in [U0, C)) or E2-forward-only.</summary>
    public static string EvidenceLabel(PairCategory category, DateTimeOffset? firstOpen = null) => category switch
    {
        PairCategory.Core => "core-4y",
        PairCategory.E1 => $"E1-{(int)((ResearchCutoff.C - U0(firstOpen ?? throw new ArgumentNullException(nameof(firstOpen)))).TotalDays / 365.25)}y",
        _ => "E2-forward-only",
    };
}
