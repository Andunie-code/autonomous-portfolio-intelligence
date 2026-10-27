using System.Globalization;

namespace PortfolioIntelligence.Execution.Exchange;

public enum OrderSide
{
    Buy,
    Sell,
}

public enum OrderType
{
    Limit,
    Market,
    StopLoss,
    StopLossLimit,
}

/// <summary>Why the system places an order (m6-protocol §2.2); part of the deterministic client order id.</summary>
public enum OrderPurpose
{
    Entry,
    Stop,
    Exit,
    Close,
}

/// <summary>
/// The internal order state machine (m6-protocol §2.1, architecture §5). Stored as append-only events; the latest
/// event is the order's state. Terminal: Filled, Canceled, Expired, ExchangeRejected, NeverAccepted, LocalRejected.
/// </summary>
public enum OrderStatus
{
    Submitting,
    Accepted,
    PartiallyFilled,
    Filled,
    Canceled,
    Expired,
    ExchangeRejected,
    Unknown,
    NeverAccepted,
    LocalRejected,
    CancelRequested,
}

public static class OrderStatuses
{
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Expired or OrderStatus.ExchangeRejected or OrderStatus.NeverAccepted or OrderStatus.LocalRejected;

    /// <summary>Binance order status (S-B15) to the internal state.</summary>
    public static OrderStatus FromExchange(string status) => status switch
    {
        "NEW" or "PENDING_NEW" => OrderStatus.Accepted,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELED" => OrderStatus.Canceled,
        "PENDING_CANCEL" => OrderStatus.CancelRequested,
        "REJECTED" => OrderStatus.ExchangeRejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
        _ => throw new FormatException($"Unknown exchange order status {status}."),
    };
}

/// <summary>An order ready to send: rounded and checked against the symbol's filters (m6-protocol §2.4).</summary>
public sealed record OrderRequest(
    string Symbol,
    string ClientOrderId,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? Price = null,
    decimal? StopPrice = null,
    string? TimeInForce = null);

/// <summary>One trade of an order (myTrades or the FULL response's fills).</summary>
public sealed record ExchangeTrade(long TradeId, long OrderId, decimal Price, decimal Quantity, decimal QuoteQuantity, decimal Commission, string CommissionAsset, DateTimeOffset Time, bool IsBuyer);

/// <summary>The exchange's view of one order.</summary>
public sealed record OrderState(
    string Symbol,
    string ClientOrderId,
    long OrderId,
    OrderStatus Status,
    string ExchangeStatus,
    decimal OriginalQuantity,
    decimal ExecutedQuantity,
    decimal CumulativeQuote,
    decimal? StopPrice,
    IReadOnlyList<ExchangeTrade> Fills);

/// <summary>Result of a cancel-replace (S-B14): either leg can fail (-2021 partial, -2022 both).</summary>
public sealed record CancelReplaceResult(bool CancelSucceeded, OrderState? NewOrder, int? NewOrderErrorCode, string? NewOrderError, int? CancelErrorCode, string? CancelError);

public sealed record AssetBalance(string Asset, decimal Free, decimal Locked)
{
    public decimal Total => Free + Locked;
}

public sealed record AccountSnapshot(bool CanTrade, IReadOnlyDictionary<string, AssetBalance> Balances);

public sealed record PriceFilter(decimal MinPrice, decimal MaxPrice, decimal TickSize);

public sealed record LotFilter(decimal MinQuantity, decimal MaxQuantity, decimal StepSize);

public sealed record NotionalFilter(decimal MinNotional, decimal MaxNotional, bool ApplyMinToMarket, bool ApplyMaxToMarket);

public sealed record PercentPriceBySide(decimal BidMultiplierUp, decimal BidMultiplierDown, decimal AskMultiplierUp, decimal AskMultiplierDown);

