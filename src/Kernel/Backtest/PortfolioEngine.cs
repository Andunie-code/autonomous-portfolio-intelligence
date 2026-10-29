using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Kernel.Backtest;

/// <summary>One strategy's share of the portfolio (m5-protocol §2.2): its own cash, positions and pending orders.</summary>
public sealed record SleeveSpec(string Name, IStrategy Strategy, decimal InitialCapital);

/// <summary>Static configuration of a <see cref="PortfolioEngine"/> run.</summary>
public sealed record PortfolioSetup(
    IReadOnlyList<SleeveSpec> Sleeves,
    CostModel Costs,
    RiskConfig Risk,
    IReadOnlyList<string> Symbols,
    CandleInterval Interval,
    DateTimeOffset TradingStart,
    IReadOnlySet<string>? ExperimentalSymbols = null,
    bool RecordEvents = false,
    bool ExternalFills = false);

public enum ExternalFillKind
{
    Entry,
    Exit,
    Stop,
}

/// <summary>
/// An exchange fill applied in the external fill mode (ADR-0021 §3), aggregated per order. <see cref="Quantity"/> is
/// the base quantity the position gains or loses (net of a commission paid in the base asset), <see cref="Notional"/>
/// the quote amount paid or received and <see cref="Fee"/> a commission paid in the quote asset. An exit with
/// <see cref="ClosesPosition"/> ends the position even if an unsellable remainder (dust) stays on the exchange.
/// </summary>
public sealed record ExternalFill(
    int Sleeve,
    string Symbol,
    ExternalFillKind Kind,
    DateTimeOffset Time,
    decimal Price,
    decimal Quantity,
    decimal Notional,
    decimal Fee,
    ExitReason Reason = ExitReason.Signal,
    bool ClosesPosition = false);

/// <summary>Why a pending entry of the previous close did not fill (local filter rejection, exchange rejection, IOC expiry, …).</summary>
public sealed record ExternalOutcome(int Sleeve, string Symbol, string Reason);

/// <summary>A position whose protective stop order is not on the exchange (m6-protocol §5.3).</summary>
public sealed record ExternalPosition(int Sleeve, string Symbol);

/// <summary>What the exchange did since the previous step; recorded with the step so a replay rebuilds the same state.</summary>
public sealed record ExternalExecution(
    IReadOnlyList<ExternalFill> Fills,
    IReadOnlyList<ExternalOutcome>? Outcomes = null,
    IReadOnlyList<ExternalPosition>? Unprotected = null)
{
    public static ExternalExecution None { get; } = new([]);
}

/// <summary>An order the engine wants at the next open: an entry with its notional, or an exit with its reason.</summary>
public sealed record EngineOrder(int Sleeve, string Symbol, bool Entry, decimal Notional, ExitReason Reason);

/// <summary>
/// Operational inputs of one step (m5-protocol §2.4). They are not part of the strategy and exist only in paper and
/// replay; the backtest always passes <see cref="Normal"/> (and <see cref="Final"/> on its last bar). In the external
/// fill mode <see cref="External"/> carries the exchange's fills instead of the simulated open and stop fills.
/// <see cref="Allocation"/> carries the allocator's overrides of this step's new entries (m8-protocol §3.2).
/// </summary>
public sealed record StepContext(
    string? EntriesBlockedReason = null,
    IReadOnlySet<int>? DisabledSleeves = null,
    IReadOnlySet<(int Sleeve, string Symbol)>? ManualCloses = null,
    bool CloseAll = false,
    bool IsFinal = false,
    ExternalExecution? External = null,
    IReadOnlyList<AllocationOverride>? Allocation = null)
{
    public static StepContext Normal { get; } = new();

    public static StepContext Final { get; } = new(IsFinal: true);

    public bool IsDisabled(int sleeve) => DisabledSleeves?.Contains(sleeve) == true;
}

public enum EngineEventKind
{
    // L1: signals, fills, stops and pre-risk unit weights (ADR-0015).
    EntrySignal,
    ExitSignal,
    EntryFill,
    ExitFill,
    StopSet,
    StopRaised,
    UnitWeight,

    // L2: risk and sizer decisions.
    Approved,
    Clipped,
    Rejected,
    Canceled,

    // L2, M8 (ADR-0023): an allocator override applied to an entry (Before/After are T11 and applied weights) or ignored.
    AllocatorAdjusted,
    AllocatorIgnored,
}

/// <summary>One engine event. <see cref="Value"/> is a price, level or weight; Before/After are notionals of L2 decisions.</summary>
public sealed record EngineEvent(DateTimeOffset Time, int Sleeve, string Symbol, EngineEventKind Kind, decimal Value, string Detail, decimal Before = 0, decimal After = 0)
{
    public bool IsDecision => Kind >= EngineEventKind.Approved;

    public string Canonical() => string.Create(CultureInfo.InvariantCulture,
        $"{Time:O}|{Sleeve}|{Symbol}|{Kind}|{Value}|{Detail}|{Before}|{After}");
}

public static class EngineEvents
{
    /// <summary>Canonical text of the L1 (signals) or L2 (decisions) layer, one event per line (m5-protocol §5.1).</summary>
    public static string Canonical(IEnumerable<EngineEvent> events, bool decisions)
    {
        var s = new StringBuilder();
        foreach (var e in events.Where(e => e.IsDecision == decisions))
        {
            s.Append(e.Canonical()).Append('\n');
        }

        return s.ToString();
    }

