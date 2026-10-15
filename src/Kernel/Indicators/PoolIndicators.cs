namespace PortfolioIntelligence.Kernel.Indicators;

// Incremental, resettable indicators added in M3 with the exact definitions of docs/m3-protocol.md §1. All decimal.

/// <summary>SMA(n): arithmetic mean of the last n inputs.</summary>
public sealed class Sma(int period)
{
    private readonly Queue<decimal> _window = new();
    private decimal _sum;

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public decimal? Value { get; private set; }

    public decimal? Update(decimal input)
    {
        _window.Enqueue(input);
        _sum += input;
        if (_window.Count > Period)
        {
            _sum -= _window.Dequeue();
        }

        Value = _window.Count == Period ? _sum / Period : null;
        return Value;
    }

    public void Reset()
    {
        _window.Clear();
        _sum = 0;
        Value = null;
    }
}

public readonly record struct MacdValue(decimal Line, decimal Signal, decimal Histogram);

/// <summary>MACD(f, s, g): line = EMA_f − EMA_s; signal = EMA_g of the line (seeded with the mean of the first g lines).</summary>
public sealed class Macd
{
    private readonly Ema _fast;
    private readonly Ema _slow;
    private readonly Ema _signal;

    public Macd(int fast, int slow, int signal)
    {
        if (fast >= slow)
        {
            throw new ArgumentException("MACD requires fast < slow.");
        }

        _fast = new Ema(fast);
        _slow = new Ema(slow);
        _signal = new Ema(signal);
    }

    public MacdValue? Value { get; private set; }

    public decimal? Line { get; private set; }

    public MacdValue? Update(decimal close)
    {
        var fast = _fast.Update(close);
        var slow = _slow.Update(close);
        Line = fast is { } f && slow is { } s ? f - s : null;
        Value = Line is { } line && _signal.Update(line) is { } signal ? new MacdValue(line, signal, line - signal) : null;
        return Value;
    }

    public void Reset()
    {
        _fast.Reset();
        _slow.Reset();
        _signal.Reset();
        Line = null;
        Value = null;
    }
}

public readonly record struct AdxValue(decimal PlusDi, decimal MinusDi, decimal? Adx);

/// <summary>ADX(n) with Wilder smoothing: smoothed sums seeded with the sum of the first n values, ADX seeded with the mean of the first n DX.</summary>
public sealed class Adx(int period)
{
    private decimal? _previousHigh;
    private decimal? _previousLow;
    private decimal? _previousClose;
    private int _moves;
    private decimal _trSum;
    private decimal _plusSum;
    private decimal _minusSum;
    private decimal? _trSmoothed;
    private decimal? _plusSmoothed;
    private decimal? _minusSmoothed;
    private int _dxCount;
    private decimal _dxSum;
    private decimal? _adx;

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public AdxValue? Value { get; private set; }

    public AdxValue? Update(decimal high, decimal low, decimal close)
    {
        if (_previousHigh is { } previousHigh && _previousLow is { } previousLow)
        {
            var up = high - previousHigh;
            var down = previousLow - low;
            var plusDm = up > down && up > 0 ? up : 0m;
            var minusDm = down > up && down > 0 ? down : 0m;
            var tr = Atr.TrueRange(high, low, _previousClose);
            _moves++;
            if (_trSmoothed is { } trS && _plusSmoothed is { } plusS && _minusSmoothed is { } minusS)
            {
                _trSmoothed = trS - trS / Period + tr;
                _plusSmoothed = plusS - plusS / Period + plusDm;
                _minusSmoothed = minusS - minusS / Period + minusDm;
            }
            else
            {
                _trSum += tr;
                _plusSum += plusDm;
                _minusSum += minusDm;
                if (_moves == Period)
                {
                    _trSmoothed = _trSum;
                    _plusSmoothed = _plusSum;
                    _minusSmoothed = _minusSum;
                }
            }

            if (_trSmoothed is { } trN && _plusSmoothed is { } plusN && _minusSmoothed is { } minusN)
            {
                var plusDi = trN == 0 ? 0m : 100m * plusN / trN;
                var minusDi = trN == 0 ? 0m : 100m * minusN / trN;
                var diSum = plusDi + minusDi;
                var dx = diSum == 0 ? 0m : 100m * Math.Abs(plusDi - minusDi) / diSum;
                _dxCount++;
                if (_adx is { } previousAdx)
                {
                    _adx = (previousAdx * (Period - 1) + dx) / Period;
                }
                else
                {
                    _dxSum += dx;
                    if (_dxCount == Period)
                    {
                        _adx = _dxSum / Period;
                    }
                }

                Value = new AdxValue(plusDi, minusDi, _adx);
            }
        }

        _previousHigh = high;
        _previousLow = low;
        _previousClose = close;
        return Value;
    }

