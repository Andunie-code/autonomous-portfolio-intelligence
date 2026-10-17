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

    private static double LogChoose(int n, int k) => LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    private static double LogFactorial(int n)
    {
        var sum = 0.0;
        for (var i = 2; i <= n; i++)
        {
            sum += Math.Log(i);
        }

        return sum;
    }
}
