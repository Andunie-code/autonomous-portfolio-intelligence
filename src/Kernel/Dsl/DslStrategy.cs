using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Indicators;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Kernel.Dsl;

/// <summary>Kleene three-valued truth: undefined inputs are Unknown and never become True (m3-protocol §1).</summary>
public enum Tri : byte
{
    False,
    True,
    Unknown,
}

/// <summary>
/// One comparison atom of the entry or exit condition after the last update (signal radar, ADR-0020 §5):
/// operand labels, their values at lag 0 and lag 1 (the previous bar, used by crosses) and the atom's truth value.
/// </summary>
public sealed record DslAtomState(string Side, string Op, string Left, string Right, decimal? Left0, decimal? Right0, decimal? Left1, decimal? Right1, Tri Value);

/// <summary>Read-only view of an instance after its last update: entry and exit truth values and every atom.</summary>
public sealed record DslInspection(Tri Entry, Tri Exit, IReadOnlyList<DslAtomState> Atoms);

/// <summary>
/// Interpreter for a validated DSL v1 definition. One instance per symbol; BTC-sourced indicators live in a
/// single reference tracker per run, fed by the engine with the BTC candle of the same open time (m3-protocol §2).
/// </summary>
public sealed class DslStrategy : IStrategy, IReferenceSeriesConsumer
{
    private readonly DslDefinition _definition;
    private readonly DslIndicator[] _self;
    private readonly DslIndicator[] _btc;

    public DslStrategy(DslDefinition definition)
    {
        _definition = DslValidator.Validate(definition);
        _self = definition.Indicators.Where(i => i.Source == DslSource.Self).ToArray();
        _btc = definition.Indicators.Where(i => i.Source == DslSource.Btc).ToArray();
        Definition = DslCanonical.Canonicalize(definition, CanonicalMode.Definition);
        DefinitionHash = DslCanonical.Sha256(Definition);
        StructureHash = DslCanonical.StructureHash(definition);
        WarmupBars = (_self.Length == 0 ? 0 : _self.Max(Warmup)) + 1;
        UsesReference = _btc.Length > 0 || UsesBtcPrice(definition.Entry) || UsesBtcPrice(definition.Exit);
    }

    public static DslStrategy FromJson(string json) => new(DslParser.Parse(json));

    public string Name => _definition.Name;

    public string Version => "dsl-" + DslDefinition.Version;

    /// <summary>Canonical definition JSON; its SHA-256 is the definition hash.</summary>
    public string Definition { get; }

    public string DefinitionHash { get; }

    public string StructureHash { get; }

    public int WarmupBars { get; }

    public bool UsesReference { get; }

    public DslDefinition Source => _definition;

    public IReferenceTracker CreateReferenceTracker() => new Tracker(_btc);

    /// <summary>An instance without a reference feed: every BTC-dependent atom stays Unknown.</summary>
    public IStrategyInstance CreateInstance() => CreateInstance(CreateReferenceTracker());

    public IStrategyInstance CreateInstance(IReferenceTracker tracker) =>
        new Instance(_definition, _self, tracker as Tracker ?? throw new ArgumentException("Tracker of another strategy.", nameof(tracker)));

    /// <summary>
    /// Inspection of an instance of this strategy (ADR-0020 §5). It only reads the instance's state; the atom
    /// evaluators are compiled on first use, so <see cref="IStrategyInstance.Update"/> is unchanged.
    /// </summary>
    public static DslInspection Inspect(IStrategyInstance instance) =>
        (instance as Instance ?? throw new ArgumentException("Not a DSL instance.", nameof(instance))).Inspect();

    public static int Warmup(DslIndicator indicator) => DslCatalog.Indicators[indicator.Type].Warmup(indicator.Params);

    private static bool UsesBtcPrice(DslNode node) => node switch
    {
        DslCondition c => c.Args.Any(UsesBtcPrice),
        DslPrice p => p.Source == DslSource.Btc,
        _ => false,
    };

    /// <summary>Indicator with its current and previous output vectors (lag 0 and lag 1).</summary>
    private sealed class Slot
    {
        private readonly Action<Candle> _update;
        private readonly Func<decimal?>[] _outputs;
        private readonly Action _reset;
        private decimal?[] _current;
        private decimal?[] _previous;
        private decimal?[] _previous2;

        public Slot(DslIndicator indicator)
        {
            var spec = DslCatalog.Indicators[indicator.Type];
            OutputNames = spec.Outputs;
            (_update, _outputs, _reset) = Build(indicator);
            _current = new decimal?[_outputs.Length];
            _previous = new decimal?[_outputs.Length];
            _previous2 = new decimal?[_outputs.Length];
        }

        public IReadOnlyList<string> OutputNames { get; }

