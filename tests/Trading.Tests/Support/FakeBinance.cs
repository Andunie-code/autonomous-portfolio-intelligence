using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Exchange.Binance;

namespace PortfolioIntelligence.Trading.Tests.Support;

/// <summary>Faults injected into one request (fault injection, m6-protocol §9).</summary>
public enum Fault
{
    TimeoutBeforeApply,
    TimeoutAfterApply,
    ServerErrorAfterApply,
    BackendTimeoutAfterApply,
    ConnectionErrorBeforeApply,
    HiddenAfterApply,
    ClockSkew,
    RateLimited,
    IpBanned,
    Redirect,
}

/// <summary>Controllable clock of the tests (exchange time, horizons, trade times).</summary>
public sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// An in-memory Binance Spot Testnet behind the real signing handler and guard (no network). It checks the HMAC
/// signature and the API key, keeps balances with locks, fills IOC and market orders at the reference price, keeps
/// GTC limits and stops open, triggers stops on demand with the gap rule, supports cancel and cancel-replace, rejects a
/// second open order with the same client order id, and injects faults before or after applying a request.
/// </summary>
public sealed class FakeBinance(TimeProvider time) : HttpMessageHandler
{
    public const string ApiKey = "fake-testnet-key-0123456789";
    public const string SigningKey = "fake-testnet-signing-key-abcdefghij";

    public sealed class FakeOrder
    {
        public required string Symbol { get; init; }
        public required string ClientOrderId { get; init; }
        public required long OrderId { get; init; }
        public required string Side { get; init; }
        public required string Type { get; init; }
        public string? TimeInForce { get; init; }
        public required decimal Quantity { get; init; }
        public decimal? Price { get; init; }
        public decimal? StopPrice { get; init; }
        public string Status { get; set; } = "NEW";
        public decimal Executed { get; set; }
        public decimal Quote { get; set; }
        public int HiddenQueries { get; set; }
        public DateTimeOffset Time { get; init; }
    }

    public sealed record FakeTrade(long Id, long OrderId, string Symbol, bool Buyer, decimal Price, decimal Quantity, decimal Commission, string CommissionAsset, DateTimeOffset Time);

    private readonly object _lock = new();
    private long _nextOrder = 1000;
    private long _nextTrade = 50000;
    private readonly List<(string Method, string Path, Fault Fault, int Hidden)> _faults = [];

