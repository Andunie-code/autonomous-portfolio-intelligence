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
/// run, record the cycle metric. HTTP 418 engages the trading kill switch automatically (risk-model §3.1); 429
/// and per-symbol failures leave the bar to the gap rule or to the next cycle.
/// </summary>
public sealed class TradingCycle(PaperStore store, PaperTrader trader, LiveCandleFeed? feed, TradingHealth health, TimeProvider clock)
{
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
                var run = await store.LatestRunAsync(ct);
                var symbols = run?.Config.Symbols ?? (await trader.LoadMembersAsync(r => r.State == "PaperTrading", ct)).Members
                    .SelectMany(m => m.Manifest.Pairs.Select(p => p.Symbol)).Distinct().ToList();
                var warmup = run?.WarmupStart ?? PaperTrader.NextBoundary(started) - PaperTrader.Warmup;
                try
                {
                    var fetched = await feed.FetchAsync(symbols, CandleInterval.FourHours, warmup, clock.GetUtcNow(), ct);
                    detail["requests"] = fetched.Requests;
                    detail["inserted"] = fetched.Inserted;
                    detail["anomalies"] = fetched.Anomalies;
                    detail["failedSymbols"] = fetched.Failed.Count;
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

            result = await trader.ProcessAsync(clock.GetUtcNow(), ct);
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

    /// <summary>The next hourly schedule point (HH:00:15 UTC) strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextSchedule(DateTimeOffset now)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddSeconds(15);
        return hour > now ? hour : hour.AddHours(1);
    }
}
