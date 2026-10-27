using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;

namespace PortfolioIntelligence.Execution;

/// <summary>Latest cycle state for /health/ready (architecture §11).</summary>
public sealed class TradingHealth
{
    private volatile Snapshot _current = new(null, "not-started", []);

    public sealed record Snapshot(DateTimeOffset? LastCycle, string Outcome, IReadOnlyList<string> Degraded);

    public Snapshot Current => _current;

    public void Update(Snapshot snapshot) => _current = snapshot;
}

/// <summary>
/// One hourly cycle (m5-protocol §2.1): fetch newly closed candles (when live data is enabled), process the paper
/// runs, record the cycle metric. HTTP 418 engages the trading kill switch automatically (risk-model §3.1); 429
/// and per-symbol failures leave the bar to the gap rule or to the next cycle. <paramref name="watchSymbols"/> are
/// fetched for the signal radar only (ADR-0020 §5): they enter no run and produce no order. HTTP 418 from the exchange
/// (M6) engages the kill switch the same way.
/// </summary>
public sealed class TradingCycle(PaperStore store, PaperTrader trader, LiveCandleFeed? feed, TradingHealth health, TimeProvider clock, IReadOnlyList<string>? watchSymbols = null)
{
    /// <summary>History fetched for a watch-only symbol: the radar's longest indicator (SMA 300) plus margin.</summary>
    public static readonly TimeSpan WatchLookback = TimeSpan.FromDays(56);

    public async Task<CycleOutcome> RunAsync(DateTimeOffset scheduledFor, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var detail = new Dictionary<string, object?>();
        var outcome = "ok";
        CycleOutcome result;
        try
        {
            if (feed is not null)
            {
                try
                {
                    var plan = await trader.FeedPlanAsync(started, ct);
                    var fetches = new List<FeedOutcome>();
                    if (plan is { } p)
                    {
                        fetches.Add(await feed.FetchAsync(p.Symbols, CandleInterval.FourHours, p.WarmupStart, clock.GetUtcNow(), ct));
                    }

                    var watch = (watchSymbols ?? []).Except(plan?.Symbols ?? [], StringComparer.Ordinal).ToList();
                    if (watch.Count > 0)
                    {
                        fetches.Add(await feed.FetchAsync(watch, CandleInterval.FourHours, PaperTrader.NextBoundary(started) - WatchLookback, clock.GetUtcNow(), ct));
                    }

                    detail["requests"] = fetches.Sum(f => f.Requests);
                    detail["inserted"] = fetches.Sum(f => f.Inserted);
                    detail["anomalies"] = fetches.Sum(f => f.Anomalies);
                    detail["failedSymbols"] = fetches.Sum(f => f.Failed.Count);
                }
                catch (LiveIpBannedException)
                {
                    await store.EngageKillSwitchAsync("system", "HTTP 418 from the market data API", ct);
                    await store.AuditAsync("trading", "live_data_ip_banned", new { scheduledFor }, ct);
                    outcome = "ip-banned";
                }
                catch (LiveRateLimitedException e)
                {
                    detail["retryAfterSeconds"] = e.RetryAfter?.TotalSeconds;
                    outcome = "rate-limited";
                }
            }

            try
            {
                result = await trader.ProcessAsync(clock.GetUtcNow(), ct);
            }
            catch (ExchangeIpBannedException)
            {
                await store.EngageKillSwitchAsync("system", "HTTP 418 from the exchange", ct);
                await store.AuditAsync("trading", "exchange_ip_banned", new { scheduledFor }, ct);
                result = new CycleOutcome(0, false, false, true, ["kill-switch"]);
                outcome = "ip-banned";
            }

            if (result.Degraded.Count > 0 && outcome == "ok")
            {
                outcome = "degraded";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new CycleOutcome(0, false, false, false, ["cycle-error"]);
            outcome = "error";
            detail["error"] = e.GetType().Name;
            await store.AuditAsync("trading", "cycle_error", new { scheduledFor, error = e.GetType().Name, e.Message }, ct);
        }

        detail["degraded"] = result.Degraded;
        var finished = clock.GetUtcNow();
        await store.AddCycleMetricAsync(new CycleMetric(scheduledFor, started, finished, result.BarsProcessed, result.CatchUp, result.Stale, outcome, detail), ct);
        health.Update(new TradingHealth.Snapshot(finished, outcome, result.Degraded));
        return result;
    }

    /// <summary>An immediate exchange maintenance pass (kill switch, m6-protocol §5.5); failures wait for the next cycle.</summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        try
        {
            await trader.MaintainTestnetAsync(ct);
        }
        catch (ExchangeException e)
        {
            await store.AuditAsync("trading", "exchange_maintenance_failed", new { error = e.GetType().Name }, ct);
        }
    }

    /// <summary>The next hourly schedule point (HH:00:15 UTC) strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextSchedule(DateTimeOffset now)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddSeconds(15);
        return hour > now ? hour : hour.AddHours(1);
    }
}
