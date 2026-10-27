using System.Net;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Exchange.Binance;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// The Testnet adapter without a database (m6-protocol §8.1, §9; D33): host, path, method and symbol allow-list,
/// redirect rejection, 418 and 429, HMAC signing with the documented vector, error semantics (unknown versus
/// rejected), deterministic client order ids and the local filter rules. Nothing here reaches a network.
/// </summary>
public class ExchangeAdapterTests
{
    private static readonly IReadOnlySet<string> Symbols = new HashSet<string> { "BTCUSDT", "ETHUSDT" };
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 15, TimeSpan.Zero);

    private sealed class CountingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Theory]
    [InlineData("GET", "https://api.binance.com/api/v3/order?symbol=BTCUSDT")]
    [InlineData("GET", "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT")]
    [InlineData("GET", "https://testnet.binance.vision.evil.example/api/v3/time")]
    [InlineData("GET", "http://testnet.binance.vision/api/v3/time")]
    [InlineData("GET", "https://testnet.binance.vision:8443/api/v3/time")]
    [InlineData("GET", "https://user@testnet.binance.vision/api/v3/time")]
    [InlineData("GET", "https://testnet.binance.vision/sapi/v1/capital/withdraw/apply")]
    [InlineData("POST", "https://testnet.binance.vision/api/v3/order/oco?symbol=BTCUSDT")]
    [InlineData("DELETE", "https://testnet.binance.vision/api/v3/openOrders?symbol=BTCUSDT")]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/order?symbol=DOGEUSDT&origClientOrderId=x")]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/order?origClientOrderId=x")]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/order?symbol=BTCUSDT&symbol=ETHUSDT")]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/exchangeInfo?symbols=%5B%22DOGEUSDT%22%5D")]
    public async Task The_guard_refuses_everything_but_the_testnet_allow_list_before_the_network(string method, string url)
    {
        var inner = new CountingHandler();
        using var http = new HttpClient(new TestnetGuardHandler(Symbols, inner));

        await Assert.ThrowsAsync<ExchangeRequestRejectedException>(() => http.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)));
        Assert.Equal(0, inner.Requests);
    }

    [Theory]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/time")]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/exchangeInfo?symbols=%5B%22BTCUSDT%22%2C%22ETHUSDT%22%5D")]
    [InlineData("GET", "https://testnet.binance.vision/api/v3/order?symbol=BTCUSDT&origClientOrderId=pi-e-abc")]
    [InlineData("POST", "https://testnet.binance.vision/api/v3/order?symbol=ETHUSDT&side=BUY")]
    [InlineData("POST", "https://testnet.binance.vision/api/v3/order/cancelReplace?symbol=BTCUSDT")]
    [InlineData("DELETE", "https://testnet.binance.vision/api/v3/order?symbol=BTCUSDT&origClientOrderId=x")]
    public async Task The_guard_lets_the_allowed_requests_through(string method, string url)
    {
        var inner = new CountingHandler();
        using var http = new HttpClient(new TestnetGuardHandler(Symbols, inner));

        using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(1, inner.Requests);
    }

    [Fact]
    public async Task Redirects_418_and_429_are_typed_and_the_network_handler_never_follows_redirects()
    {
        foreach (var (status, expected) in new[] { (HttpStatusCode.Found, typeof(ExchangeRequestRejectedException)), ((HttpStatusCode)418, typeof(ExchangeIpBannedException)), (HttpStatusCode.TooManyRequests, typeof(ExchangeRateLimitedException)) })
        {
            using var http = new HttpClient(new TestnetGuardHandler(Symbols, new CountingHandler(status)));
            var e = await Assert.ThrowsAnyAsync<ExchangeException>(() => http.GetAsync("https://testnet.binance.vision/api/v3/time"));
            Assert.IsType(expected, e);
        }

        using var handler = (SocketsHttpHandler)TestnetGuardHandler.CreateNetworkHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal("https://testnet.binance.vision", BinanceTestnetAdapter.BaseAddress);
    }

    [Fact]
    public void Hmac_matches_the_documented_example()
    {
        // The SIGNED endpoint example of the Binance Spot API documentation (S-B11): a public sample key, not a secret.
        const string payload = "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559";
        const string documented = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";

        Assert.Equal("c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71", HmacSigningHandler.Sign(payload, documented));
    }

    [Fact]
    public async Task Signed_requests_carry_recvwindow_timestamp_signature_and_key_over_the_encoded_payload()
    {
        var time = new ManualTime(Now);
        var clock = new ExchangeClock(time);
        clock.Update(Now.AddMilliseconds(250), Now, Now); // the exchange is 250 ms ahead
        var fake = new FakeBinance(time);
        var adapter = new BinanceTestnetAdapter(fake.Client(Symbols, clock), clock);

        // The fake recomputes the HMAC over the query string exactly as it travels (percent-encoded, S-B11) and checks the key.
        var account = await adapter.AccountAsync(default);

        Assert.True(account.CanTrade);
        Assert.Equal(TimeSpan.FromMilliseconds(250), clock.Offset);
        Assert.Contains("GET /api/v3/account", fake.Log);
        Assert.Equal("TestnetCredentials(***)", new TestnetCredentials("key", "secret").ToString());
    }

    [Fact]
    public async Task Timeouts_5xx_backend_timeouts_and_broken_connections_are_unknown_not_failures()
    {
        var time = new ManualTime(Now);
        var clock = new ExchangeClock(time);
        foreach (var fault in new[] { Fault.TimeoutBeforeApply, Fault.TimeoutAfterApply, Fault.ServerErrorAfterApply, Fault.BackendTimeoutAfterApply, Fault.ConnectionErrorBeforeApply })
        {
            var fake = new FakeBinance(time);
            var adapter = new BinanceTestnetAdapter(fake.Client(Symbols, clock), clock);
            fake.FaultOnce("POST", "/api/v3/order", fault);

            await Assert.ThrowsAsync<ExchangeUnknownException>(() => adapter.PlaceAsync(new OrderRequest("BTCUSDT", "pi-e-x", OrderSide.Buy, OrderType.Market, 1), default));
        }

        var plain = new FakeBinance(time);
        var client = new BinanceTestnetAdapter(plain.Client(Symbols, clock), clock);
        Assert.Null(await client.QueryAsync("BTCUSDT", "pi-e-missing", default)); // -2013
        Assert.Null(await client.CancelAsync("BTCUSDT", "pi-e-missing", default)); // -2011 Unknown order sent.
        plain.FaultOnce("POST", "/api/v3/order", Fault.ClockSkew);
        var skew = await Assert.ThrowsAsync<ExchangeRejectedException>(() => client.PlaceAsync(new OrderRequest("BTCUSDT", "pi-e-y", OrderSide.Buy, OrderType.Market, 1), default));
        Assert.Equal(-1021, skew.Code);
        plain.FaultOnce("POST", "/api/v3/order", Fault.Redirect);
        await Assert.ThrowsAsync<ExchangeRequestRejectedException>(() => client.PlaceAsync(new OrderRequest("BTCUSDT", "pi-e-z", OrderSide.Buy, OrderType.Market, 1), default));
    }

    [Fact]
    public void Client_order_ids_are_deterministic_valid_and_distinct_per_intent()
    {
        var bar = new DateTimeOffset(2026, 11, 3, 4, 0, 0, TimeSpan.Zero);
        var id = ClientOrderIds.For(7, 1, "BTCUSDT", bar, OrderPurpose.Entry, 1);

        Assert.Equal(id, ClientOrderIds.For(7, 1, "BTCUSDT", bar, OrderPurpose.Entry, 1)); // a restart gives the same id
        Assert.Equal(id, ClientOrderIds.For(7, 1, "BTCUSDT", bar.ToOffset(TimeSpan.FromHours(3)), OrderPurpose.Entry, 1));
        Assert.StartsWith("pi-e-", id);
        Assert.Equal(31, id.Length);
        Assert.True(ClientOrderIds.IsValid(id));
        var all = new[]
        {
            id, ClientOrderIds.For(7, 1, "BTCUSDT", bar, OrderPurpose.Entry, 2), ClientOrderIds.For(7, 1, "BTCUSDT", bar, OrderPurpose.Stop, 1),
            ClientOrderIds.For(7, 0, "BTCUSDT", bar, OrderPurpose.Entry, 1), ClientOrderIds.For(8, 1, "BTCUSDT", bar, OrderPurpose.Entry, 1),
            ClientOrderIds.For(7, 1, "BTCUSDT", bar.AddHours(4), OrderPurpose.Entry, 1), ClientOrderIds.Drill("gtc", 1, "n"),
        };
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.All(all, x => Assert.True(ClientOrderIds.IsValid(x)));
        Assert.False(ClientOrderIds.IsSystem(ClientOrderIds.Drill("gtc", 1, "n")));
    }

    private static SymbolRules Rules(bool stopLoss = true) => new("BTCUSDT", "BTC", "USDT", "TRADING",
        stopLoss ? new HashSet<OrderType> { OrderType.Limit, OrderType.Market, OrderType.StopLoss, OrderType.StopLossLimit } : new HashSet<OrderType> { OrderType.Limit, OrderType.Market, OrderType.StopLossLimit },
        new PriceFilter(0.01m, 1_000_000m, 0.01m), new LotFilter(0.00001m, 9000m, 0.00001m), new LotFilter(0, 100m, 0.0001m),
        new NotionalFilter(5m, 9_000_000m, true, false), new PercentPriceBySide(5m, 0.2m, 5m, 0.2m), 200, 5);

    [Fact]
    public void Filters_round_quantities_down_and_prices_toward_safety_never_letting_a_violation_pass()
    {
        var random = new Random(6006);
        var rules = Rules();
        for (var i = 0; i < 2000; i++)
        {
            var reference = (decimal)(random.NextDouble() * 90_000 + 10);
            var quantity = (decimal)random.NextDouble() * 0.01m;
            var buy = ExchangeFilters.Prepare(new OrderDraft("BTCUSDT", "id", OrderSide.Buy, OrderType.Limit, quantity, reference * 1.01m, null, "IOC"), rules, reference);
            if (buy.Order is { } o)
            {
                Assert.True(o.Quantity <= quantity && o.Quantity % 0.00001m == 0 && o.Quantity >= 0.00001m);
                Assert.True(o.Price <= reference * 1.01m && o.Price % 0.01m == 0);
                Assert.True(o.Price * o.Quantity >= 5m);
            }
            else
            {
                Assert.True(ExchangeFilters.RoundDown(quantity, 0.00001m) * ExchangeFilters.RoundDown(reference * 1.01m, 0.01m) < 5m || quantity < 0.00001m);
            }

            var level = reference * 0.9m;
            var stop = ExchangeFilters.Prepare(new OrderDraft("BTCUSDT", "id", OrderSide.Sell, OrderType.StopLoss, 1m, null, level), rules, reference);
            Assert.True(stop.Order!.StopPrice >= level && stop.Order.StopPrice - level < 0.01m); // up: closer to the entry
            var market = ExchangeFilters.Prepare(new OrderDraft("BTCUSDT", "id", OrderSide.Sell, OrderType.Market, quantity * 100), rules, reference);
            Assert.True(market.Order is null || market.Order.Quantity % 0.0001m == 0); // MARKET_LOT_SIZE step
        }
    }

    [Theory]
    [InlineData("LOT_SIZE", 0.000001, 50000, "Limit")]
    [InlineData("NOTIONAL", 0.00001, 50000, "Limit")]
    [InlineData("PRICE_FILTER", 1, 2_000_000, "Limit")]
    [InlineData("PERCENT_PRICE_BY_SIDE", 1, 5000, "Limit")]
    [InlineData("ORDER_TYPE", 1, 0, "StopLoss")]
    public void Each_filter_violation_is_named_and_rejected_locally(string filter, double quantity, double price, string type)
    {
        var rules = Rules(stopLoss: filter != "ORDER_TYPE");
        var draft = new OrderDraft("BTCUSDT", "id", OrderSide.Buy, Enum.Parse<OrderType>(type), (decimal)quantity, price > 0 ? (decimal)price : null, type == "StopLoss" ? 40_000m : null, type == "Limit" ? "GTC" : null);

        var result = ExchangeFilters.Prepare(draft, rules, referencePrice: 50_000m);

        Assert.Null(result.Order);
        Assert.Equal($"filter:{filter}", result.Rejection);
        Assert.Equal("filter:MAX_NUM_ALGO_ORDERS",
            ExchangeFilters.Prepare(new OrderDraft("BTCUSDT", "id", OrderSide.Sell, OrderType.StopLoss, 0.1m, null, 40_000m), Rules(), 50_000m, openOrders: 5, openAlgoOrders: 5).Rejection);
    }
}
