using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Tests.Support;
using static PortfolioIntelligence.MarketData.Tests.Support.SyntheticKlines;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>H2 and H2b (planner) and the per-file integrity rules (policy §6–7).</summary>
public class PlannerAndParserTests
{
    [Fact]
    public void Requests_reaching_past_the_cutoff_are_rejected_before_planning()
    {
        Assert.Throws<CutoffViolationException>(() =>
            MonthlyFilePlanner.Plan("BTCUSDT", CandleInterval.OneHour, new TimeRange(Utc(2025, 6, 1), Utc(2026, 2, 1))));
    }

    [Theory]
    [InlineData("1h", true)]
    [InlineData("4h", true)]
    [InlineData("1d", true)]
    [InlineData("1M", true)]
    [InlineData("1w", false)]
    public void December_2025_file_is_planned_only_when_all_its_candles_close_by_the_cutoff(string interval, bool planned)
    {
        var files = MonthlyFilePlanner.Plan("BTCUSDT", CandleInterval.FromCode(interval), new TimeRange(Utc(2025, 11, 1), ResearchCutoff.C));

        Assert.Equal(planned, files.Any(f => f.Year == 2025 && f.Month == 12));
    }

    [Fact]
    public void Planner_never_plans_a_file_with_a_candle_closing_after_the_cutoff_for_random_ranges()
    {
        var random = new Random(20260101);
        for (var i = 0; i < 2_000; i++)
        {
            var interval = CandleInterval.All[random.Next(CandleInterval.All.Count)];
            var start = Utc(2017, 1, 1).AddHours(random.Next(0, 9 * 365 * 24));
            var end = start.AddHours(random.Next(1, 4 * 365 * 24));
            if (end > ResearchCutoff.C)
            {
                Assert.Throws<CutoffViolationException>(() => MonthlyFilePlanner.Plan("ETHUSDT", interval, new TimeRange(start, end)));
                continue;
            }

            foreach (var key in MonthlyFilePlanner.Plan("ETHUSDT", interval, new TimeRange(start, end)))
            {
                var last = interval.LastOpenBefore(key.MonthStart, key.MonthEnd);
                Assert.True(last is null || interval.CloseOf(last.Value) <= ResearchCutoff.C, key.Path);
            }
        }
    }

    [Fact]
    public void Valid_file_parses_into_utc_candles()
    {
        var key = new ArchiveFileKey("BTCUSDT", CandleInterval.FourHours, 2025, 3);
        var candles = KlineCsvParser.Parse(SyntheticKlines.Lines(key), key);

        Assert.Equal(31 * 6, candles.Count);
        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.OpenTime.Offset));
        Assert.Equal(Utc(2025, 3, 1), candles[0].OpenTime);
    }

    [Fact]
    public void Header_row_is_tolerated()
    {
        var key = new ArchiveFileKey("BTCUSDT", CandleInterval.OneDay, 2023, 2);
        var lines = new List<string> { "open_time,open,high,low,close,volume,close_time,quote_volume,count,taker_buy_volume,taker_buy_quote_volume,ignore" };
        lines.AddRange(SyntheticKlines.Lines(key));

        Assert.Equal(28, KlineCsvParser.Parse(lines, key).Count);
    }

    [Fact]
    public void Candle_closing_after_the_cutoff_is_a_cutoff_violation()
    {
        var key = new ArchiveFileKey("BTCUSDT", CandleInterval.OneWeek, 2025, 12);
        var line = Line(key, Utc(2025, 12, 29), (100m, 101m, 99m, 100.5m, 1m, 100m, 1));

        Assert.Throws<CutoffViolationException>(() => KlineCsvParser.Parse([line], key));
    }

    public static TheoryData<string> CorruptRows => new()
    {
        "ohlc",
        "duplicate",
        "outside-month",
        "off-grid",
        "columns",
        "negative-volume",
    };

    [Theory]
    [MemberData(nameof(CorruptRows))]
    public void Any_integrity_violation_rejects_the_whole_file(string corruption)
    {
        var key = new ArchiveFileKey("ETHUSDT", CandleInterval.OneHour, 2023, 5);
        var lines = SyntheticKlines.Lines(key).Take(10).ToList();
        switch (corruption)
        {
            case "ohlc":
                lines[3] = Line(key, Utc(2023, 5, 1, 3), (100m, 99m, 98m, 100m, 1m, 100m, 1));
                break;
            case "duplicate":
                lines[4] = lines[3];
                break;
            case "outside-month":
                lines.Add(Line(key, Utc(2023, 6, 1, 0), (100m, 101m, 99m, 100m, 1m, 100m, 1)));
                break;
            case "off-grid":
                lines.Add(Line(key, Utc(2023, 5, 2, 0).AddMinutes(30), (100m, 101m, 99m, 100m, 1m, 100m, 1)));
                break;
            case "columns":
                lines[2] += ",extra";
                break;
            case "negative-volume":
                lines[1] = Line(key, Utc(2023, 5, 1, 1), (100m, 101m, 99m, 100m, -1m, 100m, 1));
                break;
        }

        Assert.Throws<ArchiveDataException>(() => KlineCsvParser.Parse(lines, key));
    }
}
