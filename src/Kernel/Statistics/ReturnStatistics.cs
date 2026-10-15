using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Kernel.Statistics;

/// <summary>Return series and the primary metric of m3-protocol §4 (cash benchmark: excess = M).</summary>
public static class ReturnStatistics
{
    /// <summary>4h periods per year.</summary>
    public const int PeriodsPerYear = 2190;

    /// <summary>Log returns r_t = ln(E_t / E_{t−1}) with E_0 = the initial capital.</summary>
    public static double[] LogReturns(decimal initialCapital, IReadOnlyList<EquityPoint> equity)
    {
        var returns = new double[equity.Count];
        var previous = (double)initialCapital;
        for (var i = 0; i < equity.Count; i++)
        {
            var current = (double)equity[i].Equity;
            returns[i] = previous > 0 && current > 0 ? Math.Log(current / previous) : double.NegativeInfinity;
            previous = current;
        }

        return returns;
    }

    /// <summary>M = mean(r) × periods per year.</summary>
    public static double AnnualisedMean(IReadOnlyList<double> returns, int periodsPerYear = PeriodsPerYear) =>
        returns.Count == 0 ? 0 : Mean(returns) * periodsPerYear;

    public static double PrimaryMetric(BacktestResult result) => AnnualisedMean(LogReturns(result.InitialCapital, result.Equity));

    /// <summary>Maximum drawdown of the equity series, starting from the initial capital.</summary>
    public static double MaxDrawdown(decimal initialCapital, IReadOnlyList<EquityPoint> equity)
    {
        var peak = initialCapital;
        var worst = 0m;
        foreach (var point in equity)
        {
            peak = Math.Max(peak, point.Equity);
            if (peak > 0)
            {
                worst = Math.Max(worst, 1 - point.Equity / peak);
            }
        }

        return (double)worst;
    }

    public static double Mean(IReadOnlyList<double> x)
    {
        var sum = 0.0;
        for (var i = 0; i < x.Count; i++)
        {
            sum += x[i];
        }

        return sum / x.Count;
    }

    /// <summary>Per-period Sharpe ratio (risk-free 0), sample standard deviation; 0 when undefined.</summary>
    public static double Sharpe(IReadOnlyList<double> x)
    {
        if (x.Count < 2)
        {
            return 0;
        }

        var mean = Mean(x);
        var variance = 0.0;
        foreach (var v in x)
        {
            variance += (v - mean) * (v - mean);
        }

        var sd = Math.Sqrt(variance / (x.Count - 1));
        return sd > 0 ? mean / sd : 0;
    }

    /// <summary>Sample skewness and (non-excess) kurtosis, population moments.</summary>
    public static (double Skewness, double Kurtosis) Moments(IReadOnlyList<double> x)
    {
        if (x.Count < 3)
        {
            return (0, 3);
        }

        var mean = Mean(x);
        double m2 = 0, m3 = 0, m4 = 0;
        foreach (var v in x)
        {
            var d = v - mean;
            m2 += d * d;
            m3 += d * d * d;
            m4 += d * d * d * d;
        }

        m2 /= x.Count;
        m3 /= x.Count;
        m4 /= x.Count;
        return m2 > 0 ? (m3 / Math.Pow(m2, 1.5), m4 / (m2 * m2)) : (0, 3);
    }
}

/// <summary>One-sided bounds of a bootstrap distribution (m3-protocol §4).</summary>
public sealed record BootstrapBounds(double Estimate, double Lower, double Upper, double PValue);

/// <summary>Stationary block bootstrap (Politis and Romano 1994) of an annualised mean, seeded and deterministic.</summary>
public static class StationaryBootstrap
{
    public static int MeanBlockLength(int n) => Math.Max(1, (int)Math.Ceiling(Math.Cbrt(n)));

