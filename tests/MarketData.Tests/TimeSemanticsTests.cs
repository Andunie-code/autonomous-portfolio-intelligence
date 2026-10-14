using PortfolioIntelligence.Kernel.MarketData;
using static PortfolioIntelligence.MarketData.Tests.Support.SyntheticKlines;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>H1b and H1c (testing-strategy.md §7.1, roadmap M1 criteria 2–3).</summary>
public class TimeSemanticsTests
{
    private static readonly TimeRange Research = new(Utc(2022, 1, 1), ResearchCutoff.C);

    [Theory]
    [InlineData("1h", 2025, 12, 31, 23, true)]
    [InlineData("1h", 2026, 1, 1, 0, false)]
    [InlineData("4h", 2025, 12, 31, 20, true)]
    [InlineData("4h", 2026, 1, 1, 0, false)]
    [InlineData("1d", 2025, 12, 31, 0, true)]
    [InlineData("1d", 2026, 1, 1, 0, false)]
    [InlineData("1w", 2025, 12, 22, 0, true)]
    [InlineData("1w", 2025, 12, 29, 0, false)]
    [InlineData("1M", 2025, 12, 1, 0, true)]
    [InlineData("1M", 2026, 1, 1, 0, false)]
    public void Candle_membership_at_the_cutoff_uses_whole_intervals(string interval, int y, int m, int d, int h, bool member)
    {
        Assert.Equal(member, Research.ContainsCandle(Utc(y, m, d, h), CandleInterval.FromCode(interval)));
    }

    [Fact]
    public void Weekly_and_monthly_close_times_are_calendar_correct()
    {
        Assert.Equal(DayOfWeek.Monday, Utc(2025, 12, 29).DayOfWeek);
        Assert.Equal(Utc(2026, 1, 5), CandleInterval.OneWeek.CloseOf(Utc(2025, 12, 29)));
        Assert.Equal(Utc(2025, 3, 1), CandleInterval.OneMonth.CloseOf(Utc(2025, 2, 1)));
        Assert.Equal(ResearchCutoff.C, CandleInterval.OneMonth.CloseOf(Utc(2025, 12, 1)));
    }

    [Fact]
    public void Research_window_has_the_documented_candle_counts()
    {
        Assert.Equal(35_064, CandleInterval.OneHour.MemberOpens(ResearchCutoff.ResearchWindow).Count());
        Assert.Equal(8_766, CandleInterval.FourHours.MemberOpens(ResearchCutoff.ResearchWindow).Count());
        Assert.Equal(180, CandleInterval.OneDay.MemberOpens(ResearchCutoff.RankingWindow).Count());
        Assert.Equal(Utc(2021, 12, 2), ResearchCutoff.CorePoolLatestFirstOpen);
    }

    [Fact]
    public void Milliseconds_and_microseconds_give_the_same_utc_instant()
    {
        var instant = Utc(2025, 1, 1, 5);
        var ms = KlineCsvParser.ParseTimestamp(instant.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            new ArchiveFileKey("BTCUSDT", CandleInterval.OneHour, 2024, 12), 1);
        var us = KlineCsvParser.ParseTimestamp(((instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
            new ArchiveFileKey("BTCUSDT", CandleInterval.OneHour, 2025, 1), 1);

        Assert.Equal(instant, ms);
        Assert.Equal(instant, us);
        Assert.Equal(TimeSpan.Zero, ms.Offset);
        Assert.Equal(TimeSpan.Zero, us.Offset);
    }

    [Fact]
    public void Timestamp_unit_must_match_the_file_month()
    {
        var milliseconds = Utc(2025, 2, 1).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        // A millisecond value in a 2025 (microsecond) file would otherwise be read as 1970.
        Assert.Throws<ArchiveDataException>(() =>
            KlineCsvParser.ParseTimestamp(milliseconds, new ArchiveFileKey("BTCUSDT", CandleInterval.OneHour, 2025, 2), 1));
    }

    [Fact]
    public void Time_ranges_reject_non_utc_offsets()
    {
        Assert.Throws<ArgumentException>(() => new TimeRange(new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(3)), ResearchCutoff.C));
    }

    [Fact]
    public void MarketData_code_never_uses_the_local_clock_or_time_zone()
    {
        var root = Path.Combine(RepositoryRootPath(), "src");
        var files = Directory.EnumerateFiles(Path.Combine(root, "Kernel", "MarketData"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Modules", "MarketData"), "*.cs", SearchOption.AllDirectories));
        var forbidden = new[] { "DateTime.Now", "DateTimeOffset.Now", "ToLocalTime", "TimeZoneInfo.Local", "DateTimeKind.Local" };

        var hits = files.SelectMany(f => forbidden.Where(token => File.ReadAllText(f).Contains(token, StringComparison.Ordinal))
            .Select(token => $"{Path.GetFileName(f)}: {token}")).ToList();

        Assert.Empty(hits);
    }

    internal static string RepositoryRootPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PortfolioIntelligence.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
