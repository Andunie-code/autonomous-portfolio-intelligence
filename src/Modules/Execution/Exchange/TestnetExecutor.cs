using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution.Exchange;

/// <summary>Result of the account-wide Testnet reconciliation (m6-protocol §4.3).</summary>
public sealed record TestnetReconciliation(bool Ok, string? Reason, object Detail);

/// <summary>
/// Order execution on the exchange for Testnet runs (m6-protocol §2–§5, ADR-0021). Every order goes through the
/// outbox: the order and its Submitting event are stored before sending; a timeout or 5xx leaves it Unknown and it is
/// resolved by its client order id, never sent again, until the exchange can no longer accept it (NeverAccepted after
/// timestamp + recvWindow + 60 s and two consecutive "does not exist" answers). Filters are checked locally before
/// sending. Each fill is assigned to the bar whose step applies it. Protective stops follow the exchange holdings:
/// placed right after an entry fill, raised with cancel-replace, cancelled before an exit; kill switch cancels entries
/// only (D11 rev.).
/// </summary>
public sealed class TestnetExecutor(IExchangeAdapter exchange, ExchangeStore store, ExchangeClock clock, Func<string, object, CancellationToken, Task> audit)
{
    /// <summary>A-1: an entry buys with an IOC limit at most 1 % above the reference price.</summary>
    public const decimal EntryBand = 0.01m;

    /// <summary>A-2: the fallback STOP_LOSS_LIMIT sells down to 2 % below the stop price.</summary>
    public const decimal StopLimitBand = 0.02m;

    /// <summary>A-3: margin after timestamp + recvWindow before an order that the exchange does not know is NeverAccepted.</summary>
    public static readonly TimeSpan UnknownMargin = TimeSpan.FromSeconds(60);

    public const int NeverAcceptedQueries = 2;

    private IReadOnlyDictionary<string, SymbolRules> _rules = new Dictionary<string, SymbolRules>();

    public IReadOnlyDictionary<string, SymbolRules> Rules => _rules;

    public ExchangeClock Clock => clock;

    public IExchangeAdapter Exchange => exchange;

    /// <summary>Start of a cycle: server time offset and the symbol rules (a changed rule snapshot is stored and audited).</summary>
    public async Task PrepareAsync(IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        await exchange.ServerTimeAsync(ct);
        _rules = await exchange.SymbolRulesAsync(symbols, ct);
        var text = string.Join('\n', _rules.Values.OrderBy(r => r.Symbol, StringComparer.Ordinal).Select(r => r.Canonical()));
        var hash = DslCanonical.Sha256(text);
        if (hash != await store.LatestRulesHashAsync(ct))
        {
            await store.AddRulesAsync(_rules, hash, ct);
            await audit("exchange_rules_snapshot", new { hash, symbols = _rules.Count, missing = symbols.Except(_rules.Keys).Order(StringComparer.Ordinal).ToList() }, ct);
        }
    }

    public async Task<AccountSnapshot> AccountAsync(CancellationToken ct) => await exchange.AccountAsync(ct);

    public Task<IReadOnlySet<string>> BaselineSourcesAsync(CancellationToken ct) => store.BaselineSourcesAsync(ct);

    public Task BaselineAsync(long runId, string source, AccountSnapshot account, CancellationToken ct) =>
        store.AddBaselineAsync(runId, source, account.Balances.ToDictionary(b => b.Key, b => b.Value.Total), ct);

    // ---------------------------------------------------------------- holdings

    private string BaseAsset(string symbol) => _rules.TryGetValue(symbol, out var r) ? r.BaseAsset : symbol.EndsWith("USDT", StringComparison.Ordinal) ? symbol[..^4] : symbol;

    private string QuoteAsset(string symbol) => _rules.TryGetValue(symbol, out var r) ? r.QuoteAsset : "USDT";

    /// <summary>Base quantity a buy adds: the commission is deducted when it is paid in the base asset.</summary>
    private decimal Net(OutboxFill f) => f.Side == OrderSide.Buy
        ? f.Quantity - (f.CommissionAsset == BaseAsset(f.Symbol) ? f.Commission : 0)
        : -f.Quantity;

