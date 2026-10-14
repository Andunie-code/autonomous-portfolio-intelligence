namespace PortfolioIntelligence.Kernel.MarketData;

public sealed record Candle(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal BaseVolume,
    decimal QuoteVolume,
    long TradeCount)
{
    public DateTimeOffset CloseTime => Interval.CloseOf(OpenTime);
}
