namespace PortfolioIntelligence.Kernel.Dsl;

/// <summary>DSL equivalents of the M2 baselines (ADR-0016 appendix); parity with the C# baselines is tested.</summary>
public static class DslBaselines
{
    public const string EmaTrendJson = """
        {
          "dsl": "1",
          "name": "ema-trend",
          "interval": "4h",
          "indicators": [
            { "id": "fast", "type": "ema", "params": { "period": 50 } },
            { "id": "slow", "type": "ema", "params": { "period": 100 } },
            { "id": "atr", "type": "atr", "params": { "period": 14 } }
          ],
          "entry": { "op": "cross_above", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
          "exit":  { "op": "cross_below", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
          "stop":  { "type": "atr", "ref": "atr.value", "multiple": 3, "trailing": false }
        }
        """;

    public const string SupertrendJson = """
        {
          "dsl": "1", "name": "supertrend", "interval": "4h",
          "indicators": [ { "id": "st", "type": "supertrend", "params": { "period": 10, "multiplier": 3 } } ],
          "entry": { "op": "cross_above", "args": [ { "ref": "st.dir" }, { "const": 0 } ] },
          "exit":  { "op": "cross_below", "args": [ { "ref": "st.dir" }, { "const": 0 } ] },
          "stop":  { "type": "indicator", "ref": "st.lower", "trailing": true }
        }
        """;

    public static DslStrategy EmaTrend() => DslStrategy.FromJson(EmaTrendJson);

    public static DslStrategy Supertrend() => DslStrategy.FromJson(SupertrendJson);
}
