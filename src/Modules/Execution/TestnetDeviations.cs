namespace PortfolioIntelligence.Execution;

/// <summary>
/// L3 categories of the Testnet deviations (ADR-0014, ADR-0015; m6-protocol §9): exchange rejections, local filter
/// rejections, IOC entries that did not fill or filled partly, the STOP_LOSS_LIMIT fallback, orders that never reached
/// the exchange, and protective-stop fills below their stop (gap and slippage, in basis points). Informational only:
/// it enters no validation.
/// </summary>
public static class TestnetDeviations
{
    public const string Sql = """
        SELECT
          count(*) FILTER (WHERE trading.order_status(o.id) = 'ExchangeRejected') AS exchange_rejected,
          count(*) FILTER (WHERE trading.order_status(o.id) = 'LocalRejected') AS filter_rejected,
          count(*) FILTER (WHERE o.purpose = 'Entry' AND trading.order_status(o.id) = 'Expired'
                           AND NOT EXISTS (SELECT 1 FROM trading.exchange_fills f WHERE f.order_id = o.id)) AS ioc_unfilled,
          count(*) FILTER (WHERE o.purpose = 'Entry' AND trading.order_status(o.id) = 'Expired'
                           AND EXISTS (SELECT 1 FROM trading.exchange_fills f WHERE f.order_id = o.id)) AS partial_fills,
          count(*) FILTER (WHERE o.order_type = 'StopLossLimit') AS stop_limit_fallback,
          count(*) FILTER (WHERE trading.order_status(o.id) = 'NeverAccepted') AS never_accepted,
          (SELECT count(DISTINCT f.order_id) FROM trading.exchange_fills f JOIN trading.exchange_orders s ON s.id = f.order_id
           WHERE s.purpose = 'Stop' AND s.stop_price > 0 AND f.price < s.stop_price) AS stop_fills_below_level,
          (SELECT round(avg((s.stop_price - f.price) / s.stop_price * 10000), 2) FROM trading.exchange_fills f JOIN trading.exchange_orders s ON s.id = f.order_id
           WHERE s.purpose = 'Stop' AND s.stop_price > 0) AS stop_slippage_bps
        FROM trading.exchange_orders o
        """;
}
