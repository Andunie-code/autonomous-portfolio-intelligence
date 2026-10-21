using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Evaluation;
using PortfolioIntelligence.Research.Families;
using PortfolioIntelligence.Research.Hypotheses;
using PortfolioIntelligence.Researcher;

/// <summary>
/// M4 Gate B: the pre-registered LLM-less research cycle (docs/m4-protocol.md v1) on the 20 core pairs. Loads and
/// verifies docs/m4-hypotheses.v1.json against the pre-registered list hash, runs every grid point through the
/// Researcher tools, gates each hypothesis and writes docs/m4-llmless-results.md. Local database (&lt; C) only; no
/// network and no AI call.
/// </summary>
internal static class ResearchM4
{
    private const string ProtocolVersion = "m4-protocol v1";
    private const string ProtocolCommit = "32cf7c5";
    internal const string ListHash = "89ab3965a8cbaf068788a95bc354f0340c29ae1b8d3834ef6c840bad1d4c2ec7";
    private const int GlobalTrialCap = 100;
    private const int DeflatedSharpeTrials = 98;

    public static async Task<int> RunAsync(string connectionString, string repositoryRoot, string engineSha)
    {
        var started = DateTimeOffset.UtcNow;
        var list = HypothesisList.Load(await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "docs", "m4-hypotheses.v1.json")), ListHash);
        Console.WriteLine($"Hypothesis list {list.Version}: {list.Hypotheses.Count} hypotheses, {list.TrialCount} points, list hash {list.ListHash[..12]} (verified).");

        var universe = CoreUniverse.LoadDefault();
        var store = new CandleStore(connectionString);
        var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
        var candles = new Dictionary<string, IReadOnlyList<Candle>>();
        foreach (var symbol in universe.Ranks.Keys.Order(StringComparer.Ordinal))
        {
            candles[symbol] = await store.ReadAsync(symbol, CandleInterval.FourHours, range, CancellationToken.None);
        }

        var input = new BacktestInput(candles, universe.Ranks, ResearchCutoff.U, ResearchCutoff.C, Reference: candles["BTCUSDT"]);
        var familyStore = new NpgsqlFamilyStore(connectionString);
        var trials = new TrialRegistry(familyStore, universe.SnapshotHash);
        var before = await trials.CountAsync(CancellationToken.None);
        Console.WriteLine($"Global trial counter before the run: {before}.");

        var audit = new AuditLog(connectionString);
        var settings = new ResearchCycleSettings(ProtocolVersion, ProtocolCommit, engineSha, universe.SnapshotHash, GlobalTrialCap, DeflatedSharpeTrials,
            new GateThresholds(100, 0.75, 0.80, 0.90));
        var backend = new ResearchToolBackend(input, settings, trials, new ExperimentStore(connectionString), new ExperimentRequestGuard(audit),
            new DatasetCatalog(connectionString), new HypothesisStore(connectionString));
        var safety = new InMemorySafetyLog();
        var provenance = new HypothesisProvenance("dev-assisted", "grid", "claude-opus-5-5 (Claude Code)", new DateOnly(2026, 6, 30), true,
            $"user (M4 Gate A, 2026-10-18, {ProtocolCommit})");
        await audit.RecordAsync(ResearchToolBackend.Actor, "research-cycle-started",
            new { protocol = ProtocolVersion, commit = ProtocolCommit, listHash = ListHash, engineSha, trialsBefore = before }, CancellationToken.None);

        var cycle = new ResearchCycle(new ResearcherToolbox(backend, safety), backend, safety);
        var report = await cycle.RunAsync(new ApprovedHypothesisSource(list, provenance), message =>
            Console.WriteLine($"[{(DateTimeOffset.UtcNow - started).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}] {message}"), CancellationToken.None);

        var after = await trials.CountAsync(CancellationToken.None);
        await audit.RecordAsync(ResearchToolBackend.Actor, "research-cycle-finished", new
        {
            protocol = ProtocolVersion, trialsAfter = after,
            candidates = report.Verdicts.Count(v => v.Category == HypothesisCategory.Candidate),
            safetyViolations = report.SafetyViolations.Count,
        }, CancellationToken.None);

        var years = (ResearchCutoff.C - ResearchCutoff.U).TotalDays / 365.25;
        var trading = candles.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value.Where(c => c.OpenTime >= ResearchCutoff.U).ToList());
        var benchmarks = new[]
        {
            Benchmarks.BuyAndHold("BTCUSDT", trading["BTCUSDT"], ResearchCutoff.U, ResearchCutoff.C, CostModel.V1, input.InitialCapital),
            Benchmarks.Basket("equal-weight-20", trading, ResearchCutoff.U, ResearchCutoff.C, CostModel.V1, input.InitialCapital),
        }.Select(b => (b.Strategy, Metric: ReturnStatistics.PrimaryMetric(b), Metrics: Metrics.Compute(b, years))).ToList();

        var duration = DateTimeOffset.UtcNow - started;
        var markdown = Markdown(report, list, engineSha, universe.SnapshotHash, before, after, benchmarks, duration);
        var output = Path.Combine(repositoryRoot, "docs", "m4-llmless-results.md");
        await File.WriteAllTextAsync(output, markdown, new UTF8Encoding(false));

        var reports = Directory.CreateDirectory(Path.Combine(ArchiveCacheDirectory.DefaultRoot, "reports")).FullName;
        var json = Path.Combine(reports, $"m4-llmless-{started.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.json");
        await File.WriteAllTextAsync(json, new JsonObject
        {
            ["protocol"] = ProtocolVersion, ["commit"] = ProtocolCommit, ["engineSha"] = engineSha, ["listHash"] = ListHash,
            ["trialsBefore"] = before, ["trialsAfter"] = after,
            ["verdicts"] = new JsonArray(report.Verdicts.Select(v => (JsonNode)new JsonObject
            {
                ["hypothesis"] = v.Key, ["category"] = v.Category.ToString(), ["selected"] = v.SelectedPoint,
                ["failures"] = new JsonArray(v.Failures.Select(f => (JsonNode)f).ToArray()), ["details"] = v.Details.DeepClone(),
            }).ToArray()),
            ["points"] = new JsonArray(report.Points.Select(p => (JsonNode)new JsonObject
            {
                ["hypothesis"] = p.HypothesisKey, ["point"] = p.Point, ["definitionHash"] = p.DefinitionHash,
                ["submission"] = p.Submission.DeepClone(), ["result"] = p.Result?.DeepClone(),
            }).ToArray()),
            ["safetyViolations"] = new JsonArray(report.SafetyViolations.Select(v => (JsonNode)v).ToArray()),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Report: {output}");
        Console.WriteLine($"Details: {json}");
        Console.WriteLine($"Global trial counter after the run: {after}.");
        var complete = report.Verdicts.Count >= 10 && report.Verdicts.All(v => v.Category != HypothesisCategory.Error) && report.SafetyViolations.Count == 0;
        return complete && after <= GlobalTrialCap ? 0 : 4;
    }

    private static string Markdown(CycleReport report, HypothesisList list, string engineSha, string snapshot, int before, int after,
        IReadOnlyList<(string Strategy, double Metric, PerformanceMetrics Metrics)> benchmarks, TimeSpan duration)
    {
        static string P(JsonNode? node) => node is null ? "—" : string.Create(CultureInfo.InvariantCulture, $"{node.GetValue<double>():P1}");
        static string F(JsonNode? node, string format = "0.00") => node is null ? "—" : node.GetValue<double>().ToString(format, CultureInfo.InvariantCulture);
        static string Gate(JsonNode? node) => node?.GetValue<bool>() == true ? "✓" : "✗";

        var candidates = report.Verdicts.Count(v => v.Category == HypothesisCategory.Candidate);
        var s = new StringBuilder();
        s.AppendLine("# M4 LLM'siz araştırma sonuçları (m4-protocol v1)");
        s.AppendLine();
        s.AppendLine(CultureInfo.InvariantCulture, $"> **Sonuç:** {report.Verdicts.Count} hipotez ve {report.Points.Count} grid noktası uçtan uca işlendi. {candidates} hipotez tüm araştırma kapılarını geçti{(candidates == 0 ? "; hiçbir aday yok" : " (aday; otomatik geçiş yok, m4-protocol §6)")}. Güvenlik ihlali: {report.SafetyViolations.Count}. AI çağrısı yapılmadı (0 USD).");
        s.AppendLine(">");
        s.AppendLine("> Bu, roadmap M4'teki boru hattı kriteridir (istatistiksel değildir). Koşu ön kayda göre bir kez yapıldı; sonuç olduğu gibi yazılır (m4-protocol §7).");
        s.AppendLine();
        s.AppendLine(CultureInfo.InvariantCulture, $"- Ön kayıt: `{ProtocolVersion}`, commit `{ProtocolCommit}`. Hipotez listesi `{list.Version}`, liste hash'i `{list.ListHash}` (yüklemede yeniden hesaplandı ve doğrulandı).");
        s.AppendLine(CultureInfo.InvariantCulture, $"- Engine: `{engineSha}`. Veri: `core-4y-S` (20 parite, snapshot `{snapshot[..12]}…`), 4h, R = [2022-01-01, 2026-01-01). Yerel dev DB; ağ erişimi yok.");
        s.AppendLine(CultureInfo.InvariantCulture, $"- Global deneme sayacı: koşudan önce {before}, sonra **{after}** (tavan {GlobalTrialCap}). G6'da N = {DeflatedSharpeTrials}.");
        s.AppendLine("- Kapılar `no-halt` koşusunda, v2 eşikleriyle: `N_min = 100`, `τ_wf = 0,75` (G3: 2023–2025 ankore genişleyen pencere), `τ_nb = 0,80`, `τ_dsr = 0,90`. Maliyet modeli v1, D25 hard stop. Bootstrap B = 10.000, seed 20260101.");
        s.AppendLine("- Köken: hipotez `dev-assisted` (Claude Opus 5.5, bilgi kesimi 2026-06; tanısal pencereyle çakışma bayrağı), grid noktaları `grid`.");
        s.AppendLine(CultureInfo.InvariantCulture, $"- Süre: {duration:hh\\:mm\\:ss}.");
        s.AppendLine();
        s.AppendLine("## Hipotez özeti (seçilen aday, `no-halt`)");
        s.AppendLine();
        s.AppendLine("| # | Hipotez | Seçilen nokta | M_R | İşlem | %95 alt sınır | G3 (ankore) | G4 komşuluk | DSR olasılığı | G1 | G2 | G3 | G4 | G5 | G6 | Sonuç |");
        s.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var v in report.Verdicts)
        {
            var d = v.Details;
            var n = d["noHalt"];
            var g = d["gates"];
            s.AppendLine(CultureInfo.InvariantCulture,
                $"| {v.Key.ToUpperInvariant()} | {v.Title} | `{v.SelectedPoint}` | {P(n?["primaryMetric"])} | {n?["trades"]} | {P(n?["lower95"])} | {F(n?["anchoredWalkForwardShare"])} | {F(n?["neighbourhoodShare"])} | {F(n?["deflatedSharpeProbability"], "0.000")} | {Gate(g?["G1"])} | {Gate(g?["G2"])} | {Gate(g?["G3"])} | {Gate(g?["G4"])} | {Gate(g?["G5"])} | {Gate(g?["G6"])} | {(v.Category == HypothesisCategory.Candidate ? "**Aday**" : v.Category == HypothesisCategory.Rejected ? "Reddedildi" : "Hata")} |");
        }

        s.AppendLine();
        s.AppendLine("## Betimleyici: seçilen adayın `policy` koşusu ve benchmark'lar (kapı değil)");
        s.AppendLine();
        s.AppendLine("| # | M_R (policy) | İşlem | Maks. DD (policy) | Halt | Maks. DD (no-halt) |");
        s.AppendLine("|---|---|---|---|---|---|");
        foreach (var v in report.Verdicts)
        {
            var policy = v.Details["policy"];
            s.AppendLine(CultureInfo.InvariantCulture,
                $"| {v.Key.ToUpperInvariant()} | {P(policy?["primaryMetric"])} | {policy?["trades"]} | {P(policy?["maxDrawdown"])} | {policy?["haltedAt"]?.GetValue<string>() ?? "—"} | {P(v.Details["noHalt"]?["maxDrawdown"])} |");
        }

        s.AppendLine();
        foreach (var (strategy, metric, metrics) in benchmarks)
        {
            s.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Benchmark `{strategy}`: M = {metric:P1}, toplam getiri {metrics.TotalReturn:P1}, maks. DD {metrics.MaxDrawdown:P1}."));
        }

        s.AppendLine("- Nakit (birincil benchmark): M = 0.");
        s.AppendLine();
        s.AppendLine("## Grid noktaları (`no-halt`, M_R)");
        s.AppendLine();
        foreach (var v in report.Verdicts)
        {
            var folds = string.Join("; ", (v.Details["noHalt"]?["folds"]?.AsArray() ?? []).Select(f => string.Create(CultureInfo.InvariantCulture,
                $"{f!["testYear"]}: seçim `{f["selected"]}` → test {P(f["testMetric"])}")));
            s.AppendLine(CultureInfo.InvariantCulture, $"**{v.Key.ToUpperInvariant()} — {v.Title}.** Ankore katmanlar: {folds}.");
            s.AppendLine();
            s.AppendLine("| Nokta | M_R | İşlem | Sonuç hash'i |");
            s.AppendLine("|---|---|---|---|");
            foreach (var point in v.Details["grid"]?.AsArray() ?? [])
            {
                s.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{point!["point"]}`{(point["point"]!.GetValue<string>() == v.SelectedPoint ? " ★" : "")} | {P(point["primaryMetric"])} | {point["trades"]} | `{point["resultHash"]!.GetValue<string>()[..12]}` |");
            }

            s.AppendLine();
        }

        s.AppendLine("## Kapıdan geçemeyenler");
        s.AppendLine();
        foreach (var v in report.Verdicts.Where(v => v.Failures.Count > 0))
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"- {v.Key.ToUpperInvariant()}: {string.Join(", ", v.Failures)}");
        }

        s.AppendLine();
        s.AppendLine("Tüm deneme ve sonuç kayıtları `research.trials`, `research.experiments` ve `research.hypotheses` tablolarında; ayrıntılı JSON, depo dışındaki rapor dizininde.");
        return s.ToString();
    }
}
