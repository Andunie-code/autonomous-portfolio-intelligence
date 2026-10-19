using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.MarketData.Tests.Support;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>Live kline client and guard (m5-protocol §2.3, S-B9/S-B10); in-memory handler only, no network.</summary>
public class LiveKlineTests
{
    private static readonly IReadOnlySet<string> Symbols = new HashSet<string> { "BTCUSDT", "ETHUSDT" };
    private static readonly DateTimeOffset Open = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static string Row(DateTimeOffset open, decimal o = 100, decimal h = 101, decimal l = 99, decimal c = 100.5m, int lengthHours = 4) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[{open.ToUnixTimeMilliseconds()},\"{o}\",\"{h}\",\"{l}\",\"{c}\",\"12.5\",{(open.AddHours(lengthHours).ToUnixTimeMilliseconds() - 1)},\"1250\",42,\"6\",\"600\",\"0\"]");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Uri Url(string query) => new($"https://data-api.binance.vision/api/v3/klines?{query}");

    [Fact]
    public void Guard_allows_only_the_klines_endpoint_for_manifest_symbols_with_bounded_ranges()
    {
        LiveKlineGuardHandler.Validate(Url("symbol=BTCUSDT&interval=4h&startTime=1&endTime=2&limit=1000"), Symbols);
        foreach (var bad in new[]
                 {
                     "https://api.binance.com/api/v3/klines?symbol=BTCUSDT&interval=4h&startTime=1&endTime=2",
                     "https://data.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=4h&startTime=1&endTime=2",
                     "http://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=4h&startTime=1&endTime=2",
                     "https://data-api.binance.vision/api/v3/order?symbol=BTCUSDT&interval=4h&startTime=1&endTime=2",
                     "https://data-api.binance.vision/api/v3/klines?symbol=XRPUSDT&interval=4h&startTime=1&endTime=2",
                     "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=1m&startTime=1&endTime=2",
                     "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=4h",
                     "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=4h&startTime=3&endTime=2",
                     "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=4h&startTime=1&endTime=2&limit=5000",
                     "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=4h&startTime=1&endTime=2&timeZone=8",
                     "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&symbol=ETHUSDT&interval=4h&startTime=1&endTime=2",
                 })
        {
            Assert.Throws<LiveRequestRejectedException>(() => LiveKlineGuardHandler.Validate(new Uri(bad), Symbols));
        }
    }

    [Fact]
    public async Task Redirects_429_and_418_are_typed_failures()
    {
        foreach (var (status, expected) in new (HttpStatusCode, Type)[]
                 {
                     (HttpStatusCode.Found, typeof(LiveRequestRejectedException)),
                     ((HttpStatusCode)418, typeof(LiveIpBannedException)),
                     (HttpStatusCode.TooManyRequests, typeof(LiveRateLimitedException)),
                 })
        {
            var stub = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            });
            var client = new LiveKlineClient(new HttpClient(new LiveKlineGuardHandler(Symbols, stub)));
            await Assert.ThrowsAsync(expected, () => client.FetchClosedAsync("BTCUSDT", CandleInterval.FourHours, Open, Open.AddDays(1), default));
        }
    }

    [Fact]
    public async Task Only_closed_aligned_candles_are_accepted_and_bad_rows_reject_the_response()
    {
        var now = Open.AddHours(9); // the 08:00 candle is still open
        var stub = new StubHandler(_ => Json($"[{Row(Open)},{Row(Open.AddHours(4))},{Row(Open.AddHours(8))}]"));
        var guard = new LiveKlineGuardHandler(Symbols, stub);
        var candles = await new LiveKlineClient(new HttpClient(guard)).FetchClosedAsync("BTCUSDT", CandleInterval.FourHours, Open, now, default);

        Assert.Equal([Open, Open.AddHours(4)], candles.Select(c => c.OpenTime));
        Assert.Equal(100.5m, candles[0].Close);
        var request = Assert.Single(stub.Requests);
        Assert.Contains($"startTime={Open.ToUnixTimeMilliseconds()}", request.Query);

        foreach (var bad in new[]
                 {
                     $"[{Row(Open.AddHours(1))}]",
                     $"[{Row(Open, lengthHours: 3)}]",
                     $"[{Row(Open, l: 101)}]",
                     $"[{Row(Open.AddHours(4))},{Row(Open)}]",
                     $"[{Row(ResearchCutoff.C.AddHours(-4))}]",
                 })
        {
            using var document = JsonDocument.Parse(bad);
            Assert.Throws<LiveDataException>(() => LiveKlineClient.Parse("BTCUSDT", CandleInterval.FourHours, document.RootElement, now.AddDays(400)));
        }

        await Assert.ThrowsAsync<LiveRequestRejectedException>(() =>
            new LiveKlineClient(new HttpClient(guard)).FetchClosedAsync("BTCUSDT", CandleInterval.FourHours, ResearchCutoff.C.AddHours(-4), now, default));
    }

    [Fact]
    public async Task Store_keeps_the_first_value_and_records_a_changed_candle_as_an_anomaly()
    {
        await using var database = new MigratedDatabase();
        await database.InitializeAsync();
        var store = new LiveCandleStore(database.ConnectionString);
        var candle = new Candle("BTCUSDT", CandleInterval.FourHours, Open, 100, 101, 99, 100.5m, 12.5m, 1250, 42);

        Assert.Equal((1, 0), await store.AddAsync([candle], default));
        Assert.Equal((0, 0), await store.AddAsync([candle], default));
        Assert.Equal((0, 1), await store.AddAsync([candle with { Close = 100.7m }], default));
        var read = await store.ReadAsync(["BTCUSDT"], CandleInterval.FourHours, Open, Open.AddDays(1), default);
        Assert.Equal(100.5m, Assert.Single(read["BTCUSDT"]).Close);
        Assert.Equal(Open, await store.LastOpenAsync("BTCUSDT", CandleInterval.FourHours, default));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.AddAsync([candle with { OpenTime = ResearchCutoff.C.AddHours(-4) }], default));
    }
}
