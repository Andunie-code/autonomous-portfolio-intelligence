using System.Text.RegularExpressions;

namespace PortfolioIntelligence.Kernel.Dsl;

public sealed record DslParameterSpec(string Name, decimal Min, decimal Max, bool Integer);

public sealed record DslIndicatorSpec(string Type, IReadOnlyList<DslParameterSpec> Parameters, IReadOnlyList<string> Outputs, Func<IReadOnlyDictionary<string, decimal>, int> Warmup);

/// <summary>The indicator whitelist of ADR-0016 with parameter ranges, outputs and warm-up length L (m3-protocol §1).</summary>
public static class DslCatalog
{
    private static readonly DslParameterSpec Period2To300 = new("period", 2, 300, true);
    private static readonly DslParameterSpec Period2To100 = new("period", 2, 100, true);

    public static IReadOnlyDictionary<string, DslIndicatorSpec> Indicators { get; } = new[]
    {
        new DslIndicatorSpec("sma", [Period2To300], ["value"], p => P(p, "period")),
        new DslIndicatorSpec("ema", [Period2To300], ["value"], p => P(p, "period")),
        new DslIndicatorSpec("rsi", [Period2To100], ["value"], p => P(p, "period") + 1),
        new DslIndicatorSpec("atr", [Period2To100], ["value"], p => P(p, "period")),
        new DslIndicatorSpec("roc", [new("period", 1, 300, true)], ["value"], p => P(p, "period") + 1),
        new DslIndicatorSpec("volume_sma", [Period2To300], ["value"], p => P(p, "period")),
        new DslIndicatorSpec("macd", [new("fast", 2, 100, true), new("slow", 3, 300, true), new("signal", 2, 100, true)],
            ["line", "signal", "hist"], p => P(p, "slow") + P(p, "signal") - 1),
        new DslIndicatorSpec("adx", [Period2To100], ["adx", "plus_di", "minus_di"], p => 2 * P(p, "period")),
        new DslIndicatorSpec("bollinger", [Period2To300, new("k", 0.5m, 4m, false)],
            ["middle", "upper", "lower", "bandwidth", "percent_b"], p => P(p, "period")),
        new DslIndicatorSpec("donchian", [Period2To300], ["upper", "lower", "middle"], p => P(p, "period") + 1),
        new DslIndicatorSpec("supertrend", [Period2To100, new("multiplier", 0.5m, 10m, false)], ["upper", "lower", "dir"], p => P(p, "period")),
    }.ToDictionary(s => s.Type, StringComparer.Ordinal);

    public static readonly string[] PriceFields = ["open", "high", "low", "close", "volume"];

    public static readonly string[] Comparisons = ["gt", "gte", "lt", "lte", "cross_above", "cross_below"];

    public static readonly string[] Logical = ["and", "or", "not"];

    public const int MaxIndicators = 8;

    public const int MaxDepth = 3;

    private static int P(IReadOnlyDictionary<string, decimal> parameters, string name) => (int)parameters[name];
}