/// <summary>
/// A symbol's trading rules from exchangeInfo (S-B17). Absent filters are null. MIN_NOTIONAL and NOTIONAL are both
/// mapped to <see cref="Notional"/> (MIN_NOTIONAL has no maximum).
/// </summary>
public sealed record SymbolRules(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Status,
    IReadOnlySet<OrderType> OrderTypes,
    PriceFilter? Price,
    LotFilter? Lot,
    LotFilter? MarketLot,
    NotionalFilter? Notional,
    PercentPriceBySide? PercentPrice,
    int? MaxNumOrders,
    int? MaxNumAlgoOrders)
{
    /// <summary>Canonical text for the rule snapshot hash (trading.exchange_symbol_rules).</summary>
    public string Canonical() => string.Create(CultureInfo.InvariantCulture,
        $"{Symbol}|{BaseAsset}|{QuoteAsset}|{Status}|{string.Join(',', OrderTypes.Order())}|{Price}|{Lot}|{MarketLot}|{Notional}|{PercentPrice}|{MaxNumOrders}|{MaxNumAlgoOrders}");
}

/// <summary>Exchange semantics of a failed request.</summary>
public abstract class ExchangeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The request reached the exchange and was refused; nothing was executed (HTTP 4xx with a Binance code).</summary>
public sealed class ExchangeRejectedException(int code, string message, int httpStatus = 400) : ExchangeException($"Exchange rejected the request ({code}): {message}")
{
    public int Code { get; } = code;

    public string ExchangeMessage { get; } = message;

    public int HttpStatus { get; } = httpStatus;
}

/// <summary>
/// The execution status is unknown (S-B12): timeout, HTTP 5xx, -1007 or a broken connection. The order may or may
/// not exist; it is resolved by querying its client order id, never by sending it again (m6-protocol §3.3).
/// </summary>
public sealed class ExchangeUnknownException(string reason, Exception? inner = null) : ExchangeException($"Execution status unknown: {reason}.", inner)
{
    public string Reason { get; } = reason;
}

/// <summary>HTTP 418: the IP is banned; the trading kill switch engages (risk-model §3.1).</summary>
public sealed class ExchangeIpBannedException() : ExchangeException("IP banned by the exchange (418).");

/// <summary>HTTP 429: the request was not processed; back off for <see cref="RetryAfter"/>.</summary>
public sealed class ExchangeRateLimitedException(TimeSpan? retryAfter) : ExchangeException("Rate limited by the exchange (429).")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The request was stopped before the network by the Testnet guard (D33).</summary>
public sealed class ExchangeRequestRejectedException(string message) : ExchangeException(message);

/// <summary>
/// The exchange-independent interface (D6, ADR-0021 §1). Query and cancel return null when the exchange does not know the
/// order (-2013, or -2011 "Unknown order sent."); every other failure is an <see cref="ExchangeException"/>.
/// </summary>
public interface IExchangeAdapter
{
    Task<DateTimeOffset> ServerTimeAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<string, SymbolRules>> SymbolRulesAsync(IReadOnlyCollection<string> symbols, CancellationToken ct);

    /// <summary>The exchange's reference price for limits and filters (GET /api/v3/avgPrice).</summary>
    Task<decimal> ReferencePriceAsync(string symbol, CancellationToken ct);

    Task<AccountSnapshot> AccountAsync(CancellationToken ct);

    Task<OrderState> PlaceAsync(OrderRequest request, CancellationToken ct);

    Task<OrderState?> QueryAsync(string symbol, string clientOrderId, CancellationToken ct);

    Task<OrderState?> CancelAsync(string symbol, string clientOrderId, CancellationToken ct);

    Task<CancelReplaceResult> CancelReplaceAsync(string symbol, string cancelClientOrderId, OrderRequest replacement, CancellationToken ct);

    Task<IReadOnlyList<OrderState>> OpenOrdersAsync(string symbol, CancellationToken ct);

    Task<IReadOnlyList<ExchangeTrade>> TradesAsync(string symbol, long orderId, CancellationToken ct);
}
