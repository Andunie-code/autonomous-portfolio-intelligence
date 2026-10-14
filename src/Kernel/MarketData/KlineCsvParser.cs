using System.Globalization;

namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>
/// Parses one monthly kline CSV file and enforces the integrity rules of docs/m1-data-policy.md §6–7.
/// Assumed column order (binance-public-data README, not yet re-verified): open_time, open, high, low, close,
/// volume, close_time, quote_volume, count, taker_buy_volume, taker_buy_quote_volume, ignore.
/// Any violation rejects the whole file. A candle closing after C raises <see cref="CutoffViolationException"/>
/// before any other field of that row is read.
/// </summary>
public static class KlineCsvParser
{
    private const int ColumnCount = 12;

    public static IReadOnlyList<Candle> Parse(IEnumerable<string> lines, ArchiveFileKey file)
    {
        var candles = new List<Candle>();
        var fileRange = new TimeRange(file.MonthStart, file.MonthEnd);
        var lineNumber = 0;
        DateTimeOffset? previous = null;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            if (lineNumber == 1 && fields[0].StartsWith("open_time", StringComparison.Ordinal))
            {
                continue; // Tolerated header row; the column order is checked by field count and parsing.
            }

            if (fields.Length != ColumnCount)
            {
                throw Reject(file, lineNumber, $"expected {ColumnCount} columns, found {fields.Length}");
            }

            var open = ParseTimestamp(fields[0], file, lineNumber);
            if (file.Interval.CloseOf(open) > ResearchCutoff.C)
            {
                throw new CutoffViolationException(
                    $"{file.FileName} line {lineNumber}: a candle closes after the research cutoff; the file is rejected.");
            }

            if (!fileRange.Contains(open))
            {
                throw Reject(file, lineNumber, "open_time lies outside the file's month");
            }

            if (!file.Interval.IsAligned(open))
            {
                throw Reject(file, lineNumber, "open_time is not on the interval grid");
            }

            if (previous is { } last && open <= last)
            {
                throw Reject(file, lineNumber, "open_time is duplicated or not increasing");
            }

            var candle = new Candle(
                file.Symbol,
                file.Interval,
                open,
                ParseDecimal(fields[1], file, lineNumber),
                ParseDecimal(fields[2], file, lineNumber),
                ParseDecimal(fields[3], file, lineNumber),
                ParseDecimal(fields[4], file, lineNumber),
                ParseDecimal(fields[5], file, lineNumber),
                ParseDecimal(fields[7], file, lineNumber),
                ParseLong(fields[8], file, lineNumber));

            var closeTime = ParseTimestamp(fields[6], file, lineNumber);
            if (closeTime <= open || closeTime >= candle.CloseTime)
            {
                throw Reject(file, lineNumber, "close_time is inconsistent with open_time");
            }

            ValidatePrices(candle, file, lineNumber);
            candles.Add(candle);
            previous = open;
        }

        return candles;
    }

    /// <summary>
    /// Archive timestamps: 13 digits are milliseconds, 16 digits microseconds. The unit must match the file month
    /// (S-B7); a millisecond value read as microseconds would land in 1970 and pass a cutoff check unnoticed.
    /// </summary>
    public static DateTimeOffset ParseTimestamp(string text, ArchiveFileKey file, int lineNumber)
    {
        if (text.Length is not (13 or 16) || !text.All(char.IsAsciiDigit))
        {
            throw Reject(file, lineNumber, "timestamp is neither 13-digit milliseconds nor 16-digit microseconds");
        }

        var isMicroseconds = text.Length == 16;
        if (isMicroseconds != file.ExpectsMicroseconds)
        {
            throw Reject(file, lineNumber, "timestamp unit does not match the file month");
        }

        var value = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        var ticks = isMicroseconds ? value * 10 : value * TimeSpan.TicksPerMillisecond;
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + ticks, TimeSpan.Zero);
    }

    private static void ValidatePrices(Candle candle, ArchiveFileKey file, int lineNumber)
    {
        if (candle.Low <= 0)
        {
            throw Reject(file, lineNumber, "price must be positive");
        }

        if (candle.Low > Math.Min(candle.Open, candle.Close) || Math.Max(candle.Open, candle.Close) > candle.High)
        {
            throw Reject(file, lineNumber, "OHLC rule violated (Low ≤ Open, Close ≤ High)");
        }

        if (candle.BaseVolume < 0 || candle.QuoteVolume < 0 || candle.TradeCount < 0)
        {
            throw Reject(file, lineNumber, "volume and trade count must not be negative");
        }
    }

    private static decimal ParseDecimal(string text, ArchiveFileKey file, int lineNumber) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Reject(file, lineNumber, "invalid decimal value");

    private static long ParseLong(string text, ArchiveFileKey file, int lineNumber) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Reject(file, lineNumber, "invalid integer value");

    private static ArchiveDataException Reject(ArchiveFileKey file, int lineNumber, string reason) =>
        new($"{file.FileName} line {lineNumber}: {reason}; the file is rejected.");
}

file static class TimeRangeExtensions
{
    public static bool Contains(this TimeRange range, DateTimeOffset instant) => range.Start <= instant && instant < range.End;
}
