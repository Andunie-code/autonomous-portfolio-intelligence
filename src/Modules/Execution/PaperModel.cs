using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution;

/// <summary>One sleeve of a paper run: a frozen cohort member and its share of the nominal capital (m5-protocol §2.2).</summary>
public sealed record SleeveConfig(long CohortId, int MemberIndex, string Name, string DefinitionHash, decimal Capital);

/// <summary>
/// Immutable configuration of a paper run; its hash is stored with the run (trading.paper_runs). Since ADR-0019 a
/// new run holds one cohort. <see cref="ActionsAfter"/> is the last operational action before the run was created:
/// the run applies only later ones. It is not written when absent, so runs created before M5b keep their JSON and hash.
/// <see cref="Venue"/> is "testnet" for a Testnet run (M6), which mirrors its cohort's paper run.
/// </summary>
public sealed record PaperRunConfig(
    string ManifestHash,
    IReadOnlyList<SleeveConfig> Sleeves,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Experimental,
    string Interval,
    string? ReferenceSymbol,
    string CostModel,
    RiskConfigDocument InitialRisk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ActionsAfter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Venue = null)
{
    public const decimal NominalCapital = 10_000m;

    public const string TestnetVenue = "testnet";

    /// <summary>A Testnet run (M6, m6-protocol §4.1); paper runs have no venue field, so their JSON and hash are unchanged.</summary>
    [JsonIgnore]
    public bool IsTestnet => Venue == TestnetVenue;

    public string Json() => JsonSerializer.Serialize(this, PaperJson.Options);

    public string Hash() => DslCanonical.Sha256(Json());

    public static PaperRunConfig Parse(string json) => JsonSerializer.Deserialize<PaperRunConfig>(json, PaperJson.Options)!;
}

public sealed record PaperRun(long Id, DateTimeOffset TradingStart, DateTimeOffset WarmupStart, PaperRunConfig Config);

/// <summary>
/// The recorded operational inputs of one step. Replaying the stored candles of <see cref="Symbols"/> with these
/// contexts must rebuild the stored state hash (ADR-0018 §1); this is the restart and reconciliation rule. A Testnet
/// step also records the exchange's fills (<see cref="External"/>, ADR-0021 §3); paper steps keep their JSON.
/// </summary>
public sealed record StepInputs(
    string? Blocked,
    IReadOnlyList<int> Disabled,
    IReadOnlyList<ManualClose> Manual,
    bool CloseAll,
    IReadOnlyList<string> Symbols,
    long LastActionId,
    long RiskVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExternalExecution? External = null)
{
    public StepContext ToContext() => new(
        Blocked,
        Disabled.Count == 0 ? null : Disabled.ToHashSet(),
        Manual.Count == 0 ? null : Manual.Select(m => (m.Sleeve, m.Symbol)).ToHashSet(),
        CloseAll,
        External: External);

    public string Json() => JsonSerializer.Serialize(this, PaperJson.Options);

    public static StepInputs Parse(string json) => JsonSerializer.Deserialize<StepInputs>(json, PaperJson.Options)!;
}

public sealed record ManualClose(int Sleeve, string Symbol);

public sealed record StepRecord(DateTimeOffset BarTime, StepInputs Inputs, string StateHash);

