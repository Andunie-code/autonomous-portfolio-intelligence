using PortfolioIntelligence.Ai.Tests.Support;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>Pre-registration harness (T8, testing §6.1) and the monthly reconciliation report (ADR-0010).</summary>
public class QualificationAndReconciliationTests
{
    private const string Document = "# HypothesisGeneration qualification protocol (test)\nα = 0.05\n";

    private static readonly IReadOnlyList<QualificationItem> Pilot = [new("p1", "pilot one"), new("p2", "pilot two")];

    private static readonly IReadOnlyList<QualificationItem> Set = Enumerable.Range(1, 6).Select(i => new QualificationItem($"q{i}", $"item {i}")).ToList();

    [Fact]
    public async Task Harness_refuses_to_run_without_a_registered_protocol_hash()
    {
        var rig = new RouterRig();
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(Document, Pilot, Set, new ScriptedTask([]), default));
        await RegisterAsync(rig);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(Document + "edited after registration", Pilot, Set, new ScriptedTask([]), default));
        Assert.Empty(rig.Qualifications.Results);
    }

    [Fact]
    public async Task Harness_refuses_changed_or_overlapping_sets()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig);
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(Document, Pilot, Set.Take(5).ToList(), new ScriptedTask([]), default));

        var overlapping = new List<QualificationItem>(Pilot) { new("q1", "item 1") };
        var protocol = await rig.Qualifications.FindProtocolAsync(QualificationProtocol.HashDocument(Document), default);
        var other = new RouterRig();
        await other.Qualifications.RegisterProtocolAsync(protocol! with { PilotSetHash = QualificationItem.SetHash(overlapping) }, default);
        var otherHarness = new QualificationHarness(other.Qualifications, other.Router, other.Ledger, other.Time);
        await Assert.ThrowsAsync<InvalidOperationException>(() => otherHarness.RunAsync(Document, overlapping, Set, new ScriptedTask([]), default));
    }

    [Fact]
    public async Task Full_sample_with_a_clear_advantage_passes_and_is_recorded()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig, sample: 6, alpha: 0.05);
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);

        var result = await harness.RunAsync(Document, Pilot, Set, new ScriptedTask(Enumerable.Repeat((true, false), 6).ToList()), default);

        Assert.Equal(QualificationVerdict.Passed, result.Verdict);
        Assert.Equal(6, result.TrialsLlm);
        Assert.True(result.PValue < 0.05);
        Assert.Equal(QualificationProtocol.HashDocument(Document), rig.Qualifications.Results.Single().ProtocolHash);
        Assert.Equal(result.Id, rig.Qualifications.Results.Single().Id);
    }

    [Fact]
    public async Task No_advantage_fails_and_a_failure_is_recorded_too()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig, sample: 6);
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);

        var result = await harness.RunAsync(Document, Pilot, Set, new ScriptedTask(Enumerable.Repeat((false, false), 6).ToList()), default);

        Assert.Equal(QualificationVerdict.Failed, result.Verdict);
        Assert.Single(rig.Qualifications.Results);
    }

    [Fact]
    public async Task A_single_safety_violation_fails_the_qualification()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig, sample: 6);
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);
        var outcomes = Enumerable.Repeat((true, false), 6).ToList();

        var result = await harness.RunAsync(Document, Pilot, Set, new ScriptedTask(outcomes, violationAt: 2), default);

        Assert.Equal(QualificationVerdict.Failed, result.Verdict);
        Assert.Equal(1, result.SafetyViolations);
    }

    [Fact]
    public async Task Budget_exhausted_before_the_sample_is_inconclusive()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig, sample: 6, budget: 0.01m);
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);
        await rig.SpendAsync(0.01m, "qualification-" + QualificationProtocol.HashDocument(Document)[..12], JobKind.Qualification);

        var result = await harness.RunAsync(Document, Pilot, Set, new ScriptedTask(Enumerable.Repeat((true, false), 6).ToList()), default);

        Assert.Equal(QualificationVerdict.Inconclusive, result.Verdict);
        Assert.Equal(0, result.TrialsLlm);
        Assert.Contains("budget", result.Notes);
    }

    [Fact]
    public async Task Kill_switch_stops_a_qualification_run_as_inconclusive()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig, sample: 6);
        await rig.KillSwitch.TripAsync(KillSwitchTrigger.Manual, "user:test", "stop", default);
        var harness = new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time);

        var result = await harness.RunAsync(Document, Pilot, Set, new ScriptedTask(Enumerable.Repeat((true, false), 6).ToList()), default);

        Assert.Equal(QualificationVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public async Task Qualification_mode_runs_without_a_qualified_model_and_never_affects_decisions()
    {
        var rig = new RouterRig();
        await RegisterAsync(rig);
        var protocol = (await rig.Qualifications.FindProtocolAsync(QualificationProtocol.HashDocument(Document), default))!;
        rig.Provider.Returns(RouterRig.Answer());

        var result = await rig.Router.ExecuteForQualificationAsync(
            new QualificationSession(protocol, new JobContext("qualification-x", JobKind.Qualification), rig.Router), TestConfig.Input(), default);

        Assert.Equal(TaskOutcome.Completed, result.Outcome);
        Assert.Equal(RouterMode.Qualification, result.Mode);
        Assert.False(result.MayAffectDecisions);
        Assert.Equal(protocol.ProtocolHash, rig.Traces.Traces.Single().QualificationProtocolHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Router.ExecuteForQualificationAsync(
            new QualificationSession(protocol, TestConfig.Research, rig.Router), TestConfig.Input(), default));
    }

    [Fact]
    public void Reconciliation_reports_totals_and_flags_a_difference_above_ten_percent()
    {
        var at = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        var call = Guid.NewGuid();
        var open = Guid.NewGuid();
        var entries = new List<LedgerEntry>
        {
            new(at, call, "job-a", JobKind.Spike, TaskClass.Summary, LedgerEntryKind.Reservation, new UsdAmount(1.0m), "gemini", "m", 0, 0, "p"),
            new(at, call, "job-a", JobKind.Spike, TaskClass.Summary, LedgerEntryKind.Settlement, new UsdAmount(0.40m), "gemini", "m", 1000, 100, "p"),
            new(at.AddDays(1), open, "job-b", JobKind.ResearchCycle, null, LedgerEntryKind.Reservation, new UsdAmount(0.10m), "gemini", "m", 0, 0, "p"),
            new(at.AddMonths(1), Guid.NewGuid(), "job-c", JobKind.Spike, null, LedgerEntryKind.Reservation, new UsdAmount(5m), "gemini", "m", 0, 0, "p"),
        };

        var within = ReconciliationReport.Build(entries, 2026, 10, "p", new InvoiceInput(20.0m, 40m)); // 0.50 USD
        var off = ReconciliationReport.Build(entries, 2026, 10, "p", new InvoiceInput(24.0m, 40m)); // 0.60 USD

        Assert.Equal(new UsdAmount(0.50m), within.LedgerTotal);
        Assert.Equal(2, within.Calls);
        Assert.Equal(0m, within.Difference);
        Assert.False(within.PriceTableReviewNeeded);
        Assert.True(off.PriceTableReviewNeeded);
        Assert.Contains("%10'u aşıyor", off.ToMarkdown());
        Assert.Contains("job-b", within.ToMarkdown());
    }

    private static Task RegisterAsync(RouterRig rig, int sample = 6, double alpha = 0.05, decimal budget = 3m) =>
        rig.Qualifications.RegisterProtocolAsync(new QualificationProtocol(QualificationProtocol.HashDocument(Document), "abc1234",
            TaskClass.HypothesisGeneration, "gemini", TestConfig.Model, "hg v1", TestConfig.AnswerSchema.Hash,
            QualificationItem.SetHash(Pilot), QualificationItem.SetHash(Set), alpha, 0.2, sample, budget), default);

    private sealed class ScriptedTask(IReadOnlyList<(bool Llm, bool Baseline)> outcomes, int violationAt = -1) : IQualificationTask
    {
        private int _llm;
        private int _baseline;

        public TaskClass Task => TaskClass.HypothesisGeneration;

        public Task<ArmOutcome> RunLlmAsync(QualificationSession session, QualificationItem item, CancellationToken ct)
        {
            var i = _llm++;
            return System.Threading.Tasks.Task.FromResult(new ArmOutcome(outcomes[i].Llm, i == violationAt ? ["disallowed tool call 'shell'"] : []));
        }

        public Task<ArmOutcome> RunBaselineAsync(QualificationItem item, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new ArmOutcome(outcomes[_baseline++].Baseline, []));
    }
}