        public void Update(Candle candle)
        {
            (_previous2, _previous, _current) = (_previous, _current, _previous2);
            _update(candle);
            for (var i = 0; i < _outputs.Length; i++)
            {
                _current[i] = _outputs[i]();
            }
        }

        /// <summary>Output at lag 0, 1 or 2 (a cross over a lag-1 operand needs lag 2).</summary>
        public decimal? Get(int output, int lag) => lag switch
        {
            0 => _current[output],
            1 => _previous[output],
            _ => _previous2[output],
        };

        public void Reset()
        {
            _reset();
            Array.Clear(_current);
            Array.Clear(_previous);
            Array.Clear(_previous2);
        }

        private static (Action<Candle>, Func<decimal?>[], Action) Build(DslIndicator indicator)
        {
            int P(string name) => (int)indicator.Params[name];
            switch (indicator.Type)
            {
                case "sma":
                {
                    var x = new Sma(P("period"));
                    return (c => x.Update(c.Close), [() => x.Value], x.Reset);
                }

                case "ema":
                {
                    var x = new Ema(P("period"));
                    return (c => x.Update(c.Close), [() => x.Value], x.Reset);
                }

                case "rsi":
                {
                    var x = new Rsi(P("period"));
                    return (c => x.Update(c.Close), [() => x.Value], x.Reset);
                }

                case "atr":
                {
                    var x = new Atr(P("period"));
                    return (c => x.Update(c.High, c.Low, c.Close), [() => x.Value], x.Reset);
                }

                case "roc":
                {
                    var x = new Roc(P("period"));
                    return (c => x.Update(c.Close), [() => x.Value], x.Reset);
                }

                case "volume_sma":
                {
                    var x = new Sma(P("period"));
                    return (c => x.Update(c.QuoteVolume), [() => x.Value], x.Reset);
                }

                case "macd":
                {
                    var x = new Macd(P("fast"), P("slow"), P("signal"));
                    return (c => x.Update(c.Close), [() => x.Value?.Line, () => x.Value?.Signal, () => x.Value?.Histogram], x.Reset);
                }

                case "adx":
                {
                    var x = new Adx(P("period"));
                    return (c => x.Update(c.High, c.Low, c.Close), [() => x.Value?.Adx, () => x.Value?.PlusDi, () => x.Value?.MinusDi], x.Reset);
                }

                case "bollinger":
                {
                    var x = new Bollinger(P("period"), indicator.Params["k"]);
                    return (c => x.Update(c.Close),
                        [() => x.Value?.Middle, () => x.Value?.Upper, () => x.Value?.Lower, () => x.Value?.Bandwidth, () => x.Value?.PercentB], x.Reset);
                }

                case "donchian":
                {
                    var x = new Donchian(P("period"));
                    return (c => x.Update(c.High, c.Low), [() => x.Value?.Upper, () => x.Value?.Lower, () => x.Value?.Middle], x.Reset);
                }

                case "supertrend":
                {
                    var x = new Supertrend(P("period"), indicator.Params["multiplier"]);
                    return (c => x.Update(c.High, c.Low, c.Close),
                        [() => x.Value?.Upper, () => x.Value?.Lower, () => x.Value is { } v ? (v.IsUp ? 1m : -1m) : null], x.Reset);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(indicator), indicator.Type, "Not whitelisted.");
            }
        }
    }

    /// <summary>BTC state shared by all instances of one run; a missing BTC candle resets it (gap rule).</summary>
    private sealed class Tracker : IReferenceTracker
    {
        private bool _seen;

        public Tracker(DslIndicator[] indicators)
        {
            Ids = indicators.Select(i => i.Id).ToArray();
            Slots = indicators.Select(i => new Slot(i)).ToArray();
        }

        public string[] Ids { get; }

        public Slot[] Slots { get; }

        public Candle? Current { get; private set; }

        public Candle? Previous { get; private set; }

        public Candle? Previous2 { get; private set; }

        public void Update(Candle? bar)
        {
            if (bar is null)
            {
                if (_seen)
                {
                    foreach (var slot in Slots)
                    {
                        slot.Reset();
                    }
                }

                Current = null;
                Previous = null;
                Previous2 = null;
                return;
            }

            _seen = true;
            Previous2 = Previous;
            Previous = Current;
            Current = bar;
            foreach (var slot in Slots)
            {
                slot.Update(bar);
            }
        }
    }

    private sealed class Instance : IStrategyInstance
    {
        private readonly Slot[] _slots;
        private readonly Tracker _tracker;
        private readonly Func<Tri> _entry;
        private readonly Func<Tri> _exit;
        private readonly DslStop? _stop;
        private readonly Func<decimal?>? _stopLevel;
        private readonly DslDefinition _definition;
        private readonly Dictionary<string, (Slot Slot, IReadOnlyList<string> Outputs)> _lookup;
        private List<(string Side, DslCondition Atom, Func<decimal?>[] Operands, Func<Tri> Value)>? _atoms;
        private Candle? _current;
        private Candle? _previous;
        private Candle? _previous2;