    public static string Hash(IEnumerable<EngineEvent> events, bool decisions) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(events, decisions)))).ToLowerInvariant();
}

/// <summary>A fill for the ledger (m5-protocol §2.5). Cash change: buy −Notional − Fee; sell +Notional − Fee.</summary>
public sealed record FillRecord(DateTimeOffset Time, int Sleeve, string Symbol, bool Buy, decimal Price, decimal Quantity, decimal Notional, decimal Fee, string Reason);

/// <summary>Open position as seen by the ledger and the Ops API.</summary>
public sealed record PositionView(int Sleeve, string Symbol, DateTimeOffset EntryTime, decimal EntryPrice, decimal Quantity, decimal Stop, decimal LastClose);

/// <summary>
/// Step-wise, multi-sleeve, long-only spot engine (ADR-0018 §1). One <see cref="Step"/> processes one bar time with
/// the exact event order of docs/m2-protocol.md §2 and ADR-0014: open fills (exits, then entries), intrabar
/// protective stop, then close: indicators, equity, risk rules, exit signals and new orders for the next open.
/// With one sleeve and <see cref="StepContext.Normal"/> it reproduces the M2–M4 batch engine bit for bit.
/// D7 limits apply to the whole portfolio, sleeves in order (m5-protocol §2.2). Indicator state is not
/// serialised: it is rebuilt by replaying the recorded steps, and <see cref="StateHash"/> verifies the result.
/// </summary>
public sealed class PortfolioEngine
{
    private readonly List<FillRecord> _fills = [];
    private PortfolioSetup _setup;
    private readonly TimeSpan _length;
    private readonly Dictionary<string, int> _index;
    private readonly IReadOnlySet<string> _experimental;
    private readonly Sleeve[] _sleeves;
    private readonly List<EngineEvent> _events = [];
    private bool _begun;
    private PendingClose? _pendingClose;
    private List<EntryCandidate>? _preview;
    private decimal _peak;
    private decimal _dayStartEquity;
    private DateTimeOffset _noEntriesUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastStep;

    public PortfolioEngine(PortfolioSetup setup)
    {
        if (setup.Sleeves.Count == 0)
        {
            throw new ArgumentException("At least one sleeve is required.");
        }

        _setup = setup;
        _length = setup.Interval.FixedLength ?? throw new ArgumentException("Fixed-length interval required.");
        _index = setup.Symbols.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        _experimental = setup.ExperimentalSymbols ?? new HashSet<string>();
        _sleeves = setup.Sleeves.Select(spec => new Sleeve(spec, setup.Symbols)).ToArray();
        _peak = _dayStartEquity = setup.Sleeves.Sum(s => s.InitialCapital);
    }

    public IReadOnlyList<EngineEvent> Events => _events;

    /// <summary>Fills, recorded together with events (<see cref="PortfolioSetup.RecordEvents"/>).</summary>
    public IReadOnlyList<FillRecord> Fills => _fills;

    public RiskConfig Risk => _setup.Risk;

    /// <summary>A new RiskConfig version takes effect from the next step (m5-protocol §3); open stops keep their level.</summary>
    public void SetRisk(RiskConfig risk) => _setup = _setup with { Risk = risk };

    public DateTimeOffset? HaltedAt { get; private set; }

    public DateTimeOffset? LastStep => _lastStep;

    public int SleeveCount => _sleeves.Length;

    public decimal Cash(int sleeve) => _sleeves[sleeve].Cash;

    public IReadOnlyList<Trade> Trades(int sleeve) => _sleeves[sleeve].Trades;

    public IReadOnlyList<EquityPoint> Equity(int sleeve) => _sleeves[sleeve].Equity;

    public decimal TotalEquity => _sleeves.Sum(s => s.Cash + s.MarketValue());

    /// <summary>
    /// The effective protective stop (D25) the next step gives an entry filled at <paramref name="entryPrice"/>: read-only,
    /// from the indicator state of the last close. The exchange stop is placed with it right after the entry fill
    /// (m6-protocol §5.1), before the step that applies the fill.
    /// </summary>
    public decimal ProtectiveStopFor(int sleeve, string symbol, decimal entryPrice) =>
        ProtectiveStop.Effective(_sleeves[sleeve].States[symbol].Instance.InitialStop(entryPrice), entryPrice, _setup.Risk.Settings.MaxPositionLoss);

    /// <summary>Orders for the next open: pending exits (sleeve order, then symbol rank) and pending entries with their notional.</summary>
    public IReadOnlyList<EngineOrder> PendingOrders() =>
        _sleeves.SelectMany((s, k) => s.PendingExits.OrderBy(x => _index[x])
                .Select(x => new EngineOrder(k, x, false, 0, s.ExitReasons.GetValueOrDefault(x, ExitReason.Signal)))
                .Concat(s.PendingEntries.Select(e => new EngineOrder(k, e.Symbol, true, e.Notional, ExitReason.Signal))))
            .ToList();

    public IEnumerable<PositionView> Positions() =>
        _sleeves.SelectMany((s, i) => s.Positions.OrderBy(p => _index[p.Key]).Select(p =>
            new PositionView(i, p.Key, p.Value.EntryTime, p.Value.EntryPrice, p.Value.Quantity, p.Value.Stop, s.States[p.Key].LastClose ?? p.Value.EntryPrice)));