    /// <summary>What the run holds on the exchange per (sleeve, symbol), from its recorded fills (dust included).</summary>
    public async Task<Dictionary<(int Sleeve, string Symbol), decimal>> HeldAsync(long runId, DateTimeOffset? throughStep, CancellationToken ct)
    {
        var held = new Dictionary<(int, string), decimal>();
        foreach (var f in await store.FillsAsync([runId], 0, ct))
        {
            if (throughStep is { } t && f.StepBar > t)
            {
                continue;
            }

            held[(f.Sleeve, f.Symbol)] = held.GetValueOrDefault((f.Sleeve, f.Symbol)) + Net(f);
        }

        return held;
    }

    /// <summary>The sellable part of a holding (rounded to the lot step; zero below the minimum quantity).</summary>
    public decimal Tradable(string symbol, decimal quantity, bool market)
    {
        if (!_rules.TryGetValue(symbol, out var rules))
        {
            return Math.Max(quantity, 0);
        }

        var lot = market && rules.MarketLot is { StepSize: > 0 } m ? m : rules.Lot;
        var rounded = lot is null ? quantity : ExchangeFilters.RoundDown(quantity, lot.StepSize);
        return lot is not null && rounded < lot.MinQuantity ? 0 : Math.Max(rounded, 0);
    }

    // ---------------------------------------------------------------- maintenance and Unknown resolution

    /// <summary>
    /// Brings every non-terminal order of the run up to date (m6-protocol §3.2): queries by client order id, records
    /// state changes and new trades, resolves Unknown orders (§3.3) and, with the kill switch on, cancels open entry
    /// orders and leaves protective stops on the exchange (D11 rev.). Returns the number of orders still Unknown.
    /// </summary>
    public async Task<int> MaintainAsync(PaperRun run, DateTimeOffset nextBar, bool killSwitch, CancellationToken ct)
    {
        var length = CandleInterval(run);
        foreach (var order in (await store.OrdersAsync([run.Id], ct)).Where(o => !o.IsTerminal))
        {
            await RefreshAsync(order, nextBar, length, ct);
        }

        if (killSwitch)
        {
            await CancelEntriesAsync(run, nextBar, ct);
        }

        return (await store.OrdersAsync([run.Id], ct)).Count(o => o.Status is OrderStatus.Unknown or OrderStatus.Submitting);
    }

    /// <summary>D11 rev.: open entry orders are cancelled; stop orders are not touched.</summary>
    public async Task<int> CancelEntriesAsync(PaperRun run, DateTimeOffset nextBar, CancellationToken ct)
    {
        var length = CandleInterval(run);
        var cancelled = 0;
        foreach (var order in (await store.OrdersAsync([run.Id], ct)).Where(o => o.Purpose == OrderPurpose.Entry && o.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled))
        {
            try
            {
                var state = await exchange.CancelAsync(order.Symbol, order.ClientOrderId, ct);
                if (state is null)
                {
                    await RefreshAsync(order, nextBar, length, ct);
                    continue;
                }

                await RecordAsync(order, state, nextBar, length, "kill-switch", ct);
                cancelled++;
            }
            catch (ExchangeUnknownException)
            {
                // Retried at the next maintenance.
            }
        }

        if (cancelled > 0)
        {
            await audit("testnet_entries_cancelled", new { runId = run.Id, cancelled, reason = "kill-switch" }, ct);
        }

        return cancelled;
    }

    private async Task RefreshAsync(OutboxOrder order, DateTimeOffset nextBar, TimeSpan length, CancellationToken ct)
    {
        OrderState? state;
        try
        {
            state = await exchange.QueryAsync(order.Symbol, order.ClientOrderId, ct);
        }
        catch (ExchangeUnknownException)
        {
            return;
        }

        if (state is not null)
        {
            await RecordAsync(order, state, nextBar, length, null, ct);
            return;
        }

        if (order.Status is not (OrderStatus.Submitting or OrderStatus.Unknown))
        {
            // An accepted order the exchange no longer knows: lost (e.g. a Testnet reset); reconciliation reports it.
            return;
        }

        var horizon = (order.SentAt ?? clock.Now) + TimeSpan.FromMilliseconds(Binance.HmacSigningHandler.RecvWindowMs) + UnknownMargin;
        if (clock.Now < horizon)
        {
            if (order.Status == OrderStatus.Submitting)
            {
                await store.AddEventAsync(order.Id, OrderStatus.Unknown, null, null, new { query = "not-found", reason = "restart" }, ct);
            }

            return;
        }

        if (order.Status == OrderStatus.Submitting)
        {
            await store.AddEventAsync(order.Id, OrderStatus.Unknown, null, null, new { query = "not-found-after-horizon", reason = "restart" }, ct);
        }
        else
        {
            await store.AddEventAsync(order.Id, OrderStatus.Unknown, null, null, new { query = "not-found-after-horizon" }, ct);
        }

        if (order.UnknownQueries + 1 >= NeverAcceptedQueries)
        {
            await store.AddEventAsync(order.Id, OrderStatus.NeverAccepted, null, 0, new { reason = "not found after timestamp + recvWindow + 60 s" }, ct);
            await audit("exchange_order_never_accepted", new { order.ClientOrderId, order.Symbol, purpose = order.Purpose.ToString() }, ct);
        }
    }

