using System.Globalization;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Indicators;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Execution.Radar;

/// <summary>An active strategy for the radar: its frozen definition and the pairs of its manifest.</summary>
public sealed record RadarStrategy(string Name, long CohortId, string DefinitionJson, IReadOnlyList<string> Symbols);

/// <summary>An open paper position of a strategy (latest balance snapshot): its entry and its effective stop.</summary>
public sealed record RadarPosition(string Strategy, string Symbol, decimal EntryPrice, decimal Stop);

public sealed record IndicatorPanel(
    decimal Close,
    decimal? Ema50,
    decimal? Ema100,
    decimal? Sma150,
    decimal? Atr14,
    decimal? AtrPct,
    decimal? Rsi14,
    int? SupertrendDirection,
    decimal? SupertrendLevel,
    decimal? MacdLine,
    decimal? MacdSignal,
    decimal? MacdHistogram,
    decimal? Adx14,
    decimal? PlusDi,
    decimal? MinusDi,
    decimal? BollingerPercentB,
    decimal? BollingerBandwidth,
    decimal? DonchianUpper,
    decimal? DonchianLower,
    decimal? DonchianUpperDistancePct,
    decimal? Roc42,
    decimal? VolumeSma20,
    decimal? VolumeRatio);

public sealed record RadarSymbol(string Symbol, string Category, int Bars, DateTimeOffset LastBar, IndicatorPanel Indicators);

public sealed record RadarRegime(string Symbol, string State, decimal? Close, decimal? Sma300, decimal? Ema100, decimal? Sma300DistancePct, decimal? Ema100DistancePct);

/// <summary>
/// One atom. <c>LeftName</c>/<c>RightName</c> are display names (EMA50, BTC SMA300, Donchian50 üst); <c>Gap</c> is
/// left − right; <c>DistancePct</c> is how far the left operand must move, in % of the right, for the atom to hold,
/// given only when both operands are positive (prices, averages, volumes). For oscillators that cross zero (MACD,
/// Supertrend direction) a percentage of a value near zero means nothing, so only the gap is given.
/// <c>RightMult</c> is the right operand's multiplier (volume &gt; 2.5 × average: 2.5).
/// </summary>
public sealed record RadarAtom(
    string Side, string Op, string Left, string Right, string LeftName, string RightName, decimal? LeftValue, decimal? RightValue,
    decimal? RightMult, string Value, decimal? Gap, decimal? DistancePct);

/// <summary>
/// <c>EntryKind</c>: "cross" when the entry needs a crossing on this candle (a new signal, once per crossing);
/// "level" when it is a set of conditions that can hold on many candles in a row. Either way a flat sleeve buys at
/// the next 4h candle's open, unless a risk rule clips or rejects it.
/// </summary>
public sealed record RadarProximity(
    string Symbol,
    string Status,
    string EntryKind,
    int Satisfied,
    int Total,
    string Entry,
    string Exit,
    decimal? EntryPrice,
    decimal? Stop,
    decimal? StopDistancePct,
    IReadOnlyList<RadarAtom> Atoms);

public sealed record RadarStrategyView(string Name, long CohortId, string DefinitionHash, IReadOnlyList<RadarProximity> Symbols);

public sealed record RadarReport(DateTimeOffset? AsOf, int BarsUsed, RadarRegime Regime, IReadOnlyList<RadarSymbol> Symbols, IReadOnlyList<RadarStrategyView> Strategies);

/// <summary>
/// Signal radar (ADR-0020 §5): a pure function of closed candles. For every symbol the eleven indicators with fixed
/// display parameters and the BTCUSDT regime; for every active strategy and symbol, the interpreter's own view of
/// its entry and exit conditions (<see cref="DslStrategy.Inspect"/>), so the radar never re-implements a rule.
/// It is informational: it makes no decision. Recursive indicators (EMA, ATR, RSI, ADX) start from the first of
/// the <see cref="Bars"/> candles, so they can differ slightly from a run that started earlier.
/// </summary>
public static class SignalRadar
{
    public const int Bars = 320;
    public const string ReferenceSymbol = "BTCUSDT";

    public static RadarReport Compute(
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles,
        IReadOnlyList<RadarStrategy> strategies,
        IReadOnlyList<RadarPosition> positions,
        IReadOnlySet<string> watch)
    {
        var series = candles.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(c => c.OpenTime).TakeLast(Bars).ToList());
        var btc = series.GetValueOrDefault(ReferenceSymbol) ?? [];
        var btcByTime = btc.ToDictionary(c => c.OpenTime);
        var traded = strategies.SelectMany(s => s.Symbols).ToHashSet(StringComparer.Ordinal);
        var symbols = series.Keys.Where(s => series[s].Count > 0).Order(StringComparer.Ordinal)
            .Select(s => new RadarSymbol(s, traded.Contains(s) ? "core" : watch.Contains(s) ? "watch" : "other", series[s].Count, series[s][^1].OpenTime, Panel(series[s])))
            .ToList();

