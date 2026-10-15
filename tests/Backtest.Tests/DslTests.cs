using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>DSL v1 (ADR-0016): validation, normalisation and hashes (m3-protocol §8), interpreter semantics and baseline parity.</summary>
public class DslTests
{
    private const string Filtered = """
        {
          "dsl": "1", "name": "filtered", "interval": "4h",
          "indicators": [
            { "id": "fast", "type": "ema", "params": { "period": 20 } },
            { "id": "slow", "type": "sma", "params": { "period": 50 } },
            { "id": "trend", "type": "adx", "params": { "period": 14 } },
            { "id": "regime", "type": "ema", "params": { "period": 30 }, "source": "btc" }
          ],
          "entry": { "op": "and", "args": [
            { "op": "cross_above", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
            { "op": "gt", "args": [ { "ref": "trend.adx" }, { "const": 20 } ] },
            { "op": "gt", "args": [ { "price": "close", "source": "btc" }, { "ref": "regime.value" } ] }
          ] },
          "exit": { "op": "lt", "args": [ { "price": "close" }, { "ref": "slow.value", "mult": 0.98 } ] },
          "stop": { "type": "percent", "pct": 0.05 }
        }
        """;

    [Fact]
    public void Baseline_definitions_parse_and_have_the_m2_warm_up_lengths()
    {
        Assert.Equal(101, DslBaselines.EmaTrend().WarmupBars);
        Assert.Equal(11, DslBaselines.Supertrend().WarmupBars);
        Assert.Equal("ema-trend", DslBaselines.EmaTrend().Name);
        Assert.False(DslBaselines.EmaTrend().UsesReference);
        Assert.True(DslStrategy.FromJson(Filtered).UsesReference);
    }