    /// <summary>Records a new exchange state of an order and its new trades (with the step bar that applies them).</summary>
    private async Task RecordAsync(OutboxOrder order, OrderState state, DateTimeOffset nextBar, TimeSpan length, string? reason, CancellationToken ct)
    {
        if (state.Status != order.Status || state.ExecutedQuantity != order.ExecutedQuantity)
        {
            await store.AddEventAsync(order.Id, state.Status, state.OrderId, state.ExecutedQuantity, new { exchangeStatus = state.ExchangeStatus, reason }, ct);
        }

        if (state.ExecutedQuantity > 0)
        {
            var trades = state.Fills.Count > 0 && state.Fills.Sum(f => f.Quantity) == state.ExecutedQuantity && state.Fills.All(f => f.Time != default)
                ? state.Fills
                : await exchange.TradesAsync(order.Symbol, state.OrderId, ct);
            await store.AddFillsAsync(order.Id, trades, t => StepBar(order, t, nextBar, length), ct);
        }
    }

    /// <summary>
    /// The step that applies a trade (m6-protocol §4.1): an entry or exit decided at bar d fills at the next open, so
    /// its step is d + length; a stop belongs to the bar it triggered in. A fill found after that step was processed
    /// goes to the next unprocessed bar.
    /// </summary>
    public static DateTimeOffset StepBar(OutboxOrder order, ExchangeTrade trade, DateTimeOffset nextBar, TimeSpan length)
    {
        var bar = order.Purpose == OrderPurpose.Stop
            ? new DateTimeOffset(trade.Time.UtcTicks / length.Ticks * length.Ticks, TimeSpan.Zero)
            : order.DecisionBar + length;
        return bar < nextBar ? nextBar : bar;
    }

    // ---------------------------------------------------------------- placement

    internal async Task<OutboxOrder> PlaceAsync(PaperRun run, int sleeve, OrderPurpose purpose, DateTimeOffset decisionBar, OrderDraftSpec spec, DateTimeOffset nextBar, string? replaces, CancellationToken ct)
    {
        var length = CandleInterval(run);
        var orders = await store.OrdersAsync([run.Id], ct);
        var attempt = orders.Where(o => o.Sleeve == sleeve && o.Symbol == spec.Symbol && o.DecisionBar == decisionBar && o.Purpose == purpose).Select(o => o.Attempt).DefaultIfEmpty(0).Max() + 1;
        var id = ClientOrderIds.For(run.Id, sleeve, spec.Symbol, decisionBar, purpose, attempt);
        var draft = new OrderDraft(spec.Symbol, id, spec.Side, spec.Type, spec.Quantity, spec.Price, spec.StopPrice, spec.TimeInForce);
        FilterResult prepared;
        if (!_rules.TryGetValue(spec.Symbol, out var rules))
        {
            prepared = new FilterResult(null, "filter:SYMBOL_UNKNOWN");
        }
        else
        {
            var open = orders.Where(o => o.Symbol == spec.Symbol && !o.IsTerminal).ToList();
            prepared = ExchangeFilters.Prepare(draft, rules, spec.Reference, open.Count, open.Count(o => o.Type is OrderType.StopLoss or OrderType.StopLossLimit));
        }

        if (!prepared.Accepted)
        {
            await store.CreateAsync(run.Id, sleeve, purpose, decisionBar, attempt, draft, OrderStatus.LocalRejected, null, replaces, new { reason = prepared.Rejection }, ct);
            await audit("exchange_order_rejected_locally", new { runId = run.Id, sleeve, spec.Symbol, purpose = purpose.ToString(), reason = prepared.Rejection }, ct);
            return await store.OrderAsync(id, ct);
        }

        var request = prepared.Order!;
        var rounded = draft with { Quantity = request.Quantity, Price = request.Price, StopPrice = request.StopPrice };
        await store.CreateAsync(run.Id, sleeve, purpose, decisionBar, attempt, rounded, OrderStatus.Submitting, clock.Now, replaces, new { }, ct);
        var order = await store.OrderAsync(id, ct);
        try
        {
            var state = replaces is null
                ? await exchange.PlaceAsync(request, ct)
                : await CancelReplaceAsync(run, replaces, request, nextBar, length, ct);
            if (state is not null)
            {
                await RecordAsync(order, state, nextBar, length, null, ct);
            }
        }
        catch (ExchangeUnknownException e)
        {
            await store.AddEventAsync(order.Id, OrderStatus.Unknown, null, null, new { reason = e.Reason }, ct);
        }
        catch (ExchangeRejectedException e)
        {
            await store.AddEventAsync(order.Id, OrderStatus.ExchangeRejected, null, 0, new { reason = $"exchange-rejected:{e.Code}", code = e.Code, message = e.ExchangeMessage }, ct);
            if (e.Code == -1021)
            {
                await exchange.ServerTimeAsync(ct); // clock drift: refresh the offset; the intent may be sent again with a new attempt
            }
        }
        catch (ExchangeRateLimitedException e)
        {
            await store.AddEventAsync(order.Id, OrderStatus.ExchangeRejected, null, 0, new { reason = "exchange-rejected:429", retryAfterSeconds = e.RetryAfter?.TotalSeconds }, ct);
        }
        catch (ExchangeIpBannedException)
        {
            await store.AddEventAsync(order.Id, OrderStatus.ExchangeRejected, null, 0, new { reason = "exchange-rejected:418" }, ct);
            throw;
        }

        return await store.OrderAsync(id, ct);
    }

