using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using static PortfolioIntelligence.Backtest.Tests.Support.Synthetic;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>
/// Two-phase step and allocator overrides (m8-protocol §3, ADR-0023): invariants I-1…I-4 on synthetic random walks.
/// Exits, stops and fills are produced by <see cref="PortfolioEngine.BeginStep"/>; an override only reaches the
/// new entries sized by <see cref="PortfolioEngine.CompleteStep"/>.
/// </summary>
public class AllocatorOverrideTests
{
    private static readonly string[] Symbols = ["AUSDT", "BUSDT", "CUSDT", "DUSDT", "EUSDT"];

    private static readonly EngineEventKind[] EntryPassKinds =
    [
        EngineEventKind.EntrySignal, EngineEventKind.UnitWeight, EngineEventKind.Approved, EngineEventKind.Clipped,
        EngineEventKind.Rejected, EngineEventKind.AllocatorAdjusted, EngineEventKind.AllocatorIgnored,
    ];

    private static List<Candle>[] Series(int seed) =>
        Symbols.Select((s, i) => RandomWalk(s, 900, seed + i)).ToArray();

    private static PortfolioEngine Engine(RiskConfig? risk = null, ISet<string>? experimental = null) =>
        new(new PortfolioSetup(
            [new SleeveSpec("st", new SupertrendStrategy(), 5_000m), new SleeveSpec("ema", new EmaTrendStrategy(), 5_000m)],
            CostModel.V1, risk ?? RiskConfig.Policy, Symbols, CandleInterval.FourHours, At(250), experimental?.ToHashSet(), RecordEvents: true));

    private static IEnumerable<(DateTimeOffset T, Dictionary<string, Candle> Bars)> Bars(List<Candle>[] series)
    {
        var byTime = series.Select(s => s.ToDictionary(c => c.OpenTime)).ToArray();
        for (var t = series.Min(s => s[0].OpenTime); t <= series.Max(s => s[^1].OpenTime); t += TimeSpan.FromHours(4))
        {
            var bars = new Dictionary<string, Candle>();
            for (var i = 0; i < series.Length; i++)
            {
                if (byTime[i].TryGetValue(t, out var bar))
                {
                    bars[Symbols[i]] = bar;
                }
            }

            yield return (t, bars);
        }
    }

    /// <summary>Random overrides for the candidates, plus one for an open position (which must be ignored).</summary>
    private static List<AllocationOverride> RandomOverrides(PortfolioEngine engine, IReadOnlyList<EntryCandidate> candidates, Random rng)
    {
        var overrides = candidates.Select(c => new AllocationOverride(c.Sleeve, c.Symbol, (decimal)(rng.NextDouble() * 4 - 1), rng.Next(5) == 0)).ToList();
        if (engine.Positions().FirstOrDefault() is { } held)
        {
            overrides.Add(new AllocationOverride(held.Sleeve, held.Symbol, rng.Next(2) == 0 ? 0m : 9m, rng.Next(2) == 0));
        }

        return overrides;
    }

    [Fact]
    public void Two_phases_without_overrides_equal_one_step()
    {
        var series = Series(31);
        var single = Engine();
        var phased = Engine();
        foreach (var (t, bars) in Bars(series))
        {
            single.Step(t, bars, null, StepContext.Normal);
            phased.BeginStep(t, bars, null, StepContext.Normal);
            phased.CompleteStep(null);
        }

        Assert.Equal(single.StateHash(), phased.StateHash());
        Assert.Equal(EngineEvents.Hash(single.Events, false), EngineEvents.Hash(phased.Events, false));
        Assert.Equal(EngineEvents.Hash(single.Events, true), EngineEvents.Hash(phased.Events, true));
        Assert.Contains(single.Events, e => e.Kind == EngineEventKind.Approved);
    }

