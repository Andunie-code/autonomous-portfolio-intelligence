using System.Text.RegularExpressions;

namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>
/// One monthly kline file of the Binance public data archive, e.g.
/// <c>data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2023-04.zip</c> (naming per S-B6; not yet re-verified).
/// </summary>
public sealed partial record ArchiveFileKey
{
    public const string MonthlyKlinesRoot = "data/spot/monthly/klines/";

    public ArchiveFileKey(string symbol, CandleInterval interval, int year, int month)
    {
        if (!SymbolPattern().IsMatch(symbol))
        {
            throw new ArgumentException($"Invalid symbol '{symbol}'.", nameof(symbol));
        }

        if (month is < 1 or > 12 || year is < 2000 or > 2100)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Invalid file month.");
        }

        Symbol = symbol;
        Interval = interval;
        Year = year;
        Month = month;
    }

    public string Symbol { get; }

    /// <summary>True for plain upper-case ASCII symbols such as BTCUSDT or 1000SATSUSDT.</summary>
    public static bool IsValidSymbol(string symbol) => SymbolPattern().IsMatch(symbol);

    public CandleInterval Interval { get; }

    public int Year { get; }

    public int Month { get; }

    public DateTimeOffset MonthStart => new(Year, Month, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset MonthEnd => MonthStart.AddMonths(1);

    public string FileName => $"{Symbol}-{Interval.ArchiveName}-{Year:D4}-{Month:D2}.zip";

    public string Path => $"{MonthlyKlinesRoot}{Symbol}/{Interval.ArchiveName}/{FileName}";

    public string ChecksumPath => Path + ".CHECKSUM";

    /// <summary>Spot timestamps are in microseconds from 2025-01-01 (S-B7), milliseconds before.</summary>
    public bool ExpectsMicroseconds => MonthStart >= new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Parses an archive path back into a key; returns null when it is not a monthly kline zip.</summary>
    public static ArchiveFileKey? TryParsePath(string path)
    {
        var match = PathPattern().Match(path);
        if (!match.Success)
        {
            return null;
        }

        var interval = CandleInterval.All.FirstOrDefault(i => i.ArchiveName == match.Groups["interval"].Value);
        if (interval is null || match.Groups["symbol"].Value != match.Groups["fileSymbol"].Value
            || match.Groups["interval"].Value != match.Groups["fileInterval"].Value)
        {
            return null;
        }

        return new ArchiveFileKey(match.Groups["symbol"].Value, interval,
            int.Parse(match.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["month"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public override string ToString() => Path;

    [GeneratedRegex("^[A-Z0-9]{2,30}$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"^data/spot/monthly/klines/(?<symbol>[A-Z0-9]{2,30})/(?<interval>[0-9a-zA-Z]{2,3})/(?<fileSymbol>[A-Z0-9]{2,30})-(?<fileInterval>[0-9a-zA-Z]{2,3})-(?<year>\d{4})-(?<month>\d{2})\.zip(\.CHECKSUM)?$")]
    private static partial Regex PathPattern();
}
