using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Ai.Gemini;
using PortfolioIntelligence.Ai.Persistence;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.Research.Qualification;

/// <summary>
/// HypothesisGeneration pilot and qualification (docs/m4-hg-qualification-design.md). Synthetic items only; nothing is
/// written to research.trials. The pilot never records a TaskQualification; the qualification runs only through the
/// harness with a registered protocol whose document is committed unchanged.
/// </summary>
internal static class HgCommands
{
    public const int PilotSeedBase = 30_000;
    public const int PilotItems = 20;
    public const decimal PilotBudgetUsd = 0.50m;
    public const double MinimumEffect = 0.15;
    public const double Alpha = 0.05;
    public const double TargetPower = 0.80;
    public static readonly int[] SampleCandidates = [.. Enumerable.Range(2, 19).Select(i => i * 10)]; // 20, 30, …, 200

    /// <summary>
    /// Pilot settings of a design document: its <c>```json hg-pilot</c> block (variant, model, seed base, count, budget), or
    /// the v1 defaults for the v1 design, which has no block. The pilot runs exactly what the committed design states.
    /// </summary>
    public sealed record PilotSpec(int Variant, string? Model, int SeedBase, int Count, decimal BudgetUsd);

    public static async Task<PilotSpec> ReadPilotSpecAsync(string designPath)
    {
        var design = await File.ReadAllTextAsync(designPath);
        var match = Regex.Match(design, "```json hg-pilot\\s*\\n(?<json>.*?)\\n```", RegexOptions.Singleline);
        if (!match.Success)
        {
            return new PilotSpec(1, null, PilotSeedBase, PilotItems, PilotBudgetUsd);
        }

        var spec = JsonNode.Parse(match.Groups["json"].Value)!;
        return new PilotSpec(spec["variant"]!.GetValue<int>(), spec["model"]!.GetValue<string>(), spec["pilot"]!["seedBase"]!.GetValue<int>(),
            spec["pilot"]!["count"]!.GetValue<int>(), spec["budgetUsd"]!.GetValue<decimal>());
    }

