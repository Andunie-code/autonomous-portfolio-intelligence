using System.Globalization;
using System.Text;

namespace PortfolioIntelligence.Research.Evaluation;

/// <summary>Metrics-only calibration report (docs/m3-calibration-results.md).</summary>
public static class CalibrationMarkdown
{
    public static string Render(CalibrationReport report, TimeSpan elapsed)
    {
        var o = report.Options;
        var s = new StringBuilder();
        s.AppendLine(o.ProtocolVersion >= 2 ? "# M3 kalibrasyon sonuçları — Protokol v2 (m3-protocol-v2 §1–§3)" : "# M3 kalibrasyon sonuçları (m3-protocol v1 §7)");
        s.AppendLine();
        s.AppendLine("- Veri: yalnızca sentetik (seed'li). Kalibrasyon koşuları deneme sayacına girmez.");
        s.AppendLine(Inv($"- Null: {o.NullStrategies} rastgele DSL stratejisi (üreteç seed {o.StrategySeed}) × {o.NullUniverses} null evren (seed {o.NullUniverseSeedBase}…), 20 parite, 4h, {report.RiskVariant} koşusu, maliyet modeli v1."));
        s.AppendLine(Inv($"- Doğru pozitif: `tp-trend` (EMA 20/60, 3 × ATR(14) stop; tanım hash'i `{report.TruePositiveStrategyHash[..12]}…`) × {o.TruePositiveUniverses} gömülü trendli evren (seed {o.TruePositiveSeedBase}…; κ = {o.TrendKappa}, ortalama rejim {o.TrendMeanBars} mum, yukarı payı {o.TrendUpShare}). Ortalama birincil metrik: {report.TruePositiveMeanMetric:P1}."));
        if (report.Kappa is { } k)
        {
            s.AppendLine(Inv($"- κ araması (v2 §3): hedef t* = {k.TargetT:0.000}; seçilen κ = {k.Kappa:0.000}, ortalama t = {k.MeanT:0.000} (ızgara 0,020–0,080)."));
            s.AppendLine(Inv($"- Deflated Sharpe: N = {o.CalibrationTrials}, V = 1/(T − 1) (v2 §2; 4h dönem başına)."));
        }
        else
        {
            s.AppendLine(Inv($"- Deflated Sharpe: N = {o.CalibrationTrials}, deneme Sharpe varyansı (null dağılımından, 4h dönem başına) = {report.NullSharpeVariance:E3}."));
        }
        s.AppendLine(Inv($"- Hedefler: FP ≤ {o.TargetFalsePositive:P0}, TP ≥ {o.TargetTruePositive:P0}. Süre: {elapsed:hh\\:mm\\:ss}."));
        s.AppendLine();
        s.AppendLine("## Kapı bazında geçme oranları (en gevşek eşiklerle)");
        s.AppendLine();
        var lenient = GateThresholds.MostLenient;
        s.AppendLine("| Küme | G1 | G2 | G3 | G4 | G5 | G6 | Hepsi |");
        s.AppendLine("|---|---|---|---|---|---|---|---|");
        s.AppendLine(Rates("Null", report.Null, lenient));
        s.AppendLine(Rates("Doğru pozitif", report.TruePositive, lenient));
        s.AppendLine();
        s.AppendLine("## Seçim");
        s.AppendLine();
        s.AppendLine(report.Selected is { } selected
            ? Inv($"**Seçilen eşikler:** `N_min = {selected.MinTrades}`, `τ_wf = {selected.WalkForward}`, `τ_nb = {selected.Neighbourhood}`, `τ_dsr = {selected.DeflatedSharpe}`.")
            : "**Hiçbir bileşim iki şartı birlikte sağlamadı.** Eşikler gevşetilmedi; protokol gereği durulur ve rapor kullanıcıya sunulur.");
        s.AppendLine();
        s.AppendLine("## Izgara (FP ≤ hedef ve TP ≥ hedef olanlar ✓)");
        s.AppendLine();
        s.AppendLine("| N_min | τ_wf | τ_nb | τ_dsr | FP | TP | Uygun |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var g in report.Grid)
        {
            var t = g.Thresholds;
            s.AppendLine(Inv($"| {t.MinTrades} | {t.WalkForward:0.00} | {t.Neighbourhood:0.00} | {t.DeflatedSharpe:0.000} | {g.FalsePositive:P1} | {g.TruePositive:P1} | {(g.Admissible ? "✓" : "")} |"));
        }

        return s.ToString();
    }

    private static string Rates(string label, IReadOnlyList<CandidateStatistics> set, GateThresholds t)
    {
        string Rate(Func<CandidateStatistics, bool> pass) => Inv($"{(double)set.Count(pass) / set.Count:P1}");
        return $"| {label} | {Rate(x => x.G1)} | {Rate(x => x.G2(t))} | {Rate(x => x.G3(t))} | {Rate(x => x.G4(t))} | {Rate(x => x.G5)} | {Rate(x => x.G6(t))} | {Rate(x => x.PassesAll(t))} |";
    }

    private static string Inv(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
