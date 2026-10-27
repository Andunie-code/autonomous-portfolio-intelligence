using System.Text.RegularExpressions;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// Kernel external fill mode (ADR-0021 §3): fed with exactly the fills the simulated engine produced, the external
/// mode reproduces every step's state hash and the L1 and L2 event streams (apart from the entry fill's source label).
/// So on Testnet only the fills differ from paper; signals, risk and sizing are the same code. No database.
/// </summary>
public class ExternalFillModeTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static ExternalFill ToExternal(FillRecord f) => f.Buy
        ? new ExternalFill(f.Sleeve, f.Symbol, ExternalFillKind.Entry, f.Time, f.Price, f.Quantity, f.Notional, f.Fee)
        : new ExternalFill(f.Sleeve, f.Symbol, f.Reason == nameof(ExitReason.Stop) ? ExternalFillKind.Stop : ExternalFillKind.Exit, f.Time, f.Price, f.Quantity, f.Notional, f.Fee,
            Enum.Parse<ExitReason>(f.Reason), ClosesPosition: true);

    [Fact]
    public void The_external_mode_rebuilds_the_simulated_run_from_its_fills()
    {
        var manifest = CohortManifestReader.Read(TestCohorts.Kohort0Json());
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var candles = symbols.Select((s, i) => (s, LiveSynthetic.Walk(s, Start, 900, 71 + i).ToDictionary(c => c.OpenTime))).ToDictionary(x => x.s, x => x.Item2);
        PortfolioEngine Engine(bool external) => new(new PortfolioSetup(
            manifest.Members.Select(m => new SleeveSpec(m.Name, DslStrategy.FromJson(m.DefinitionJson), 5_000m)).ToList(),
            CostModel.V1, PaperTrader.InitialRisk(manifest).ToConfig(), symbols, CandleInterval.FourHours, Start.AddDays(45), new HashSet<string>(), RecordEvents: true, ExternalFills: external));
        var simulated = Engine(external: false);
        var external = Engine(external: true);

        for (var t = Start; t < Start.AddHours(4 * 900); t = t.AddHours(4))
        {
            var bars = symbols.ToDictionary(s => s, s => candles[s][t]);
            var (fillsBefore, eventsBefore) = (simulated.Fills.Count, simulated.Events.Count);
            simulated.Step(t, bars, bars["BTCUSDT"], StepContext.Normal);
            var fills = simulated.Fills.Skip(fillsBefore).Select(ToExternal).ToList();
            // An entry refused at the open (e.g. too small for the cash left) is what the exchange outcome reports.
            var outcomes = simulated.Events.Skip(eventsBefore).Where(e => e.Time == t && e.Kind is EngineEventKind.Rejected or EngineEventKind.Canceled)
                .Select(e => new ExternalOutcome(e.Sleeve, e.Symbol, e.Detail)).ToList();
            external.Step(t, bars, bars["BTCUSDT"], new StepContext(External: new ExternalExecution(fills, outcomes)));
            Assert.Equal(simulated.StateHash(), external.StateHash());
        }

        static string Normalise(string canonical) => canonical.Replace("|EntryFill|", "|Fill|").Replace("|open|", "|src|").Replace("|exchange|", "|src|");
        Assert.Contains(simulated.Events, e => e.Kind == EngineEventKind.ExitFill && e.Detail == "Stop");
        Assert.Contains(simulated.Events, e => e.Kind == EngineEventKind.ExitFill && e.Detail == "Signal");
        Assert.Equal(Normalise(EngineEvents.Canonical(simulated.Events, false)), Normalise(EngineEvents.Canonical(external.Events, false)));
        // At the open the external mode records an unfilled entry as Canceled with the exchange's reason.
        var expected = Regex.Replace(EngineEvents.Canonical(simulated.Events, true), @"\|Rejected\|0\|min-notional\|([^|\n]*)\|[^|\n]*\n", "|Canceled|0|min-notional|$1|0\n");
        Assert.Equal(expected, EngineEvents.Canonical(external.Events, true));
        Assert.Equal(simulated.Fills.Count, external.Fills.Count);
    }

    [Fact]
    public void Unfilled_entries_are_canceled_with_the_exchange_reason_and_unprotected_positions_exit_at_their_stop()
    {
        var manifest = CohortManifestReader.Read(TestCohorts.Kohort0Json());
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var candles = symbols.Select((s, i) => (s, LiveSynthetic.Walk(s, Start, 700, 11 + i).ToDictionary(c => c.OpenTime))).ToDictionary(x => x.s, x => x.Item2);
        var engine = new PortfolioEngine(new PortfolioSetup(
            manifest.Members.Select(m => new SleeveSpec(m.Name, DslStrategy.FromJson(m.DefinitionJson), 5_000m)).ToList(),
            CostModel.V1, PaperTrader.InitialRisk(manifest).ToConfig(), symbols, CandleInterval.FourHours, Start.AddDays(45), new HashSet<string>(), RecordEvents: true, ExternalFills: true));

        var canceled = false;
        var t = Start;
        for (; t < Start.AddHours(4 * 700) && !canceled; t = t.AddHours(4))
        {
            var bars = symbols.ToDictionary(s => s, s => candles[s][t]);
            var outcomes = engine.PendingOrders().Where(o => o.Entry).Select(o => new ExternalOutcome(o.Sleeve, o.Symbol, "ioc-expired")).ToList();
            engine.Step(t, bars, bars["BTCUSDT"], new StepContext(External: new ExternalExecution([], outcomes)));
            canceled = outcomes.Count > 0;
        }

        Assert.True(canceled);
        Assert.Contains(engine.Events, e => e.Kind == EngineEventKind.Canceled && e.Detail == "ioc-expired");
        Assert.Empty(engine.Positions()); // nothing filled, nothing held

        // A pending entry that fills, then the position loses its exchange stop and the bar reaches the stop level.
        var entry = engine.PendingOrders().FirstOrDefault(o => o.Entry);
        for (; entry is null && t < Start.AddHours(4 * 700); t = t.AddHours(4))
        {
            var bars = symbols.ToDictionary(s => s, s => candles[s][t]);
            engine.Step(t, bars, bars["BTCUSDT"], new StepContext(External: ExternalExecution.None));
            entry = engine.PendingOrders().FirstOrDefault(o => o.Entry);
        }

        Assert.NotNull(entry);
        var open = candles[entry.Symbol][t].Open;
        var quantity = Math.Round(entry.Notional / open, 6);
        var fill = new ExternalFill(entry.Sleeve, entry.Symbol, ExternalFillKind.Entry, t.AddSeconds(20), open, quantity, quantity * open, 0);
        var low = symbols.ToDictionary(s => s, s => candles[s][t]);
        low[entry.Symbol] = low[entry.Symbol] with { Low = 0.000001m }; // the bar falls through any stop
        engine.Step(t, low, low["BTCUSDT"], new StepContext(External: new ExternalExecution([fill], null, [new ExternalPosition(entry.Sleeve, entry.Symbol)])));

        Assert.Single(engine.Positions());
        Assert.Contains(engine.Events, e => e.Kind == EngineEventKind.ExitSignal && e.Detail == "unprotected-stop");
        var exit = Assert.Single(engine.PendingOrders(), o => !o.Entry);
        Assert.Equal(ExitReason.Stop, exit.Reason);
    }
}