    public BacktestResult Result(int sleeve) =>
        new(_sleeves[sleeve].Spec.Strategy.Name, _setup.Risk.Settings.Variant, _sleeves[sleeve].Spec.InitialCapital, _sleeves[sleeve].Trades, _sleeves[sleeve].Equity, HaltedAt);

    /// <summary>Processes the bar opening at <paramref name="t"/>. Steps must be strictly increasing and on the interval grid.</summary>
    public void Step(DateTimeOffset t, IReadOnlyDictionary<string, Candle> bars, Candle? reference, StepContext context)
    {
        Begin(t, bars, reference, context);
        CompleteStep(context.Allocation);
    }

    /// <summary>
    /// First phase of a step (m8-protocol §3.1, ADR-0023): everything up to the new entries — gap rule, open, intrabar
    /// stop, close, risk rules, exit signals and trailing stops. Returns this step's entry candidates with their T11
    /// weights, previewed without overrides. <see cref="CompleteStep"/> must follow before the next step.
    /// </summary>
    public IReadOnlyList<EntryCandidate> BeginStep(DateTimeOffset t, IReadOnlyDictionary<string, Candle> bars, Candle? reference, StepContext context)
    {
        Begin(t, bars, reference, context);
        if (_pendingClose is not { } pending)
        {
            return [];
        }

        var candidates = new List<EntryCandidate>();
        _preview = candidates;
        try
        {
            EntryPass(pending, null);
        }
        finally
        {
            _preview = null;
            foreach (var sleeve in _sleeves)
            {
                sleeve.PendingEntries.Clear();
            }
        }

        return candidates;
    }

    /// <summary>
    /// Second phase of a step: sizes this step's new entries with the optional allocator <paramref name="overrides"/>
    /// (applied to entry candidates only, within <see cref="AllocatorBand"/> and below every approved cap).
    /// </summary>
    public void CompleteStep(IReadOnlyList<AllocationOverride>? overrides)
    {
        if (!_begun)
        {
            throw new InvalidOperationException("CompleteStep needs a BeginStep first.");
        }

        _begun = false;
        if (_pendingClose is { } pending)
        {
            _pendingClose = null;
            EntryPass(pending, overrides);
        }
    }

    private void Begin(DateTimeOffset t, IReadOnlyDictionary<string, Candle> bars, Candle? reference, StepContext context)
    {
        if (_begun)
        {
            throw new InvalidOperationException("The previous step was begun but not completed.");
        }

        if (_lastStep is { } last && t <= last)
        {
            throw new InvalidOperationException($"Steps must increase: {t:O} after {last:O}.");
        }

        _begun = true;

        _lastStep = t;
        var risk = _setup.Risk.Settings;
        var costs = _setup.Costs;

        // Gap rule: a missing expected candle resets the symbol's indicators and cancels its orders.
        foreach (var sleeve in _sleeves)
        {
            foreach (var symbol in _setup.Symbols.Where(s => sleeve.States[s].Seen && !bars.ContainsKey(s)))
            {
                sleeve.States[symbol].Instance.Reset();
                sleeve.States[symbol].ValidBars = 0;
                sleeve.PendingExits.Remove(symbol);
                sleeve.PendingEntries.RemoveAll(e => e.Symbol == symbol);
            }
        }

        if (_setup.ExternalFills)
        {
            // 1–2. Open and intrabar stop from the exchange's fills (ADR-0021 §3).
            ApplyExternal(t, bars, context);
        }
        else
        {
            StepOpenAndStop(t, bars, context, risk, costs);
        }

        _pendingClose = CloseStep(t, bars, reference, context, risk);
    }

