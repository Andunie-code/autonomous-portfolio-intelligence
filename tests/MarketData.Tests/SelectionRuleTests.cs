using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Tests.Support;
using static PortfolioIntelligence.MarketData.Tests.Support.SyntheticKlines;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>Coverage, 1h/4h consistency, ranking, classification and selection rules (policy §5–6).</summary>
public class SelectionRuleTests
{
    private static readonly TimeRange R = ResearchCutoff.ResearchWindow;

    private static List<DateTimeOffset> AllHours(Func<DateTimeOffset, bool>? drop = null) =>
        CandleInterval.OneHour.MemberOpens(R).Where(o => drop is null || !drop(o)).ToList();

    [Fact]
    public void Full_coverage_is_continuous()
    {
        var result = CoverageAnalyzer.Analyze(AllHours(), CandleInterval.OneHour, R);

        Assert.Equal(CoverageLabel.Continuous, result.Label);
        Assert.Equal(35_064, result.Expected);
        Assert.Equal(0, result.Missing);
    }

    [Fact]
    public void A_72_hour_gap_passes_and_a_73_hour_gap_fails()
    {
        var start = Utc(2023, 3, 1);
        var ok = CoverageAnalyzer.Analyze(AllHours(o => o >= start && o < start.AddHours(72)), CandleInterval.OneHour, R);
        var bad = CoverageAnalyzer.Analyze(AllHours(o => o >= start && o < start.AddHours(73)), CandleInterval.OneHour, R);

        Assert.Equal(CoverageLabel.Continuous, ok.Label);
        Assert.Equal(CoverageLabel.InternalGap, bad.Label);
    }

    [Fact]
    public void Coverage_below_99_percent_fails_even_without_long_gaps()
    {
        // 351 single missing hours, never adjacent: the 350-candle allowance is exceeded.
        var dropped = CandleInterval.OneHour.MemberOpens(R).Where((_, i) => i % 99 == 50).Take(351).ToHashSet();
        var exactly = CandleInterval.OneHour.MemberOpens(R).Where((_, i) => i % 99 == 50).Take(350).ToHashSet();

        Assert.Equal(CoverageLabel.LowCoverage, CoverageAnalyzer.Analyze(AllHours(dropped.Contains), CandleInterval.OneHour, R).Label);
        Assert.Equal(CoverageLabel.Continuous, CoverageAnalyzer.Analyze(AllHours(exactly.Contains), CandleInterval.OneHour, R).Label);
    }

    [Fact]
    public void Data_ending_before_the_cutoff_is_labelled_ended()
    {
        var result = CoverageAnalyzer.Analyze(AllHours(o => o >= Utc(2024, 6, 1)), CandleInterval.OneHour, R);

        Assert.Equal(CoverageLabel.Ended, result.Label);
    }

    [Fact]
    public void Four_hour_gap_limit_is_18_candles()
    {
        var start = Utc(2023, 3, 1);
        var hours = CandleInterval.FourHours.MemberOpens(R).ToList();

        Assert.True(CoverageAnalyzer.Analyze(hours.Where(o => o < start || o >= start.AddHours(72)), CandleInterval.FourHours, R).Passes);
        Assert.False(CoverageAnalyzer.Analyze(hours.Where(o => o < start || o >= start.AddHours(76)), CandleInterval.FourHours, R).Passes);
    }

    [Fact]
    public void Consistent_blocks_match_and_mismatches_and_missing_parts_are_reported_separately()
    {
        var oneHourKey = new ArchiveFileKey("BTCUSDT", CandleInterval.OneHour, 2023, 1);
        var fourHourKey = new ArchiveFileKey("BTCUSDT", CandleInterval.FourHours, 2023, 1);
        var hourly = KlineCsvParser.Parse(SyntheticKlines.Lines(oneHourKey, o => o != Utc(2023, 1, 2, 1)), oneHourKey);
        var fourHour = KlineCsvParser.Parse(SyntheticKlines.Lines(fourHourKey), fourHourKey).ToList();
        fourHour[10] = fourHour[10] with { QuoteVolume = fourHour[10].QuoteVolume + 0.00000001m };

        var result = IntervalConsistency.Check(hourly, fourHour);

        Assert.Equal(1, result.Unverifiable);
        var anomaly = Assert.Single(result.Anomalies);
        Assert.Equal(new[] { "quote_volume" }, anomaly.MismatchedFields);
        Assert.Equal(fourHour.Count - 1, result.Verified);
    }