    [Fact]
    public void Unit_multipliers_change_nothing_and_the_preview_matches_the_t11_weights()
    {
        var series = Series(41);
        var plain = Engine();
        var unit = Engine();
        var previews = 0;
        foreach (var (t, bars) in Bars(series))
        {
            plain.Step(t, bars, null, StepContext.Normal);
            var candidates = unit.BeginStep(t, bars, null, StepContext.Normal);
            var before = unit.Events.Count;
            unit.CompleteStep(candidates.Select(c => new AllocationOverride(c.Sleeve, c.Symbol, 1m)).ToList());
            var weights = unit.Events.Skip(before).Where(e => e.Kind == EngineEventKind.UnitWeight).Select(e => (e.Sleeve, e.Symbol, e.Value)).ToList();
            Assert.Equal(weights, candidates.Select(c => (c.Sleeve, c.Symbol, c.Weight)).ToList());
            previews += candidates.Count;
        }

        Assert.True(previews > 0);
        Assert.Equal(plain.StateHash(), unit.StateHash());
        Assert.Equal(EngineEvents.Hash(plain.Events, true), EngineEvents.Hash(unit.Events, true));
        Assert.DoesNotContain(unit.Events, e => e.Kind is EngineEventKind.AllocatorAdjusted or EngineEventKind.AllocatorIgnored);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Overrides_never_touch_exits_stops_or_positions_and_stay_in_the_band_and_caps(int seed)
    {
        var rng = new Random(seed);
        var experimental = new HashSet<string> { "DUSDT", "EUSDT" };
        var engine = Engine(RiskConfig.Policy with { ClusterCap = RiskConfig.P1ClusterCap }, experimental);
        var risk = engine.Risk;
        var adjusted = 0;
        var vetoes = 0;
        foreach (var (t, bars) in Bars(Series(100 * seed)))
        {
            var candidates = engine.BeginStep(t, bars, null, StepContext.Normal);
            var positions = engine.Positions().ToList();
            var exits = engine.PendingOrders().Where(o => !o.Entry).ToList();
            var before = engine.Events.Count;
            var overrides = RandomOverrides(engine, candidates, rng);
            engine.CompleteStep(overrides);
            var stepEvents = engine.Events.Skip(before).ToList();

            // I-2: the entry pass records no exit, stop or fill event, and leaves positions, stops and exits unchanged.
            Assert.All(stepEvents, e => Assert.Contains(e.Kind, EntryPassKinds));
            Assert.Equal(positions, engine.Positions().ToList());
            Assert.Equal(exits, engine.PendingOrders().Where(o => !o.Entry).ToList());

            // I-4: applied weight = clamp(m) × T11 (or the asset cap); a veto never becomes an entry.
            foreach (var e in stepEvents.Where(e => e.Kind == EngineEventKind.AllocatorAdjusted))
            {
                adjusted++;
                var ratio = e.After / e.Before;
                Assert.True(ratio is >= AllocatorBand.Low and <= AllocatorBand.High || e.After == risk.Settings.MaxAssetWeight, $"ratio {ratio}");
                Assert.True(e.After <= risk.Settings.MaxAssetWeight);
            }

            var entries = engine.PendingOrders().Where(o => o.Entry).ToList();
            foreach (var veto in overrides.Where(o => o.Veto && candidates.Any(c => c.Sleeve == o.Sleeve && c.Symbol == o.Symbol)))
            {
                vetoes++;
                Assert.DoesNotContain(entries, o => o.Sleeve == veto.Sleeve && o.Symbol == veto.Symbol);
                Assert.Contains(stepEvents, e => e.Kind == EngineEventKind.Rejected && e.Detail == "allocator-veto" && e.Symbol == veto.Symbol);
            }

            // I-3: entries stay below the approved caps whatever the multiplier.
            var equity = engine.TotalEquity;
            var held = positions.Where(p => !exits.Any(x => x.Sleeve == p.Sleeve && x.Symbol == p.Symbol)).ToList();
            foreach (var group in entries.GroupBy(o => o.Sleeve))
            {
                var sleeveEquity = engine.Cash(group.Key) + positions.Where(p => p.Sleeve == group.Key).Sum(p => p.Quantity * p.LastClose);
                Assert.All(group, o => Assert.True(o.Notional <= risk.Settings.MaxAssetWeight * sleeveEquity + 1e-12m));
                var sleeveHeld = held.Where(p => p.Sleeve == group.Key).Sum(p => p.Quantity * p.LastClose);
                Assert.True(group.Sum(o => o.Notional) + sleeveHeld <= risk.Settings.MaxTotalExposure * sleeveEquity + 1e-9m);
            }

            var experimentalExposure = entries.Where(o => experimental.Contains(o.Symbol)).Sum(o => o.Notional)
                + held.Where(p => experimental.Contains(p.Symbol)).Sum(p => p.Quantity * p.LastClose);
            var newExperimental = entries.Where(o => experimental.Contains(o.Symbol)).Sum(o => o.Notional);
            Assert.True(newExperimental == 0 || experimentalExposure <= risk.ExperimentalPairsMaxExposure * equity + 1e-9m);

            // Overrides for anything but a candidate are recorded as ignored.
            foreach (var other in overrides.Where(o => !candidates.Any(c => c.Sleeve == o.Sleeve && c.Symbol == o.Symbol)))
            {
                Assert.Contains(stepEvents, e => e.Kind == EngineEventKind.AllocatorIgnored && e.Sleeve == other.Sleeve && e.Symbol == other.Symbol);
            }
        }

        Assert.True(adjusted > 0);
        Assert.True(vetoes > 0);
    }

    [Fact]
    public void The_same_overrides_through_the_step_context_replay_to_the_same_state()
    {
        var series = Series(57);
        var phased = Engine();
        var replayed = Engine();
        var rng = new Random(57);
        foreach (var (t, bars) in Bars(series))
        {
            var candidates = phased.BeginStep(t, bars, null, StepContext.Normal);
            var overrides = RandomOverrides(phased, candidates, rng);
            phased.CompleteStep(overrides);
            replayed.Step(t, bars, null, new StepContext(Allocation: overrides));
        }

        Assert.Equal(phased.StateHash(), replayed.StateHash());
        Assert.Equal(EngineEvents.Hash(phased.Events, true), EngineEvents.Hash(replayed.Events, true));
        Assert.Contains(phased.Events, e => e.Kind == EngineEventKind.AllocatorAdjusted);
    }

    [Fact]
    public void A_step_must_be_completed_before_the_next_and_cannot_be_completed_twice()
    {
        var series = Series(5);
        var engine = Engine();
        var steps = Bars(series).Take(2).ToList();
        engine.BeginStep(steps[0].T, steps[0].Bars, null, StepContext.Normal);
        Assert.Throws<InvalidOperationException>(() => engine.BeginStep(steps[1].T, steps[1].Bars, null, StepContext.Normal));
        Assert.Throws<InvalidOperationException>(() => engine.Step(steps[1].T, steps[1].Bars, null, StepContext.Normal));
        engine.CompleteStep(null);
        Assert.Throws<InvalidOperationException>(() => engine.CompleteStep(null));
    }

    [Theory]
    [InlineData(-3, 0.5)]
    [InlineData(0, 0.5)]
    [InlineData(0.7, 0.7)]
    [InlineData(1.5, 1.5)]
    [InlineData(9, 1.5)]
    public void The_band_clamps_the_multiplier(decimal multiplier, decimal expected) =>
        Assert.Equal(expected, AllocatorBand.Clamp(multiplier));
}