/// <summary>Semantic validation of a parsed definition (ADR-0016 "Doğrulama").</summary>
public static partial class DslValidator
{
    public static DslDefinition Validate(DslDefinition definition)
    {
        var errors = new List<string>();
        if (!NameRegex().IsMatch(definition.Name))
        {
            errors.Add("name: 1–64 characters of [a-z0-9-]");
        }

        if (definition.Interval != "4h")
        {
            errors.Add("interval: only '4h' is supported in DSL v1");
        }

        if (definition.Indicators.Count is 0 or > DslCatalog.MaxIndicators)
        {
            errors.Add($"indicators: 1–{DslCatalog.MaxIndicators} required");
        }

        var byId = new Dictionary<string, DslIndicator>(StringComparer.Ordinal);
        foreach (var indicator in definition.Indicators)
        {
            ValidateIndicator(indicator, errors);
            if (!byId.TryAdd(indicator.Id, indicator))
            {
                errors.Add($"indicator '{indicator.Id}': duplicate id");
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        ValidateCondition(definition.Entry, "entry", 1, byId, used, errors);
        ValidateCondition(definition.Exit, "exit", 1, byId, used, errors);
        if (definition.Stop is { } stop)
        {
            ValidateStop(stop, byId, used, errors);
        }

        foreach (var id in byId.Keys.Where(id => !used.Contains(id)))
        {
            errors.Add($"indicator '{id}': declared but never used");
        }

        return errors.Count == 0 ? definition : throw new DslException(errors);
    }

    private static void ValidateIndicator(DslIndicator indicator, List<string> errors)
    {
        var where = $"indicator '{indicator.Id}'";
        if (!IdRegex().IsMatch(indicator.Id))
        {
            errors.Add($"{where}: id must match [a-z][a-z0-9_]{{0,15}}");
        }

        if (!DslCatalog.Indicators.TryGetValue(indicator.Type, out var spec))
        {
            errors.Add($"{where}: type '{indicator.Type}' is not whitelisted");
            return;
        }

        foreach (var name in indicator.Params.Keys.Where(k => spec.Parameters.All(p => p.Name != k)))
        {
            errors.Add($"{where}: unknown parameter '{name}'");
        }

        foreach (var p in spec.Parameters)
        {
            if (!indicator.Params.TryGetValue(p.Name, out var value))
            {
                errors.Add($"{where}: missing parameter '{p.Name}'");
            }
            else if (value < p.Min || value > p.Max || (p.Integer && value != decimal.Truncate(value)))
            {
                errors.Add($"{where}: parameter '{p.Name}' must be {(p.Integer ? "an integer " : "")}in [{p.Min}, {p.Max}]");
            }
        }

        if (indicator.Type == "macd" && indicator.Params.TryGetValue("fast", out var fast) && indicator.Params.TryGetValue("slow", out var slow) && fast >= slow)
        {
            errors.Add($"{where}: fast must be less than slow");
        }
    }

    private static void ValidateCondition(DslCondition condition, string path, int depth, Dictionary<string, DslIndicator> byId, HashSet<string> used, List<string> errors)
    {
        if (depth > DslCatalog.MaxDepth)
        {
            errors.Add($"{path}: condition depth exceeds {DslCatalog.MaxDepth}");
            return;
        }

        if (DslCatalog.Comparisons.Contains(condition.Op))
        {
            if (condition.Args.Count != 2 || condition.Args.Any(a => a is not DslOperand))
            {
                errors.Add($"{path}: '{condition.Op}' takes exactly two operands");
                return;
            }

            for (var i = 0; i < 2; i++)
            {
                ValidateOperand((DslOperand)condition.Args[i], $"{path}.args[{i}]", byId, used, errors);
            }

            if (condition.Args.All(a => a is DslConst))
            {
                errors.Add($"{path}: comparison of constants only");
            }
            else if (condition.Args[0] == condition.Args[1])
            {
                errors.Add($"{path}: comparison of an operand with itself");
            }

            return;
        }

        var (min, max) = condition.Op switch
        {
            "and" or "or" => (2, 4),
            "not" => (1, 1),
            _ => (-1, -1),
        };
        if (min < 0)
        {
            errors.Add($"{path}: unknown operator '{condition.Op}'");
            return;
        }

        if (condition.Args.Count < min || condition.Args.Count > max || condition.Args.Any(a => a is not DslCondition))
        {
            errors.Add($"{path}: '{condition.Op}' takes {min}–{max} conditions");
            return;
        }

        for (var i = 0; i < condition.Args.Count; i++)
        {
            ValidateCondition((DslCondition)condition.Args[i], $"{path}.args[{i}]", depth + 1, byId, used, errors);
        }
    }

    private static void ValidateOperand(DslOperand operand, string path, Dictionary<string, DslIndicator> byId, HashSet<string> used, List<string> errors)
    {
        switch (operand)
        {
            case DslRef r:
                ValidateRef(r, path, byId, used, errors);
                break;
            case DslPrice p:
                if (!DslCatalog.PriceFields.Contains(p.Field))
                {
                    errors.Add($"{path}: unknown price field '{p.Field}'");
                }

                ValidateLagAndMult(p.Lag, p.Mult, path, errors);
                break;
        }
    }

    private static void ValidateRef(DslRef r, string path, Dictionary<string, DslIndicator> byId, HashSet<string> used, List<string> errors)
    {
        if (!byId.TryGetValue(r.Indicator, out var indicator))
        {
            errors.Add($"{path}: unknown indicator '{r.Indicator}'");
            return;
        }

        used.Add(r.Indicator);
        if (DslCatalog.Indicators.TryGetValue(indicator.Type, out var spec) && !spec.Outputs.Contains(r.Output))
        {
            errors.Add($"{path}: '{indicator.Type}' has no output '{r.Output}'");
        }

        ValidateLagAndMult(r.Lag, r.Mult, path, errors);
    }

    private static void ValidateLagAndMult(int lag, decimal mult, string path, List<string> errors)
    {
        if (lag is < 0 or > 1)
        {
            errors.Add($"{path}: lag must be 0 or 1");
        }

        if (mult <= 0 || mult > 100)
        {
            errors.Add($"{path}: mult must be in (0, 100]");
        }
    }

    private static void ValidateStop(DslStop stop, Dictionary<string, DslIndicator> byId, HashSet<string> used, List<string> errors)
    {
        switch (stop.Type)
        {
            case "percent":
                if (stop.Pct is not { } pct || pct < 0.01m || pct > 0.10m)
                {
                    errors.Add("stop: 'percent' requires pct in [0.01, 0.10]");
                }

                if (stop.Ref is not null || stop.Multiple is not null || stop.Trailing)
                {
                    errors.Add("stop: 'percent' takes only pct");
                }

                break;
            case "atr":
                if (stop.Multiple is not { } multiple || multiple < 0.5m || multiple > 10m)
                {
                    errors.Add("stop: 'atr' requires multiple in [0.5, 10]");
                }

                if (stop.Pct is not null || stop.Trailing)
                {
                    errors.Add("stop: 'atr' takes ref and multiple and is not trailing");
                }

                ValidateStopRef(stop, byId, used, errors, mustBeAtr: true);
                break;
            case "indicator":
                if (stop.Pct is not null || stop.Multiple is not null)
                {
                    errors.Add("stop: 'indicator' takes ref and trailing");
                }

                ValidateStopRef(stop, byId, used, errors, mustBeAtr: false);
                break;
            default:
                errors.Add($"stop: unknown type '{stop.Type}'");
                break;
        }
    }

    private static void ValidateStopRef(DslStop stop, Dictionary<string, DslIndicator> byId, HashSet<string> used, List<string> errors, bool mustBeAtr)
    {
        if (stop.Ref is not { } r)
        {
            errors.Add("stop: ref required");
            return;
        }

        ValidateRef(r, "stop.ref", byId, used, errors);
        if (r.Lag != 0 || r.Mult != 1m)
        {
            errors.Add("stop.ref: lag and mult are not allowed");
        }

        if (byId.TryGetValue(r.Indicator, out var indicator))
        {
            if (indicator.Source != DslSource.Self)
            {
                errors.Add("stop.ref: must reference a 'self' indicator");
            }

            if (mustBeAtr && (indicator.Type != "atr" || r.Output != "value"))
            {
                errors.Add("stop.ref: 'atr' stops must reference an atr indicator's value");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,15}$")]
    private static partial Regex IdRegex();
}