    private void StepOpenAndStop(DateTimeOffset t, IReadOnlyDictionary<string, Candle> bars, StepContext context, RiskSettings risk, CostModel costs)
    {
        // 1. Open: exits first (signal, then manual), then entries unless blocked.
        var enteredNow = new HashSet<string>[_sleeves.Length];
        for (var k = 0; k < _sleeves.Length; k++)
        {
            var sleeve = _sleeves[k];
            foreach (var symbol in sleeve.PendingExits.Where(bars.ContainsKey).OrderBy(s => _index[s]).ToList())
            {
                Close(k, symbol, bars[symbol].Open, costs.OpenSlippage, t, ExitReason.Signal);
            }

            sleeve.PendingExits.Clear();
            foreach (var symbol in sleeve.Positions.Keys.Where(bars.ContainsKey).OrderBy(s => _index[s]).ToList())
            {
                if (context.CloseAll || context.ManualCloses?.Contains((k, symbol)) == true)
                {
                    Close(k, symbol, bars[symbol].Open, costs.OpenSlippage, t, ExitReason.Manual);
                }
            }

            enteredNow[k] = [];
            var blocked = context.EntriesBlockedReason ?? (context.IsDisabled(k) ? "sleeve-disabled" : context.CloseAll ? "close-all" : null);
            foreach (var entry in sleeve.PendingEntries.Where(e => bars.ContainsKey(e.Symbol)))
            {
                if (blocked is not null)
                {
                    Record(t, k, entry.Symbol, EngineEventKind.Canceled, 0, blocked, entry.Notional, 0);
                    continue;
                }

                var fill = costs.BuyPrice(bars[entry.Symbol].Open, costs.OpenSlippage);
                var notional = Math.Min(entry.Notional, sleeve.Cash / (1 + costs.FeeRate));
                if (notional < costs.MinimumNotional)
                {
                    Record(t, k, entry.Symbol, EngineEventKind.Rejected, 0, "min-notional", entry.Notional, notional);
                    continue;
                }

                var quantity = notional / fill;
                var fee = costs.Fee(notional);
                sleeve.Cash -= notional + fee;
                var strategyStop = sleeve.States[entry.Symbol].Instance.InitialStop(fill);
                var position = new Position
                {
                    EntryTime = t,
                    EntryPrice = fill,
                    Quantity = quantity,
                    EntryFee = fee,
                    EntrySlippage = quantity * (fill - bars[entry.Symbol].Open),
                    StrategyStop = strategyStop,
                    Stop = ProtectiveStop.Effective(strategyStop, fill, risk.MaxPositionLoss),
                };
                sleeve.Positions[entry.Symbol] = position;
                enteredNow[k].Add(entry.Symbol);
                Record(t, k, entry.Symbol, EngineEventKind.EntryFill, fill, "open", 0, quantity);
                if (_setup.RecordEvents)
                {
                    _fills.Add(new FillRecord(t, k, entry.Symbol, true, fill, quantity, notional, fee, "Entry"));
                }

                Record(t, k, entry.Symbol, EngineEventKind.StopSet, position.Stop, string.Create(CultureInfo.InvariantCulture, $"strategy={strategyStop}"));
            }

            sleeve.PendingEntries.Clear();
        }

        // 2. Intrabar protective stop (ADR-0014): runs regardless of kill switch or disabling.
        for (var k = 0; k < _sleeves.Length; k++)
        {
            var sleeve = _sleeves[k];
            foreach (var symbol in sleeve.Positions.Keys.Where(bars.ContainsKey).OrderBy(s => _index[s]).ToList())
            {
                if (ProtectiveStop.Trigger(bars[symbol], sleeve.Positions[symbol].Stop, enteredNow[k].Contains(symbol)) is { } stopReference)
                {
                    Close(k, symbol, stopReference, costs.StopSlippage, t, ExitReason.Stop);
                }
            }
        }
    }

    /// <summary>
    /// External fill mode (ADR-0021 §3, m6-protocol §4.1): the exchange fills replace the simulated open fills and the
    /// OHLC stop rule. Exit and stop fills close (or reduce) the positions they name; a pending exit without a fill
    /// stays pending; a pending entry fills only with its exchange fill and is otherwise canceled with the outcome's
    /// reason. Manual closes and close-all become pending exits sent at this step's close. The order is the simulated
    /// one: exits at the open, entries, then stops (a stop may hit a position entered at this open).
    /// </summary>
    private void ApplyExternal(DateTimeOffset t, IReadOnlyDictionary<string, Candle> bars, StepContext context)
    {
        var external = context.External ?? ExternalExecution.None;
        var risk = _setup.Risk.Settings;
        void Apply(int k, ExternalFillKind kind)
        {
            var sleeve = _sleeves[k];
            foreach (var fill in external.Fills.Where(f => f.Sleeve == k && f.Kind == kind).OrderBy(f => f.Time).ThenBy(f => _index.GetValueOrDefault(f.Symbol)))
            {
                if (sleeve.Positions.ContainsKey(fill.Symbol))
                {
                    ApplyExit(k, fill, t);
                }
            }

            foreach (var symbol in sleeve.PendingExits.Where(s => !sleeve.Positions.ContainsKey(s)).ToList())
            {
                sleeve.PendingExits.Remove(symbol);
                sleeve.ExitReasons.Remove(symbol);
            }
        }

        for (var k = 0; k < _sleeves.Length; k++)
        {
            var sleeve = _sleeves[k];
            Apply(k, ExternalFillKind.Exit);

            foreach (var symbol in sleeve.Positions.Keys.OrderBy(s => _index[s]).ToList())
            {
                if ((context.CloseAll || context.ManualCloses?.Contains((k, symbol)) == true) && sleeve.PendingExits.Add(symbol))
                {
                    sleeve.ExitReasons[symbol] = ExitReason.Manual;
                }
            }

            foreach (var entry in sleeve.PendingEntries)
            {
                var fills = external.Fills.Where(f => f.Sleeve == k && f.Symbol == entry.Symbol && f.Kind == ExternalFillKind.Entry).ToList();
                var quantity = fills.Sum(f => f.Quantity);
                if (quantity <= 0 || sleeve.Positions.ContainsKey(entry.Symbol))
                {
                    var reason = external.Outcomes?.FirstOrDefault(o => o.Sleeve == k && o.Symbol == entry.Symbol)?.Reason ?? "not-filled";
                    Record(t, k, entry.Symbol, EngineEventKind.Canceled, 0, reason, entry.Notional, 0);
                    continue;
                }

                var notional = fills.Sum(f => f.Notional);
                var fee = fills.Sum(f => f.Fee);
                // One fill keeps its exact price; several are volume-weighted (x · q / q is not exact in decimal).
                var price = fills.Count == 1 ? fills[0].Price : fills.Sum(f => f.Price * f.Quantity) / quantity;
                sleeve.Cash -= notional + fee;
                var strategyStop = sleeve.States[entry.Symbol].Instance.InitialStop(price);
                var position = new Position
                {
                    EntryTime = t,
                    EntryPrice = price,
                    Quantity = quantity,
                    EntryFee = fee,
                    EntrySlippage = bars.TryGetValue(entry.Symbol, out var bar) ? quantity * (price - bar.Open) : 0,
                    StrategyStop = strategyStop,
                    Stop = ProtectiveStop.Effective(strategyStop, price, risk.MaxPositionLoss),
                };
                sleeve.Positions[entry.Symbol] = position;
                Record(t, k, entry.Symbol, EngineEventKind.EntryFill, price, "exchange", 0, quantity);
                if (_setup.RecordEvents)
                {
                    _fills.Add(new FillRecord(fills.Min(f => f.Time), k, entry.Symbol, true, price, quantity, notional, fee, "Entry"));
                }

                Record(t, k, entry.Symbol, EngineEventKind.StopSet, position.Stop, string.Create(CultureInfo.InvariantCulture, $"strategy={strategyStop}"));
            }

            sleeve.PendingEntries.Clear();
        }

        // 2. Protective stops that triggered on the exchange during the bar.
        for (var k = 0; k < _sleeves.Length; k++)
        {
            Apply(k, ExternalFillKind.Stop);
        }
    }

