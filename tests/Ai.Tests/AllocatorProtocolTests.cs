using PortfolioIntelligence.Ai.Tests.Support;
using PortfolioIntelligence.Allocator;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>
/// The allocator's pre-registered documents and their link to the code (m8-protocol §8, v1.1): the pilot protocol
/// parses with this prompt, schema and band; a changed prompt, a qualification window outside 28–56 days or a different
/// margin is refused; the shadow evaluation keeps performance values out of the operational report and the ai schema.
/// </summary>
public class AllocatorProtocolTests
{
    private static string Document(string role = "pilot", int days = 14, string? promptHash = null, double margin = 0.03) => $$"""
        # test protocol

        ```json allocation-protocol
        {
          "task": "AllocationProposal",
          "provider": "gemini",
          "model": "gemini-3.5-flash-lite",
          "promptVersion": "{{AllocationPrompt.PromptVersion}}",
          "promptHash": "{{promptHash ?? AllocationPrompt.PromptHash}}",
          "schemaHash": "{{AllocationPrompt.Schema.Hash}}",
          "band": "{{AllocatorBand.Version}}",
          "design": "NonInferiority",
          "role": "{{role}}",
          "days": {{days}},
          "budgetUsd": 1.00,
          "alpha": 0.05,
          "marginAnnual": {{margin.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "seed": 83000
        }
        ```
        """;

    [Fact]
    public void The_committed_pilot_protocol_matches_this_code()
    {
        var path = Path.Combine(RepositoryRoot.Path, "docs", "m8-alloc-pilot-protocol.md");
        var protocol = AllocatorProtocol.Parse(File.ReadAllText(path));

        Assert.Equal(AllocatorProtocol.Pilot, protocol.Role);
        Assert.Equal(14, protocol.Days); // A-7
        Assert.Equal(1.00m, protocol.BudgetUsd); // U-M8-5
        Assert.Equal("gemini-3.5-flash-lite", protocol.Model);
        Assert.Equal(NonInferiority.AnnualMargin, protocol.MarginAnnual);

        // m8-protocol §8 (v1.1) carries the same prompt and schema hashes.
        var design = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "docs", "m8-protocol.md"));
        Assert.Contains(AllocationPrompt.PromptHash, design, StringComparison.Ordinal);
        Assert.Contains(AllocationPrompt.Schema.Hash, design, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_prompt_a_window_outside_28_to_56_days_or_another_margin_is_refused()
    {
        Assert.Equal(AllocatorProtocol.Qualification, AllocatorProtocol.Parse(Document("qualification", 28)).Role);
        Assert.Throws<FormatException>(() => AllocatorProtocol.Parse(Document(promptHash: new string('0', 64))));
        Assert.Throws<FormatException>(() => AllocatorProtocol.Parse(Document("qualification", 27)));
        Assert.Throws<FormatException>(() => AllocatorProtocol.Parse(Document("qualification", 57)));
        Assert.Throws<FormatException>(() => AllocatorProtocol.Parse(Document(margin: 0.05)));
        Assert.Throws<FormatException>(() => AllocatorProtocol.Parse(Document("final")));
        Assert.Throws<FormatException>(() => AllocatorProtocol.Parse("no block"));
    }

    [Fact]
    public void The_registered_form_has_distinct_window_hashes_and_the_non_inferiority_design()
    {
        var protocol = AllocatorProtocol.Parse(Document("qualification", 30));
        var registered = protocol.ToQualificationProtocol("abc");

        Assert.Equal(QualificationDesign.NonInferiority, registered.Design);
        Assert.Equal(30, registered.SamplePerArm);
        Assert.Equal(protocol.Hash, registered.ProtocolHash);
        Assert.NotEqual(registered.PilotSetHash, registered.QualificationSetHash);
        Assert.Equal(QualificationProtocol.HashDocument(Document("qualification", 30)), protocol.Hash);
    }

    [Fact]
    public void The_report_keeps_performance_out_of_the_operational_part_and_the_qualification_record()
    {
        var protocol = AllocatorProtocol.Parse(Document("qualification", 28));
        var start = new DateTimeOffset(2026, 12, 1, 20, 0, 0, TimeSpan.Zero);
        var values = Enumerable.Range(0, 30).SelectMany(d => new[] { 1L, 2L }.Select(run =>
            new ShadowValueRow(run, start.AddDays(d), 10_000m + 13.37m * d, 10_000m + 11.11m * d))).ToList();
        var decisions = Enumerable.Range(0, 20).Select(i => new ShadowDecisionRow(1, start.AddHours(4 * i), i < 18 ? AllocationOutcome.Proposal : AllocationOutcome.Schema, 0, 1, 1, 2)).ToList();

        var report = ShadowEvaluation.Report(protocol, decisions, values, 0.012345m);

        Assert.Contains("calls=20 valid_proposals=18 valid_rate=0.900 safety_events=0", report.Operational, StringComparison.Ordinal);
        Assert.Contains("days=29", report.Operational, StringComparison.Ordinal);
        Assert.DoesNotContain("sigma", report.Operational, StringComparison.Ordinal);
        Assert.DoesNotContain("drawdown", report.Operational, StringComparison.Ordinal);
        Assert.DoesNotContain("mean", report.Operational, StringComparison.Ordinal);
        Assert.Contains("sigma_d=", report.UserOnly, StringComparison.Ordinal);

        var (result, record) = ShadowEvaluation.Qualify(protocol, decisions, values, new UsdAmount(0.012345m), start.AddDays(31));
        Assert.Equal(QualificationVerdict.Passed, result.Verdict); // the shadow gains every day and never draws down
        Assert.True(double.IsNaN(record.PValue));
        Assert.Equal(20, record.TrialsLlm);
        Assert.Equal(18, record.SuccessesLlm);
        Assert.Equal(29, record.TrialsBaseline);
        Assert.Equal("non-inferior", record.Notes);
        Assert.Throws<InvalidOperationException>(() => ShadowEvaluation.Qualify(AllocatorProtocol.Parse(Document()), decisions, values, UsdAmount.Zero, start));
    }

    [Fact]
    public void The_pilot_report_gives_the_window_length_by_the_rule()
    {
        var protocol = AllocatorProtocol.Parse(Document());
        var start = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
        var values = Enumerable.Range(0, 15).Select(d => new ShadowValueRow(1, start.AddDays(d), 10_000m, 10_000m)).ToList();

        var report = ShadowEvaluation.Report(protocol, [], values, 0m);

        Assert.Contains("qualification_window_days=28 underpowered=false", report.Operational, StringComparison.Ordinal);
    }
}
