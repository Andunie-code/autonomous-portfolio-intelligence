using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Trading.Tests.Support;

/// <summary>
/// Manifests for tests. <see cref="Kohort1Json"/> is Kohort 0's manifest with the three Kohort 1 members of
/// docs/cohort-1-preregistration.md (definitions copied from docs/m4-hypotheses.v1.json); it is not the frozen
/// manifest, which is generated in Gate C.
/// </summary>
public static class TestCohorts
{
    public const string H08 = """
        {"dsl":"1","name":"h08-smabtc-n150-b300","interval":"4h","indicators":[
          {"id":"trend","type":"sma","params":{"period":150}},{"id":"btc_trend","type":"sma","params":{"period":300},"source":"btc"},
          {"id":"atr","type":"atr","params":{"period":14}}],
         "entry":{"op":"and","args":[{"op":"cross_above","args":[{"price":"close"},{"ref":"trend.value"}]},
                                     {"op":"gt","args":[{"price":"close","source":"btc"},{"ref":"btc_trend.value"}]}]},
         "exit":{"op":"cross_below","args":[{"price":"close"},{"ref":"trend.value"}]},
         "stop":{"type":"atr","ref":"atr.value","multiple":3,"trailing":false}}
        """;

    public const string H04 = """
        {"dsl":"1","name":"h04-macd-f12-s26-g12","interval":"4h","indicators":[
          {"id":"macd","type":"macd","params":{"fast":12,"slow":26,"signal":12}},{"id":"atr","type":"atr","params":{"period":14}}],
         "entry":{"op":"and","args":[{"op":"cross_above","args":[{"ref":"macd.line"},{"ref":"macd.signal"}]},
                                     {"op":"gt","args":[{"ref":"macd.line"},{"const":0}]}]},
         "exit":{"op":"cross_below","args":[{"ref":"macd.line"},{"ref":"macd.signal"}]},
         "stop":{"type":"atr","ref":"atr.value","multiple":3,"trailing":false}}
        """;

    public const string H07 = """
        {"dsl":"1","name":"h07-volbo-n50-v2p5","interval":"4h","indicators":[
          {"id":"dc","type":"donchian","params":{"period":50}},{"id":"vol","type":"volume_sma","params":{"period":20}},
          {"id":"fast","type":"ema","params":{"period":20}},{"id":"atr","type":"atr","params":{"period":14}}],
         "entry":{"op":"and","args":[{"op":"gt","args":[{"price":"close"},{"ref":"dc.upper"}]},
                                     {"op":"gt","args":[{"price":"volume"},{"ref":"vol.value","mult":2.5}]}]},
         "exit":{"op":"lt","args":[{"price":"close"},{"ref":"fast.value"}]},
         "stop":{"type":"atr","ref":"atr.value","multiple":2.5,"trailing":false}}
        """;

    public static string Kohort0Json() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cohort-0.json"));

    public static string Kohort1Json()
    {
        var root = JsonNode.Parse(Kohort0Json())!.AsObject();
        root["cohort"] = "Kohort 1 (test)";
        var strategies = new JsonArray();
        foreach (var definition in new[] { H08, H04, H07 })
        {
            strategies.Add(new JsonObject
            {
                ["definition"] = JsonNode.Parse(definition),
                ["definitionHash"] = DslStrategy.FromJson(definition).DefinitionHash,
                ["researchMetric"] = 0,
            });
        }

        root["strategies"] = strategies;
        return root.ToJsonString();
    }
}