        var views = strategies.OrderBy(s => s.CohortId).ThenBy(s => s.Name, StringComparer.Ordinal).Select(s =>
        {
            var strategy = DslStrategy.FromJson(s.DefinitionJson);
            var names = OperandNames(s.DefinitionJson);
            var rows = s.Symbols.Concat(watch).Distinct().Order(StringComparer.Ordinal)
                .Where(symbol => series.TryGetValue(symbol, out var bars) && bars.Count > 0)
                .Select(symbol => Proximity(strategy, names, symbol, series[symbol], btcByTime, positions.FirstOrDefault(p => p.Strategy == s.Name && p.Symbol == symbol)))
                .ToList();
            return new RadarStrategyView(s.Name, s.CohortId, strategy.DefinitionHash, rows);
        }).ToList();

        var asOf = symbols.Count == 0 ? (DateTimeOffset?)null : symbols.Max(s => s.LastBar);
        return new RadarReport(asOf, Bars, Regime(btc), symbols, views);
    }

    private static IndicatorPanel Panel(IReadOnlyList<Candle> bars)
    {
        var ema50 = new Ema(50);
        var ema100 = new Ema(100);
        var sma150 = new Sma(150);
        var atr = new Atr(14);
        var rsi = new Rsi(14);
        var supertrend = new Supertrend(10, 3m);
        var macd = new Macd(12, 26, 9);
        var adx = new Adx(14);
        var bollinger = new Bollinger(20, 2m);
        var donchian = new Donchian(50);
        var roc = new Roc(42);
        var volume = new Sma(20);
        foreach (var c in bars)
        {
            ema50.Update(c.Close);
            ema100.Update(c.Close);
            sma150.Update(c.Close);
            atr.Update(c.High, c.Low, c.Close);
            rsi.Update(c.Close);
            supertrend.Update(c.High, c.Low, c.Close);
            macd.Update(c.Close);
            adx.Update(c.High, c.Low, c.Close);
            bollinger.Update(c.Close);
            donchian.Update(c.High, c.Low);
            roc.Update(c.Close);
            volume.Update(c.QuoteVolume);
        }

        var last = bars[^1];
        var st = supertrend.Value;
        return new IndicatorPanel(
            last.Close, R(ema50.Value), R(ema100.Value), R(sma150.Value), R(atr.Value), Pct(atr.Value, last.Close), R(rsi.Value),
            st is { } s ? (s.IsUp ? 1 : -1) : null, st is { } s2 ? R(s2.IsUp ? s2.Lower : s2.Upper) : null,
            R(macd.Value?.Line), R(macd.Value?.Signal), R(macd.Value?.Histogram),
            R(adx.Value?.Adx), R(adx.Value?.PlusDi), R(adx.Value?.MinusDi),
            R(bollinger.Value?.PercentB), R(bollinger.Value?.Bandwidth),
            R(donchian.Value?.Upper), R(donchian.Value?.Lower), donchian.Value is { } d ? Pct(d.Upper - last.Close, last.Close) : null,
            R(roc.Value), R(volume.Value), volume.Value is { } v && v != 0 ? R(last.QuoteVolume / v) : null);
    }

    /// <summary>The BTCUSDT regime of the radar (close against SMA300) from <paramref name="btc"/>, oldest first.</summary>
    public static RadarRegime Regime(IReadOnlyList<Candle> btc)
    {
        if (btc.Count == 0)
        {
            return new RadarRegime(ReferenceSymbol, "Unknown", null, null, null, null, null);
        }

        var sma = new Sma(300);
        var ema = new Ema(100);
        foreach (var c in btc)
        {
            sma.Update(c.Close);
            ema.Update(c.Close);
        }

        var close = btc[^1].Close;
        var state = sma.Value is { } level ? (close > level ? "Bull" : "Bear") : "Unknown";
        return new RadarRegime(ReferenceSymbol, state, close, R(sma.Value), R(ema.Value),
            sma.Value is { } s ? Pct(close - s, s) : null, ema.Value is { } e ? Pct(close - e, e) : null);
    }

    private static RadarProximity Proximity(DslStrategy strategy, IReadOnlyDictionary<string, string> names, string symbol, IReadOnlyList<Candle> bars, Dictionary<DateTimeOffset, Candle> btc, RadarPosition? position)
    {
        var tracker = strategy.CreateReferenceTracker();
        var instance = strategy.CreateInstance(tracker);
        foreach (var bar in bars)
        {
            tracker.Update(btc.GetValueOrDefault(bar.OpenTime));
            instance.Update(bar);
        }

        var view = DslStrategy.Inspect(instance);
        var side = position is null ? "entry" : "exit";
        var atoms = view.Atoms.Select(a => new RadarAtom(a.Side, a.Op, a.Left, a.Right, DisplayName(a.Left, names), DisplayName(a.Right, names),
            R(a.Left0), R(a.Right0), Multiplier(a.Right), a.Value.ToString(), a.Left0 is { } l && a.Right0 is { } r ? R(l - r) : null, Distance(a))).ToList();
        var entryKind = atoms.Any(a => a.Side == "entry" && a.Op.StartsWith("cross_", StringComparison.Ordinal)) ? "cross" : "level";
        var relevant = atoms.Where(a => a.Side == side).ToList();
        var satisfied = relevant.Count(a => a.Value == nameof(Tri.True));
        var close = bars[^1].Close;
        var status = position is not null
            ? (view.Exit == Tri.True ? "exit-signal" : "in-position")
            : view.Entry switch
            {
                Tri.True => "entry-signal",
                Tri.Unknown => "unknown", // warm-up or missing BTC bar: no entry (Kleene, m3-protocol §2)
                _ => satisfied > 0 ? "partial" : "far",
            };
        return new RadarProximity(symbol, status, entryKind, satisfied, relevant.Count, view.Entry.ToString(), view.Exit.ToString(),
            position?.EntryPrice, position?.Stop, position is { } p ? Pct(close - p.Stop, close) : null, atoms);
    }

    /// <summary>Display names of a definition's indicator outputs, keyed by the inspector's label ("fast.value" → "EMA50").</summary>
    private static Dictionary<string, string> OperandNames(string definitionJson)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in JsonNode.Parse(definitionJson)?["indicators"]?.AsArray() ?? [])
        {
            if (node?["id"]?.GetValue<string>() is not { } id || node["type"]?.GetValue<string>() is not { } type)
            {
                continue;
            }

            var p = node["params"]?.AsObject();
            string P(string key) => p?[key]?.ToJsonString() ?? "";
            var btc = node["source"]?.GetValue<string>() == "btc" ? "BTC " : "";
            var title = btc + type switch
            {
                "ema" or "sma" or "atr" or "rsi" or "roc" or "adx" => type.ToUpperInvariant() + P("period"),
                "donchian" => "Donchian" + P("period"),
                "volume_sma" => "Hacim ort." + P("period"),
                "macd" => "MACD",
                "supertrend" => "Supertrend",
                "bollinger" => "Bollinger",
                _ => id,
            };
            foreach (var (output, suffix) in new[] { ("value", ""), ("line", ""), ("signal", " sinyal"), ("histogram", " histogram"), ("hist", " histogram"),
                         ("upper", " üst"), ("lower", " alt"), ("middle", " orta"), ("dir", " yönü"), ("percent_b", " %B"), ("adx", ""), ("plus_di", " +DI"), ("minus_di", " −DI") })
            {
                names[$"{id}.{output}"] = title + suffix;
            }
        }

        return names;
    }

    /// <summary>"fast.value" → "EMA50", "btc.close" → "BTC fiyatı", "close[1]" → "Fiyat (önceki mum)"; the multiplier is kept out (see RightMult).</summary>
    private static string DisplayName(string label, IReadOnlyDictionary<string, string> names)
    {
        var core = label.Split('×')[0];
        var lagged = core.EndsWith(']');
        if (lagged)
        {
            core = core[..core.IndexOf('[')];
        }

        var name = core switch
        {
            "close" => "Fiyat",
            "open" => "Açılış",
            "high" => "Yüksek",
            "low" => "Düşük",
            "volume" => "Hacim",
            "btc.close" => "BTC fiyatı",
            _ when core.StartsWith("btc.", StringComparison.Ordinal) => "BTC " + core[4..],
            _ => names.GetValueOrDefault(core, core),
        };
        return lagged ? name + " (önceki mum)" : name;
    }

    private static decimal? Multiplier(string label) =>
        label.Split('×') is [_, var m] && decimal.TryParse(m, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// For gt/gte/cross_above: how far the left operand must rise, in % of the right (≤ 0: already above). For
    /// lt/lte/cross_below: how far it must fall. Unknown operands give no distance.
    /// </summary>
    private static decimal? Distance(DslAtomState atom)
    {
        if (atom.Left0 is not { } left || atom.Right0 is not { } right || left <= 0 || right <= 0)
        {
            return null;
        }

        return atom.Op is "gt" or "gte" or "cross_above" ? Pct(right - left, Math.Abs(right)) : Pct(left - right, Math.Abs(right));
    }

    private static decimal? R(decimal? value) => value is { } v ? Math.Round(v, 8) : null;

    private static decimal? Pct(decimal? part, decimal whole) => part is { } p && whole != 0 ? Math.Round(p / whole * 100m, 4) : null;
}