        public Instance(DslDefinition definition, DslIndicator[] self, Tracker tracker)
        {
            _slots = self.Select(i => new Slot(i)).ToArray();
            _tracker = tracker;
            _definition = definition;
            var lookup = _lookup = new Dictionary<string, (Slot Slot, IReadOnlyList<string> Outputs)>(StringComparer.Ordinal);
            for (var i = 0; i < self.Length; i++)
            {
                lookup[self[i].Id] = (_slots[i], _slots[i].OutputNames);
            }

            for (var i = 0; i < tracker.Ids.Length; i++)
            {
                lookup[tracker.Ids[i]] = (tracker.Slots[i], tracker.Slots[i].OutputNames);
            }

            _entry = CompileCondition(definition.Entry, lookup);
            _exit = CompileCondition(definition.Exit, lookup);
            _stop = definition.Stop;
            if (_stop?.Ref is { } stopRef)
            {
                _stopLevel = CompileOperand(stopRef, lookup);
            }
        }

        public bool EntrySignal { get; private set; }

        public bool ExitSignal { get; private set; }

        public decimal? TrailingStop => _stop is { Type: "indicator", Trailing: true } ? _stopLevel!() : null;

        public void Update(Candle candle)
        {
            _previous2 = _previous;
            _previous = _current;
            _current = candle;
            foreach (var slot in _slots)
            {
                slot.Update(candle);
            }

            EntrySignal = _entry() == Tri.True;
            ExitSignal = _exit() == Tri.True;
        }

        /// <summary>
        /// Strategy stop from the decision candle's state. If its level is undefined the strategy stop is 0, so
        /// only the D25 hard stop applies (ProtectiveStop.Effective takes the level closest to the entry).
        /// </summary>
        public decimal InitialStop(decimal entryFill) => _stop switch
        {
            null => 0m,
            { Type: "percent", Pct: { } pct } => entryFill * (1 - pct),
            { Type: "atr", Multiple: { } multiple } => _stopLevel!() is { } atr ? entryFill - multiple * atr : 0m,
            { Type: "indicator" } => _stopLevel!() ?? 0m,
            _ => throw new InvalidOperationException("Unknown stop type."),
        };

        public DslInspection Inspect()
        {
            _atoms ??= [.. Atoms("entry", _definition.Entry), .. Atoms("exit", _definition.Exit)];
            return new DslInspection(_entry(), _exit(), _atoms.Select(a => new DslAtomState(
                a.Side, a.Atom.Op, Label((DslOperand)a.Atom.Args[0]), Label((DslOperand)a.Atom.Args[1]),
                a.Operands[0](), a.Operands[1](), a.Operands[2](), a.Operands[3](), a.Value())).ToList());
        }

        /// <summary>The comparison atoms under the logical operators, in definition order.</summary>
        private IEnumerable<(string, DslCondition, Func<decimal?>[], Func<Tri>)> Atoms(string side, DslCondition condition)
        {
            if (condition.Op is "and" or "or" or "not")
            {
                return condition.Args.SelectMany(a => Atoms(side, (DslCondition)a));
            }

            var a = (DslOperand)condition.Args[0];
            var b = (DslOperand)condition.Args[1];
            Func<decimal?>[] operands = [CompileOperand(a, _lookup), CompileOperand(b, _lookup), CompileOperand(Lagged(a), _lookup), CompileOperand(Lagged(b), _lookup)];
            return [(side, condition, operands, CompileCondition(condition, _lookup))];
        }

