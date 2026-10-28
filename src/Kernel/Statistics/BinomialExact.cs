namespace PortfolioIntelligence.Kernel.Statistics;

/// <summary>
/// One-sided exact binomial test (m7-protocol §5.4, FindingExtraction): H0: p ≤ p0 against H1: p &gt; p0 with
/// <c>successes</c> of <c>trials</c>. The p-value is P(X ≥ successes) for X ~ Binomial(trials, p0), computed in log space.
/// Rejecting at α is equivalent to the one-sided (1 − α) Clopper–Pearson lower bound exceeding p0.
/// </summary>
public static class BinomialExact
{
    public static double GreaterPValue(int successes, int trials, double p0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successes, trials);
        if (p0 is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p0), "p0 must be in (0, 1).");
        }

        if (successes == 0)
        {
            return 1;
        }

        var logTerms = new List<double>();
        for (var x = successes; x <= trials; x++)
        {
            logTerms.Add(LogChoose(trials, x) + x * Math.Log(p0) + (trials - x) * Math.Log(1 - p0));
        }

        var max = logTerms.Max();
        return Math.Clamp(Math.Exp(max) * logTerms.Sum(t => Math.Exp(t - max)), 0, 1);
    }

    /// <summary>Smallest number of successes out of <paramref name="trials"/> that rejects H0 at α, or null if none does.</summary>
    public static int? CriticalSuccesses(int trials, double p0, double alpha)
    {
        for (var k = 0; k <= trials; k++)
        {
            if (GreaterPValue(k, trials, p0) < alpha)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>Exact power: P(reject at α) when the true success rate is <paramref name="p"/>.</summary>
    public static double Power(int trials, double p, double p0, double alpha)
    {
        if (CriticalSuccesses(trials, p0, alpha) is not { } critical)
        {
            return 0;
        }

        var power = 0.0;
        for (var k = critical; k <= trials; k++)
        {
            power += p <= 0 ? (k == 0 ? 1 : 0)
                : p >= 1 ? (k == trials ? 1 : 0)
                : Math.Exp(LogChoose(trials, k) + k * Math.Log(p) + (trials - k) * Math.Log(1 - p));
        }

        return Math.Clamp(power, 0, 1);
    }

    /// <summary>Smallest n in <paramref name="candidates"/> with power ≥ <paramref name="targetPower"/>, or null if none.</summary>
    public static int? RequiredSample(double p, double p0, double alpha, double targetPower, IEnumerable<int> candidates) =>
        candidates.Order().Cast<int?>().FirstOrDefault(n => Power(n!.Value, p, p0, alpha) >= targetPower);

    private static double LogChoose(int n, int k)
    {
        var sum = 0.0;
        for (var i = 1; i <= k; i++)
        {
            sum += Math.Log(n - k + i) - Math.Log(i);
        }

        return sum;
    }
}