    [Theory]
    [InlineData("\"name\": \"filtered\",", "\"name\": \"filtered\", \"extra\": 1,", "unknown property")]
    [InlineData("\"type\": \"adx\"", "\"type\": \"ichimoku\"", "not whitelisted")]
    [InlineData("\"period\": 20 }", "\"period\": 400 }", "must be an integer in")]
    [InlineData("\"period\": 20 }", "\"period\": 20.5 }", "must be an integer in")]
    [InlineData("\"interval\": \"4h\"", "\"interval\": \"1h\"", "only '4h'")]
    [InlineData("{ \"ref\": \"trend.adx\" }", "{ \"ref\": \"trend.adx\", \"lag\": 2 }", "lag must be 0 or 1")]
    [InlineData("{ \"ref\": \"trend.adx\" }", "{ \"ref\": \"trend.value\" }", "has no output")]
    [InlineData("{ \"ref\": \"trend.adx\" }", "{ \"ref\": \"nothing.value\" }", "unknown indicator")]
    [InlineData("{ \"ref\": \"trend.adx\" }", "{ \"const\": 3 }", "constants only")]
    [InlineData("\"op\": \"and\"", "\"op\": \"xor\"", "unknown operator")]
    [InlineData("\"pct\": 0.05", "\"pct\": 0.5", "pct in")]
    [InlineData("\"source\": \"btc\" }", "\"source\": \"eth\" }", "unknown source")]
    public void Invalid_definitions_are_rejected(string find, string replace, string expected)
    {
        Assert.Contains(find, Filtered);
        var e = Assert.Throws<DslException>(() => DslParser.Parse(Filtered.Replace(find, replace)));
        Assert.Contains(e.Errors, m => m.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Unused_indicators_deep_conditions_and_bad_stops_are_rejected()
    {
        var unused = Filtered.Replace("\"indicators\": [", "\"indicators\": [ { \"id\": \"spare\", \"type\": \"rsi\", \"params\": { \"period\": 14 } },");
        Assert.Contains(Assert.Throws<DslException>(() => DslParser.Parse(unused)).Errors, m => m.Contains("never used"));

        var deep = Filtered.Replace(
            "{ \"op\": \"gt\", \"args\": [ { \"ref\": \"trend.adx\" }, { \"const\": 20 } ] }",
            "{ \"op\": \"not\", \"args\": [ { \"op\": \"not\", \"args\": [ { \"op\": \"gt\", \"args\": [ { \"ref\": \"trend.adx\" }, { \"const\": 20 } ] } ] } ] }");
        Assert.Contains(Assert.Throws<DslException>(() => DslParser.Parse(deep)).Errors, m => m.Contains("depth"));

        var atrStop = DslBaselines.EmaTrendJson.Replace("\"ref\": \"atr.value\", \"multiple\"", "\"ref\": \"fast.value\", \"multiple\"");
        Assert.Contains(Assert.Throws<DslException>(() => DslParser.Parse(atrStop)).Errors, m => m.Contains("atr indicator"));
    }

    [Fact]
    public void Hashes_ignore_field_order_whitespace_and_indicator_ids()
    {
        var reordered = """
            {"exit":{"args":[{"ref":"s.value"},{"ref":"f.value"}],"op":"cross_above"},
             "entry":{"op":"cross_above","args":[{"ref":"f.value"},{"ref":"s.value"}]},
             "stop":{"trailing":false,"multiple":3,"ref":"a.value","type":"atr"},
             "indicators":[{"type":"atr","id":"a","params":{"period":14}},{"params":{"period":100},"id":"s","type":"ema"},{"id":"f","type":"ema","params":{"period":50}}],
             "interval":"4h","name":"renamed","dsl":"1"}
            """;
        // exit "cross_below(fast, slow)" written as "cross_above(slow, fast)" is a different rule in v1 (no symmetric rewrite for crosses).
        var baseline = DslParser.Parse(DslBaselines.EmaTrendJson);
        var same = DslParser.Parse(reordered.Replace("\"args\":[{\"ref\":\"s.value\"},{\"ref\":\"f.value\"}],\"op\":\"cross_above\"", "\"args\":[{\"ref\":\"f.value\"},{\"ref\":\"s.value\"}],\"op\":\"cross_below\""));

        Assert.Equal(DslCanonical.DefinitionHash(baseline), DslCanonical.DefinitionHash(same));
        Assert.Equal(DslCanonical.StructureHash(baseline), DslCanonical.StructureHash(same));
        Assert.NotEqual(DslCanonical.StructureHash(baseline), DslCanonical.StructureHash(DslParser.Parse(reordered)));
    }

    [Fact]
    public void A_parameter_change_keeps_the_structure_and_changes_the_definition()
    {
        var baseline = DslParser.Parse(DslBaselines.EmaTrendJson);
        var tuned = DslParser.Parse(DslBaselines.EmaTrendJson.Replace("\"period\": 50", "\"period\": 40").Replace("\"multiple\": 3", "\"multiple\": 2.5"));

        Assert.Equal(DslCanonical.StructureHash(baseline), DslCanonical.StructureHash(tuned));
        Assert.NotEqual(DslCanonical.DefinitionHash(baseline), DslCanonical.DefinitionHash(tuned));
    }

    [Fact]
    public void A_rule_change_changes_the_structure()
    {
        var baseline = DslParser.Parse(DslBaselines.EmaTrendJson);
        var sma = DslParser.Parse(DslBaselines.EmaTrendJson.Replace("\"type\": \"ema\", \"params\": { \"period\": 100 }", "\"type\": \"sma\", \"params\": { \"period\": 100 }"));
        var trailing = DslParser.Parse(DslBaselines.SupertrendJson.Replace("\"trailing\": true", "\"trailing\": false"));

        Assert.NotEqual(DslCanonical.StructureHash(baseline), DslCanonical.StructureHash(sma));
        Assert.NotEqual(DslCanonical.StructureHash(DslParser.Parse(DslBaselines.SupertrendJson)), DslCanonical.StructureHash(trailing));
    }

    [Fact]
    public void Normalisation_rewrites_lt_double_negation_and_commutative_operands()
    {
        var gt = Filtered;
        var lt = Filtered.Replace(
            "{ \"op\": \"gt\", \"args\": [ { \"ref\": \"trend.adx\" }, { \"const\": 20 } ] }",
            "{ \"op\": \"lt\", \"args\": [ { \"const\": 20 }, { \"ref\": \"trend.adx\" } ] }");
        var notNot = Filtered.Replace(
            "\"exit\": { \"op\": \"lt\", \"args\": [ { \"price\": \"close\" }, { \"ref\": \"slow.value\", \"mult\": 0.98 } ] }",
            "\"exit\": { \"op\": \"not\", \"args\": [ { \"op\": \"not\", \"args\": [ { \"op\": \"gt\", \"args\": [ { \"ref\": \"slow.value\", \"mult\": 0.98 }, { \"price\": \"close\" } ] } ] } ] }");
        var swapped = Filtered.Replace(
            "{ \"op\": \"gt\", \"args\": [ { \"ref\": \"trend.adx\" }, { \"const\": 20 } ] },\n    { \"op\": \"gt\", \"args\": [ { \"price\": \"close\", \"source\": \"btc\" }, { \"ref\": \"regime.value\" } ] }",
            "{ \"op\": \"gt\", \"args\": [ { \"price\": \"close\", \"source\": \"btc\" }, { \"ref\": \"regime.value\" } ] },\n    { \"op\": \"gt\", \"args\": [ { \"ref\": \"trend.adx\" }, { \"const\": 20 } ] }");
        Assert.NotEqual(Filtered, swapped);

        var hash = DslCanonical.DefinitionHash(DslParser.Parse(gt));
        Assert.Equal(hash, DslCanonical.DefinitionHash(DslParser.Parse(lt)));
        Assert.Equal(hash, DslCanonical.DefinitionHash(DslParser.Parse(notNot)));
        Assert.Equal(hash, DslCanonical.DefinitionHash(DslParser.Parse(swapped)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Dsl_baselines_reproduce_the_coded_baselines_bit_for_bit(int seed)
    {
        var symbols = Enumerable.Range(0, 6).Select(i => Synthetic.RandomWalk($"S{i}USDT", 1800, seed * 100 + i)).ToList();
        var input = new BacktestInput(
            symbols.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s),
            symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2),
            Synthetic.At(300),
            symbols[0][^1].OpenTime.AddHours(4));

        foreach (var risk in new[] { RiskSettings.Policy, RiskSettings.NoHalt })
        {
            foreach (var (coded, dsl) in new (IStrategy, IStrategy)[] { (new EmaTrendStrategy(), DslBaselines.EmaTrend()), (new SupertrendStrategy(), DslBaselines.Supertrend()) })
            {
                var expected = new BacktestEngine(coded, CostModel.V1, risk).Run(input);
                var actual = new BacktestEngine(dsl, CostModel.V1, risk).Run(input);

                Assert.NotEmpty(expected.Trades);
                Assert.Equal(expected.Hash(), actual.Hash());
            }
        }
    }

    [Fact]
    public void Btc_dependent_entries_need_the_btc_candle_of_the_same_open_time()
    {
        const string json = """
            {
              "dsl": "1", "name": "btc-gate", "interval": "4h",
              "indicators": [ { "id": "f", "type": "sma", "params": { "period": 2 } } ],
              "entry": { "op": "and", "args": [
                { "op": "gt", "args": [ { "price": "close" }, { "ref": "f.value", "mult": 0.5 } ] },
                { "op": "not", "args": [ { "op": "lt", "args": [ { "price": "close", "source": "btc" }, { "const": 1 } ] } ] }
              ] },
              "exit": { "op": "lt", "args": [ { "price": "close" }, { "const": 0.01 } ] }
            }
            """;
        var strategy = DslStrategy.FromJson(json);
        var tracker = strategy.CreateReferenceTracker();
        var instance = strategy.CreateInstance(tracker);
        var pair = Synthetic.Flat("XUSDT", 6, 10m);
        var btc = Synthetic.Flat("BTCUSDT", 6, 100m);

        var signals = new List<bool>();
        for (var i = 0; i < 6; i++)
        {
            tracker.Update(i == 3 ? null : btc[i]);
            instance.Update(pair[i]);
            signals.Add(instance.EntrySignal);
        }

        // Bar 0: SMA not ready. Bar 3: BTC missing → Unknown, and not(Unknown) stays Unknown.
        Assert.Equal([false, true, true, false, true, true], signals);

        var unfed = strategy.CreateInstance();
        unfed.Update(pair[0]);
        unfed.Update(pair[1]);
        Assert.False(unfed.EntrySignal);
    }

    [Fact]
    public void Btc_gap_resets_btc_indicators_but_not_the_pairs()
    {
        const string json = """
            {
              "dsl": "1", "name": "btc-trend", "interval": "4h",
              "indicators": [
                { "id": "own", "type": "sma", "params": { "period": 3 } },
                { "id": "b", "type": "sma", "params": { "period": 3 }, "source": "btc" }
              ],
              "entry": { "op": "gt", "args": [ { "ref": "own.value" }, { "ref": "b.value" } ] },
              "exit": { "op": "lt", "args": [ { "ref": "own.value" }, { "const": 0 } ] }
            }
            """;
        var strategy = DslStrategy.FromJson(json);
        var tracker = strategy.CreateReferenceTracker();
        var instance = strategy.CreateInstance(tracker);
        var pair = Synthetic.Flat("XUSDT", 9, 10m);
        var btc = Synthetic.Flat("BTCUSDT", 9, 5m);

        var signals = new List<bool>();
        for (var i = 0; i < 9; i++)
        {
            tracker.Update(i == 4 ? null : btc[i]);
            instance.Update(pair[i]);
            signals.Add(instance.EntrySignal);
        }

        // BTC SMA3 ready at bar 2, reset by the gap at bar 4, ready again at bar 7; the pair's SMA is never reset.
        Assert.Equal([false, false, true, true, false, false, false, true, true], signals);
    }

    [Fact]
    public void Signals_up_to_t_do_not_depend_on_later_btc_candles()
    {
        var strategy = DslStrategy.FromJson(Filtered);
        for (var seed = 0; seed < 10; seed++)
        {
            var pair = Synthetic.RandomWalk("XUSDT", 400, seed);
            var btc = Synthetic.RandomWalk("BTCUSDT", 400, seed + 50);
            var changedBtc = btc.Take(200).Concat(Synthetic.RandomWalk("BTCUSDT", 400, seed + 999, 10m).Skip(200)).ToList();

            Assert.Equal(Run(pair, btc).Take(200), Run(pair, changedBtc).Take(200));
        }

        List<string> Run(List<Candle> pair, List<Candle> btc)
        {
            var tracker = strategy.CreateReferenceTracker();
            var instance = strategy.CreateInstance(tracker);
            var result = new List<string>();
            for (var i = 0; i < pair.Count; i++)
            {
                tracker.Update(btc[i]);
                instance.Update(pair[i]);
                result.Add($"{instance.EntrySignal}|{instance.ExitSignal}");
            }

            return result;
        }
    }

    [Fact]
    public void Engine_feeds_the_reference_series_and_stays_deterministic()
    {
        var symbols = Enumerable.Range(0, 4).Select(i => Synthetic.RandomWalk($"S{i}USDT", 1500, 40 + i)).ToList();
        var btc = Synthetic.RandomWalk("BTCUSDT", 1500, 77);
        var input = new BacktestInput(
            symbols.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s),
            symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2),
            Synthetic.At(300),
            symbols[0][^1].OpenTime.AddHours(4),
            Reference: btc);
        var strategy = DslStrategy.FromJson(Filtered);

        var withBtc = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
        var again = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
        var withoutBtc = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input with { Reference = null });

        Assert.NotEmpty(withBtc.Trades);
        Assert.Equal(withBtc.Hash(), again.Hash());
        Assert.Empty(withoutBtc.Trades);
    }
}
