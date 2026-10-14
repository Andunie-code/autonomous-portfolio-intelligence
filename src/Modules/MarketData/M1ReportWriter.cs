using System.Globalization;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData;

/// <summary>
/// Acceptance report of the M1 pipeline (Markdown and JSON). Contains counts, symbols, statistics and reasons only;
/// never candle rows. The proposed core list becomes final only after the user approves this report.
/// </summary>
public static class M1ReportWriter
{
    public static (string MarkdownPath, string JsonPath) Write(M1Result result, string directory)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var markdownPath = Path.Combine(directory, $"m1-acceptance-{stamp}.md");
        var jsonPath = Path.Combine(directory, $"m1-acceptance-{stamp}.json");
        File.WriteAllText(markdownPath, Markdown(result), Encoding.UTF8);
        File.WriteAllText(jsonPath, Json(result), Encoding.UTF8);
        return (markdownPath, jsonPath);
    }

    public static string Markdown(M1Result result)
    {
        var s = new StringBuilder();
        var selection = result.Selection;
        s.AppendLine($"# M1 kabul raporu ({M1PipelineOptions.PolicyVersion})");
        s.AppendLine();
        s.AppendLine($"- Snapshot hash: `{result.SnapshotHash}`");
        s.AppendLine($"- Durum: {(selection.Complete ? $"{selection.Selected.Count} çekirdek parite **önerildi** (kullanıcı onayı bekliyor)" : $"**Eksik:** yalnızca {selection.Selected.Count} parite kalite şartlarını sağladı; eşikler gevşetilmedi, durulmalı")}");
        s.AppendLine($"- Arşivde listelenen sembol dizini: {result.ListedSymbols}; USDT sembolü: {result.Classification.Count}");
        s.AppendLine($"- Çekirdek havuzu: {result.Pool.Count}; sıralanan: {result.Ranking.Count(r => r.Status == RankingStatus.Ranked)}");
        s.AppendLine();

        s.AppendLine("## Önerilen çekirdek liste");
        s.AppendLine("| Sıra | Sembol | Medyan quote hacmi | 1h eksik | 4h eksik | 1h/4h anomali | Doğrulanamaz blok |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var picked in selection.Selected)
        {
            var a = selection.Assessments.First(x => x.Symbol == picked.Symbol);
            s.AppendLine($"| {picked.Rank} | {picked.Symbol} | {Format(picked.Stats.MedianQuoteVolume)} | {a.OneHour?.Missing} | {a.FourHour?.Missing} | {a.Consistency?.Anomalies.Count} | {a.Consistency?.Unverifiable} |");
        }

        s.AppendLine();
        s.AppendLine("## Hayatta kalma yanlılığı raporu");
        s.AppendLine("Çekirdek `core-4y-S`'dir: C'ye kadar kesintisiz veri koşuluyla seçilir (survivor-conditioned). Arşivin delist edilmiş sembolleri tutup tutmadığı doğrulanmadı; havuz bugünkü arşivde görünen sembollerle sınırlıdır.");
        s.AppendLine();
        s.AppendLine("| Sıra | Sembol | Eleme nedeni |");
        s.AppendLine("|---|---|---|");
        foreach (var e in selection.Eliminated)
        {
            s.AppendLine($"| {e.Rank} | {e.Symbol} | {e.Reason} |");
        }

        s.AppendLine();
        s.AppendLine("## Sıralanamayanlar");
        s.AppendLine("| Sembol | Durum | n_exp | n_obs | n_unk |");
        s.AppendLine("|---|---|---|---|---|");
        foreach (var r in result.Ranking.Where(r => r.Status != RankingStatus.Ranked))
        {
            s.AppendLine($"| {r.Symbol} | {r.Status} | {r.Stats.Expected} | {r.Stats.Observed} | {r.Stats.Unknown} |");
        }

        s.AppendLine();
        s.AppendLine("## Dışlananlar ve incelemesi bekleyenler");
        foreach (var group in result.Classification.Where(c => c.Outcome != ClassificationOutcome.Include).GroupBy(c => c.Category?.ToString() ?? c.Outcome.ToString()))
        {
            s.AppendLine($"- **{group.Key}** ({group.Count()}): {string.Join(", ", group.Select(g => g.Symbol))}");
        }

        s.AppendLine();
        s.AppendLine("## Dosyalar");
        foreach (var group in result.Files.GroupBy(f => f.Status))
        {
            s.AppendLine($"- {group.Key}: {group.Count()}");
        }

        foreach (var rejected in result.Files.Where(f => f.Status == FileStatus.Rejected))
        {
            s.AppendLine($"  - Reddedildi: `{rejected.Key.Path}` — {rejected.Reason}");
        }

        s.AppendLine();
        s.AppendLine("## E1 ön adayları (yalnızca liste; veri import edilmedi)");
        foreach (var e in result.E1)
        {
            s.AppendLine($"- {e.Symbol}: ilk ay {e.FirstYear:D4}-{e.FirstMonth:D2}{(e.FirstOpenVerified ? string.Empty : " (first_open doğrulanmadı)")}");
        }

        return s.ToString();
    }

    public static string Json(M1Result result) => JsonSerializer.Serialize(new
    {
        policy = M1PipelineOptions.PolicyVersion,
        snapshotHash = result.SnapshotHash,
        complete = result.Selection.Complete,
        selected = result.Selection.Selected.Select(s => new { s.Symbol, s.Rank, median = s.Stats.MedianQuoteVolume }),
        eliminated = result.Selection.Eliminated.Select(e => new { e.Symbol, e.Rank, reason = e.Reason.ToString() }),
        assessments = result.Selection.Assessments.Select(a => new
        {
            a.Symbol,
            oneHour = a.OneHour,
            fourHour = a.FourHour,
            a.IntegrityFailure,
            anomalies = a.Consistency?.Anomalies.Count,
            unverifiable = a.Consistency?.Unverifiable,
        }),
        ranking = result.Ranking.Select(r => new { r.Symbol, r.Rank, status = r.Status.ToString(), r.Stats.Expected, r.Stats.Observed, r.Stats.Unknown, median = r.Stats.MedianQuoteVolume }),
        classification = result.Classification.Select(c => new { c.Symbol, outcome = c.Outcome.ToString(), category = c.Category?.ToString(), c.Reason }),
        files = result.Files.GroupBy(f => f.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
        rejectedFiles = result.Files.Where(f => f.Status == FileStatus.Rejected).Select(f => new { path = f.Key.Path, f.Reason }),
        e1 = result.E1,
    }, new JsonSerializerOptions { WriteIndented = true });

    private static string Format(decimal? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
}