    /// <summary>Cancel-replace of a stop (S-B14, STOP_ON_FAILURE): returns the new order's state, or null if it was not placed.</summary>
    private async Task<OrderState?> CancelReplaceAsync(PaperRun run, string replaces, OrderRequest request, DateTimeOffset nextBar, TimeSpan length, CancellationToken ct)
    {
        var old = await store.OrderAsync(replaces, ct);
        var result = await exchange.CancelReplaceAsync(request.Symbol, replaces, request, ct);
        if (result.CancelSucceeded)
        {
            await store.AddEventAsync(old.Id, OrderStatus.Canceled, old.ExchangeOrderId, old.ExecutedQuantity, new { reason = "cancel-replace", by = request.ClientOrderId }, ct);
        }
        else
        {
            await RefreshAsync(old, nextBar, length, ct); // the old stop may have filled meanwhile
        }

        if (result.NewOrder is { } state)
        {
            return state;
        }

        var code = result.NewOrderErrorCode ?? result.CancelErrorCode ?? 0;
        throw new ExchangeRejectedException(code, result.NewOrderError ?? result.CancelError ?? "cancel-replace failed", 409);
    }

    internal sealed record OrderDraftSpec(string Symbol, OrderSide Side, OrderType Type, decimal Quantity, decimal? Price, decimal? StopPrice, string? TimeInForce, decimal Reference);

    // ---------------------------------------------------------------- intents of a step