    [Fact]
    public void Unknown_days_are_never_counted_as_zero_volume()
    {
        var days = CandleInterval.OneDay.MemberOpens(ResearchCutoff.RankingWindow).ToList();
        var candles = days.Where((_, i) => i % 3 != 0)
            .Select(d => new Candle("AAAUSDT", CandleInterval.OneDay, d, 1, 1, 1, 1, 1, 100m, 1)).ToList();

        var stats = CoreRanking.ComputeStats("AAAUSDT", candles, Utc(2020, 1, 1));

        Assert.Equal(180, stats.Expected);
        Assert.Equal(60, stats.Unknown);
        Assert.Equal(100m, stats.MedianQuoteVolume);
        Assert.Equal(RankingStatus.TooManyUnknownDays, CoreRanking.StatusOf(stats));
    }

    [Theory]
    [InlineData(90, 0, RankingStatus.Ranked)]
    [InlineData(89, 0, RankingStatus.InsufficientObservations)]
    [InlineData(162, 18, RankingStatus.Ranked)]
    [InlineData(161, 19, RankingStatus.TooManyUnknownDays)]
    public void Rankability_needs_90_observations_and_at_most_10_percent_unknown(int observed, int unknown, RankingStatus status)
    {
        Assert.Equal(status, CoreRanking.StatusOf(new RankingStats("X", observed + unknown, observed, unknown, 1m)));
    }

    [Fact]
    public void Ranking_sorts_by_median_then_ordinal_symbol_and_median_of_even_count_is_the_middle_mean()
    {
        Assert.Equal(2.5m, CoreRanking.Median([4m, 1m, 3m, 2m]));
        Assert.Null(CoreRanking.Median([]));

        var ranked = CoreRanking.Rank(
        [
            new RankingStats("bBUSDT", 180, 180, 0, 5m),
            new RankingStats("BAUSDT", 180, 180, 0, 5m),
            new RankingStats("ZZUSDT", 180, 180, 0, 9m),
            new RankingStats("LOWUSDT", 180, 50, 0, 1m),
        ]);

        Assert.Equal(new[] { "ZZUSDT", "BAUSDT", "bBUSDT", "LOWUSDT" }, ranked.Select(r => r.Symbol));
        Assert.Null(ranked[3].Rank);
    }

    [Fact]
    public void Classifier_applies_categories_and_structural_leveraged_rule()
    {
        var list = ExclusionListLoader.LoadDefault();
        var result = SymbolClassifier.Classify(
            ["BTCUSDT", "BTCUPUSDT", "JUPUSDT", "USDCUSDT", "EURUSDT", "WBTCUSDT", "PAXGUSDT", "ETHBTC", "ETHUSDT"], list)
            .ToDictionary(c => c.Symbol);

        Assert.DoesNotContain("ETHBTC", result.Keys);
        Assert.Equal(ClassificationOutcome.Include, result["BTCUSDT"].Outcome);
        Assert.Equal(ExclusionCategory.Leveraged, result["BTCUPUSDT"].Category);
        Assert.Equal(ClassificationOutcome.ReviewPending, result["JUPUSDT"].Outcome);
        Assert.Equal(ExclusionCategory.Stablecoin, result["USDCUSDT"].Category);
        Assert.Equal(ExclusionCategory.Fiat, result["EURUSDT"].Category);
        Assert.Equal(ExclusionCategory.Wrapped, result["WBTCUSDT"].Category);
        Assert.Equal(ExclusionCategory.Commodity, result["PAXGUSDT"].Category);
    }

    [Fact]
    public async Task Selector_replaces_failing_candidates_in_order_and_reports_incomplete_without_relaxing()
    {
        var ranking = Enumerable.Range(1, 5)
            .Select(i => new RankedSymbol($"S{i}USDT", new RankingStats($"S{i}USDT", 180, 180, 0, 10 - i), RankingStatus.Ranked, i)).ToList();
        var pass = new CoverageResult(CandleInterval.OneHour, 1, 1, 0, 0, 0, 0, CoverageLabel.Continuous);
        var ended = pass with { Label = CoverageLabel.Ended };

        Task<QualityAssessment> Assess(RankedSymbol s, CancellationToken _) => Task.FromResult(
            new QualityAssessment(s.Symbol, s.Symbol == "S2USDT" ? ended : pass, pass, s.Symbol == "S4USDT", null));

        var three = await CoreSelector.SelectAsync(ranking, Assess, 3, CancellationToken.None);
        Assert.True(three.Complete);
        Assert.Equal(new[] { "S1USDT", "S3USDT", "S5USDT" }, three.Selected.Select(s => s.Symbol));
        Assert.Equal(new[] { (2, EliminationReason.Ended), (4, EliminationReason.Integrity) }, three.Eliminated.Select(e => (e.Rank, e.Reason)));

        var four = await CoreSelector.SelectAsync(ranking, Assess, 4, CancellationToken.None);
        Assert.False(four.Complete);
        Assert.Equal(3, four.Selected.Count);
    }
}
