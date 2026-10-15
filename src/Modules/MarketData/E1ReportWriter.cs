using System.Globalization;
using System.Text;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData;

/// <summary>E1 subset report (metrics only, no candle rows); written to the cache reports directory.</summary>
public static class E1ReportWriter
{
    public static string Write(E1Result result, string directory)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"m3-e1-subset-{stamp}.md");
        File.WriteAllText(path, Markdown(result), Encoding.UTF8);
        return path;
    }

    public static string Markdown(E1Result result)
    {
        var s = new StringBuilder();
        s.AppendLine($"# M3 E1 alt küme raporu ({E1PipelineOptions.PolicyVersion})");
        s.AppendLine();
        s.AppendLine($"- Snapshot hash: `{result.SnapshotHash}`");
        s.AppendLine($"- Durum: {(result.Complete ? $"{result.Selected.Count} E1 paritesi kalite şartlarını sağladı" : $"**Eksik:** {result.Selected.Count} parite; eşikler gevşetilmedi")}");
        s.AppendLine($"- Listelenen sembol dizini: {result.ListedSymbols}; E1 adayı: {result.Candidates.Count}");
        s.AppendLine("- Amaç: boru hattı doğrulaması (m3-protocol §11). Araştırma evreni değildir.");
        s.AppendLine();
        s.AppendLine("## Seçilen E1 pariteleri");
        s.AppendLine("| Sıra | Sembol | İlk açılış | U0 | Etiket | 1h eksik | 4h eksik | 1h/4h anomali |");
        s.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var a in result.Selected)
        {
            var c = a.Candidate;
            s.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {c.Rank} | {c.Symbol} | {c.FirstOpen:yyyy-MM-dd HH:mm} | {c.U0:yyyy-MM-dd HH:mm} | {PairCategories.EvidenceLabel(PairCategory.E1, c.FirstOpen)} | {a.Quality.OneHour?.Missing} | {a.Quality.FourHour?.Missing} | {a.Quality.Consistency?.Anomalies.Count} |"));
        }

        s.AppendLine();
        s.AppendLine("## Elenenler (yerine sıradaki geçti; alt kategoriye indirilmedi)");
        foreach (var a in result.Eliminated)
        {
            s.AppendLine($"- #{a.Candidate.Rank} {a.Candidate.Symbol}: {a.Quality.Reason}");
        }

        return s.ToString();
    }
}
