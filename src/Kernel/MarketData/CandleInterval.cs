namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>
/// Candle intervals with UTC close-time arithmetic. Weekly candles are assumed to open on Monday 00:00 UTC and
/// monthly candles on the first day of the month (not yet verified against the archive; docs/sources.md).
/// </summary>
public sealed class CandleInterval
{
    public static readonly CandleInterval OneHour = new("1h", "1h", TimeSpan.FromHours(1));
    public static readonly CandleInterval FourHours = new("4h", "4h", TimeSpan.FromHours(4));
    public static readonly CandleInterval OneDay = new("1d", "1d", TimeSpan.FromDays(1));
    public static readonly CandleInterval OneWeek = new("1w", "1w", TimeSpan.FromDays(7));
    public static readonly CandleInterval OneMonth = new("1M", "1mo", null);

    public static IReadOnlyList<CandleInterval> All { get; } = [OneHour, FourHours, OneDay, OneWeek, OneMonth];

    private readonly TimeSpan? _fixedLength;

    private CandleInterval(string code, string archiveName, TimeSpan? fixedLength)
    {
        Code = code;
        ArchiveName = archiveName;
        _fixedLength = fixedLength;
    }

    /// <summary>Code stored in the database (<c>market.candles.candle_interval</c>).</summary>
    public string Code { get; }

    /// <summary>Interval segment used in archive paths and file names.</summary>
    public string ArchiveName { get; }

    /// <summary>Fixed length, or null for the calendar month.</summary>
    public TimeSpan? FixedLength => _fixedLength;

    public static CandleInterval FromCode(string code) =>
        All.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.Ordinal))
        ?? throw new ArgumentException($"Unknown candle interval '{code}'.", nameof(code));

    /// <summary>Close (exclusive end) of a candle that opens at <paramref name="open"/>.</summary>
    public DateTimeOffset CloseOf(DateTimeOffset open)
    {
        var utc = open.ToUniversalTime();
        return _fixedLength is { } length ? utc + length : utc.AddMonths(1);
    }

    /// <summary>True when <paramref name="open"/> is a valid open time on this interval's UTC grid.</summary>
    public bool IsAligned(DateTimeOffset open)
    {
        if (open.Offset != TimeSpan.Zero)
        {
            return false;
        }

        if (this == OneMonth)
        {
            return open.Day == 1 && open.TimeOfDay == TimeSpan.Zero;
        }

        if (this == OneWeek)
        {
            return open.DayOfWeek == DayOfWeek.Monday && open.TimeOfDay == TimeSpan.Zero;
        }

        return open.UtcTicks % _fixedLength!.Value.Ticks == 0;
    }

    /// <summary>Latest aligned open time t with <paramref name="start"/> ≤ t &lt; <paramref name="end"/>, or null.</summary>
    public DateTimeOffset? LastOpenBefore(DateTimeOffset start, DateTimeOffset end)
    {
        DateTimeOffset candidate;
        if (this == OneMonth)
        {
            var last = end.AddTicks(-1);
            candidate = new DateTimeOffset(last.Year, last.Month, 1, 0, 0, 0, TimeSpan.Zero);
        }
        else if (this == OneWeek)
        {
            var last = end.AddTicks(-1);
            var day = new DateTimeOffset(last.Year, last.Month, last.Day, 0, 0, 0, TimeSpan.Zero);
            candidate = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        }
        else
        {
            var length = _fixedLength!.Value.Ticks;
            var ticks = end.UtcTicks - 1;
            candidate = new DateTimeOffset(ticks - (ticks % length), TimeSpan.Zero);
        }

        return candidate >= start ? candidate : null;
    }

    /// <summary>All aligned open times of candles that are members of <paramref name="range"/>.</summary>
    public IEnumerable<DateTimeOffset> MemberOpens(TimeRange range)
    {
        var first = FirstOpenAtOrAfter(range.Start);
        for (var open = first; CloseOf(open) <= range.End; open = CloseOf(open))
        {
            yield return open;
        }
    }

    private DateTimeOffset FirstOpenAtOrAfter(DateTimeOffset start)
    {
        if (IsAligned(start))
        {
            return start;
        }

        var previous = LastOpenBefore(DateTimeOffset.MinValue.AddYears(1), start.AddTicks(1))
            ?? throw new InvalidOperationException("No aligned open before start.");
        return CloseOf(previous);
    }

    public override string ToString() => Code;
}
