using System.Text.Json;
using PortfolioIntelligence.Execution.Radar;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// Signal radar (ADR-0020 §5) on synthetic candles: indicator values are the interpreter's values, proximity is the
/// interpreter's own view of the conditions, a missing BTC bar makes the regime atom Unknown, open positions switch
/// to the exit view, and the report is a deterministic function of its inputs.
/// </summary>
public class RadarTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Symbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"];

    private static Dictionary<string, IReadOnlyList<Candle>> Candles(int bars = 400) =>
        Symbols.Select((s, i) => (s, Series: (IReadOnlyList<Candle>)LiveSynthetic.Walk(s, Start, bars, 70 + i))).ToDictionary(x => x.s, x => x.Series);

    private static readonly RadarStrategy[] Strategies =
    [
        new("h08-smabtc-n150-b300", 2, TestCohorts.H08, Symbols),
        new("h04-macd-f12-s26-g12", 2, TestCohorts.H04, Symbols),
        new("h07-volbo-n50-v2p5", 2, TestCohorts.H07, Symbols),
    ];

    private const string Probe = """
        {"dsl":"1","name":"probe","interval":"4h","indicators":[
          {"id":"e","type":"ema","params":{"period":50}},{"id":"r","type":"rsi","params":{"period":14}},
          {"id":"m","type":"macd","params":{"fast":12,"slow":26,"signal":9}},{"id":"d","type":"donchian","params":{"period":50}},
          {"id":"v","type":"volume_sma","params":{"period":20}},{"id":"a","type":"adx","params":{"period":14}},
          {"id":"b","type":"bollinger","params":{"period":20,"k":2}},{"id":"s","type":"sma","params":{"period":150}}],
         "entry":{"op":"and","args":[{"op":"gt","args":[{"ref":"e.value"},{"const":0}]},{"op":"gt","args":[{"ref":"r.value"},{"const":0}]},
                                     {"op":"gt","args":[{"ref":"m.line"},{"ref":"d.upper"}]},{"op":"gt","args":[{"ref":"v.value"},{"ref":"a.adx"}]}]},
         "exit":{"op":"or","args":[{"op":"lt","args":[{"ref":"b.percent_b"},{"ref":"s.value"}]},{"op":"lt","args":[{"price":"close"},{"const":0}]}]},
         "stop":{"type":"percent","pct":0.05}}
        """;

    [Fact]
    public void Indicator_values_are_the_interpreters_values_on_the_same_candles()
    {
        var candles = Candles();
        var report = SignalRadar.Compute(candles, [new RadarStrategy("probe", 9, Probe, ["ETHUSDT"])], [], new HashSet<string>());
        var panel = report.Symbols.Single(s => s.Symbol == "ETHUSDT").Indicators;
        var atoms = report.Strategies.Single().Symbols.Single().Atoms;
        decimal? Value(string label) => atoms.First(a => a.Left == label).LeftValue ?? atoms.First(a => a.Right == label).RightValue;
        decimal? Right(string label) => atoms.First(a => a.Right == label).RightValue;

        Assert.Equal(SignalRadar.Bars, report.Symbols.Single(s => s.Symbol == "ETHUSDT").Bars);
        Assert.Equal(Value("e.value"), panel.Ema50);
        Assert.Equal(Value("r.value"), panel.Rsi14);
        Assert.Equal(Value("m.line"), panel.MacdLine);
        Assert.Equal(Right("d.upper"), panel.DonchianUpper);
        Assert.Equal(Value("v.value"), panel.VolumeSma20);
        Assert.Equal(Right("a.adx"), panel.Adx14);
        Assert.Equal(Value("b.percent_b"), panel.BollingerPercentB);
        Assert.Equal(Right("s.value"), panel.Sma150);
        Assert.Equal(candles["ETHUSDT"][^1].Close, panel.Close);
    }

    [Fact]
    public void Proximity_matches_the_strategy_signal_on_the_last_bar()
    {
        var candles = Candles();
        foreach (var cut in Enumerable.Range(330, 60))
        {
            var slice = candles.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value.Take(cut).ToList());
            var report = SignalRadar.Compute(slice, Strategies, [], new HashSet<string>());
            foreach (var view in report.Strategies)
            {
                var strategy = DslStrategy.FromJson(Strategies.Single(s => s.Name == view.Name).DefinitionJson);
                foreach (var row in view.Symbols)
                {
                    var tracker = strategy.CreateReferenceTracker();
                    var instance = strategy.CreateInstance(tracker);
                    var btc = slice["BTCUSDT"].TakeLast(SignalRadar.Bars).ToDictionary(c => c.OpenTime);
                    foreach (var bar in slice[row.Symbol].TakeLast(SignalRadar.Bars))
                    {
                        tracker.Update(btc.GetValueOrDefault(bar.OpenTime));
                        instance.Update(bar);
                    }

                    Assert.Equal(instance.EntrySignal, row.Status == "entry-signal");
                    Assert.Equal(row.Atoms.Count(a => a.Side == "entry"), row.Total);
                    Assert.InRange(row.Satisfied, 0, row.Total);
                }
            }
        }
    }

    [Fact]
    public void A_missing_btc_bar_makes_the_regime_filtered_strategy_unknown()
    {
        var candles = Candles();
        candles["BTCUSDT"] = candles["BTCUSDT"].Take(candles["BTCUSDT"].Count - 1).ToList();

        var report = SignalRadar.Compute(candles, Strategies, [], new HashSet<string>());

        var h08 = report.Strategies.Single(s => s.Name.StartsWith("h08", StringComparison.Ordinal)).Symbols.Single(r => r.Symbol == "ETHUSDT");
        Assert.Equal(nameof(Tri.Unknown), h08.Atoms.Single(a => a.Left == "btc.close").Value);
        Assert.NotEqual("entry-signal", h08.Status);
        Assert.Contains(report.Regime.State, new[] { "Bull", "Bear" }); // the regime panel uses the BTC bars it has
        Assert.NotNull(report.Regime.Sma300);
    }

    [Fact]
    public void An_open_position_shows_the_exit_view_and_the_stop_distance()
    {
        var candles = Candles();
        var close = candles["SOLUSDT"][^1].Close;
        var report = SignalRadar.Compute(candles, Strategies, [new RadarPosition("h04-macd-f12-s26-g12", "SOLUSDT", close, close * 0.9m)], new HashSet<string> { "WIFUSDT" });

        var row = report.Strategies.Single(s => s.Name.StartsWith("h04", StringComparison.Ordinal)).Symbols.Single(r => r.Symbol == "SOLUSDT");
        Assert.Contains(row.Status, new[] { "in-position", "exit-signal" });
        Assert.Equal(10m, row.StopDistancePct);
        Assert.Equal(row.Atoms.Count(a => a.Side == "exit"), row.Total);
        Assert.DoesNotContain(report.Symbols, s => s.Symbol == "WIFUSDT"); // no candles for it: not shown
    }

    [Fact]
    public void Atoms_carry_display_names_and_no_percentage_of_a_value_near_zero()
    {
        var report = SignalRadar.Compute(Candles(), Strategies, [], new HashSet<string>());
        RadarProximity Row(string prefix) => report.Strategies.Single(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)).Symbols.Single(r => r.Symbol == "ETHUSDT");

        foreach (var atom in report.Strategies.SelectMany(s => s.Symbols).SelectMany(r => r.Atoms))
        {
            if (atom.LeftValue is { } l && atom.RightValue is { } r)
            {
                Assert.Equal(Math.Round(l - r, 8), atom.Gap);
                if (l <= 0 || r <= 0)
                {
                    Assert.Null(atom.DistancePct); // MACD and other oscillators: a % of a value near zero means nothing
                }
            }
        }

        var h04 = Row("h04");
        Assert.Equal("cross", h04.EntryKind);
        Assert.Contains(h04.Atoms, a => a.Left == "macd.line" && a.LeftName == "MACD" && a.Right == "macd.signal" && a.RightName == "MACD sinyal");
        Assert.Null(Assert.Single(h04.Atoms, a => a.Right == "0").DistancePct);

        var h08 = Row("h08");
        Assert.Contains(h08.Atoms, a => a.Left == "close" && a.LeftName == "Fiyat" && a.RightName == "SMA150");
        Assert.Contains(h08.Atoms, a => a.Left == "btc.close" && a.LeftName == "BTC fiyatı" && a.RightName == "BTC SMA300");

        var h07 = Row("h07");
        Assert.Equal("level", h07.EntryKind);
        var volume = Assert.Single(h07.Atoms, a => a.Left == "volume");
        Assert.Equal(("Hacim", "Hacim ort.20", 2.5m), (volume.LeftName, volume.RightName, volume.RightMult));
        Assert.Contains(h07.Atoms, a => a.RightName == "Donchian50 üst");
        Assert.Contains(h07.Atoms, a => a.Side == "exit" && a.RightName == "EMA20");
    }

    [Fact]
    public void The_report_is_deterministic_and_independent_of_input_order()
    {
        var candles = Candles();
        var reversed = candles.Reverse().ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value.Reverse().ToList());

        var a = JsonSerializer.Serialize(SignalRadar.Compute(candles, Strategies, [], new HashSet<string> { "SOLUSDT" }));
        var b = JsonSerializer.Serialize(SignalRadar.Compute(reversed, [.. Strategies.Reverse()], [], new HashSet<string> { "SOLUSDT" }));

        Assert.Equal(a, b);
        Assert.Equal(DslCanonical.Sha256(a), DslCanonical.Sha256(b));
    }
}