    /// <summary>One exit or stop fill from the exchange: the position shrinks by the filled quantity or ends.</summary>
    private void ApplyExit(int k, ExternalFill fill, DateTimeOffset t)
    {
        var sleeve = _sleeves[k];
        var position = sleeve.Positions[fill.Symbol];
        var quantity = Math.Min(fill.Quantity, position.Quantity);
        var closes = fill.ClosesPosition || quantity >= position.Quantity;
        var share = position.Quantity == 0 ? 1 : quantity / position.Quantity;
        var entryFee = closes ? position.EntryFee : position.EntryFee * share;
        var reason = fill.Kind == ExternalFillKind.Stop ? ExitReason.Stop : fill.Reason;
        sleeve.Trades.Add(new Trade(fill.Symbol, position.EntryTime, position.EntryPrice, quantity, fill.Time, fill.Price, reason,
            entryFee + fill.Fee, (closes ? position.EntrySlippage : position.EntrySlippage * share)));
        sleeve.Cash += fill.Notional - fill.Fee;
        if (closes)
        {
            sleeve.Positions.Remove(fill.Symbol);
        }
        else
        {
            position.Quantity -= quantity;
            position.EntryFee -= entryFee;
            position.EntrySlippage -= position.EntrySlippage * share;
        }

        Record(t, k, fill.Symbol, EngineEventKind.ExitFill, fill.Price, reason.ToString(), 0, quantity);
        if (_setup.RecordEvents)
        {
            _fills.Add(new FillRecord(fill.Time, k, fill.Symbol, false, fill.Price, quantity, fill.Notional, fill.Fee, reason.ToString()));
        }
    }

    /// <summary>The close up to the new entries; null when no entries follow (warm-up or the final bar).</summary>
    private PendingClose? CloseStep(DateTimeOffset t, IReadOnlyDictionary<string, Candle> bars, Candle? reference, StepContext context, RiskSettings risk)
    {
        var costs = _setup.Costs;
        // 3. Close: indicators, equity, risk, signals.
        var closeTime = t + _length;
        foreach (var sleeve in _sleeves)
        {
            sleeve.Tracker?.Update(reference);
            foreach (var (symbol, bar) in bars)
            {
                if (!sleeve.States.TryGetValue(symbol, out var state))
                {
                    continue;
                }

                state.Seen = true;
                state.LastClose = bar.Close;
                state.Instance.Update(bar);
                state.ValidBars++;
                if (closeTime.TimeOfDay == TimeSpan.Zero)
                {
                    state.DayEndCloses[DateOnly.FromDateTime(t.UtcDateTime)] = bar.Close;
                }
            }
        }

        if (context.IsFinal)
        {
            for (var k = 0; k < _sleeves.Length; k++)
            {
                foreach (var symbol in _sleeves[k].Positions.Keys.OrderBy(s => _index[s]).ToList())
                {
                    Close(k, symbol, _sleeves[k].States[symbol].LastClose!.Value, costs.OpenSlippage, closeTime, ExitReason.EndOfTest);
                }
            }
        }

        var sleeveEquity = new decimal[_sleeves.Length];
        var currentEquity = 0m;
        for (var k = 0; k < _sleeves.Length; k++)
        {
            var marketValue = _sleeves[k].MarketValue();
            sleeveEquity[k] = _sleeves[k].Cash + marketValue;
            currentEquity += sleeveEquity[k];
        }

        if (t < _setup.TradingStart)
        {
            return null;
        }

        for (var k = 0; k < _sleeves.Length; k++)
        {
            var marketValue = _sleeves[k].MarketValue();
            _sleeves[k].Equity.Add(new EquityPoint(closeTime, sleeveEquity[k], sleeveEquity[k] == 0 ? 0 : marketValue / sleeveEquity[k]));
        }

        if (context.IsFinal)
        {
            return null;
        }

        _peak = Math.Max(_peak, currentEquity);
        if (risk.DrawdownHaltEnabled && HaltedAt is null && currentEquity <= _peak * (1 - risk.MaxDrawdown))
        {
            HaltedAt = closeTime;
        }

        if (currentEquity <= _dayStartEquity * (1 - risk.DailyLossLimit))
        {
            _noEntriesUntil = closeTime.AddHours(24);
        }

        if (closeTime.TimeOfDay == TimeSpan.Zero)
        {
            _dayStartEquity = currentEquity;
        }

        // External fill mode, safety net (m6-protocol §5.3): a position without a protective stop on the exchange whose
        // closed bar reached its stop leaves at the next open with a market order.
        foreach (var (k, symbol) in context.External?.Unprotected?.Select(p => (p.Sleeve, p.Symbol)) ?? [])
        {
            var sleeve = _sleeves[k];
            if (_setup.ExternalFills && sleeve.Positions.TryGetValue(symbol, out var position) && bars.TryGetValue(symbol, out var bar)
                && bar.Low <= position.Stop && sleeve.PendingExits.Add(symbol))
            {
                sleeve.ExitReasons[symbol] = ExitReason.Stop;
                Record(closeTime, k, symbol, EngineEventKind.ExitSignal, position.Stop, "unprotected-stop");
            }
        }

        var ready = new List<string>[_sleeves.Length];
        for (var k = 0; k < _sleeves.Length; k++)
        {
            var sleeve = _sleeves[k];
            ready[k] = bars.Keys.Where(s => sleeve.States.TryGetValue(s, out var state) && state.ValidBars >= sleeve.Spec.Strategy.WarmupBars).ToList();
            foreach (var symbol in ready[k].Where(sleeve.Positions.ContainsKey))
            {
                var position = sleeve.Positions[symbol];
                var instance = sleeve.States[symbol].Instance;
                if (instance.ExitSignal)
                {
                    sleeve.PendingExits.Add(symbol);
                    Record(closeTime, k, symbol, EngineEventKind.ExitSignal, 0, "signal");
                }
                else if (instance.TrailingStop is { } trailing && trailing > position.StrategyStop)
                {
                    position.StrategyStop = trailing;
                    position.Stop = ProtectiveStop.Effective(trailing, position.EntryPrice, risk.MaxPositionLoss);
                    Record(closeTime, k, symbol, EngineEventKind.StopRaised, position.Stop, string.Create(CultureInfo.InvariantCulture, $"strategy={trailing}"));
                }
            }
        }

        return new PendingClose(closeTime, sleeveEquity, currentEquity, ready, context);
    }

