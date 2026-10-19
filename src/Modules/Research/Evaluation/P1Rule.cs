using System.Globalization;

namespace PortfolioIntelligence.Research.Evaluation;

/// <summary>One P1 analysis run (docs/m5-protocol.md §9). Fractions, not percentages.</summary>
public sealed record P1Row(
    string Strategy,
    double Threshold,
    decimal? Cap,
    double M,
    double MaxDrawdown,
    double Worst30d,
    int Trades,
    int ClusterClips,
    int ClusterRejects,
    double MeanExposure,
    double MeanEffectiveCount,
    double MedianMaxShare,
    double P95MaxShare,
    double MaxMaxShare);

public sealed record P1Recommendation(decimal? Cap, double WarningThreshold, string Text);

/// <summary>
/// The pre-registered P1 decision rule (m5-protocol §9), applied to the primary-threshold rows: a cap is eligible
/// when it binds at least once, costs no strategy more than 1.0 point of annual M, and improves the maximum
/// drawdown of at least one strategy by 0.5 points; the largest eligible cap is recommended (warning at 0.9 × cap).
/// Without an eligible cap: no binding cap, warning at the larger uncapped p95 of the largest cluster share.
/// </summary>
public static class P1Rule
{
    public static readonly decimal?[] Caps = [0.30m, 0.40m, 0.50m, 0.60m, null];

    public static P1Recommendation Recommend(IReadOnlyList<P1Row> primary)
    {
        var strategies = primary.Select(r => r.Strategy).Distinct().ToList();
        P1Row Uncapped(string s) => primary.Single(r => r.Strategy == s && r.Cap is null);
        var eligible = primary.Where(r => r.Cap is not null).Select(r => r.Cap!.Value).Distinct().Where(cap =>
        {
            var rows = strategies.Select(s => (Capped: primary.Single(r => r.Strategy == s && r.Cap == cap), Free: Uncapped(s))).ToList();
            var binds = rows.Any(x => x.Capped.ClusterClips + x.Capped.ClusterRejects > 0);
            var returnOk = rows.All(x => x.Capped.M - x.Free.M >= -0.01);
            var drawdownBetter = rows.Any(x => x.Free.MaxDrawdown - x.Capped.MaxDrawdown >= 0.005);
            return binds && returnOk && drawdownBetter;
        }).ToList();
        if (eligible.Count > 0)
        {
            var cap = eligible.Max();
            return new P1Recommendation(cap, (double)(0.9m * cap), string.Create(CultureInfo.InvariantCulture, $"binding cluster cap {cap:P0}, warning at {0.9m * cap:P0}"));
        }

        var warning = strategies.Max(s => Uncapped(s).P95MaxShare);
        return new P1Recommendation(null, warning, string.Create(CultureInfo.InvariantCulture, $"no binding cap, monitoring only; warning at the uncapped p95 cluster share {warning:P1}"));
    }
}