    /// <summary>Pilot (design §5): runs the pilot set in qualification mode under a transient pilot session; prints rates and the power table.</summary>
    public static async Task<int> PilotAsync(AiConfiguration configuration, string connection, string apiKey, string model, string designPath, string reports, TimeProvider time)
    {
        var design = await File.ReadAllTextAsync(designPath);
        var hash = QualificationProtocol.HashDocument(design);
        var pilot = await ReadPilotSpecAsync(designPath);
        var (promptVersion, schema) = HypothesisGenerationTask.Prompt(pilot.Variant);
        var items = HgItem.Range(pilot.SeedBase, pilot.Count, pilot.Variant).Select(i => i.ToQualificationItem()).ToList();
        var protocol = new QualificationProtocol(hash, "pilot", TaskClass.HypothesisGeneration, GeminiProvider.ProviderName, model, promptVersion,
            schema.Hash, QualificationItem.SetHash(items), QualificationItem.SetHash([]), Alpha, MinimumEffect, pilot.Count, pilot.BudgetUsd);
        var (router, ledger, provider) = Wire(configuration, connection, apiKey, time);
        using var _ = provider;
        var job = new JobContext($"hg-pilot-{hash[..12]}", JobKind.Qualification);
        var session = new QualificationSession(protocol, job, router);
        var task = new HypothesisGenerationTask(time, variant: pilot.Variant);
        var started = time.GetUtcNow();
        foreach (var item in items)
        {
            if (await router.KillSwitch.IsTrippedAsync(CancellationToken.None))
            {
                Console.WriteLine("Stopped: AI kill switch is on.");
                break;
            }

            if ((await ledger.SummaryAsync(time.GetUtcNow(), job.JobId, TimeSpan.Zero, CancellationToken.None)).Job.Value >= pilot.BudgetUsd)
            {
                Console.WriteLine($"Stopped: pilot budget {pilot.BudgetUsd} USD reached.");
                break;
            }

            var llm = await task.RunLlmAsync(session, item, CancellationToken.None);
            var baseline = await task.RunBaselineAsync(item, CancellationToken.None);
            Console.WriteLine($"[{(time.GetUtcNow() - started).TotalSeconds:0}s] {item.Id}: llm {(llm.Success ? "PASS" : "fail")}, baseline {(baseline.Success ? "PASS" : "fail")}" +
                $"{(llm.SafetyViolations.Count > 0 ? " SAFETY: " + string.Join("; ", llm.SafetyViolations) : "")} — {Reason(task.Records, item.Id)}");
        }

        var summary = Summarize(task.Records);
        var cost = (await ledger.SummaryAsync(time.GetUtcNow(), job.JobId, TimeSpan.Zero, CancellationToken.None)).Job;
        var p0 = summary.BaselineRate;
        var p1 = Math.Min(1, p0 + MinimumEffect);
        var table = SampleCandidates.Select(n => (n, Power: FisherExact.Power(n, p1, p0, Alpha))).ToList();
        var required = FisherExact.RequiredPerArm(p1, p0, Alpha, TargetPower, SampleCandidates);
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Pilot ({summary.Items} items): LLM {summary.LlmSuccesses}/{summary.Items} ({summary.LlmRate:P1}), baseline {summary.BaselineSuccesses}/{summary.Items} ({p0:P1}); " +
            $"invalid DSL {summary.InvalidDefinitions}, LLM failures without a definition {summary.LlmWithoutDefinition}, duplicates {summary.Duplicates}, safety violations {summary.SafetyViolations}; cost {cost}."));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Power (one-sided Fisher, α = {Alpha}, baseline rate {p0:0.000}, LLM rate baseline + {MinimumEffect}): ") +
            string.Join(", ", table.Select(t => string.Create(CultureInfo.InvariantCulture, $"n={t.n}: {t.Power:0.00}"))));
        Console.WriteLine(required is { } n ? $"Required sample per arm for power ≥ {TargetPower}: {n}." : $"No sample up to {SampleCandidates.Max()} per arm reaches power {TargetPower}.");

        var path = Path.Combine(reports, $"{job.JobId}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            job = job.JobId, designHash = hash, model, variant = pilot.Variant, promptVersion, schemaHash = schema.Hash,
            pilotSetHash = protocol.PilotSetHash, summary, cost = cost.Value, power = table.Select(t => new { t.n, t.Power }), requiredPerArm = required,
            records = task.Records,
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Details: {path}");
        return 0;
    }

    /// <summary>
    /// Baseline arm only (no network, no database, no model): the deterministic grid's success rate on synthetic items, to
    /// check a design's baseline band on calibration seeds that are never used for a pilot or a qualification set.
    /// </summary>
    public static async Task<int> BaselineAsync(int variant, int seedBase, int count, string reports)
    {
        var task = new HypothesisGenerationTask(TimeProvider.System, variant: variant);
        var started = DateTimeOffset.UtcNow;
        foreach (var item in HgItem.Range(seedBase, count, variant).Select(i => i.ToQualificationItem()))
        {
            var outcome = await task.RunBaselineAsync(item, CancellationToken.None);
            var record = task.Records[^1];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{(DateTimeOffset.UtcNow - started).TotalSeconds:0}s] {item.Id}: {(outcome.Success ? "PASS" : "fail")} M={record.EvaluationMetric:0.000} {string.Join(", ", record.GateFailures)}"));
        }

        var successes = task.Records.Count(r => r.Success);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Baseline (variant {variant}, seeds {seedBase}–{seedBase + count - 1}): {successes}/{count} ({(double)successes / count:P1})."));
        var path = Path.Combine(reports, $"hg-baseline-v{variant}-{seedBase}-{count}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { variant, seedBase, count, successes, records = task.Records }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Details: {path}");
        return 0;
    }

    /// <summary>Decision traces of one job (read-only): outcome, reason and validator results; no prompt or output text is stored.</summary>
    public static async Task<int> TracesAsync(string connection, string jobId)
    {
        await using var db = new Npgsql.NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT at, outcome, degraded, calls, input_tokens, output_tokens, actual_usd, reason, validator_results::text, safety_violations::text
            FROM ai.decision_traces WHERE job_id = $1 ORDER BY at
            """, db);
        command.Parameters.AddWithValue(jobId);
        await using var reader = await command.ExecuteReaderAsync();
        var i = 0;
        while (await reader.ReadAsync())
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{++i,3} {reader.GetFieldValue<DateTimeOffset>(0):HH:mm:ss} {reader.GetString(1)}{(reader.GetBoolean(2) ? " DEGRADED" : "")} calls={reader.GetInt32(3)} tokens={reader.GetInt32(4)}/{reader.GetInt32(5)} " +
                $"usd={reader.GetDecimal(6):0.000000} reason={(reader.IsDBNull(7) ? "-" : reader.GetString(7))} validators={reader.GetString(8)} safety={reader.GetString(9)}"));
        }

        Console.WriteLine($"{i} trace(s) for job {jobId}.");
        return 0;
    }

    /// <summary>Registers the protocol of a committed, unchanged protocol document (no network).</summary>
    public static async Task<int> RegisterAsync(string connection, string protocolPath)
    {
        var (document, spec) = await LoadAsync(protocolPath);
        var commit = CommittedUnchanged(protocolPath);
        var protocol = IsFindingExtraction(spec) ? FeCommands.ToProtocol(document, spec, commit) : ToProtocol(document, spec, commit);
        await new NpgsqlQualificationStore(connection).RegisterProtocolAsync(protocol, CancellationToken.None);
        await new NpgsqlAiAudit(connection).RecordAsync("user:" + Environment.UserName, "qualification-protocol-registered",
            new { protocol.ProtocolHash, protocol.DocumentCommit, protocol.Task, protocol.Model, protocol.SamplePerArm, Design = protocol.Design.ToString(), protocol.P0 }, CancellationToken.None);
        Console.WriteLine($"Registered {protocol.Task} protocol {protocol.ProtocolHash[..12]} (commit {commit[..7]}): model {protocol.Model}, " +
            $"{protocol.SamplePerArm} items per arm, α {protocol.Alpha}, minimum effect {protocol.MinimumEffect}, budget {protocol.BudgetUsd} USD.");
        return 0;
    }

    /// <summary>The qualification run: only through the harness, which refuses an unregistered or edited document.</summary>
    public static async Task<int> QualifyAsync(AiConfiguration configuration, string connection, string apiKey, string protocolPath, string reports, TimeProvider time)
    {
        var (document, spec) = await LoadAsync(protocolPath);
        CommittedUnchanged(protocolPath);
        if (IsFindingExtraction(spec))
        {
            return await FeCommands.QualifyAsync(configuration, connection, apiKey, document, spec, reports, time);
        }

        var (router, ledger, provider) = Wire(configuration, connection, apiKey, time);
        using var _ = provider;
        var task = new HypothesisGenerationTask(time, variant: Variant(spec));
        var harness = new QualificationHarness(new NpgsqlQualificationStore(connection), router, ledger, time);
        var result = await harness.RunAsync(document, Items(spec["pilot"]!, Variant(spec)), Items(spec["qualification"]!, Variant(spec)), task, CancellationToken.None);
        var summary = Summarize(task.Records);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TaskQualification {result.Id}: {result.Verdict} — LLM {result.SuccessesLlm}/{result.TrialsLlm}, baseline {result.SuccessesBaseline}/{result.TrialsBaseline}, " +
            $"one-sided Fisher p = {result.PValue:0.0000}, safety violations {result.SafetyViolations}, cost {result.Cost}. {result.Notes}"));
        var path = Path.Combine(reports, $"hg-qualification-{result.ProtocolHash[..12]}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { result, summary, records = task.Records }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Details: {path}");
        return 0;
    }

    public sealed record Summary(int Items, int LlmSuccesses, int BaselineSuccesses, int InvalidDefinitions, int LlmWithoutDefinition, int Duplicates, int SafetyViolations)
    {
        public double LlmRate => Items == 0 ? 0 : (double)LlmSuccesses / Items;

        public double BaselineRate => Items == 0 ? 0 : (double)BaselineSuccesses / Items;
    }

    public static Summary Summarize(IReadOnlyList<HgArmRecord> records)
    {
        var llm = records.Where(r => r.Arm == "llm").ToList();
        var baseline = records.Where(r => r.Arm == "baseline").ToList();
        return new Summary(llm.Count, llm.Count(r => r.Success), baseline.Count(r => r.Success),
            llm.Count(r => r.Reason?.StartsWith("invalid DSL", StringComparison.Ordinal) == true),
            llm.Count(r => r.DefinitionHash is null), llm.Count(r => r.DuplicatesExploration), records.Sum(r => r.SafetyViolations.Count));
    }

    private static string Reason(IReadOnlyList<HgArmRecord> records, string itemId)
    {
        var llm = records.Last(r => r.ItemId == itemId && r.Arm == "llm");
        return llm.Reason ?? (llm.GateFailures.Count == 0 ? "llm passed all gates" : "llm gates: " + string.Join(", ", llm.GateFailures));
    }

    private static bool IsFindingExtraction(JsonObject spec) => spec["task"]?.GetValue<string>() == nameof(TaskClass.FindingExtraction);

    internal static (ModelRouter Router, ICostLedger Ledger, GeminiProvider Provider) Wire(AiConfiguration configuration, string connection, string apiKey, TimeProvider time)
    {
        var ledger = new NpgsqlCostLedger(connection);
        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(connection), new NpgsqlAiAudit(connection), time);
        var provider = new GeminiProvider(apiKey, configuration.Prices, GeminiGuardHandler.CreateNetworkHandler());
        var guard = new AiCallGuard(provider, configuration, ledger, killSwitch, time);
        var router = new ModelRouter(configuration, guard, new NpgsqlDecisionTraceStore(connection), new QualificationRegistry(new NpgsqlQualificationStore(connection)), time);
        return (router, ledger, provider);
    }

    /// <summary>The protocol document and its machine-readable block (<c>```json qualification-protocol</c>).</summary>
    private static async Task<(string Document, JsonObject Spec)> LoadAsync(string path)
    {
        var document = await File.ReadAllTextAsync(path);
        var match = Regex.Match(document, "```json qualification-protocol\\s*\\n(?<json>.*?)\\n```", RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidDataException("No ```json qualification-protocol block in the protocol document.");
        }

        return (document, JsonNode.Parse(match.Groups["json"].Value)!.AsObject());
    }

    private static QualificationProtocol ToProtocol(string document, JsonObject spec, string commit)
    {
        var (promptVersion, schema) = HypothesisGenerationTask.Prompt(Variant(spec));
        if (spec["promptVersion"]!.GetValue<string>() != promptVersion || spec["schemaHash"]!.GetValue<string>() != schema.Hash)
        {
            throw new InvalidDataException("Prompt version or schema hash in the protocol differs from the code; a change needs a new protocol.");
        }

        return new QualificationProtocol(QualificationProtocol.HashDocument(document), commit, Enum.Parse<TaskClass>(spec["task"]!.GetValue<string>()),
            spec["provider"]!.GetValue<string>(), spec["model"]!.GetValue<string>(), spec["promptVersion"]!.GetValue<string>(), spec["schemaHash"]!.GetValue<string>(),
            QualificationItem.SetHash(Items(spec["pilot"]!, Variant(spec))), QualificationItem.SetHash(Items(spec["qualification"]!, Variant(spec))),
            spec["alpha"]!.GetValue<double>(), spec["minimumEffect"]!.GetValue<double>(), spec["samplePerArm"]!.GetValue<int>(), spec["budgetUsd"]!.GetValue<decimal>());
    }

    private static IReadOnlyList<QualificationItem> Items(JsonNode set, int variant) =>
        HgItem.Range(set["seedBase"]!.GetValue<int>(), set["count"]!.GetValue<int>(), variant).Select(i => i.ToQualificationItem()).ToList();

    /// <summary>The protocol's item and prompt variant (absent in the v1 protocol: 1).</summary>
    private static int Variant(JsonObject spec) => spec["variant"]?.GetValue<int>() ?? 1;

    /// <summary>The document must be committed and unchanged since (T8: the protocol is fixed before the run). Returns the commit.</summary>
    internal static string CommittedUnchanged(string path)
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path)).Replace('\\', '/');
        var status = Git("status", "--porcelain", "--", relative);
        if (status.Length > 0)
        {
            throw new InvalidOperationException($"{relative} has uncommitted changes; commit the protocol before registering or running it.");
        }

        var commit = Git("log", "-1", "--format=%H", "--", relative);
        return commit.Length == 40 ? commit : throw new InvalidOperationException($"{relative} is not committed.");
    }

    private static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}