    /// <summary>
    /// Sends the engine's orders for the next open (decided at <paramref name="decisionBar"/>), idempotently: an intent
    /// that already has an order is skipped. Exits first (stop cancelled, then a market sell); entries only while the
    /// next bar is still open and nothing blocks them. Then the protective stops are synchronised.
    /// </summary>
    public async Task SubmitAsync(PaperRun run, PortfolioEngine engine, DateTimeOffset decisionBar, DateTimeOffset now, string? entriesBlocked, CancellationToken ct)
    {
        var length = CandleInterval(run);
        var nextBar = decisionBar + length;
        var intents = engine.PendingOrders();
        var orders = await store.OrdersAsync([run.Id], ct);
        bool Exists(int sleeve, string symbol, OrderPurpose purpose) =>
            orders.Any(o => o.Sleeve == sleeve && o.Symbol == symbol && o.DecisionBar == decisionBar && o.Purpose == purpose && !Retryable(o));

        foreach (var exit in intents.Where(i => !i.Entry))
        {
            var purpose = exit.Reason == ExitReason.Manual ? OrderPurpose.Close : OrderPurpose.Exit;
            if (!Exists(exit.Sleeve, exit.Symbol, purpose))
            {
                await ExitAsync(run, exit.Sleeve, exit.Symbol, purpose, decisionBar, nextBar, ct);
            }
        }

        // Entries buy at the open after the decision: not later than that bar (a late decision is not sent).
        if (entriesBlocked is null && now < nextBar + length)
        {
            foreach (var entry in intents.Where(i => i.Entry && !Exists(i.Sleeve, i.Symbol, OrderPurpose.Entry)))
            {
                decimal reference;
                try
                {
                    reference = await exchange.ReferencePriceAsync(entry.Symbol, ct);
                }
                catch (ExchangeUnknownException)
                {
                    continue; // not sent; the step records it as not-submitted
                }

                var price = reference * (1 + EntryBand);
                var tick = _rules.TryGetValue(entry.Symbol, out var r) && r.Price is { } pf ? pf.TickSize : 0;
                var limit = ExchangeFilters.RoundDown(price, tick);
                var quantity = limit > 0 ? entry.Notional / limit : 0;
                var order = await PlaceAsync(run, entry.Sleeve, OrderPurpose.Entry, decisionBar,
                    new OrderDraftSpec(entry.Symbol, OrderSide.Buy, OrderType.Limit, quantity, price, null, "IOC", reference), nextBar, null, ct);
                if (order.ExecutedQuantity > 0)
                {
                    await SyncStopsAsync(run, engine, decisionBar, ct);
                }
            }
        }

        await SyncStopsAsync(run, engine, decisionBar, ct);
    }

    /// <summary>
    /// An intent may go out again (with the next attempt) only when its order surely never executed: NeverAccepted, or
    /// refused before the matching engine for clock skew (-1021) or rate limiting (429) (m6-protocol §3.1, §3.3).
    /// </summary>
    internal static bool Retryable(OutboxOrder order) =>
        order.Status == OrderStatus.NeverAccepted
        || (order.Status == OrderStatus.ExchangeRejected && order.Reason is "exchange-rejected:-1021" or "exchange-rejected:429");

    /// <summary>
    /// Exit order sequence (m6-protocol §5.4): cancel the position's stop; if it already filled, nothing is sold;
    /// otherwise sell the sellable holding at market. A remainder gets its stop back from the stop synchronisation.
    /// </summary>
    internal async Task ExitAsync(PaperRun run, int sleeve, string symbol, OrderPurpose purpose, DateTimeOffset decisionBar, DateTimeOffset nextBar, CancellationToken ct)
    {
        var length = CandleInterval(run);
        foreach (var stop in (await store.OrdersAsync([run.Id], ct)).Where(o => o.Sleeve == sleeve && o.Symbol == symbol && o.Purpose == OrderPurpose.Stop && !o.IsTerminal))
        {
            try
            {
                var state = await exchange.CancelAsync(symbol, stop.ClientOrderId, ct);
                if (state is null)
                {
                    await RefreshAsync(stop, nextBar, length, ct);
                }
                else
                {
                    await RecordAsync(stop, state, nextBar, length, $"exit:{purpose}", ct);
                }
            }
            catch (ExchangeUnknownException)
            {
                return; // the stop's state is unknown: no sell now (it could be filling); retried next cycle
            }
        }

        var held = (await HeldAsync(run.Id, null, ct)).GetValueOrDefault((sleeve, symbol));
        var quantity = Tradable(symbol, held, market: true);
        if (quantity <= 0)
        {
            return; // the stop sold it, or only dust is left
        }

        decimal reference;
        try
        {
            reference = await exchange.ReferencePriceAsync(symbol, ct);
        }
        catch (ExchangeUnknownException)
        {
            return;
        }

        await PlaceAsync(run, sleeve, purpose, decisionBar, new OrderDraftSpec(symbol, OrderSide.Sell, OrderType.Market, quantity, null, null, null, reference), nextBar, null, ct);
    }

