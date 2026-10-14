namespace PortfolioIntelligence.Kernel.Indicators;

// Incremental, resettable indicators with the exact definitions of docs/m2-protocol.md §3. All decimal.

/// <summary>EMA(n): α = 2/(n+1), seeded with the simple mean of the first n closes.</summary>
public sealed class Ema(int period)
{
    private readonly decimal _alpha = 2m / (period + 1);
    private decimal _sum;
    private int _count;

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public decimal? Value { get; private set; }

    public decimal? Update(decimal close)
    {
        _count++;
        if (Value is { } previous)
        {
            Value = _alpha * close + (1 - _alpha) * previous;
        }
        else
        {
            _sum += close;
            if (_count == Period)
            {
                Value = _sum / Period;
            }
        }

        return Value;
    }

    public void Reset()
    {
        _sum = 0;
        _count = 0;
        Value = null;
    }
}

/// <summary>Average true range with Wilder smoothing, seeded with the simple mean of the first n true ranges.</summary>
public sealed class Atr(int period)
{
    private decimal? _previousClose;
    private decimal _sum;
    private int _count;

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public decimal? Value { get; private set; }

    public static decimal TrueRange(decimal high, decimal low, decimal? previousClose) =>
        previousClose is { } c ? Math.Max(high - low, Math.Max(Math.Abs(high - c), Math.Abs(low - c))) : high - low;

    public decimal? Update(decimal high, decimal low, decimal close)
    {
        var tr = TrueRange(high, low, _previousClose);
        _previousClose = close;
        _count++;
        if (Value is { } previous)
        {
            Value = (previous * (Period - 1) + tr) / Period;
        }
        else
        {
            _sum += tr;
            if (_count == Period)
            {
                Value = _sum / Period;
            }
        }

        return Value;
    }

    public void Reset()
    {
        _previousClose = null;
        _sum = 0;
        _count = 0;
        Value = null;
    }
}

/// <summary>RSI(n) with Wilder smoothing; 100 when the average loss is zero.</summary>
public sealed class Rsi(int period)
{
    private decimal? _previousClose;
    private decimal _gainSum;
    private decimal _lossSum;
    private int _changes;
    private decimal? _averageGain;
    private decimal? _averageLoss;

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public decimal? Value { get; private set; }

    public decimal? Update(decimal close)
    {
        if (_previousClose is { } previous)
        {
            var change = close - previous;
            var gain = Math.Max(change, 0);
            var loss = Math.Max(-change, 0);
            _changes++;
            if (_averageGain is { } g && _averageLoss is { } l)
            {
                _averageGain = (g * (Period - 1) + gain) / Period;
                _averageLoss = (l * (Period - 1) + loss) / Period;
            }
            else
            {
                _gainSum += gain;
                _lossSum += loss;
                if (_changes == Period)
                {
                    _averageGain = _gainSum / Period;
                    _averageLoss = _lossSum / Period;
                }
            }

            if (_averageGain is { } avgGain && _averageLoss is { } avgLoss)
            {
                Value = avgLoss == 0 ? 100m : 100m - 100m / (1 + avgGain / avgLoss);
            }
        }

        _previousClose = close;
        return Value;
    }

    public void Reset()
    {
        _previousClose = null;
        _gainSum = 0;
        _lossSum = 0;
        _changes = 0;
        _averageGain = null;
        _averageLoss = null;
        Value = null;
    }
}

public readonly record struct SupertrendValue(decimal Upper, decimal Lower, bool IsUp);

/// <summary>Supertrend(n, m) on Wilder ATR(n) with the standard recursive final-band and direction rules.</summary>
public sealed class Supertrend(int period, decimal multiplier)
{
    private readonly Atr _atr = new(period);
    private decimal? _previousClose;

    public SupertrendValue? Value { get; private set; }

    public SupertrendValue? Update(decimal high, decimal low, decimal close)
    {
        var atr = _atr.Update(high, low, close);
        if (atr is { } range)
        {
            var hl2 = (high + low) / 2;
            var basicUpper = hl2 + multiplier * range;
            var basicLower = hl2 - multiplier * range;
            if (Value is { } previous && _previousClose is { } previousClose)
            {
                var upper = basicUpper < previous.Upper || previousClose > previous.Upper ? basicUpper : previous.Upper;
                var lower = basicLower > previous.Lower || previousClose < previous.Lower ? basicLower : previous.Lower;
                var isUp = previous.IsUp ? close >= lower : close > upper;
                Value = new SupertrendValue(upper, lower, isUp);
            }
            else
            {
                Value = new SupertrendValue(basicUpper, basicLower, close > basicUpper);
            }
        }

        _previousClose = close;
        return Value;
    }

    public void Reset()
    {
        _atr.Reset();
        _previousClose = null;
        Value = null;
    }
}
