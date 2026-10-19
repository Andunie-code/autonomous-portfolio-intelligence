using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;

namespace PortfolioIntelligence.Execution;

/// <summary>Reads the paper engine's candles from market.live_candles.</summary>
public sealed class LiveCandleSource(LiveCandleStore store) : ICandleSource
{
    public Task<IReadOnlyDictionary<string, IReadOnlyList<Candle>>> ReadAsync(IReadOnlyList<string> symbols, CandleInterval interval, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        store.ReadAsync(symbols, interval, from, to, ct);
}

public sealed record FeedOutcome(int Requests, int Inserted, int Anomalies, IReadOnlyList<string> Failed);

/// <summary>
/// Fetches newly closed candles for every symbol (backfill from the warm-up start on first use, m5-protocol §2.1)
/// and stores them. A failing symbol does not stop the others; 418 and 429 propagate to the host.
/// </summary>
public sealed class LiveCandleFeed(LiveKlineClient client, LiveCandleStore store)
{
    public async Task<FeedOutcome> FetchAsync(IReadOnlyList<string> symbols, CandleInterval interval, DateTimeOffset warmupStart, DateTimeOffset now, CancellationToken ct)
    {
        var length = interval.FixedLength!.Value;
        int requests = 0, inserted = 0, anomalies = 0;
        var failed = new List<string>();
        foreach (var symbol in symbols)
        {
            var last = await store.LastOpenAsync(symbol, interval, ct);
            var from = last is { } l ? l + length : warmupStart;
            if (from + length > now)
            {
                continue;
            }

            try
            {
                while (from + length <= now)
                {
                    requests++;
                    var batch = await client.FetchClosedAsync(symbol, interval, from, now, ct);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    var (added, changed) = await store.AddAsync(batch, ct);
                    inserted += added;
                    anomalies += changed;
                    from = batch[^1].OpenTime + length;
                    if (batch.Count < LiveKlineClient.MaxLimit)
                    {
                        break;
                    }
                }
            }
            catch (Exception e) when (e is LiveDataException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failed.Add(symbol);
            }
        }

        return new FeedOutcome(requests, inserted, anomalies, failed);
    }
}
