using System.Globalization;
using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Live;

public sealed class LiveDataException(string message) : Exception(message);

/// <summary>
/// Fetches closed klines (S-B9). A kline is accepted only if its open time is on the interval grid, its close
/// time is open + length − 1 ms, it closed before <c>now</c>, it is at or after the research cutoff C, and its OHLC
/// values are consistent (M1 rules). Unclosed klines are dropped; any other violation rejects the whole response.
/// </summary>
public sealed class LiveKlineClient(HttpClient http)
{
    public const int MaxLimit = 1000;

    public async Task<IReadOnlyList<Candle>> FetchClosedAsync(string symbol, CandleInterval interval, DateTimeOffset from, DateTimeOffset now, CancellationToken ct)
    {
        if (from < ResearchCutoff.C)
        {
            throw new LiveRequestRejectedException("Live data starts at the research cutoff C; earlier data comes from the archive.");
        }

        var uri = new Uri(string.Create(CultureInfo.InvariantCulture,
            $"https://{LiveKlineGuardHandler.Host}{LiveKlineGuardHandler.Path}?symbol={symbol}&interval={interval.Code}&startTime={from.ToUnixTimeMilliseconds()}&endTime={now.ToUnixTimeMilliseconds()}&limit={MaxLimit}"));
        using var response = await http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new LiveDataException($"klines {symbol}: HTTP {(int)response.StatusCode}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return Parse(symbol, interval, document.RootElement, now);
    }

    public static IReadOnlyList<Candle> Parse(string symbol, CandleInterval interval, JsonElement root, DateTimeOffset now)
    {
        var length = interval.FixedLength ?? throw new ArgumentException("Fixed-length interval required.");
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new LiveDataException("klines: response is not an array");
        }

        var candles = new List<Candle>();
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 9)
            {
                throw new LiveDataException("klines: malformed row");
            }

            var open = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64());
            var close = DateTimeOffset.FromUnixTimeMilliseconds(row[6].GetInt64());
            if (close >= now)
            {
                continue; // not closed yet
            }

            if (!interval.IsAligned(open) || close != open + length - TimeSpan.FromMilliseconds(1))
            {
                throw new LiveDataException($"klines {symbol}: {open:O} is not a closed {interval.Code} candle on the grid");
            }

            if (open < ResearchCutoff.C)
            {
                throw new LiveDataException($"klines {symbol}: {open:O} is before the research cutoff");
            }

            var candle = new Candle(symbol, interval, open, Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]), Number(row[5]), Number(row[7]), row[8].GetInt64());
            if (!(candle.Low > 0 && candle.Low <= Math.Min(candle.Open, candle.Close) && Math.Max(candle.Open, candle.Close) <= candle.High)
                || candle.BaseVolume < 0 || candle.QuoteVolume < 0 || candle.TradeCount < 0)
            {
                throw new LiveDataException($"klines {symbol}: {open:O} fails the OHLC rules");
            }

            if (candles.Count > 0 && open <= candles[^1].OpenTime)
            {
                throw new LiveDataException($"klines {symbol}: open times are not increasing");
            }

            candles.Add(candle);
        }

        return candles;
    }

    private static decimal Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.Number => value.GetDecimal(),
        _ => throw new LiveDataException("klines: numeric field expected"),
    };
}