    public void Reset()
    {
        _previousHigh = null;
        _previousLow = null;
        _previousClose = null;
        _moves = 0;
        _trSum = 0;
        _plusSum = 0;
        _minusSum = 0;
        _trSmoothed = null;
        _plusSmoothed = null;
        _minusSmoothed = null;
        _dxCount = 0;
        _dxSum = 0;
        _adx = null;
        Value = null;
    }
}

public readonly record struct BollingerValue(decimal Middle, decimal Upper, decimal Lower, decimal? Bandwidth, decimal? PercentB);

/// <summary>Bollinger(n, k): SMA middle ± k·σ (population σ over the last n closes).</summary>
public sealed class Bollinger(int period, decimal k)
{
    private readonly Queue<decimal> _window = new();

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public BollingerValue? Value { get; private set; }

    public BollingerValue? Update(decimal close)
    {
        _window.Enqueue(close);
        if (_window.Count > Period)
        {
            _window.Dequeue();
        }

        if (_window.Count < Period)
        {
            Value = null;
            return null;
        }

        var sum = 0m;
        foreach (var c in _window)
        {
            sum += c;
        }

        var middle = sum / Period;
        var squares = 0m;
        foreach (var c in _window)
        {
            squares += (c - middle) * (c - middle);
        }

        var sigma = Numerics.Sqrt(squares / Period);
        var upper = middle + k * sigma;
        var lower = middle - k * sigma;
        decimal? bandwidth = middle == 0 ? null : (upper - lower) / middle;
        decimal? percentB = upper == lower ? null : (close - lower) / (upper - lower);
        Value = new BollingerValue(middle, upper, lower, bandwidth, percentB);
        return Value;
    }

    public void Reset()
    {
        _window.Clear();
        Value = null;
    }
}

public readonly record struct DonchianValue(decimal Upper, decimal Lower, decimal Middle);

/// <summary>Donchian(n) over the previous n candles, excluding the current one.</summary>
public sealed class Donchian(int period)
{
    private readonly Queue<(decimal High, decimal Low)> _previous = new();

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public DonchianValue? Value { get; private set; }

    public DonchianValue? Update(decimal high, decimal low)
    {
        if (_previous.Count == Period)
        {
            var upper = _previous.Max(p => p.High);
            var lower = _previous.Min(p => p.Low);
            Value = new DonchianValue(upper, lower, (upper + lower) / 2);
            _previous.Dequeue();
        }
        else
        {
            Value = null;
        }

        _previous.Enqueue((high, low));
        return Value;
    }

    public void Reset()
    {
        _previous.Clear();
        Value = null;
    }
}

/// <summary>ROC(n) = C_t / C_{t−n} − 1 (a fraction).</summary>
public sealed class Roc(int period)
{
    private readonly Queue<decimal> _previous = new();

    public int Period { get; } = period > 0 ? period : throw new ArgumentOutOfRangeException(nameof(period));

    public decimal? Value { get; private set; }

    public decimal? Update(decimal close)
    {
        if (_previous.Count == Period)
        {
            var past = _previous.Dequeue();
            Value = past == 0 ? null : close / past - 1;
        }
        else
        {
            Value = null;
        }

        _previous.Enqueue(close);
        return Value;
    }

    public void Reset()
    {
        _previous.Clear();
        Value = null;
    }
}

/// <summary>Numeric helpers shared by the indicators.</summary>
public static class Numerics
{
    /// <summary>
    /// Square root computed in IEEE double and rounded to 12 significant digits (the σ rule of
    /// docs/m2-protocol.md §9), so the decimal result is reproducible across platforms.
    /// </summary>
    public static decimal Sqrt(decimal value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value == 0 ? 0m : RoundSignificant(Math.Sqrt((double)value));
    }

    public static decimal RoundSignificant(double value, int digits = 12)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return value == 0 ? 0m : throw new ArgumentOutOfRangeException(nameof(value));
        }

        var decimals = Math.Clamp(digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value))), 0, 15);
        return (decimal)Math.Round(value, decimals);
    }
}
