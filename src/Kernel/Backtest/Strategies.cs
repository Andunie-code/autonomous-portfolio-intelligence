using PortfolioIntelligence.Kernel.Indicators;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Kernel.Backtest;

/// <summary>A versioned, coded strategy (ADR-0003: DSL equivalents follow in M3). One instance per symbol.</summary>
public interface IStrategy
{
    string Name { get; }

    string Version { get; }

    /// <summary>Canonical parameter description; its hash identifies the strategy definition.</summary>
    string Definition { get; }

    /// <summary>L: consecutive valid candles needed after a reset before signals are produced (protocol §2).</summary>
    int WarmupBars { get; }

    IStrategyInstance CreateInstance();
}

public interface IStrategyInstance
{
    /// <summary>Feeds one closed candle; signals below refer to this candle's close.</summary>
    void Update(Candle candle);

    void Reset();

    bool EntrySignal { get; }

    bool ExitSignal { get; }

    /// <summary>Strategy stop for an entry filled at <paramref name="entryFill"/>, from the decision candle's state.</summary>
    decimal InitialStop(decimal entryFill);

    /// <summary>Stop level the strategy wants after this close (only ever raised by the engine), or null.</summary>
    decimal? TrailingStop { get; }
}

/// <summary>
/// A strategy that reads a reference series (BTCUSDT, m3-protocol §2). The engine creates one tracker per run,
/// feeds it the reference candle of every step (null when missing) before the symbols' closes, and creates the
/// symbol instances bound to it.
/// </summary>
public interface IReferenceSeriesConsumer
{
    IReferenceTracker CreateReferenceTracker();

    IStrategyInstance CreateInstance(IReferenceTracker tracker);
}

public interface IReferenceTracker
{
    /// <summary>The reference candle with the current step's open time, or null if it is missing.</summary>
    void Update(Candle? bar);
}

/// <summary>Baseline A — EMA 50/100 trend v1 (docs/m2-protocol.md §4).</summary>
public sealed class EmaTrendStrategy : IStrategy
{
    public string Name => "ema-trend";

    public string Version => "v1";

    public string Definition => "ema-trend v1: entry EMA50 crosses above EMA100; exit EMA50 crosses below EMA100; stop fill-3*ATR14 fixed; interval 4h";

    public int WarmupBars => 101;

    public IStrategyInstance CreateInstance() => new Instance();

    private sealed class Instance : IStrategyInstance
    {
        private readonly Ema _fast = new(50);
        private readonly Ema _slow = new(100);
        private readonly Atr _atr = new(14);
        private decimal? _previousDifference;

        public bool EntrySignal { get; private set; }

        public bool ExitSignal { get; private set; }

        public decimal? TrailingStop => null;

        public void Update(Candle candle)
        {
            var fast = _fast.Update(candle.Close);
            var slow = _slow.Update(candle.Close);
            _atr.Update(candle.High, candle.Low, candle.Close);
            decimal? difference = fast is { } f && slow is { } s ? f - s : null;
            EntrySignal = difference > 0 && _previousDifference <= 0;
            ExitSignal = difference < 0 && _previousDifference >= 0;
            _previousDifference = difference;
        }

        public decimal InitialStop(decimal entryFill) =>
            _atr.Value is { } atr ? entryFill - 3 * atr : throw new InvalidOperationException("ATR is not ready.");

        public void Reset()
        {
            _fast.Reset();
            _slow.Reset();
            _atr.Reset();
            _previousDifference = null;
            EntrySignal = false;
            ExitSignal = false;
        }
    }
}

/// <summary>Baseline B — Supertrend(10, 3) v1 (docs/m2-protocol.md §5).</summary>
public sealed class SupertrendStrategy : IStrategy
{
    public string Name => "supertrend";

    public string Version => "v1";

    public string Definition => "supertrend v1: Supertrend(10,3) on Wilder ATR; entry direction turns up; exit direction turns down; stop final lower band, raised at each close; interval 4h";

    public int WarmupBars => 11;

    public IStrategyInstance CreateInstance() => new Instance();

    private sealed class Instance : IStrategyInstance
    {
        private readonly Supertrend _supertrend = new(10, 3m);
        private bool? _previousUp;

        public bool EntrySignal { get; private set; }

        public bool ExitSignal { get; private set; }

        public decimal? TrailingStop => _supertrend.Value?.Lower;

        public void Update(Candle candle)
        {
            var value = _supertrend.Update(candle.High, candle.Low, candle.Close);
            var up = value?.IsUp;
            EntrySignal = up == true && _previousUp == false;
            ExitSignal = up == false && _previousUp == true;
            _previousUp = up;
        }

        public decimal InitialStop(decimal entryFill) =>
            _supertrend.Value?.Lower ?? throw new InvalidOperationException("Supertrend is not ready.");

        public void Reset()
        {
            _supertrend.Reset();
            _previousUp = null;
            EntrySignal = false;
            ExitSignal = false;
        }
    }
}
