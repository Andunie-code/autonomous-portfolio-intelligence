namespace PortfolioIntelligence.Kernel.MarketData;

public enum EliminationReason
{
    Ended,
    LateStart,
    InternalGap,
    LowCoverage,
    Integrity,
}

public sealed record QualityAssessment(
    string Symbol,
    CoverageResult? OneHour,
    CoverageResult? FourHour,
    bool IntegrityFailure,
    IntervalConsistencyResult? Consistency)
{
    public bool Passes => !IntegrityFailure && OneHour is { Passes: true } && FourHour is { Passes: true };

    /// <summary>Primary reason when the symbol fails; order: integrity, then the worse of the 1h/4h labels.</summary>
    public EliminationReason? Reason
    {
        get
        {
            if (Passes)
            {
                return null;
            }

            if (IntegrityFailure)
            {
                return EliminationReason.Integrity;
            }

            var labels = new[] { OneHour?.Label, FourHour?.Label }.OfType<CoverageLabel>()
                .Where(l => l != CoverageLabel.Continuous)
                .DefaultIfEmpty(CoverageLabel.LowCoverage);
            return labels.Min() switch
            {
                CoverageLabel.Ended => EliminationReason.Ended,
                CoverageLabel.LateStart => EliminationReason.LateStart,
                CoverageLabel.InternalGap => EliminationReason.InternalGap,
                _ => EliminationReason.LowCoverage,
            };
        }
    }
}

public sealed record EliminatedCandidate(int Rank, string Symbol, EliminationReason Reason);

public sealed record CoreSelection(
    IReadOnlyList<RankedSymbol> Selected,
    IReadOnlyList<EliminatedCandidate> Eliminated,
    IReadOnlyList<QualityAssessment> Assessments,
    bool Complete);

/// <summary>
/// Walks the frozen ranking in order, replacing each candidate that fails the quality gate with the next one until
/// <see cref="CoreSize"/> pairs are selected (docs/m1-data-policy.md §5 steps 5–7). Thresholds are never relaxed:
/// if the list runs out, the result is incomplete and the caller must stop and report.
/// </summary>
public static class CoreSelector
{
    public const int CoreSize = 20;

    public static Task<CoreSelection> SelectAsync(
        IReadOnlyList<RankedSymbol> ranking,
        Func<RankedSymbol, CancellationToken, Task<QualityAssessment>> assess,
        CancellationToken cancellationToken) => SelectAsync(ranking, assess, CoreSize, cancellationToken);

    public static async Task<CoreSelection> SelectAsync(
        IReadOnlyList<RankedSymbol> ranking,
        Func<RankedSymbol, CancellationToken, Task<QualityAssessment>> assess,
        int coreSize,
        CancellationToken cancellationToken)
    {
        if (coreSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(coreSize));
        }

        var selected = new List<RankedSymbol>();
        var eliminated = new List<EliminatedCandidate>();
        var assessments = new List<QualityAssessment>();

        foreach (var candidate in ranking.Where(r => r.Status == RankingStatus.Ranked).OrderBy(r => r.Rank))
        {
            if (selected.Count == coreSize)
            {
                break;
            }

            var assessment = await assess(candidate, cancellationToken);
            assessments.Add(assessment);
            if (assessment.Passes)
            {
                selected.Add(candidate);
            }
            else
            {
                eliminated.Add(new EliminatedCandidate(candidate.Rank!.Value, candidate.Symbol, assessment.Reason!.Value));
            }
        }

        return new CoreSelection(selected, eliminated, assessments, selected.Count == coreSize);
    }
}
