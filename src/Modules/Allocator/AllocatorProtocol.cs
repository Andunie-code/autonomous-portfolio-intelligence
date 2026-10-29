using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Allocator;

/// <summary>
/// A shadow-mode protocol document (m8-protocol §6, §8): its <c>```json allocation-protocol</c> block names the role
/// (pilot or qualification window), the model, the prompt and schema hashes, the window length, budget, margin and the
/// bootstrap seed. The document hash is the protocol hash; every shadow step and decision of the window carries it.
/// </summary>
public sealed record AllocatorProtocol(
    string Hash,
    string Role,
    string Provider,
    string Model,
    int Days,
    decimal BudgetUsd,
    double Alpha,
    double MarginAnnual,
    int Seed,
    bool Underpowered = false)
{
    public const string Pilot = "pilot";

    public const string Qualification = "qualification";

    private static readonly Regex Block = new("```json allocation-protocol\\s*\\n(?<json>.*?)\\n```", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses and checks a document: the task, design, prompt version, prompt hash and schema hash must be this code's
    /// (a change is a new protocol); the margin and α are the pre-registered ones (A-1); a qualification window lasts 28–56 days (A-4).
    /// </summary>
    public static AllocatorProtocol Parse(string document)
    {
        var match = Block.Match(document.ReplaceLineEndings("\n"));
        if (!match.Success)
        {
            throw new FormatException("No ```json allocation-protocol block in the document.");
        }

        var spec = JsonNode.Parse(match.Groups["json"].Value)!.AsObject();
        string Text(string name) => spec[name]?.GetValue<string>() ?? throw new FormatException($"allocation-protocol: {name} is missing.");
        void Expect(string name, string value)
        {
            if (Text(name) != value)
            {
                throw new FormatException($"allocation-protocol: {name} must be '{value}' (this code), not '{Text(name)}'.");
            }
        }

        Expect("task", nameof(TaskClass.AllocationProposal));
        Expect("design", nameof(QualificationDesign.NonInferiority));
        Expect("promptVersion", AllocationPrompt.PromptVersion);
        Expect("promptHash", AllocationPrompt.PromptHash);
        Expect("schemaHash", AllocationPrompt.Schema.Hash);
        Expect("band", Kernel.Backtest.AllocatorBand.Version);
        var role = Text("role");
        var days = spec["days"]!.GetValue<int>();
        var alpha = spec["alpha"]!.GetValue<double>();
        var margin = spec["marginAnnual"]!.GetValue<double>();
        if (role is not (Pilot or Qualification))
        {
            throw new FormatException("allocation-protocol: role must be pilot or qualification.");
        }

        if (role == Qualification && days is < NonInferiority.MinimumDays or > NonInferiority.MaximumDays)
        {
            throw new FormatException("allocation-protocol: a qualification window lasts 28 to 56 days (A-4).");
        }

        if (alpha != NonInferiority.Alpha || margin != NonInferiority.AnnualMargin)
        {
            throw new FormatException("allocation-protocol: alpha and marginAnnual are pre-registered (0.05 and 0.03, A-1).");
        }

        return new AllocatorProtocol(QualificationProtocol.HashDocument(document), role, Text("provider"), Text("model"), days,
            spec["budgetUsd"]!.GetValue<decimal>(), alpha, margin, spec["seed"]!.GetValue<int>(), spec["underpowered"]?.GetValue<bool>() ?? false);
    }

    /// <summary>
    /// The registered form. Pilot and qualification "sets" are time windows: their hashes name the window of this
    /// document, so they are distinct from every other protocol's (testing §6.1: pilot and qualification sets differ).
    /// </summary>
    public QualificationProtocol ToQualificationProtocol(string documentCommit) => new(
        Hash, documentCommit, TaskClass.AllocationProposal, Provider, Model, AllocationPrompt.PromptVersion, AllocationPrompt.Schema.Hash,
        DslCanonical.Sha256($"allocator-window|pilot|{Hash}"), DslCanonical.Sha256($"allocator-window|qualification|{Hash}"),
        Alpha, MarginAnnual, Days, BudgetUsd, QualificationDesign.NonInferiority);

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Role} window, {Days} days, budget {BudgetUsd} USD, model {Model}, protocol {Hash[..12]}…");
}
