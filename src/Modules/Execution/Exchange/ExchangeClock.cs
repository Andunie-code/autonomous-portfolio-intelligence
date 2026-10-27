namespace PortfolioIntelligence.Execution.Exchange;

/// <summary>
/// The exchange's clock as seen from here: local time plus the offset measured with GET /api/v3/time. Signed
/// requests carry <see cref="Now"/> as their timestamp (S-B11: refused if more than 1 s ahead or older than recvWindow).
/// </summary>
public sealed class ExchangeClock(TimeProvider time)
{
    private long _offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public DateTimeOffset Now => time.GetUtcNow() + Offset;

    public DateTimeOffset LocalNow => time.GetUtcNow();

    /// <summary>Offset from a server time read between <paramref name="sentAt"/> and <paramref name="receivedAt"/> (midpoint).</summary>
    public void Update(DateTimeOffset serverTime, DateTimeOffset sentAt, DateTimeOffset receivedAt) =>
        Interlocked.Exchange(ref _offsetTicks, (serverTime - (sentAt + (receivedAt - sentAt) / 2)).Ticks);
}