    /// <summary>
    /// New entries of the close (m2-protocol §2): T11 weights, the allocator overrides (m8-protocol §3.2), then the
    /// approved caps. With <see cref="_preview"/> set it only collects the candidates: events are not recorded and the
    /// caller clears the pending entries.
    /// </summary>
    private void EntryPass(PendingClose pending, IReadOnlyList<AllocationOverride>? overrides)
    {
        var (closeTime, sleeveEquity, currentEquity, ready, context) = pending;
        var risk = _setup.Risk.Settings;
        var costs = _setup.Costs;
        var portfolioBlock = HaltedAt is not null ? "drawdown-halt" : closeTime < _noEntriesUntil ? "daily-loss" : context.EntriesBlockedReason;
        var remainingAll = _sleeves.Sum(s => s.Positions.Keys.Count(p => !s.PendingExits.Contains(p)));
        var allocated = 0;
        // Latest completed UTC day: its day-end close is the close of its 20:00 candle (≤ closeTime).
        var asOf = DateOnly.FromDateTime(closeTime.UtcDateTime).AddDays(-1);
        IReadOnlyDictionary<string, int>? clusters = null;
        var candidateKeys = new HashSet<(int, string)>();
        for (var k = 0; k < _sleeves.Length; k++)
        {
            var sleeve = _sleeves[k];
            var signals = ready[k].Where(s => !sleeve.Positions.ContainsKey(s) && sleeve.States[s].Instance.EntrySignal).OrderBy(s => _index[s]).ToList();
            foreach (var symbol in signals)
            {
                Record(closeTime, k, symbol, EngineEventKind.EntrySignal, 0, "signal");
            }

            var block = portfolioBlock ?? (context.IsDisabled(k) ? "sleeve-disabled" : null);
            if (block is not null)
            {
                foreach (var symbol in signals)
                {
                    Record(closeTime, k, symbol, EngineEventKind.Rejected, 0, block);
                }

                continue;
            }

            var remaining = sleeve.Positions.Keys.Where(s => !sleeve.PendingExits.Contains(s)).ToList();
            var slots = risk.MaxOpenPositions - remainingAll - allocated;
            var withSigma = signals
                .Select(s => (Symbol: s, Sigma: InverseVolatilityAllocation.Volatility(sleeve.States[s].DayEndCloses, asOf)))
                .ToList();
            foreach (var missing in withSigma.Where(c => c.Sigma is null))
            {
                Record(closeTime, k, missing.Symbol, EngineEventKind.Rejected, 0, "no-volatility");
            }

            var eligible = withSigma.Where(c => c.Sigma is not null).ToList();
            var candidates = eligible.Take(Math.Max(slots, 0)).ToList();
            foreach (var dropped in eligible.Skip(candidates.Count))
            {
                Record(closeTime, k, dropped.Symbol, EngineEventKind.Rejected, 0, "max-positions");
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            var volatilities = candidates.ToDictionary(c => c.Symbol, c => c.Sigma!.Value);
            foreach (var symbol in remaining)
            {
                var sigma = InverseVolatilityAllocation.Volatility(sleeve.States[symbol].DayEndCloses, asOf);
                if (sigma is { } value)
                {
                    volatilities[symbol] = value;
                }
            }

            var weights = InverseVolatilityAllocation.Weights(volatilities, risk);

            // Allocator overrides (m8-protocol §3.2): candidates only, multiplier clamped to the band, weight to the asset cap.
            var applied = new Dictionary<string, decimal>();
            var vetoed = new HashSet<string>();
            foreach (var candidate in candidates)
            {
                var symbol = candidate.Symbol;
                _preview?.Add(new EntryCandidate(k, symbol, weights[symbol], candidate.Sigma!.Value));
                candidateKeys.Add((k, symbol));
                if (overrides?.FirstOrDefault(o => o.Sleeve == k && o.Symbol == symbol) is not { } adjustment)
                {
                    applied[symbol] = weights[symbol];
                }
                else if (adjustment.Veto)
                {
                    vetoed.Add(symbol);
                }
                else
                {
                    applied[symbol] = Math.Min(AllocatorBand.Clamp(adjustment.Multiplier) * weights[symbol], risk.MaxAssetWeight);
                }
            }

            var openValue = remaining.Sum(s => sleeve.Positions[s].Quantity * sleeve.States[s].LastClose!.Value);
            var targets = candidates.Where(c => !vetoed.Contains(c.Symbol)).ToDictionary(c => c.Symbol, c => applied[c.Symbol] * sleeveEquity[k]);
            var room = Math.Max(0, risk.MaxTotalExposure * sleeveEquity[k] - openValue);
            var requested = targets.Values.Sum();
            var scale = requested > room && requested > 0 ? room / requested : 1m;
            foreach (var candidate in candidates)
            {
                var symbol = candidate.Symbol;
                Record(closeTime, k, symbol, EngineEventKind.UnitWeight, weights[symbol], "inverse-volatility");
                if (vetoed.Contains(symbol))
                {
                    Record(closeTime, k, symbol, EngineEventKind.Rejected, 0, "allocator-veto", weights[symbol] * sleeveEquity[k], 0);
                    continue;
                }

                if (applied[symbol] != weights[symbol])
                {
                    Record(closeTime, k, symbol, EngineEventKind.AllocatorAdjusted, applied[symbol], AllocatorBand.Version, weights[symbol], applied[symbol]);
                }

                var before = targets[symbol];
                var notional = before * scale;
                var reason = scale < 1m ? "total-exposure" : null;

                // Portfolio-wide caps (only binding with other sleeves' holdings or experimental/cluster rules).
                var held = HeldValue(s => s == symbol) + PendingNotional(s => s == symbol);
                if (held > 0 && notional > risk.MaxAssetWeight * currentEquity - held)
                {
                    notional = Math.Max(0, risk.MaxAssetWeight * currentEquity - held);
                    reason = "asset-cap";
                }

                if (_experimental.Contains(symbol))
                {
                    var limit = _setup.Risk.ExperimentalPairsMaxExposure * currentEquity - HeldValue(_experimental.Contains) - PendingNotional(_experimental.Contains);
                    if (notional > limit)
                    {
                        notional = Math.Max(0, limit);
                        reason = "experimental-cap";
                    }
                }

                if (_setup.Risk.ClusterCap is { } cap)
                {
                    clusters ??= CorrelationClusters.Assign(_setup.Symbols, _sleeves[0].DayEndCloses(), asOf, InverseVolatilityAllocation.VolatilityDays, cap.Threshold);
                    var cluster = clusters[symbol];
                    bool InCluster(string s) => clusters.TryGetValue(s, out var c) && c == cluster;
                    var limit = cap.MaxExposure * currentEquity - HeldValue(InCluster) - PendingNotional(InCluster);
                    if (notional > limit)
                    {
                        notional = Math.Max(0, limit);
                        reason = "cluster-cap";
                    }
                }

                if (reason is "asset-cap" or "experimental-cap" or "cluster-cap" && notional < costs.MinimumNotional)
                {
                    Record(closeTime, k, symbol, EngineEventKind.Rejected, 0, reason, before, notional);
                    continue;
                }

                sleeve.PendingEntries.Add(new PendingEntry(symbol, notional));
                allocated++;
                Record(closeTime, k, symbol, reason is null ? EngineEventKind.Approved : EngineEventKind.Clipped, 0, reason ?? "ok", before, notional);
            }
        }

        // An override for anything but an entry candidate (an open position, a pending exit, no signal) has no effect.
        foreach (var ignored in overrides?.Where(o => !candidateKeys.Contains((o.Sleeve, o.Symbol))) ?? [])
        {
            Record(closeTime, ignored.Sleeve, ignored.Symbol, EngineEventKind.AllocatorIgnored, 0, "not-candidate");
        }
    }

    /// <summary>SHA-256 of the portfolio state (cash, positions, stops, pending orders, risk state, warm-up counters).</summary>
    public string StateHash()
    {
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"step={_lastStep:O};peak={_peak};day={_dayStartEquity};noentries={_noEntriesUntil:O};halt={HaltedAt:O}\n");
        for (var k = 0; k < _sleeves.Length; k++)
        {
            var sleeve = _sleeves[k];
            s.Append(CultureInfo.InvariantCulture, $"sleeve={k};cash={sleeve.Cash};trades={sleeve.Trades.Count}\n");
            foreach (var (symbol, p) in sleeve.Positions.OrderBy(p => _index[p.Key]))
            {
                s.Append(CultureInfo.InvariantCulture, $"pos={symbol};{p.EntryTime:O};{p.EntryPrice};{p.Quantity};{p.StrategyStop};{p.Stop}\n");
            }

            foreach (var symbol in sleeve.PendingExits.OrderBy(x => _index[x]))
            {
                s.Append("exit=").Append(symbol);
                if (sleeve.ExitReasons.TryGetValue(symbol, out var reason))
                {
                    s.Append(';').Append(reason); // external fill mode only: simulated hashes are unchanged
                }

                s.Append('\n');
            }

            foreach (var entry in sleeve.PendingEntries)
            {
                s.Append(CultureInfo.InvariantCulture, $"entry={entry.Symbol};{entry.Notional}\n");
            }

            foreach (var symbol in _setup.Symbols)
            {
                var state = sleeve.States[symbol];
                s.Append(CultureInfo.InvariantCulture, $"sym={symbol};{state.Seen};{state.ValidBars};{state.LastClose}\n");
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToString()))).ToLowerInvariant();
    }

    private decimal HeldValue(Func<string, bool> include) =>
        _sleeves.Sum(s => s.Positions.Where(p => include(p.Key) && !s.PendingExits.Contains(p.Key)).Sum(p => p.Value.Quantity * s.States[p.Key].LastClose!.Value));

    private decimal PendingNotional(Func<string, bool> include) =>
        _sleeves.Sum(s => s.PendingEntries.Where(e => include(e.Symbol)).Sum(e => e.Notional));

    private void Record(DateTimeOffset time, int sleeve, string symbol, EngineEventKind kind, decimal value, string detail, decimal before = 0, decimal after = 0)
    {
        if (_setup.RecordEvents && _preview is null)
        {
            _events.Add(new EngineEvent(time, sleeve, symbol, kind, value, detail, before, after));
        }
    }

    private void Close(int k, string symbol, decimal reference, decimal slippage, DateTimeOffset time, ExitReason reason)
    {
        var sleeve = _sleeves[k];
        var costs = _setup.Costs;
        var position = sleeve.Positions[symbol];
        var fill = costs.SellPrice(reference, slippage);
        var proceeds = position.Quantity * fill;
        var fee = costs.Fee(proceeds);
        sleeve.Trades.Add(new Trade(symbol, position.EntryTime, position.EntryPrice, position.Quantity, time, fill, reason,
            position.EntryFee + fee, position.EntrySlippage + position.Quantity * (reference - fill)));
        sleeve.Positions.Remove(symbol);
        sleeve.Cash += proceeds - fee;
        Record(time, k, symbol, EngineEventKind.ExitFill, fill, reason.ToString(), 0, position.Quantity);
        if (_setup.RecordEvents)
        {
            _fills.Add(new FillRecord(time, k, symbol, false, fill, position.Quantity, proceeds, fee, reason.ToString()));
        }
    }

    private sealed class SymbolState(IStrategyInstance instance)
    {
        public IStrategyInstance Instance { get; } = instance;
        public int ValidBars { get; set; }
        public bool Seen { get; set; }
        public decimal? LastClose { get; set; }
        public Dictionary<DateOnly, decimal> DayEndCloses { get; } = [];
    }

    private sealed class Position
    {
        public required DateTimeOffset EntryTime { get; init; }
        public required decimal EntryPrice { get; init; }
        public required decimal Quantity { get; set; }
        public required decimal EntryFee { get; set; }
        public required decimal EntrySlippage { get; set; }
        public required decimal StrategyStop { get; set; }
        public required decimal Stop { get; set; }
    }

    private sealed record PendingEntry(string Symbol, decimal Notional);

    /// <summary>State of a begun step that the entry pass needs (ADR-0023).</summary>
    private sealed record PendingClose(DateTimeOffset CloseTime, decimal[] SleeveEquity, decimal CurrentEquity, List<string>[] Ready, StepContext Context);

    private sealed class Sleeve
    {
        public Sleeve(SleeveSpec spec, IReadOnlyList<string> symbols)
        {
            Spec = spec;
            // Reference series (m3-protocol §2): one tracker per sleeve, fed the candle with the same open time.
            var consumer = spec.Strategy as IReferenceSeriesConsumer;
            Tracker = consumer?.CreateReferenceTracker();
            States = symbols.ToDictionary(s => s, _ => new SymbolState(Tracker is null ? spec.Strategy.CreateInstance() : consumer!.CreateInstance(Tracker)));
            Cash = spec.InitialCapital;
        }

        public SleeveSpec Spec { get; }

        public IReferenceTracker? Tracker { get; }

        public Dictionary<string, SymbolState> States { get; }

        public decimal Cash { get; set; }

        public Dictionary<string, Position> Positions { get; } = [];

        public HashSet<string> PendingExits { get; } = [];

        /// <summary>External fill mode: why a pending exit is pending when it is not a strategy signal (manual close, unprotected stop).</summary>
        public Dictionary<string, ExitReason> ExitReasons { get; } = [];

        public List<PendingEntry> PendingEntries { get; } = [];

        public List<Trade> Trades { get; } = [];

        public List<EquityPoint> Equity { get; } = [];

        public decimal MarketValue() => Positions.Sum(p => p.Value.Quantity * States[p.Key].LastClose!.Value);

        public IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, decimal>> DayEndCloses() =>
            States.ToDictionary(s => s.Key, s => (IReadOnlyDictionary<DateOnly, decimal>)s.Value.DayEndCloses);
    }
}