    /// <summary>
    /// Protective stops follow the holdings (m6-protocol §5.1–§5.3): every sellable holding without an exit in progress
    /// has one stop at the engine's effective stop (for a fill not yet stepped: the stop the next step will set). A
    /// missing stop is placed; a changed level or quantity is replaced; a stop without holding is cancelled. Returns
    /// the positions left without an accepted stop.
    /// </summary>
    public async Task<IReadOnlyList<ExternalPosition>> SyncStopsAsync(PaperRun run, PortfolioEngine engine, DateTimeOffset decisionBar, CancellationToken ct)
    {
        var length = CandleInterval(run);
        var nextBar = decisionBar + length;
        var held = await HeldAsync(run.Id, null, ct);
        var fills = await store.FillsAsync([run.Id], 0, ct);
        var positions = engine.Positions().ToDictionary(p => (p.Sleeve, p.Symbol));
        var orders = await store.OrdersAsync([run.Id], ct);
        var unprotected = new List<ExternalPosition>();
        foreach (var ((sleeve, symbol), quantity) in held.OrderBy(h => h.Key.Item1).ThenBy(h => h.Key.Item2, StringComparer.Ordinal))
        {
            var stopQuantity = Tradable(symbol, quantity, market: false);
            var active = orders.Where(o => o.Sleeve == sleeve && o.Symbol == symbol && o.Purpose == OrderPurpose.Stop && !o.IsTerminal).ToList();
            var exiting = orders.Any(o => o.Sleeve == sleeve && o.Symbol == symbol && o.Purpose is OrderPurpose.Exit or OrderPurpose.Close && !o.IsTerminal);
            if (stopQuantity <= 0 || exiting)
            {
                if (stopQuantity <= 0)
                {
                    foreach (var stop in active.Where(s => s.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled))
                    {
                        await CancelQuietlyAsync(stop, nextBar, length, ct);
                    }
                }

                continue;
            }

            decimal level;
            if (positions.TryGetValue((sleeve, symbol), out var position))
            {
                level = position.Stop;
            }
            else
            {
                var entries = fills.Where(f => f.Sleeve == sleeve && f.Symbol == symbol && f.Purpose == OrderPurpose.Entry && f.StepBar > (engine.LastStep ?? DateTimeOffset.MinValue)).ToList();
                if (entries.Count == 0)
                {
                    continue; // holding with neither a position nor a pending entry: reconciliation's case
                }

                var price = entries.Sum(f => f.Price * f.Quantity) / entries.Sum(f => f.Quantity);
                level = engine.ProtectiveStopFor(sleeve, symbol, price);
            }

            var rules = _rules.GetValueOrDefault(symbol);
            var type = rules is null || rules.OrderTypes.Contains(OrderType.StopLoss) ? OrderType.StopLoss : OrderType.StopLossLimit;
            var stopPrice = rules?.Price is { } pf ? ExchangeFilters.RoundUp(level, pf.TickSize) : level;
            var current = active.LastOrDefault();
            if (current is not null && current.StopPrice == stopPrice && current.Quantity == stopQuantity && current.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled)
            {
                continue;
            }

            if (current is not null && current.Status is OrderStatus.Unknown or OrderStatus.Submitting or OrderStatus.CancelRequested)
            {
                unprotected.Add(new ExternalPosition(sleeve, symbol)); // resolved first by maintenance
                continue;
            }

            var spec = new OrderDraftSpec(symbol, OrderSide.Sell, type, stopQuantity, type == OrderType.StopLossLimit ? level * (1 - StopLimitBand) : null, level,
                type == OrderType.StopLossLimit ? "GTC" : null, level);
            string? replaces = null;
            if (current is not null && current.DecisionBar == decisionBar)
            {
                await CancelQuietlyAsync(current, nextBar, length, ct); // same intent key: cancel, then place anew
            }
            else
            {
                replaces = current?.ClientOrderId;
            }

            var placed = await PlaceAsync(run, sleeve, OrderPurpose.Stop, decisionBar, spec, nextBar, replaces, ct);
            var stillProtected = (await store.OrdersAsync([run.Id], ct)).Any(o => o.Sleeve == sleeve && o.Symbol == symbol && o.Purpose == OrderPurpose.Stop
                && o.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled);
            if (!stillProtected)
            {
                unprotected.Add(new ExternalPosition(sleeve, symbol));
                await audit("protective_stop_failed", new { runId = run.Id, sleeve, symbol, status = placed.Status.ToString(), reason = placed.Reason }, ct);
            }
        }

        return unprotected;
    }

    private async Task CancelQuietlyAsync(OutboxOrder order, DateTimeOffset nextBar, TimeSpan length, CancellationToken ct)
    {
        try
        {
            var state = await exchange.CancelAsync(order.Symbol, order.ClientOrderId, ct);
            if (state is null)
            {
                await RefreshAsync(order, nextBar, length, ct);
            }
            else
            {
                await RecordAsync(order, state, nextBar, length, "stop-replaced", ct);
            }
        }
        catch (ExchangeUnknownException)
        {
            // Resolved at the next maintenance.
        }
    }

