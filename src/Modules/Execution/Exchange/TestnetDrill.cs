using System.Diagnostics;
using System.Globalization;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution.Exchange;

/// <summary>One scenario's outcome: PASS or FAIL and a short reason code (m6-protocol §8.3).</summary>
public sealed record DrillScenario(string Name, string Result, string? Reason);

/// <summary>
/// The operational drill report: counts, states, error codes, latencies and checks only. It has no price, quantity,
/// balance amount or PnL field (U-M5-5 principle; m6-protocol §8.3).
/// </summary>
public sealed record DrillReport(
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Rounds,
    IReadOnlyList<DrillScenario> Scenarios,
    int Orders,
    int CompletedLifecycles,
    IReadOnlyDictionary<string, int> OrdersByTypeAndStatus,
    IReadOnlyDictionary<string, int> ErrorCodes,
    long LatencyMedianMs,
    long LatencyP95Ms,
    int UnknownInjected,
    int UnknownResolved,
    int NeverAccepted,
    int DuplicateOrders,
    int LocalRejectionsWithoutRequest,
    int StopLossSupported,
    int SymbolsChecked,
    bool CanTrade,
    long ClockOffsetMs,
    bool CapitalSufficient,
    string RulesHash,
    bool Pass);

/// <summary>
/// The user's Testnet drill (Gate C, m6-protocol §8.3): at least 20 full order lifecycles on one allow-listed symbol
/// with small notionals — GTC limit and cancel, duplicate client order id, local filter rejections (no request), IOC
/// buys, STOP_LOSS place / query / cancel-replace / cancel, market sells, an injected client timeout resolved by its
/// client order id, and the kill switch rule (entry cancelled, stop kept). It ends flat: no open drill order, and the
/// balance change equals the drill's own trades. Drill ids start with "drill-", outside the strategy runs' ids.
/// </summary>
public sealed class TestnetDrill(IExchangeAdapter exchange, ExchangeClock clock, Func<TimeSpan, CancellationToken, Task> delay)
{
    private readonly List<long> _latencies = [];
    private readonly Dictionary<string, int> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Symbol, long OrderId)> _placed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OrderStatus> _final = new(StringComparer.Ordinal);
    private readonly List<DrillScenario> _scenarios = [];
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private int _sequence;
    private int _orders;
    private int _unknownInjected;
    private int _unknownResolved;
    private int _neverAccepted;
    private int _duplicates;
    private int _localRejections;

    public async Task<DrillReport> RunAsync(string symbol, IReadOnlyCollection<string> manifestSymbols, int rounds, decimal requiredCapital, CancellationToken ct)
    {
        var started = clock.LocalNow;
        await exchange.ServerTimeAsync(ct);
        var rules = await exchange.SymbolRulesAsync(manifestSymbols.Append(symbol).Distinct(StringComparer.Ordinal).ToList(), ct);
        var rulesHash = DslCanonical.Sha256(string.Join('\n', rules.Values.OrderBy(r => r.Symbol, StringComparer.Ordinal).Select(r => r.Canonical())));
        var account = await exchange.AccountAsync(ct);
        var before = account.Balances.ToDictionary(b => b.Key, b => b.Value.Total, StringComparer.Ordinal);
        if (!rules.TryGetValue(symbol, out var r))
        {
            _scenarios.Add(new DrillScenario("rules", "FAIL", "symbol-not-listed"));
            return Report(started, rounds, 0, rulesHash, account.CanTrade, false, rules, manifestSymbols);
        }

        var quote = r.QuoteAsset;
        var capital = account.Balances.TryGetValue(quote, out var q) && q.Free >= requiredCapital;
        var reference = await exchange.ReferencePriceAsync(symbol, ct);
        var quantity = SmallQuantity(r, reference);

        await Scenario("limit-gtc-cancel", async () =>
        {
            var id = await PlaceAsync(new OrderDraft(symbol, Next("gtc"), OrderSide.Buy, OrderType.Limit, quantity, reference * 0.8m, null, "GTC"), r, reference, ct);
            Expect((await exchange.QueryAsync(symbol, id, ct))?.Status == OrderStatus.Accepted, "query-not-new");
            Expect((await CancelAsync(symbol, id, ct))?.Status == OrderStatus.Canceled, "cancel-failed");
        });

        await Scenario("duplicate-client-order-id", async () =>
        {
            var draft = new OrderDraft(symbol, Next("dup"), OrderSide.Buy, OrderType.Limit, quantity, reference * 0.8m, null, "GTC");
            var id = await PlaceAsync(draft, r, reference, ct);
            try
            {
                await PlaceAsync(draft, r, reference, ct);
                _duplicates++;
                Expect(false, "duplicate-accepted");
            }
            catch (ExchangeRejectedException e)
            {
                Count(_errors, e.Code.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                await CancelAsync(symbol, id, ct);
            }
        });

        await Scenario("filter-local-reject", () =>
        {
            var lot = r.Lot ?? throw new DrillFailure("no-lot-filter");
            var drafts = new[]
            {
                new OrderDraft(symbol, Next("f1"), OrderSide.Buy, OrderType.Limit, lot.MinQuantity / 2, reference * 0.8m, null, "GTC"),
                new OrderDraft(symbol, Next("f2"), OrderSide.Buy, OrderType.Limit, lot.MinQuantity, (r.Price?.MaxPrice ?? 0) > 0 ? r.Price!.MaxPrice * 2 : reference * 1000m, null, "GTC"),
            };
            foreach (var draft in drafts)
            {
                Expect(!ExchangeFilters.Prepare(draft, r, reference).Accepted, "filter-passed");
                _localRejections++;
            }

            return Task.CompletedTask;
        });

        for (var round = 1; round <= rounds; round++)
        {
            await Scenario($"round-{round}-entry-stop-replace-exit", async () =>
            {
                var bought = await BuyAsync(symbol, r, reference, quantity, ct);
                Expect(bought > 0, "ioc-not-filled");
                var stopType = r.OrderTypes.Contains(OrderType.StopLoss) ? OrderType.StopLoss : OrderType.StopLossLimit;
                var stop = await PlaceAsync(StopDraft(symbol, stopType, bought, reference * 0.9m), r, reference, ct);
                Expect((await exchange.QueryAsync(symbol, stop, ct))?.Status == OrderStatus.Accepted, "stop-not-new");
                var raised = StopDraft(symbol, stopType, bought, reference * 0.92m);
                var replaced = ExchangeFilters.Prepare(raised, r, reference);
                Expect(replaced.Accepted, replaced.Rejection ?? "filter");
                var result = await Timed(() => exchange.CancelReplaceAsync(symbol, stop, replaced.Order!, ct));
                _orders++;
                Count(_states, $"{stopType}/{(result.NewOrder?.Status ?? OrderStatus.ExchangeRejected)}");
                Expect(result.CancelSucceeded && result.NewOrder?.Status == OrderStatus.Accepted, $"cancel-replace:{result.NewOrderErrorCode ?? result.CancelErrorCode}");
                _final[stop] = OrderStatus.Canceled;
                Remember(replaced.Order!.ClientOrderId, result.NewOrder);
                await CloseAsync(symbol, r, reference, replaced.Order!.ClientOrderId, ct);
            });
        }

        await Scenario("unknown-recovery", async () =>
        {
            var draft = ExchangeFilters.Prepare(new OrderDraft(symbol, Next("unk"), OrderSide.Buy, OrderType.Limit, quantity, reference * 0.8m, null, "GTC"), r, reference);
            Expect(draft.Accepted, draft.Rejection ?? "filter");
            var id = draft.Order!.ClientOrderId;
            var sentAt = clock.Now;
            _unknownInjected++;
            _orders++;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(1)); // client-side timeout right after sending: status unknown
                await exchange.PlaceAsync(draft.Order, timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ExchangeUnknownException && !ct.IsCancellationRequested)
            {
            }

            // Resolve by the client order id only (m6-protocol §3.3): found → its state; not found after the horizon → NeverAccepted.
            var horizon = sentAt + TimeSpan.FromMilliseconds(Binance.HmacSigningHandler.RecvWindowMs) + TestnetExecutor.UnknownMargin;
            var misses = 0;
            while (true)
            {
                var state = await exchange.QueryAsync(symbol, id, ct);
                if (state is not null)
                {
                    _unknownResolved++;
                    Count(_states, $"Limit/{state.Status}");
                    Remember(id, state);
                    if (!OrderStatuses.IsTerminal(state.Status))
                    {
                        await CancelAsync(symbol, id, ct);
                    }

                    return;
                }

                if (clock.Now >= horizon && ++misses >= TestnetExecutor.NeverAcceptedQueries)
                {
                    _unknownResolved++;
                    _neverAccepted++;
                    Count(_states, "Limit/NeverAccepted");
                    _final[id] = OrderStatus.NeverAccepted;
                    return;
                }

                await delay(clock.Now < horizon ? horizon - clock.Now : TimeSpan.FromSeconds(1), ct);
            }
        });

        await Scenario("kill-switch-entries-only", async () =>
        {
            var bought = await BuyAsync(symbol, r, reference, quantity, ct);
            Expect(bought > 0, "ioc-not-filled");
            var stopType = r.OrderTypes.Contains(OrderType.StopLoss) ? OrderType.StopLoss : OrderType.StopLossLimit;
            var stop = await PlaceAsync(StopDraft(symbol, stopType, bought, reference * 0.9m), r, reference, ct);
            var entry = await PlaceAsync(new OrderDraft(symbol, Next("ks-entry"), OrderSide.Buy, OrderType.Limit, quantity, reference * 0.8m, null, "GTC"), r, reference, ct);
            // The kill switch rule (D11 rev.): open entry orders are cancelled, protective stops are not touched.
            foreach (var open in (await exchange.OpenOrdersAsync(symbol, ct)).Where(o => o.ClientOrderId == entry))
            {
                await CancelAsync(symbol, open.ClientOrderId, ct);
            }

            var after = (await exchange.OpenOrdersAsync(symbol, ct)).Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
            Expect(!after.Contains(entry) && after.Contains(stop), "kill-switch-rule");
            await CloseAsync(symbol, r, reference, stop, ct);
        });

        await Scenario("flat-and-reconciled", async () =>
        {
            Expect(!(await exchange.OpenOrdersAsync(symbol, ct)).Any(o => o.ClientOrderId.StartsWith(ClientOrderIds.DrillPrefix, StringComparison.Ordinal)), "open-drill-orders");
            var expected = new Dictionary<string, decimal>(before, StringComparer.Ordinal);
            foreach (var (_, (s, orderId)) in _placed)
            {
                foreach (var t in await exchange.TradesAsync(s, orderId, ct))
                {
                    var sign = t.IsBuyer ? 1 : -1;
                    expected[r.BaseAsset] = expected.GetValueOrDefault(r.BaseAsset) + sign * t.Quantity;
                    expected[r.QuoteAsset] = expected.GetValueOrDefault(r.QuoteAsset) - sign * t.QuoteQuantity;
                    expected[t.CommissionAsset] = expected.GetValueOrDefault(t.CommissionAsset) - t.Commission;
                }
            }

            var now = (await exchange.AccountAsync(ct)).Balances;
            foreach (var asset in new[] { r.BaseAsset, r.QuoteAsset })
            {
                var actual = now.TryGetValue(asset, out var b) ? b.Total : 0;
                Expect(Math.Abs(actual - expected.GetValueOrDefault(asset)) <= 0.00000001m + Math.Abs(actual) * 0.000000001m, $"balance-{(asset == r.BaseAsset ? "base" : "quote")}");
            }
        });

        return Report(started, rounds, (long)clock.Offset.TotalMilliseconds, rulesHash, account.CanTrade, capital, rules, manifestSymbols);
    }

    private sealed class DrillFailure(string reason) : Exception(reason);

    private static void Expect(bool condition, string reason)
    {
        if (!condition)
        {
            throw new DrillFailure(reason);
        }
    }

    private async Task Scenario(string name, Func<Task> body)
    {
        try
        {
            await body();
            _scenarios.Add(new DrillScenario(name, "PASS", null));
        }
        catch (DrillFailure e)
        {
            _scenarios.Add(new DrillScenario(name, "FAIL", e.Message));
        }
        catch (ExchangeRejectedException e)
        {
            Count(_errors, e.Code.ToString(CultureInfo.InvariantCulture));
            _scenarios.Add(new DrillScenario(name, "FAIL", $"exchange-rejected:{e.Code}"));
        }
        catch (ExchangeException e)
        {
            _scenarios.Add(new DrillScenario(name, "FAIL", e.GetType().Name));
        }
    }

    private string Next(string scenario) => ClientOrderIds.Drill(scenario, ++_sequence, _nonce);

    private OrderDraft StopDraft(string symbol, OrderType type, decimal quantity, decimal level) =>
        new(symbol, Next("stop"), OrderSide.Sell, type, quantity, type == OrderType.StopLossLimit ? level * (1 - TestnetExecutor.StopLimitBand) : null, level,
            type == OrderType.StopLossLimit ? "GTC" : null);

    /// <summary>An IOC buy at the entry band; returns the base quantity received (net of a base commission).</summary>
    private async Task<decimal> BuyAsync(string symbol, SymbolRules rules, decimal reference, decimal quantity, CancellationToken ct)
    {
        var id = await PlaceAsync(new OrderDraft(symbol, Next("ioc"), OrderSide.Buy, OrderType.Limit, quantity, reference * (1 + TestnetExecutor.EntryBand), null, "IOC"), rules, reference, ct);
        var trades = await exchange.TradesAsync(symbol, _placed[id].OrderId, ct);
        var received = trades.Sum(t => t.Quantity - (t.CommissionAsset == rules.BaseAsset ? t.Commission : 0));
        var lot = rules.Lot;
        return lot is null ? received : ExchangeFilters.RoundDown(received, lot.StepSize);
    }

    /// <summary>Close: cancel the stop, then sell what it protected at market (m6-protocol §5.4).</summary>
    private async Task CloseAsync(string symbol, SymbolRules rules, decimal reference, string stopId, CancellationToken ct)
    {
        var stop = await CancelAsync(symbol, stopId, ct) ?? await exchange.QueryAsync(symbol, stopId, ct);
        Expect(stop is not null, "stop-unknown");
        if (stop!.Status == OrderStatus.Filled)
        {
            return; // the stop already sold
        }

        var quantity = stop.OriginalQuantity - stop.ExecutedQuantity;
        await PlaceAsync(new OrderDraft(symbol, Next("mkt"), OrderSide.Sell, OrderType.Market, quantity), rules, reference, ct);
    }

    private async Task<string> PlaceAsync(OrderDraft draft, SymbolRules rules, decimal reference, CancellationToken ct)
    {
        var prepared = ExchangeFilters.Prepare(draft, rules, reference);
        Expect(prepared.Accepted, prepared.Rejection ?? "filter");
        _orders++;
        var state = await Timed(() => exchange.PlaceAsync(prepared.Order!, ct));
        Count(_states, $"{draft.Type}/{state.Status}");
        Remember(draft.ClientOrderId, state);
        return draft.ClientOrderId;
    }

    private async Task<OrderState?> CancelAsync(string symbol, string id, CancellationToken ct)
    {
        var state = await Timed(() => exchange.CancelAsync(symbol, id, ct));
        if (state is not null)
        {
            Count(_states, $"cancel/{state.Status}");
            Remember(id, state);
        }

        return state;
    }

    private void Remember(string id, OrderState? state)
    {
        if (state is not null)
        {
            _placed[id] = (state.Symbol, state.OrderId);
            _final[id] = state.Status;
        }
    }

    private async Task<T> Timed<T>(Func<Task<T>> call)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await call();
        }
        finally
        {
            _latencies.Add(watch.ElapsedMilliseconds);
        }
    }

    private static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    /// <summary>A quantity worth about 1.5 × the minimum notional at the reference price, on the lot step.</summary>
    private static decimal SmallQuantity(SymbolRules rules, decimal reference)
    {
        var minNotional = rules.Notional?.MinNotional is > 0 and var n ? n : 10m;
        var step = rules.Lot?.StepSize ?? 0;
        var quantity = ExchangeFilters.RoundUp(minNotional * 1.5m / reference, step);
        return Math.Max(quantity, rules.Lot?.MinQuantity ?? 0);
    }

    private DrillReport Report(DateTimeOffset started, int rounds, long offsetMs, string rulesHash, bool canTrade, bool capital,
        IReadOnlyDictionary<string, SymbolRules> rules, IReadOnlyCollection<string> manifestSymbols)
    {
        var sorted = _latencies.Order().ToList();
        long Percentile(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
        // A full lifecycle: an order that reached a terminal state (filled, cancelled, expired, rejected or never accepted).
        var completed = _final.Values.Count(OrderStatuses.IsTerminal);
        var checkedSymbols = manifestSymbols.Distinct(StringComparer.Ordinal).ToList();
        var pass = _scenarios.All(s => s.Result == "PASS") && completed >= 20 && _duplicates == 0 && canTrade;
        return new DrillReport("m6-testnet-drill", started, clock.LocalNow, rounds, _scenarios, _orders, completed,
            new SortedDictionary<string, int>(_states, StringComparer.Ordinal), new SortedDictionary<string, int>(_errors, StringComparer.Ordinal),
            Percentile(0.5), Percentile(0.95), _unknownInjected, _unknownResolved, _neverAccepted, _duplicates, _localRejections,
            checkedSymbols.Count(s => rules.TryGetValue(s, out var r) && r.OrderTypes.Contains(OrderType.StopLoss)), checkedSymbols.Count,
            canTrade, offsetMs, capital, rulesHash, pass);
    }
}