        private static string Label(DslOperand operand)
        {
            static string Suffix(int lag, decimal mult) => (lag > 0 ? $"[{lag}]" : "") + (mult != 1m ? $"×{mult.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "");
            return operand switch
            {
                DslRef r => $"{r.Indicator}.{r.Output}{Suffix(r.Lag, r.Mult)}",
                DslPrice p => (p.Source == DslSource.Btc ? "btc." : "") + p.Field + Suffix(p.Lag, p.Mult),
                DslConst k => k.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => "?",
            };
        }

        public void Reset()
        {
            foreach (var slot in _slots)
            {
                slot.Reset();
            }

            _current = null;
            _previous = null;
            _previous2 = null;
            EntrySignal = false;
            ExitSignal = false;
        }

        private Func<Tri> CompileCondition(DslCondition condition, Dictionary<string, (Slot, IReadOnlyList<string>)> lookup)
        {
            switch (condition.Op)
            {
                case "and":
                {
                    var args = condition.Args.Select(a => CompileCondition((DslCondition)a, lookup)).ToArray();
                    return () =>
                    {
                        var result = Tri.True;
                        foreach (var arg in args)
                        {
                            var value = arg();
                            if (value == Tri.False)
                            {
                                return Tri.False;
                            }

                            if (value == Tri.Unknown)
                            {
                                result = Tri.Unknown;
                            }
                        }

                        return result;
                    };
                }

                case "or":
                {
                    var args = condition.Args.Select(a => CompileCondition((DslCondition)a, lookup)).ToArray();
                    return () =>
                    {
                        var result = Tri.False;
                        foreach (var arg in args)
                        {
                            var value = arg();
                            if (value == Tri.True)
                            {
                                return Tri.True;
                            }

                            if (value == Tri.Unknown)
                            {
                                result = Tri.Unknown;
                            }
                        }

                        return result;
                    };
                }

                case "not":
                {
                    var arg = CompileCondition((DslCondition)condition.Args[0], lookup);
                    return () => arg() switch
                    {
                        Tri.True => Tri.False,
                        Tri.False => Tri.True,
                        _ => Tri.Unknown,
                    };
                }
            }

            var a = (DslOperand)condition.Args[0];
            var b = (DslOperand)condition.Args[1];
            var a0 = CompileOperand(a, lookup);
            var b0 = CompileOperand(b, lookup);
            switch (condition.Op)
            {
                case "gt":
                    return () => Compare(a0(), b0(), (x, y) => x > y);
                case "gte":
                    return () => Compare(a0(), b0(), (x, y) => x >= y);
                case "lt":
                    return () => Compare(a0(), b0(), (x, y) => x < y);
                case "lte":
                    return () => Compare(a0(), b0(), (x, y) => x <= y);
            }

            var a1 = CompileOperand(Lagged(a), lookup);
            var b1 = CompileOperand(Lagged(b), lookup);
            return condition.Op switch
            {
                "cross_above" => () => Cross(a0(), b0(), a1(), b1(), above: true),
                "cross_below" => () => Cross(a0(), b0(), a1(), b1(), above: false),
                _ => throw new InvalidOperationException($"Unknown operator '{condition.Op}'."),
            };
        }

        private static Tri Compare(decimal? x, decimal? y, Func<decimal, decimal, bool> op) =>
            x is { } a && y is { } b ? (op(a, b) ? Tri.True : Tri.False) : Tri.Unknown;

        /// <summary>cross_above: a_t &gt; b_t and a_{t−1} ≤ b_{t−1}; cross_below: a_t &lt; b_t and a_{t−1} ≥ b_{t−1}.</summary>
        private static Tri Cross(decimal? a0, decimal? b0, decimal? a1, decimal? b1, bool above)
        {
            if (a0 is not { } x0 || b0 is not { } y0 || a1 is not { } x1 || b1 is not { } y1)
            {
                return Tri.Unknown;
            }

            var crossed = above ? x0 > y0 && x1 <= y1 : x0 < y0 && x1 >= y1;
            return crossed ? Tri.True : Tri.False;
        }

        /// <summary>The operand one candle earlier (lag + 1); constants are unchanged. Validation keeps lag ≤ 1, so this is ≤ 2.</summary>
        private static DslOperand Lagged(DslOperand operand) => operand switch
        {
            DslRef r => r with { Lag = r.Lag + 1 },
            DslPrice p => p with { Lag = p.Lag + 1 },
            _ => operand,
        };

        private Func<decimal?> CompileOperand(DslOperand operand, Dictionary<string, (Slot Slot, IReadOnlyList<string> Outputs)> lookup)
        {
            switch (operand)
            {
                case DslConst k:
                {
                    var value = k.Value;
                    return () => value;
                }

                case DslRef r:
                {
                    var (slot, outputs) = lookup[r.Indicator];
                    var index = outputs.ToList().IndexOf(r.Output);
                    var mult = r.Mult;
                    var lag = r.Lag;
                    return mult == 1m ? () => slot.Get(index, lag) : () => slot.Get(index, lag) * mult;
                }

                case DslPrice p:
                {
                    var field = p.Field;
                    var mult = p.Mult;
                    Func<Candle?> bar = (p.Source, p.Lag) switch
                    {
                        (DslSource.Self, 0) => () => _current,
                        (DslSource.Self, 1) => () => _previous,
                        (DslSource.Btc, 0) => () => _tracker.Current,
                        (DslSource.Btc, 1) => () => _tracker.Previous,
                        (DslSource.Self, _) => () => _previous2,
                        _ => () => _tracker.Previous2,
                    };
                    return () => bar() is { } c ? Field(c, field) * mult : null;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(operand));
            }
        }

        private static decimal Field(Candle c, string field) => field switch
        {
            "open" => c.Open,
            "high" => c.High,
            "low" => c.Low,
            "close" => c.Close,
            "volume" => c.QuoteVolume,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }
}