    /// <summary>Positions of the engine without an accepted stop order (the step's safety-net input, m6-protocol §5.3).</summary>
    public async Task<IReadOnlyList<ExternalPosition>> UnprotectedAsync(PaperRun run, PortfolioEngine engine, CancellationToken ct)
    {
        var orders = await store.OrdersAsync([run.Id], ct);
        return engine.Positions()
            .Where(p => !orders.Any(o => o.Sleeve == p.Sleeve && o.Symbol == p.Symbol && o.Purpose == OrderPurpose.Stop && o.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled))
            .Select(p => new ExternalPosition(p.Sleeve, p.Symbol)).ToList();
    }

    // ---------------------------------------------------------------- step inputs

    /// <summary>True while an order decided at <paramref name="decisionBar"/> (entry, exit, close) is not resolved: the next step waits.</summary>
    public async Task<bool> PendingAsync(PaperRun run, DateTimeOffset decisionBar, CancellationToken ct) =>
        (await store.OrdersAsync([run.Id], ct)).Any(o => o.DecisionBar == decisionBar && o.Purpose != OrderPurpose.Stop && !o.IsTerminal);

    /// <summary>
    /// The exchange's part of step <paramref name="bar"/>: fills assigned to it (per order: quantity net of a base
    /// commission, quote amount, quote commission), why each pending entry did not fill, and the unprotected positions.
    /// </summary>
    public async Task<ExternalExecution> ExternalAsync(PaperRun run, PortfolioEngine engine, DateTimeOffset bar, CancellationToken ct)
    {
        var length = CandleInterval(run);
        var all = await store.FillsAsync([run.Id], 0, ct);
        var throughStep = new Dictionary<(int, string), decimal>();
        var fills = new List<ExternalFill>();
        foreach (var group in all.Where(f => f.StepBar <= bar).GroupBy(f => f.OrderId).OrderBy(g => g.Min(f => f.Id)))
        {
            var first = group.First();
            var key = (first.Sleeve, first.Symbol);
            throughStep[key] = throughStep.GetValueOrDefault(key) + group.Sum(Net);
            if (first.StepBar != bar)
            {
                continue;
            }

            var gross = group.Sum(f => f.Quantity);
            var fee = group.Where(f => f.CommissionAsset == QuoteAsset(f.Symbol)).Sum(f => f.Commission);
            var kind = first.Purpose switch
            {
                OrderPurpose.Entry => ExternalFillKind.Entry,
                OrderPurpose.Stop => ExternalFillKind.Stop,
                _ => ExternalFillKind.Exit,
            };
            fills.Add(new ExternalFill(first.Sleeve, first.Symbol, kind, group.Min(f => f.TradeTime), group.Sum(f => f.Price * f.Quantity) / gross,
                Math.Abs(group.Sum(Net)), group.Sum(f => f.QuoteQuantity), fee,
                first.Purpose == OrderPurpose.Close ? ExitReason.Manual : first.Purpose == OrderPurpose.Stop ? ExitReason.Stop : ExitReason.Signal));
        }

        // A sell that leaves only an unsellable remainder ends the position (the dust stays on the exchange).
        fills = fills.Select(f => f.Kind == ExternalFillKind.Entry ? f : f with { ClosesPosition = Tradable(f.Symbol, throughStep.GetValueOrDefault((f.Sleeve, f.Symbol)), market: true) <= 0 }).ToList();

        var orders = await store.OrdersAsync([run.Id], ct);
        var outcomes = new List<ExternalOutcome>();
        foreach (var entry in engine.PendingOrders().Where(o => o.Entry))
        {
            var order = orders.Where(o => o.Sleeve == entry.Sleeve && o.Symbol == entry.Symbol && o.Purpose == OrderPurpose.Entry && o.DecisionBar == bar - length).MaxBy(o => o.Attempt);
            outcomes.Add(new ExternalOutcome(entry.Sleeve, entry.Symbol, order switch
            {
                null => "not-submitted",
                { Status: OrderStatus.LocalRejected } => order.Reason ?? "filter",
                { Status: OrderStatus.ExchangeRejected } => order.Reason ?? "exchange-rejected",
                { Status: OrderStatus.Expired } => "ioc-expired",
                { Status: OrderStatus.Canceled } => "canceled",
                { Status: OrderStatus.NeverAccepted } => "never-accepted",
                _ => "not-filled",
            }));
        }

        return new ExternalExecution(fills, outcomes.Count == 0 ? null : outcomes, await UnprotectedAsync(run, engine, ct) is { Count: > 0 } u ? u : null);
    }