    public List<FakeOrder> Orders { get; } = [];
    public List<FakeTrade> Trades { get; } = [];
    public Dictionary<string, decimal> Free { get; } = new(StringComparer.Ordinal) { ["USDT"] = 100_000m };
    public Dictionary<string, decimal> Locked { get; } = new(StringComparer.Ordinal);
    public Func<string, decimal> Price { get; set; } = _ => 100m;
    public decimal FillFraction { get; set; } = 1m;
    public decimal CommissionRate { get; set; }
    public HashSet<string> RejectStops { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RestingIoc { get; } = new(StringComparer.Ordinal);
    public HashSet<string> NoStopLoss { get; } = new(StringComparer.Ordinal);
    public HashSet<string> NoLiquidity { get; } = new(StringComparer.Ordinal);
    public decimal MinNotional { get; set; } = 5m;
    public decimal StepSize { get; set; } = 0.00001m;
    public decimal TickSize { get; set; } = 0.0001m;
    public int Requests { get; private set; }
    public List<string> Log { get; } = [];

    /// <summary>Time before the exchange applies a request (a client timeout shorter than this means it never arrives).</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>A real client: HMAC signing, then the Testnet guard, then this fake instead of the network.</summary>
    public HttpClient Client(IReadOnlySet<string> symbols, ExchangeClock clock) =>
        BinanceTestnetAdapter.CreateClient(new TestnetCredentials(ApiKey, SigningKey), clock, symbols, this);

    public void FaultOnce(string method, string path, Fault fault, int hidden = 2)
    {
        lock (_lock)
        {
            _faults.Add((method, path, fault, hidden));
        }
    }

    /// <summary>How many orders the exchange accepted with this client order id (a duplicate is more than one).</summary>
    public int Accepted(string clientOrderId) => Orders.Count(o => o.ClientOrderId == clientOrderId);

    public decimal Balance(string asset) => Free.GetValueOrDefault(asset) + Locked.GetValueOrDefault(asset);

    /// <summary>Stops of the symbol whose level the bar reached fill, at the open after a gap, else at the stop (ADR-0014).</summary>
    public int TriggerStops(string symbol, decimal low, decimal open, DateTimeOffset at)
    {
        lock (_lock)
        {
            var hit = Orders.Where(o => o.Symbol == symbol && o.Status == "NEW" && o.Type is "STOP_LOSS" or "STOP_LOSS_LIMIT" && o.StopPrice >= low).ToList();
            foreach (var o in hit)
            {
                Unlock(o);
                Fill(o, o.Quantity, open <= o.StopPrice ? open : o.StopPrice!.Value, at);
                o.Status = "FILLED";
            }

            return hit.Count;
        }
    }

    /// <summary>A Testnet reset (S-B19): orders and trades are wiped and balances are fresh.</summary>
    public void Reset(decimal usdt)
    {
        lock (_lock)
        {
            Orders.Clear();
            Trades.Clear();
            Free.Clear();
            Locked.Clear();
            Free["USDT"] = usdt;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken);
        }

        var uri = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(uri.Query);
        var parameters = query.AllKeys.ToDictionary(k => k!, k => query[k]!, StringComparer.Ordinal);
        var method = request.Method.Method;
        var path = uri.AbsolutePath;
        Fault? fault;
        int hidden;
        lock (_lock)
        {
            Requests++;
            Log.Add($"{method} {path}");
            var index = _faults.FindIndex(f => f.Method == method && f.Path == path);
            (fault, hidden) = index < 0 ? ((Fault?)null, 0) : (_faults[index].Fault, _faults[index].Hidden);
            if (index >= 0)
            {
                _faults.RemoveAt(index);
            }
        }

        switch (fault)
        {
            case Fault.TimeoutBeforeApply:
                throw new TaskCanceledException("injected timeout before the exchange saw the request");
            case Fault.ConnectionErrorBeforeApply:
                throw new HttpRequestException("injected connection reset");
            case Fault.ClockSkew:
                return Error(HttpStatusCode.BadRequest, -1021, "Timestamp for this request is outside of the recvWindow.");
            case Fault.RateLimited:
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)) } };
            case Fault.IpBanned:
                return new HttpResponseMessage((HttpStatusCode)418);
            case Fault.Redirect:
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.binance.com/api/v3/order") } };
        }

        if (parameters.ContainsKey("signature"))
        {
            var payload = uri.Query.TrimStart('?');
            payload = payload[..payload.LastIndexOf("&signature=", StringComparison.Ordinal)];
            if (!request.Headers.TryGetValues("X-MBX-APIKEY", out var keys) || keys.Single() != ApiKey)
            {
                return Error(HttpStatusCode.Unauthorized, -2015, "Invalid API-key, IP, or permissions for action.");
            }

            if (!string.Equals(HmacSigningHandler.Sign(payload, SigningKey), parameters["signature"], StringComparison.OrdinalIgnoreCase))
            {
                return Error(HttpStatusCode.BadRequest, -1022, "Signature for this request is not valid.");
            }
        }

        HttpResponseMessage response;
        lock (_lock)
        {
            response = Handle(method, path, parameters);
            if (fault == Fault.HiddenAfterApply && parameters.TryGetValue("newClientOrderId", out var placed))
            {
                foreach (var o in Orders.Where(o => o.ClientOrderId == placed))
                {
                    o.HiddenQueries = hidden;
                }
            }
        }

        return fault switch
        {
            Fault.TimeoutAfterApply or Fault.HiddenAfterApply => throw new TaskCanceledException("injected timeout after the exchange applied the request"),
            Fault.ServerErrorAfterApply => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") },
            Fault.BackendTimeoutAfterApply => Error(HttpStatusCode.BadRequest, -1007, "Timeout waiting for response from backend server. Send status unknown; execution status unknown."),
            _ => response,
        };
    }

    private HttpResponseMessage Handle(string method, string path, Dictionary<string, string> p)
    {
        switch (method, path)
        {
            case ("GET", "/api/v3/time"):
                return Ok(new { serverTime = time.GetUtcNow().ToUnixTimeMilliseconds() });
            case ("GET", "/api/v3/exchangeInfo"):
                return Ok(new { symbols = JsonSerializer.Deserialize<string[]>(p["symbols"])!.Select(Rules).ToArray() });
            case ("GET", "/api/v3/avgPrice"):
                return Ok(new { mins = 5, price = Text(Price(p["symbol"])) });
            case ("GET", "/api/v3/account"):
                return Ok(new
                {
                    canTrade = true,
                    permissions = new[] { "SPOT" },
                    balances = Free.Keys.Union(Locked.Keys).Order(StringComparer.Ordinal).Where(a => Balance(a) != 0)
                        .Select(a => new { asset = a, free = Text(Free.GetValueOrDefault(a)), locked = Text(Locked.GetValueOrDefault(a)) }).ToArray(),
                });
            case ("POST", "/api/v3/order"):
                return Place(p, p["newClientOrderId"], out var placed) ?? Ok(View(placed!, true));
            case ("GET", "/api/v3/order"):
            {
                var o = Orders.LastOrDefault(x => x.Symbol == p["symbol"] && x.ClientOrderId == p["origClientOrderId"]);
                if (o is null || o.HiddenQueries > 0)
                {
                    if (o is not null)
                    {
                        o.HiddenQueries--;
                    }

                    return Error(HttpStatusCode.BadRequest, -2013, "Order does not exist.");
                }

                return Ok(View(o, false));
            }

            case ("DELETE", "/api/v3/order"):
                return Cancel(p["symbol"], p["origClientOrderId"], out var canceled) ?? Ok(View(canceled!, false, cancel: true));
            case ("POST", "/api/v3/order/cancelReplace"):
            {
                var cancelError = Cancel(p["symbol"], p["cancelOrigClientOrderId"], out var old);
                if (cancelError is not null)
                {
                    return Json(HttpStatusCode.BadRequest, new
                    {
                        code = -2022, msg = "Order cancel-replace failed.",
                        data = new { cancelResult = "FAILURE", newOrderResult = "NOT_ATTEMPTED", cancelResponse = new { code = -2011, msg = "Unknown order sent." }, newOrderResponse = (object?)null },
                    });
                }

                var placeError = Place(p, p["newClientOrderId"], out var replacement);
                if (placeError is not null)
                {
                    return Json(HttpStatusCode.Conflict, new
                    {
                        code = -2021, msg = "Order cancel-replace partially failed.",
                        data = new { cancelResult = "SUCCESS", newOrderResult = "FAILURE", cancelResponse = View(old!, false, cancel: true), newOrderResponse = new { code = -2010, msg = "Order would trigger immediately." } },
                    });
                }

                return Ok(new { cancelResult = "SUCCESS", newOrderResult = "SUCCESS", cancelResponse = View(old!, false, cancel: true), newOrderResponse = View(replacement!, true) });
            }

            case ("GET", "/api/v3/openOrders"):
                return Ok(Orders.Where(o => o.Symbol == p["symbol"] && o.Status is "NEW" or "PARTIALLY_FILLED").Select(o => View(o, false)).ToArray());
            case ("GET", "/api/v3/myTrades"):
                return Ok(Trades.Where(t => t.Symbol == p["symbol"] && t.OrderId == long.Parse(p["orderId"], CultureInfo.InvariantCulture)).Select(t => new
                {
                    id = t.Id, orderId = t.OrderId, price = Text(t.Price), qty = Text(t.Quantity), quoteQty = Text(t.Price * t.Quantity),
                    commission = Text(t.Commission), commissionAsset = t.CommissionAsset, time = t.Time.ToUnixTimeMilliseconds(), isBuyer = t.Buyer,
                }).ToArray());
            default:
                return Error(HttpStatusCode.NotFound, -1, "unknown endpoint");
        }
    }

    private HttpResponseMessage? Place(Dictionary<string, string> p, string id, out FakeOrder? order)
    {
        order = null;
        if (Orders.Any(o => o.ClientOrderId == id && o.Status is "NEW" or "PARTIALLY_FILLED"))
        {
            return Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent.");
        }

        var symbol = p["symbol"];
        var type = p["type"];
        var side = p["side"];
        if (type is "STOP_LOSS" or "STOP_LOSS_LIMIT" && (RejectStops.Contains(symbol) || (type == "STOP_LOSS" && NoStopLoss.Contains(symbol))))
        {
            return Error(HttpStatusCode.BadRequest, -2010, "Order would trigger immediately.");
        }

        var quantity = Dec(p["quantity"]);
        decimal? price = p.TryGetValue("price", out var pr) ? Dec(pr) : null;
        decimal? stop = p.TryGetValue("stopPrice", out var sp) ? Dec(sp) : null;
        var reference = Price(symbol);
        var (baseAsset, quote) = (symbol[..^4], "USDT");
        var cost = quantity * (price ?? reference);
        if (side == "BUY" ? Free.GetValueOrDefault(quote) < cost : Free.GetValueOrDefault(baseAsset) < quantity)
        {
            return Error(HttpStatusCode.BadRequest, -2010, "Account has insufficient balance for requested action.");
        }

        order = new FakeOrder
        {
            Symbol = symbol, ClientOrderId = id, OrderId = ++_nextOrder, Side = side, Type = type, TimeInForce = p.GetValueOrDefault("timeInForce"),
            Quantity = quantity, Price = price, StopPrice = stop, Time = time.GetUtcNow(),
        };
        Orders.Add(order);
        var now = time.GetUtcNow();
        var marketable = type == "MARKET" || (type == "LIMIT" && side == "BUY" && price >= reference) || (type == "LIMIT" && side == "SELL" && price <= reference);
        if (type is "STOP_LOSS" or "STOP_LOSS_LIMIT" || (type == "LIMIT" && !marketable && order.TimeInForce == "GTC") || (type == "LIMIT" && RestingIoc.Contains(symbol)))
        {
            Lock(order);
            return null;
        }

        if (!marketable || NoLiquidity.Contains(symbol))
        {
            order.Status = "EXPIRED";
            return null;
        }

        var filled = Math.Floor(quantity * FillFraction / StepSize) * StepSize;
        if (filled > 0)
        {
            Fill(order, filled, reference, now);
        }

        order.Status = filled == quantity ? "FILLED" : "EXPIRED";
        return null;
    }

    private HttpResponseMessage? Cancel(string symbol, string id, out FakeOrder? order)
    {
        order = Orders.LastOrDefault(o => o.Symbol == symbol && o.ClientOrderId == id && o.Status is "NEW" or "PARTIALLY_FILLED");
        if (order is null)
        {
            return Error(HttpStatusCode.BadRequest, -2011, "Unknown order sent.");
        }

        Unlock(order);
        order.Status = "CANCELED";
        return null;
    }

    private void Lock(FakeOrder o)
    {
        var (asset, amount) = o.Side == "BUY" ? ("USDT", o.Quantity * o.Price!.Value) : (o.Symbol[..^4], o.Quantity);
        Free[asset] = Free.GetValueOrDefault(asset) - amount;
        Locked[asset] = Locked.GetValueOrDefault(asset) + amount;
    }

    private void Unlock(FakeOrder o)
    {
        var (asset, amount) = o.Side == "BUY" ? ("USDT", (o.Quantity - o.Executed) * o.Price!.Value) : (o.Symbol[..^4], o.Quantity - o.Executed);
        Free[asset] = Free.GetValueOrDefault(asset) + amount;
        Locked[asset] = Locked.GetValueOrDefault(asset) - amount;
    }

    private void Fill(FakeOrder o, decimal quantity, decimal price, DateTimeOffset at)
    {
        var (baseAsset, quote) = (o.Symbol[..^4], "USDT");
        var buyer = o.Side == "BUY";
        // Binance charges a buy in the base asset and a sell in the quote asset (without BNB discount).
        var commission = buyer ? quantity * CommissionRate : quantity * price * CommissionRate;
        var asset = buyer ? baseAsset : quote;
        Trades.Add(new FakeTrade(++_nextTrade, o.OrderId, o.Symbol, buyer, price, quantity, commission, asset, at));
        Free[baseAsset] = Free.GetValueOrDefault(baseAsset) + (buyer ? quantity - commission : -quantity);
        Free[quote] = Free.GetValueOrDefault(quote) + (buyer ? -quantity * price : quantity * price - commission);
        o.Executed += quantity;
        o.Quote += quantity * price;
    }

    private object Rules(string symbol) => new
    {
        symbol,
        status = "TRADING",
        baseAsset = symbol[..^4],
        quoteAsset = "USDT",
        orderTypes = new[] { "LIMIT", "LIMIT_MAKER", "MARKET", "STOP_LOSS_LIMIT", "TAKE_PROFIT_LIMIT" }.Concat(NoStopLoss.Contains(symbol) ? [] : ["STOP_LOSS", "TAKE_PROFIT"]).ToArray(),
        filters = new object[]
        {
            new { filterType = "PRICE_FILTER", minPrice = Text(TickSize), maxPrice = "1000000.00000000", tickSize = Text(TickSize) },
            new { filterType = "LOT_SIZE", minQty = Text(StepSize), maxQty = "9000000.00000000", stepSize = Text(StepSize) },
            new { filterType = "MARKET_LOT_SIZE", minQty = "0.00000000", maxQty = "9000000.00000000", stepSize = "0.00000000" },
            new { filterType = "NOTIONAL", minNotional = Text(MinNotional), applyMinToMarket = true, maxNotional = "9000000.00000000", applyMaxToMarket = false, avgPriceMins = 5 },
            new { filterType = "PERCENT_PRICE_BY_SIDE", bidMultiplierUp = "5", bidMultiplierDown = "0.2", askMultiplierUp = "5", askMultiplierDown = "0.2", avgPriceMins = 5 },
            new { filterType = "MAX_NUM_ORDERS", maxNumOrders = 200 },
            new { filterType = "MAX_NUM_ALGO_ORDERS", maxNumAlgoOrders = 5 },
        },
    };

    private object View(FakeOrder o, bool full, bool cancel = false) => new Dictionary<string, object?>
    {
        ["symbol"] = o.Symbol,
        ["orderId"] = o.OrderId,
        ["clientOrderId"] = cancel ? "cancel-" + o.OrderId : o.ClientOrderId,
        ["origClientOrderId"] = cancel ? o.ClientOrderId : null,
        ["transactTime"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
        ["price"] = Text(o.Price ?? 0),
        ["origQty"] = Text(o.Quantity),
        ["executedQty"] = Text(o.Executed),
        ["cummulativeQuoteQty"] = Text(o.Quote),
        ["status"] = o.Status,
        ["timeInForce"] = o.TimeInForce ?? "GTC",
        ["type"] = o.Type,
        ["side"] = o.Side,
        ["stopPrice"] = Text(o.StopPrice ?? 0),
        ["fills"] = full && o.Type is "LIMIT" or "MARKET"
            ? Trades.Where(t => t.OrderId == o.OrderId).Select(t => new { price = Text(t.Price), qty = Text(t.Quantity), commission = Text(t.Commission), commissionAsset = t.CommissionAsset, tradeId = t.Id }).ToArray()
            : null,
    }.Where(kv => kv.Value is not null).ToDictionary();

    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Text(decimal d) => d.ToString("0.00000000##########", CultureInfo.InvariantCulture);

    private static HttpResponseMessage Ok(object body) => Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage Error(HttpStatusCode status, int code, string msg) => Json(status, new { code, msg });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
