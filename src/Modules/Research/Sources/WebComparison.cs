using System.Globalization;
using System.Text;
using PortfolioIntelligence.Researcher;

namespace PortfolioIntelligence.Research.Sources;

/// <summary>One arm of the comparison: its cycle report (null when the arm was not run) and the hypotheses it had.</summary>
public sealed record ComparisonArm(string Name, IReadOnlyList<Hypothesis> Hypotheses, CycleReport? Report, decimal ExtractorCostUsd);

/// <summary>
/// The web vs. no-web comparison (m7-protocol §9; roadmap M7 criterion 7). Descriptive only: per arm the hypotheses, trials,
/// gate passes, the most frequent failing gates and the Extractor cost. It makes no statistical claim and decides nothing.
/// When FindingExtraction is not qualified the web arm is empty and the report says that web research is closed (D20).
/// </summary>
public static class WebComparison
{
    public static string Markdown(ComparisonArm web, ComparisonArm noWeb, bool webResearchOpen, string protocol, string engineSha, int trialsBefore, int trialsAfter)
    {
        var text = new StringBuilder();
        text.AppendLine("> Betimleyici rapor (m7-protocol §9). İstatistiksel iddia taşımaz ve hiçbir kapıyı, eşiği ya da kararı etkilemez.");
        text.AppendLine();
        text.AppendLine("# Web'li ve web'siz araştırma karşılaştırması");
        text.AppendLine();
        text.AppendLine(Invariant($"- Protokol: `{protocol}` · engine `{engineSha}` · global deneme sayacı {trialsBefore} → {trialsAfter}"));
        if (!webResearchOpen)
        {
            text.AppendLine($"- **Web'li kol boş:** {WebResearchGate.ClosedNote}. Yalnızca web'siz kol raporlanır.");
        }

        text.AppendLine();
        text.AppendLine("| Kol | Hipotez | Deneme (nokta) | Kapıları geçen hipotez | Hata | Extractor maliyeti | En sık takılan kapılar |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var arm in new[] { web, noWeb })
        {
            text.AppendLine(Row(arm));
        }

        text.AppendLine();
        foreach (var arm in new[] { web, noWeb }.Where(a => a.Report is not null))
        {
            text.AppendLine($"## {arm.Name}");
            text.AppendLine();
            text.AppendLine("| Hipotez | Kaynak bulgular | Sonuç | Takılan kapılar |");
            text.AppendLine("|---|---|---|---|");
            foreach (var verdict in arm.Report!.Verdicts)
            {
                var findings = arm.Hypotheses.FirstOrDefault(h => h.Key == verdict.Key)?.SourceFindingIds is { Count: > 0 } ids
                    ? string.Join(", ", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) : "—";
                text.AppendLine($"| {verdict.Key} | {findings} | {verdict.Category} | {(verdict.Failures.Count == 0 ? "—" : string.Join(", ", verdict.Failures))} |");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    public static string Row(ComparisonArm arm)
    {
        if (arm.Report is not { } report)
        {
            return $"| {arm.Name} | 0 | 0 | — | — | {Usd(arm.ExtractorCostUsd)} | — |";
        }

        var trials = report.Points.Count(p => p.Submission["newTrial"]?.GetValue<bool>() == true);
        var candidates = report.Verdicts.Count(v => v.Category == HypothesisCategory.Candidate);
        var errors = report.Verdicts.Count(v => v.Category == HypothesisCategory.Error);
        var gates = report.Verdicts.SelectMany(v => v.Failures).GroupBy(f => f, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(3).Select(g => $"{g.Key} ({g.Count()})");
        return Invariant($"| {arm.Name} | {report.Verdicts.Count} | {trials} | {candidates} | {errors} | {Usd(arm.ExtractorCostUsd)} | {string.Join(", ", gates)} |");
    }

    private static string Usd(decimal value) => value.ToString("0.000000", CultureInfo.InvariantCulture) + " USD";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
