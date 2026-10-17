using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioIntelligence.Ai;

/// <summary>A model a task may use: provider, model name, prompt and output-schema version (T24: never hard-coded).</summary>
public sealed record ModelChoice(string Provider, string Model, string PromptVersion, IReadOnlyList<string> AcceptedModelVersions)
{
    /// <summary>Whether the model version a response reports counts as the requested model (else the result is degraded).</summary>
    public bool Accepts(string actualModel) => AcceptedModelVersions.Contains(actualModel, StringComparer.Ordinal);
}

/// <summary>A qualified model of a decision-affecting task: valid only while its qualification record matches (testing §6.1 step 5).</summary>
public sealed record QualifiedModel(string Provider, string Model, string PromptVersion, long QualificationId, IReadOnlyList<string> AcceptedModelVersions)
{
    public ModelChoice Choice => new(Provider, Model, PromptVersion, AcceptedModelVersions);
}

public sealed record TaskPolicy(
    TaskKind Kind,
    UnavailableBehaviour OnUnavailable,
    int MaxOutputTokens,
    int TimeoutSeconds,
    IReadOnlyList<QualifiedModel>? QualifiedModels = null,
    ModelChoice? Model = null)
{
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

public sealed record SpendSettings(
    decimal MonthlyCapUsd,
    decimal CallCapUsd,
    IReadOnlyDictionary<JobKind, decimal> JobShares,
    int VelocityWindowMinutes,
    decimal VelocityShare,
    decimal CostDeviationFactor,
    int CostDeviationConsecutive)
{
    public UsdAmount MonthlyCap => new(MonthlyCapUsd);

    public UsdAmount CallCap => new(CallCapUsd);

    public UsdAmount JobCap(JobKind kind) => new(MonthlyCapUsd * JobShares[kind]);

    public UsdAmount VelocityCap => new(MonthlyCapUsd * VelocityShare);

    public TimeSpan VelocityWindow => TimeSpan.FromMinutes(VelocityWindowMinutes);
}

public sealed record ResilienceSettings(
    int Concurrency,
    int MaxRetries,
    double BackoffBaseSeconds,
    double MaxRetryAfterSeconds,
    int CircuitBreakerFailures,
    double KillSwitchPollSeconds,
    int MaxToolRounds);

public sealed record ModelPrice(string Provider, string Model, decimal InputPerMillionUsd, decimal OutputPerMillionUsd);

/// <summary>Versioned list-price table (ADR-0010; values from sources.md S-G2). Thinking tokens are billed as output.</summary>
public sealed record PriceTable(string Version, IReadOnlyList<ModelPrice> Models)
{
    public ModelPrice For(string provider, string model) =>
        Models.SingleOrDefault(m => m.Provider == provider && m.Model == model)
        ?? throw new InvalidOperationException($"No list price for {provider}/{model} in {Version}; no call is made without a price.");

    public UsdAmount Cost(string provider, string model, AiUsage usage)
    {
        var price = For(provider, model);
        return new UsdAmount((usage.InputTokens * price.InputPerMillionUsd + usage.OutputTokens * price.OutputPerMillionUsd) / 1_000_000m);
    }
}

/// <summary>AI configuration (ai.v1.json): spend caps, resilience values, prices and per-task policies.</summary>
public sealed record AiConfiguration(
    string Version,
    SpendSettings Spend,
    ResilienceSettings Resilience,
    PriceTable Prices,
    IReadOnlyDictionary<TaskClass, TaskPolicy> Tasks)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static AiConfiguration LoadDefault()
    {
        var assembly = typeof(AiConfiguration).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Config.ai.v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static AiConfiguration Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<AiConfiguration>(json, Options)
            ?? throw new InvalidOperationException("Empty AI configuration.");
        configuration.Check();
        return configuration;
    }

    public TaskPolicy Policy(TaskClass task) =>
        Tasks.TryGetValue(task, out var policy) ? policy : throw new InvalidOperationException($"No policy for task {task}.");

    private void Check()
    {
        if (Spend.MonthlyCapUsd <= 0 || Spend.MonthlyCapUsd > 30m)
        {
            // D4: the monthly cap lies within 10–30 USD; above that is a user decision, not a configuration change.
            throw new InvalidOperationException("Monthly cap must be within (0, 30] USD (D4).");
        }

        foreach (var kind in Enum.GetValues<JobKind>())
        {
            if (!Spend.JobShares.TryGetValue(kind, out var share) || share <= 0 || share > 1)
            {
                throw new InvalidOperationException($"Job share for {kind} must be within (0, 1].");
            }
        }

        if (Resilience.Concurrency < 1 || Resilience.MaxRetries < 0 || Resilience.MaxRetries > 2)
        {
            throw new InvalidOperationException("Concurrency ≥ 1 and at most 2 retries (ADR-0010).");
        }

        foreach (var (task, policy) in Tasks)
        {
            if (policy.Kind == TaskKind.Advisory && policy.Model is null)
            {
                throw new InvalidOperationException($"Advisory task {task} needs a model.");
            }

            if (policy.Kind == TaskKind.DecisionAffecting && policy.Model is not null)
            {
                throw new InvalidOperationException($"Decision-affecting task {task} may only use qualified models.");
            }
        }
    }
}