    /// <summary>Sorted bootstrap distribution of scale × mean over <paramref name="replicates"/> resamples.</summary>
    public static double[] Distribution(IReadOnlyList<double> values, int replicates, int seed, double scale)
    {
        var x = values as double[] ?? values.ToArray();
        var n = x.Length;
        if (n == 0)
        {
            return new double[replicates];
        }

        var p = 1.0 / MeanBlockLength(n);
        var random = new Random(seed);
        var result = new double[replicates];
        for (var b = 0; b < replicates; b++)
        {
            var index = random.Next(n);
            var sum = 0.0;
            for (var i = 0; i < n; i++)
            {
                sum += x[index];
                index = random.NextDouble() < p ? random.Next(n) : (index + 1) % n;
            }

            result[b] = scale * sum / n;
        }

        Array.Sort(result);
        return result;
    }

    /// <summary>
    /// Lower bound = the ⌈α·B⌉-th smallest, upper bound = the ⌈(1 − α)·B⌉-th smallest bootstrap value;
    /// p = #{M* ≤ 0} / B.
    /// </summary>
    public static BootstrapBounds Bounds(double estimate, double[] sortedDistribution, double alpha)
    {
        var b = sortedDistribution.Length;
        var lower = sortedDistribution[Math.Clamp((int)Math.Ceiling(alpha * b) - 1, 0, b - 1)];
        var upper = sortedDistribution[Math.Clamp((int)Math.Ceiling((1 - alpha) * b) - 1, 0, b - 1)];
        var atOrBelowZero = 0;
        foreach (var v in sortedDistribution)
        {
            if (v > 0)
            {
                break;
            }

            atOrBelowZero++;
        }

        return new BootstrapBounds(estimate, lower, upper, (double)atOrBelowZero / b);
    }
}

/// <summary>Holm step-down within a cohort (m3-protocol §4).</summary>
public static class Holm
{
    /// <summary>
    /// For each strategy (input order) the level α_k = α / (m − k + 1) at which it is tested and whether it is
    /// eligible: every strategy ranked before it was rejected. Ties keep the input order.
    /// </summary>
    public static IReadOnlyList<(double Alpha, bool Eligible)> Levels(IReadOnlyList<double> pValues, double familyAlpha)
    {
        var m = pValues.Count;
        var order = Enumerable.Range(0, m).OrderBy(i => pValues[i]).ThenBy(i => i).ToList();
        var result = new (double, bool)[m];
        var stillRejecting = true;
        for (var k = 0; k < m; k++)
        {
            var index = order[k];
            var alpha = familyAlpha / (m - k);
            result[index] = (alpha, stillRejecting);
            if (!(pValues[index] < alpha))
            {
                stillRejecting = false;
            }
        }

        return result;
    }
}

/// <summary>Deflated Sharpe ratio (Bailey and López de Prado 2014) on per-period Sharpe ratios.</summary>
public static class DeflatedSharpe
{
    private const double EulerGamma = 0.5772156649015329;

    /// <summary>Expected maximum Sharpe ratio of N trials with Sharpe variance <paramref name="trialVariance"/>.</summary>
    public static double Benchmark(int trials, double trialVariance)
    {
        if (trials < 2 || trialVariance <= 0)
        {
            return 0;
        }

        return Math.Sqrt(trialVariance) * ((1 - EulerGamma) * Normal.InverseCdf(1 - 1.0 / trials)
            + EulerGamma * Normal.InverseCdf(1 - 1.0 / (trials * Math.E)));
    }

    /// <summary>Probability that the true Sharpe exceeds the benchmark, given T observations, skewness and kurtosis.</summary>
    public static double Probability(double sharpe, int observations, double skewness, double kurtosis, int trials, double trialVariance)
    {
        if (observations < 2)
        {
            return 0;
        }

        var benchmark = Benchmark(trials, trialVariance);
        var denominator = 1 - skewness * sharpe + (kurtosis - 1) / 4 * sharpe * sharpe;
        if (denominator <= 0)
        {
            return 0;
        }

        return Normal.Cdf((sharpe - benchmark) * Math.Sqrt(observations - 1) / Math.Sqrt(denominator));
    }
}
