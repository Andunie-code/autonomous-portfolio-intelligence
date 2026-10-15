using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Research.Evaluation;

/// <summary>
/// Seeded generator of valid DSL v1 strategies drawn from the ADR-0016 whitelist (null strategies of the
/// calibration, m3-protocol §7). Atoms compare quantities on compatible scales so the strategies trade.
/// </summary>
public sealed class RandomStrategies(int seed)
{
    private readonly Random _random = new(seed);
    private int _counter;

    public DslDefinition Next()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var indicators = new List<DslIndicator>();
            var entryAtoms = Enumerable.Range(0, _random.Next(1, 3)).Select(_ => Atom(indicators, entry: true)).ToList();
            var exit = Atom(indicators, entry: false);
            var entry = entryAtoms.Count == 1 ? entryAtoms[0] : new DslCondition("and", entryAtoms);
            var stop = Stop(indicators);
            var definition = new DslDefinition($"null-{seed}-{_counter}", "4h", indicators, entry, exit, stop);
            try
            {
                var valid = DslValidator.Validate(definition);
                _counter++;
                return valid;
            }
            catch (DslException)
            {
                // Rare (e.g. more than eight indicators); draw again.
            }
        }

        throw new InvalidOperationException("Could not draw a valid strategy.");
    }

    private DslCondition Atom(List<DslIndicator> indicators, bool entry)
    {
        var up = entry ? "cross_above" : "cross_below";
        var above = entry ? "gt" : "lt";
        switch (_random.Next(7))
        {
            case 0: // price against a price-scale indicator
                return new DslCondition(Pick(up, above), [new DslPrice("close"), PriceLevel(indicators)]);
            case 1: // two price-scale indicators
                var fast = Add(indicators, Pick("ema", "sma"), ("period", _random.Next(5, 60)));
                var slow = Add(indicators, Pick("ema", "sma"), ("period", _random.Next(60, 200)));
                return new DslCondition(Pick(up, above), [Ref(fast, "value"), Ref(slow, "value")]);
            case 2: // oscillator against a constant
                var rsi = Add(indicators, "rsi", ("period", _random.Next(5, 30)));
                return new DslCondition(entry ? "gt" : "lt", [Ref(rsi, "value"), new DslConst(_random.Next(35, 66))]);
            case 3: // trend strength
                var adx = Add(indicators, "adx", ("period", _random.Next(7, 30)));
                return _random.Next(2) == 0
                    ? new DslCondition("gt", [Ref(adx, "adx"), new DslConst(_random.Next(15, 35))])
                    : new DslCondition(above, [Ref(adx, "plus_di"), Ref(adx, "minus_di")]);
            case 4: // MACD
                var f = _random.Next(5, 20);
                var macd = Add(indicators, "macd", ("fast", f), ("slow", f + _random.Next(5, 30)), ("signal", _random.Next(5, 15)));
                return new DslCondition(Pick(up, above), [Ref(macd, "line"), Ref(macd, "signal")]);
            case 5: // momentum or band position
                if (_random.Next(2) == 0)
                {
                    var roc = Add(indicators, "roc", ("period", _random.Next(3, 60)));
                    return new DslCondition(above, [Ref(roc, "value"), new DslConst(0)]);
                }

                var bands = Add(indicators, "bollinger", ("period", _random.Next(10, 60)), ("k", Math.Round(1.5m + (decimal)_random.NextDouble(), 2)));
                return new DslCondition(entry ? "gt" : "lt", [Ref(bands, "percent_b"), new DslConst(Math.Round((decimal)_random.NextDouble(), 2))]);
            default: // regime or volume filter
                if (_random.Next(2) == 0)
                {
                    var regime = Add(indicators, "ema", ("period", _random.Next(30, 200)), source: DslSource.Btc);
                    return new DslCondition(above, [new DslPrice("close", DslSource.Btc), Ref(regime, "value")]);
                }

                var volume = Add(indicators, "volume_sma", ("period", _random.Next(10, 60)));
                return new DslCondition("gt", [new DslPrice("volume"), Ref(volume, "value") with { Mult = Math.Round(0.8m + (decimal)_random.NextDouble(), 2) }]);
        }
    }

    private DslRef PriceLevel(List<DslIndicator> indicators)
    {
        switch (_random.Next(4))
        {
            case 0:
                return Ref(Add(indicators, Pick("ema", "sma"), ("period", _random.Next(10, 200))), "value");
            case 1:
                return Ref(Add(indicators, "donchian", ("period", _random.Next(10, 100))), Pick("upper", "middle"));
            case 2:
                return Ref(Add(indicators, "bollinger", ("period", _random.Next(10, 60)), ("k", 2m)), Pick("middle", "upper"));
            default:
                return Ref(Add(indicators, "supertrend", ("period", _random.Next(7, 20)), ("multiplier", Math.Round(1.5m + 2 * (decimal)_random.NextDouble(), 2))), Pick("upper", "lower"));
        }
    }

    private DslStop? Stop(List<DslIndicator> indicators)
    {
        switch (_random.Next(4))
        {
            case 0:
                return null;
            case 1:
                return new DslStop("percent", Math.Round(0.02m + 0.08m * (decimal)_random.NextDouble(), 3), null, null, false);
            case 2:
                var atr = Add(indicators, "atr", ("period", _random.Next(7, 30)));
                return new DslStop("atr", null, new DslRef(atr, "value"), Math.Round(1m + 3 * (decimal)_random.NextDouble(), 2), false);
            default:
                var level = Add(indicators, "donchian", ("period", _random.Next(10, 60)));
                return new DslStop("indicator", null, new DslRef(level, "lower"), null, _random.Next(2) == 0);
        }
    }

    private string Add(List<DslIndicator> indicators, string type, params (string Name, decimal Value)[] parameters) =>
        Add(indicators, type, DslSource.Self, parameters);

    private string Add(List<DslIndicator> indicators, string type, (string Name, decimal Value) parameter, DslSource source) =>
        Add(indicators, type, source, [parameter]);

    private static string Add(List<DslIndicator> indicators, string type, DslSource source, (string Name, decimal Value)[] parameters)
    {
        var id = $"i{indicators.Count}";
        indicators.Add(new DslIndicator(id, type, new SortedDictionary<string, decimal>(parameters.ToDictionary(p => p.Name, p => p.Value), StringComparer.Ordinal), source));
        return id;
    }

    private static DslRef Ref(string id, string output) => new(id, output);

    private T Pick<T>(T a, T b) => _random.Next(2) == 0 ? a : b;
}
