namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>
/// Fixed research dates (docs/m1-data-policy.md §1, ADR-0006). C is the single source of truth in code;
/// migration 0002 defines the same instant as <c>market.research_cutoff()</c> and a test keeps them equal (H1d).
/// </summary>
public static class ResearchCutoff
{
    /// <summary>C: research cutoff. No candle closing after this instant may be listed, downloaded or stored.</summary>
    public static readonly DateTimeOffset C = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>U: universe and ranking date; start of the research window.</summary>
    public static readonly DateTimeOffset U = new(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Latest archive first open for the core pool (U0 = first open + 30 days ≤ U).</summary>
    public static readonly DateTimeOffset CorePoolLatestFirstOpen = U - CoreQuarantine;

    /// <summary>Listing quarantine (ADR-0012).</summary>
    public static TimeSpan CoreQuarantine => TimeSpan.FromDays(30);

    /// <summary>Ranking window W: 180 daily candles opening 2021-07-05 … 2021-12-31.</summary>
    public static readonly TimeRange RankingWindow = new(new DateTimeOffset(2021, 7, 5, 0, 0, 0, TimeSpan.Zero), U);

    /// <summary>Research window R = [U, C).</summary>
    public static readonly TimeRange ResearchWindow = new(U, C);
}