    // ---------------------------------------------------------------- reconciliation

    /// <summary>
    /// Account-wide Testnet reconciliation (m6-protocol §4.3): (2) every open system order on the exchange is known
    /// and active here, and every active order here is open there; (3) every asset's balance equals the baseline plus
    /// the system's fills recorded after it. All known orders missing together with a balance change is reported as a
    /// suspected Testnet reset (§4.4).
    /// </summary>
    public async Task<TestnetReconciliation> ReconcileAsync(IReadOnlyList<PaperRun> runs, CancellationToken ct)
    {
        var runIds = runs.Select(r => r.Id).ToList();
        var orders = await store.OrdersAsync(runIds, ct);
        var active = orders.Where(o => o.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.CancelRequested).ToList();
        var symbols = runs.SelectMany(r => r.Config.Symbols).Concat(orders.Select(o => o.Symbol)).Distinct(StringComparer.Ordinal).Where(_rules.ContainsKey).ToList();
        var open = new List<OrderState>();
        foreach (var symbol in symbols.Where(s => orders.Any(o => o.Symbol == s)))
        {
            open.AddRange(await exchange.OpenOrdersAsync(symbol, ct));
        }

        var openIds = open.Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        var activeIds = active.Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        var unknownOpen = openIds.Where(id => ClientOrderIds.IsSystem(id) && !orders.Any(o => o.ClientOrderId == id && !o.IsTerminal)).Order(StringComparer.Ordinal).ToList();
        var missing = new List<string>();
        foreach (var order in active.Where(o => !openIds.Contains(o.ClientOrderId)))
        {
            // Filled or cancelled between the maintenance and this check is not lost: only an order the exchange no longer knows is.
            if (await exchange.QueryAsync(order.Symbol, order.ClientOrderId, ct) is null)
            {
                missing.Add(order.ClientOrderId);
            }
        }

        var foreign = openIds.Count(id => !ClientOrderIds.IsSystem(id));

        var baseline = await store.LatestBaselineAsync(ct);
        var balanceDiffs = new List<object>();
        if (baseline is not null)
        {
            var expected = baseline.Balances.ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal);
            foreach (var f in await store.FillsAsync(null, baseline.LastFillId, ct))
            {
                var (b, q) = (BaseAsset(f.Symbol), QuoteAsset(f.Symbol));
                var sign = f.Side == OrderSide.Buy ? 1 : -1;
                expected[b] = expected.GetValueOrDefault(b) + sign * f.Quantity;
                expected[q] = expected.GetValueOrDefault(q) - sign * f.QuoteQuantity;
                expected[f.CommissionAsset] = expected.GetValueOrDefault(f.CommissionAsset) - f.Commission;
            }

            var account = await exchange.AccountAsync(ct);
            foreach (var asset in expected.Keys.Union(account.Balances.Keys).Order(StringComparer.Ordinal))
            {
                var actual = account.Balances.TryGetValue(asset, out var balance) ? balance.Total : 0;
                var want = expected.GetValueOrDefault(asset);
                if (Math.Abs(actual - want) > BalanceTolerance(want))
                {
                    balanceDiffs.Add(new { asset, difference = actual - want });
                }
            }
        }

        string? reason = null;
        if (missing.Count > 0 && missing.Count == activeIds.Count && balanceDiffs.Count > 0)
        {
            reason = "testnet-reset-suspected";
        }
        else if (unknownOpen.Count > 0)
        {
            reason = "unknown-open-order";
        }
        else if (missing.Count > 0)
        {
            reason = "missing-order";
        }
        else if (balanceDiffs.Count > 0)
        {
            reason = "balance";
        }

        // Asset differences are reported by name and sign only in logs and the Ops API; amounts stay in the ops database.
        return new TestnetReconciliation(reason is null, reason, new { reason, unknownOpen = unknownOpen.Count, missing = missing.Count, foreignOpen = foreign, balance = balanceDiffs });
    }

    private static decimal BalanceTolerance(decimal expected) => 0.00000001m + Math.Abs(expected) * 0.000000001m;

    private static TimeSpan CandleInterval(PaperRun run) => Kernel.MarketData.CandleInterval.FromCode(run.Config.Interval).FixedLength!.Value;

}
