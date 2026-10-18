namespace PortfolioIntelligence.Kernel.Statistics;

/// <summary>
/// One-sided Fisher exact test for a 2×2 table (testing-strategy §6.2, HypothesisGeneration): arm A has
/// <c>successesA</c> of <c>trialsA</c>, arm B <c>successesB</c> of <c>trialsB</c>. H1: the success rate of A is
/// greater than that of B. The p-value is P(X ≥ successesA) under the hypergeometric distribution with all margins
/// fixed, computed in log space.
/// </summary>
public static class FisherExact
{
    public static double GreaterPValue(int successesA, int trialsA, int successesB, int trialsB)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successesA);
        ArgumentOutOfRangeException.ThrowIfNegative(successesB);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successesA, trialsA);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successesB, trialsB);

        var successes = successesA + successesB;
        var total = trialsA + trialsB;
        var low = Math.Max(0, successes - trialsB);
        var high = Math.Min(successes, trialsA);
        if (successesA <= low)
        {
            return 1;
        }

        // Sum P(X = x) for x = successesA … high relative to the largest term, for stability.
        var logTerms = new List<double>();
        for (var x = successesA; x <= high; x++)
        {
            logTerms.Add(LogChoose(trialsA, x) + LogChoose(trialsB, successes - x) - LogChoose(total, successes));
        }

        var max = logTerms.Max();
        var p = Math.Exp(max) * logTerms.Sum(t => Math.Exp(t - max));
        return Math.Clamp(p, 0, 1);
    }

    /// <summary>
    /// Exact power of the one-sided test with <paramref name="n"/> trials per arm: P(p-value &lt; α) when arm A succeeds
    /// with probability <paramref name="pA"/> and arm B with <paramref name="pB"/>, summed over all outcomes.
    /// </summary>
    public static double Power(int n, double pA, double pB, double alpha)
    {
        var a = Binomial(n, pA);
        var b = Binomial(n, pB);
        var power = 0.0;
        for (var x = 0; x <= n; x++)
        {
            if (a[x] < 1e-15)
            {
                continue;
            }

            // The p-value falls as x rises and as y falls: scan y upwards until the test stops rejecting.
            for (var y = 0; y <= n && b[y] >= 0; y++)
            {
                if (GreaterPValue(x, n, y, n) >= alpha)
                {
                    break;
                }

                power += a[x] * b[y];
            }
        }

        return Math.Clamp(power, 0, 1);
    }

    /// <summary>Smallest n in <paramref name="candidates"/> with power ≥ <paramref name="targetPower"/>, or null if none.</summary>
    public static int? RequiredPerArm(double pA, double pB, double alpha, double targetPower, IEnumerable<int> candidates) =>
        candidates.Order().Cast<int?>().FirstOrDefault(n => Power(n!.Value, pA, pB, alpha) >= targetPower);

    private static double[] Binomial(int n, double p)
    {
        var result = new double[n + 1];
        for (var k = 0; k <= n; k++)
        {
            result[k] = p <= 0 ? (k == 0 ? 1 : 0)
                : p >= 1 ? (k == n ? 1 : 0)
                : Math.Exp(LogChoose(n, k) + k * Math.Log(p) + (n - k) * Math.Log(1 - p));
        }

        return result;
    }

    private static double LogChoose(int n, int k) => LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    // ln(n!) for n ≤ 4000 (power analysis evaluates the p-value many times).
    private static readonly double[] LogFactorials = BuildLogFactorials(4000);

    private static double LogFactorial(int n)
    {
        if (n < LogFactorials.Length)
        {
            return LogFactorials[n];
        }

        var sum = LogFactorials[^1];
        for (var i = LogFactorials.Length; i <= n; i++)
        {
            sum += Math.Log(i);
        }

        return sum;
    }

    private static double[] BuildLogFactorials(int max)
    {
        var table = new double[max + 1];
        for (var i = 2; i <= max; i++)
        {
            table[i] = table[i - 1] + Math.Log(i);
        }

        return table;
    }
}
