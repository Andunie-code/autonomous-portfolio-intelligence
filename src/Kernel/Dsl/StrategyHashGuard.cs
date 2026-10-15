namespace PortfolioIntelligence.Kernel.Dsl;

public sealed class StrategyHashMismatchException(string expected, string actual)
    : Exception($"Strategy definition hash {actual} does not match the manifest hash {expected}; the strategy must not run.")
{
    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

/// <summary>
/// V14 (architecture §6.4): before running a registry strategy, Trading compares the definition hash with the
/// hash in the cohort manifest and refuses to run on a mismatch (the caller raises the alarm).
/// </summary>
public static class StrategyHashGuard
{
    public static DslStrategy LoadVerified(string definitionJson, string manifestHash)
    {
        var strategy = DslStrategy.FromJson(definitionJson);
        return string.Equals(strategy.DefinitionHash, manifestHash, StringComparison.Ordinal)
            ? strategy
            : throw new StrategyHashMismatchException(manifestHash, strategy.DefinitionHash);
    }
}