/// <summary>
/// RiskConfig v2 as stored in trading.risk_config_versions.config. The correlation-cluster fields (P1) are optional and
/// are not written when absent, so documents stored before P1 keep their exact JSON and hash.
/// </summary>
public sealed record RiskConfigDocument(
    int MaxOpenPositions,
    decimal MaxAssetWeight,
    decimal MaxTotalExposure,
    decimal DailyLossLimit,
    decimal MaxDrawdown,
    bool DrawdownHaltEnabled,
    decimal MaxPositionLoss,
    decimal ExperimentalPairsMaxExposure,
    int MaxDataLagBars,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ClusterCapThreshold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ClusterCapMaxExposure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ClusterWarningShare = null)
{
    public static RiskConfigDocument From(RiskConfig config, decimal? clusterWarning = null) => new(
        config.Settings.MaxOpenPositions, config.Settings.MaxAssetWeight, config.Settings.MaxTotalExposure, config.Settings.DailyLossLimit,
        config.Settings.MaxDrawdown, config.Settings.DrawdownHaltEnabled, config.Settings.MaxPositionLoss,
        config.ExperimentalPairsMaxExposure, config.MaxDataLagBars,
        config.ClusterCap is { } cap ? (decimal)cap.Threshold : null, config.ClusterCap?.MaxExposure, clusterWarning);

    public RiskConfig ToConfig() => new(
        new RiskSettings(MaxOpenPositions, MaxAssetWeight, MaxTotalExposure, DailyLossLimit, MaxDrawdown, DrawdownHaltEnabled, MaxPositionLoss),
        ExperimentalPairsMaxExposure, MaxDataLagBars,
        ClusterCapThreshold is { } threshold && ClusterCapMaxExposure is { } max ? new ClusterCapSettings((double)threshold, max) : null);

    public string Json() => JsonSerializer.Serialize(this, PaperJson.Options);

    public string Hash() => DslCanonical.Sha256(Json());

    /// <summary>Validation of a new version (positive limits, fractions within [0, 1]).</summary>
    public IReadOnlyList<string> Errors()
    {
        var errors = new List<string>();
        if (MaxOpenPositions < 0)
        {
            errors.Add("maxOpenPositions must be ≥ 0");
        }

        foreach (var (name, value) in new[]
                 {
                     ("maxAssetWeight", MaxAssetWeight), ("maxTotalExposure", MaxTotalExposure), ("dailyLossLimit", DailyLossLimit),
                     ("maxDrawdown", MaxDrawdown), ("maxPositionLoss", MaxPositionLoss), ("experimentalPairsMaxExposure", ExperimentalPairsMaxExposure),
                 })
        {
            if (value is < 0 or > 1)
            {
                errors.Add($"{name} must be within [0, 1]");
            }
        }

        if (MaxDataLagBars < 0)
        {
            errors.Add("maxDataLagBars must be ≥ 0");
        }

        if (ClusterCapThreshold.HasValue != ClusterCapMaxExposure.HasValue)
        {
            errors.Add("clusterCapThreshold and clusterCapMaxExposure go together");
        }

        foreach (var (name, value) in new[] { ("clusterCapThreshold", ClusterCapThreshold), ("clusterCapMaxExposure", ClusterCapMaxExposure), ("clusterWarningShare", ClusterWarningShare) })
        {
            if (value is < 0 or > 1)
            {
                errors.Add($"{name} must be within [0, 1]");
            }
        }

        return errors;
    }

    /// <summary>True if <paramref name="next"/> relaxes any limit of this version (needs explicit confirmation).</summary>
    public bool IsLoosenedBy(RiskConfigDocument next) =>
        next.MaxOpenPositions > MaxOpenPositions || next.MaxAssetWeight > MaxAssetWeight || next.MaxTotalExposure > MaxTotalExposure
        || next.DailyLossLimit > DailyLossLimit || next.MaxDrawdown > MaxDrawdown || (DrawdownHaltEnabled && !next.DrawdownHaltEnabled)
        || next.MaxPositionLoss > MaxPositionLoss || next.ExperimentalPairsMaxExposure > ExperimentalPairsMaxExposure
        || next.MaxDataLagBars > MaxDataLagBars
        // Removing the cluster cap, raising it, or raising the correlation threshold (smaller clusters) relaxes it.
        || (ClusterCapMaxExposure is { } cap && (next.ClusterCapMaxExposure is not { } nextCap || nextCap > cap))
        || (ClusterCapThreshold is { } threshold && next.ClusterCapThreshold is { } nextThreshold && nextThreshold > threshold);
}

internal static class PaperJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
