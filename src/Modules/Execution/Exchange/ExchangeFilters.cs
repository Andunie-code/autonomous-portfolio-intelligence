namespace PortfolioIntelligence.Execution.Exchange;

/// <summary>An order before rounding: raw quantity and prices from the engine's sizing and the protective stop.</summary>
public sealed record OrderDraft(string Symbol, string ClientOrderId, OrderSide Side, OrderType Type, decimal Quantity, decimal? Price = null, decimal? StopPrice = null, string? TimeInForce = null);

/// <summary>Either a rounded order that passes every filter, or the name of the first filter it fails.</summary>
public sealed record FilterResult(OrderRequest? Order, string? Rejection)
{
    public bool Accepted => Order is not null;
}

/// <summary>
/// Local exchange-filter check (m6-protocol §2.4, S-B17): rounds and validates an order so that a violating order never
/// reaches the exchange. Quantities always round down; a buy limit and a stop-limit price round down; a sell stop
/// price rounds up (closer to the entry, ADR-0014). After rounding, every rule must hold, otherwise the order is
/// rejected locally with <c>filter:&lt;NAME&gt;</c>. Market orders are checked against the reference price.
/// </summary>
public static class ExchangeFilters
{
    public static FilterResult Prepare(OrderDraft draft, SymbolRules rules, decimal referencePrice, int openOrders = 0, int openAlgoOrders = 0)
    {
        static FilterResult Reject(string filter) => new(null, $"filter:{filter}");

        if (rules.Status != "TRADING")
        {
            return Reject("SYMBOL_STATUS");
        }

        if (!rules.OrderTypes.Contains(draft.Type))
        {
            return Reject("ORDER_TYPE");
        }

        var isMarket = draft.Type == OrderType.Market;
        var isAlgo = draft.Type is OrderType.StopLoss or OrderType.StopLossLimit;
        var lot = isMarket && rules.MarketLot is { StepSize: > 0 } marketLot ? marketLot : rules.Lot;
        var quantity = lot is null ? draft.Quantity : RoundDown(draft.Quantity, lot.StepSize);
        if (quantity <= 0)
        {
            return Reject(isMarket && rules.MarketLot is not null ? "MARKET_LOT_SIZE" : "LOT_SIZE");
        }

        foreach (var filter in new[] { (rules.Lot, "LOT_SIZE"), (isMarket ? rules.MarketLot : null, "MARKET_LOT_SIZE") })
        {
            if (filter.Item1 is { } f && ((f.MinQuantity > 0 && quantity < f.MinQuantity) || (f.MaxQuantity > 0 && quantity > f.MaxQuantity) || !OnStep(quantity, f.StepSize)))
            {
                return Reject(filter.Item2);
            }
        }

        var price = draft.Price is { } p ? RoundPrice(p, rules.Price, up: false) : (decimal?)null;
        var stop = draft.StopPrice is { } s ? RoundPrice(s, rules.Price, up: draft.Side == OrderSide.Sell) : (decimal?)null;
        foreach (var value in new[] { price, stop }.OfType<decimal>())
        {
            if (value <= 0 || (rules.Price is { } pf && ((pf.MinPrice > 0 && value < pf.MinPrice) || (pf.MaxPrice > 0 && value > pf.MaxPrice))))
            {
                return Reject("PRICE_FILTER");
            }
        }

        if (rules.Notional is { } n)
        {
            var reference = price ?? (isMarket ? referencePrice : stop ?? referencePrice);
            var notional = reference * quantity;
            var checkMin = !isMarket || n.ApplyMinToMarket;
            var checkMax = n.MaxNotional > 0 && (!isMarket || n.ApplyMaxToMarket);
            if ((checkMin && notional < n.MinNotional) || (checkMax && notional > n.MaxNotional))
            {
                return Reject("NOTIONAL");
            }
        }

        if (price is { } limit && rules.PercentPrice is { } band && referencePrice > 0)
        {
            var (up, down) = draft.Side == OrderSide.Buy ? (band.BidMultiplierUp, band.BidMultiplierDown) : (band.AskMultiplierUp, band.AskMultiplierDown);
            if ((up > 0 && limit > referencePrice * up) || (down > 0 && limit < referencePrice * down))
            {
                return Reject("PERCENT_PRICE_BY_SIDE");
            }
        }

        if (rules.MaxNumOrders is { } maxOrders && openOrders + 1 > maxOrders)
        {
            return Reject("MAX_NUM_ORDERS");
        }

        if (isAlgo && rules.MaxNumAlgoOrders is { } maxAlgo && openAlgoOrders + 1 > maxAlgo)
        {
            return Reject("MAX_NUM_ALGO_ORDERS");
        }

        return new FilterResult(new OrderRequest(draft.Symbol, draft.ClientOrderId, draft.Side, draft.Type, quantity, price, stop, draft.TimeInForce), null);
    }

    /// <summary>Largest multiple of <paramref name="step"/> not above <paramref name="value"/> (no rounding when step is 0).</summary>
    public static decimal RoundDown(decimal value, decimal step) => step <= 0 ? value : Math.Floor(value / step) * step;

    /// <summary>Smallest multiple of <paramref name="step"/> not below <paramref name="value"/>.</summary>
    public static decimal RoundUp(decimal value, decimal step) => step <= 0 ? value : Math.Ceiling(value / step) * step;

    private static decimal RoundPrice(decimal value, PriceFilter? filter, bool up) =>
        filter is null ? value : up ? RoundUp(value, filter.TickSize) : RoundDown(value, filter.TickSize);

    private static bool OnStep(decimal value, decimal step) => step <= 0 || value % step == 0;
}
