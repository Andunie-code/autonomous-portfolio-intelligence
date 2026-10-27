using System.Globalization;
using System.Text.Json;

namespace PortfolioIntelligence.Execution.Exchange.Binance;

/// <summary>
/// Binance Spot Testnet over REST (ADR-0021 §1; S-B11…S-B20). The HTTP client must be built with
/// <see cref="CreateClient"/>: HMAC signing, then the Testnet guard as the last handler. Error semantics: HTTP 5xx,
/// -1007, a timeout or a broken connection mean "execution status unknown" (<see cref="ExchangeUnknownException"/>);
/// other 4xx answers are definitive rejections; -2013 on a query and "Unknown order sent." on a cancel mean the
/// exchange does not know the order (null).
/// </summary>
public sealed class BinanceTestnetAdapter(HttpClient http, ExchangeClock clock) : IExchangeAdapter
{
    public const string BaseAddress = "https://testnet.binance.vision";

    public static HttpClient CreateClient(TestnetCredentials credentials, ExchangeClock clock, IReadOnlySet<string> symbols, HttpMessageHandler network, TimeSpan? timeout = null) =>
        new(new HmacSigningHandler(credentials, clock, new TestnetGuardHandler(symbols, network))) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };

    public async Task<DateTimeOffset> ServerTimeAsync(CancellationToken ct)
    {
        var sent = clock.LocalNow;
        var json = await SendAsync(HttpMethod.Get, "/api/v3/time", [], false, ct);
        var server = DateTimeOffset.FromUnixTimeMilliseconds(json.GetProperty("serverTime").GetInt64());
        clock.Update(server, sent, clock.LocalNow);
        return server;
    }

    public async Task<IReadOnlyDictionary<string, SymbolRules>> SymbolRulesAsync(IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/v3/exchangeInfo", [("symbols", JsonSerializer.Serialize(symbols.Order(StringComparer.Ordinal)))], false, ct);
        return json.GetProperty("symbols").EnumerateArray().Select(ParseRules).ToDictionary(r => r.Symbol, StringComparer.Ordinal);
    }

    public async Task<decimal> ReferencePriceAsync(string symbol, CancellationToken ct) =>
        Dec((await SendAsync(HttpMethod.Get, "/api/v3/avgPrice", [("symbol", symbol)], false, ct)).GetProperty("price"));

    public async Task<AccountSnapshot> AccountAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/v3/account", [("omitZeroBalances", "true")], true, ct);
        var balances = json.GetProperty("balances").EnumerateArray()
            .Select(b => new AssetBalance(b.GetProperty("asset").GetString()!, Dec(b.GetProperty("free")), Dec(b.GetProperty("locked"))))
            .ToDictionary(b => b.Asset, StringComparer.Ordinal);
        return new AccountSnapshot(json.GetProperty("canTrade").GetBoolean(), balances);
    }

    public async Task<OrderState> PlaceAsync(OrderRequest request, CancellationToken ct) =>
        ParseOrder(await SendAsync(HttpMethod.Post, "/api/v3/order", [.. OrderParameters(request, "newClientOrderId")], true, ct));

    public async Task<OrderState?> QueryAsync(string symbol, string clientOrderId, CancellationToken ct)
    {
        try
        {
            return ParseOrder(await SendAsync(HttpMethod.Get, "/api/v3/order", [("symbol", symbol), ("origClientOrderId", clientOrderId)], true, ct));
        }
        catch (ExchangeRejectedException e) when (e.Code == -2013)
        {
            return null;
        }
    }

    public async Task<OrderState?> CancelAsync(string symbol, string clientOrderId, CancellationToken ct)
    {
        try
        {
            return ParseOrder(await SendAsync(HttpMethod.Delete, "/api/v3/order", [("symbol", symbol), ("origClientOrderId", clientOrderId)], true, ct));
        }
        catch (ExchangeRejectedException e) when (IsUnknownOrder(e))
        {
            return null;
        }
    }

    public async Task<CancelReplaceResult> CancelReplaceAsync(string symbol, string cancelClientOrderId, OrderRequest replacement, CancellationToken ct)
    {
        List<(string, string)> parameters = [.. OrderParameters(replacement, "newClientOrderId"), ("cancelReplaceMode", "STOP_ON_FAILURE"), ("cancelOrigClientOrderId", cancelClientOrderId)];
        try
        {
            var json = await SendAsync(HttpMethod.Post, "/api/v3/order/cancelReplace", parameters, true, ct);
            return new CancelReplaceResult(true, ParseOrder(json.GetProperty("newOrderResponse")), null, null, null, null);
        }
        catch (ExchangeRejectedException e) when (e.Code is -2021 or -2022 && e.Data["body"] is JsonElement body && body.TryGetProperty("data", out var data))
        {
            var cancelOk = data.TryGetProperty("cancelResult", out var c) && c.GetString() == "SUCCESS";
            var newResponse = data.TryGetProperty("newOrderResponse", out var n) ? n : default;
            var cancelResponse = data.TryGetProperty("cancelResponse", out var cr) ? cr : default;
            var newOk = data.TryGetProperty("newOrderResult", out var nr) && nr.GetString() == "SUCCESS";
            return new CancelReplaceResult(cancelOk, newOk ? ParseOrder(newResponse) : null,
                newOk ? null : Code(newResponse), newOk ? null : Message(newResponse), cancelOk ? null : Code(cancelResponse), cancelOk ? null : Message(cancelResponse));
        }
    }

    public async Task<IReadOnlyList<OrderState>> OpenOrdersAsync(string symbol, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, "/api/v3/openOrders", [("symbol", symbol)], true, ct)).EnumerateArray().Select(ParseOrder).ToList();

    public async Task<IReadOnlyList<ExchangeTrade>> TradesAsync(string symbol, long orderId, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, "/api/v3/myTrades", [("symbol", symbol), ("orderId", orderId.ToString(CultureInfo.InvariantCulture))], true, ct))
        .EnumerateArray().Select(t => new ExchangeTrade(t.GetProperty("id").GetInt64(), t.GetProperty("orderId").GetInt64(), Dec(t.GetProperty("price")), Dec(t.GetProperty("qty")),
            Dec(t.GetProperty("quoteQty")), Dec(t.GetProperty("commission")), t.GetProperty("commissionAsset").GetString()!,
            DateTimeOffset.FromUnixTimeMilliseconds(t.GetProperty("time").GetInt64()), t.GetProperty("isBuyer").GetBoolean())).ToList();

    private static bool IsUnknownOrder(ExchangeRejectedException e) =>
        e.Code == -2013 || (e.Code == -2011 && e.ExchangeMessage.Contains("Unknown order", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string, string)> OrderParameters(OrderRequest r, string idName)
    {
        yield return ("symbol", r.Symbol);
        yield return ("side", r.Side == OrderSide.Buy ? "BUY" : "SELL");
        yield return ("type", r.Type switch
        {
            OrderType.Limit => "LIMIT",
            OrderType.Market => "MARKET",
            OrderType.StopLoss => "STOP_LOSS",
            OrderType.StopLossLimit => "STOP_LOSS_LIMIT",
            _ => throw new ArgumentOutOfRangeException(nameof(r)),
        });
        if (r.TimeInForce is { } tif)
        {
            yield return ("timeInForce", tif);
        }

        yield return ("quantity", Text(r.Quantity));
        if (r.Price is { } price)
        {
            yield return ("price", Text(price));
        }

        if (r.StopPrice is { } stop)
        {
            yield return ("stopPrice", Text(stop));
        }

        yield return (idName, r.ClientOrderId);
        // Stops answer ACK by default (S-B14); RESULT gives their status. FULL adds the fills of market and limit orders.
        yield return ("newOrderRespType", r.Type is OrderType.StopLoss or OrderType.StopLossLimit ? "RESULT" : "FULL");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyList<(string Name, string Value)> parameters, bool signed, CancellationToken ct)
    {
        var query = string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(method, $"{BaseAddress}{path}{(query.Length > 0 ? "?" : "")}{query}");
        request.Options.Set(HmacSigningHandler.Signed, signed);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ExchangeUnknownException("connection error", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ExchangeUnknownException("timeout", e);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (status >= 500)
            {
                throw new ExchangeUnknownException($"HTTP {status}");
            }

            JsonElement json;
            try
            {
                json = JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException) when (status is >= 200 and < 300)
            {
                throw new ExchangeUnknownException("unreadable answer");
            }
            catch (JsonException)
            {
                throw new ExchangeRejectedException(0, $"HTTP {status}", status);
            }

            if (status is >= 200 and < 300)
            {
                return json;
            }

            var code = Code(json) ?? 0;
            if (code == -1007)
            {
                throw new ExchangeUnknownException("-1007 timeout waiting for the backend");
            }

            var error = new ExchangeRejectedException(code, Message(json) ?? $"HTTP {status}", status);
            error.Data["body"] = json;
            throw error;
        }
    }

    private static int? Code(JsonElement json) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : null;

    private static string? Message(JsonElement json) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("msg", out var m) ? m.GetString() : null;

    private static OrderState ParseOrder(JsonElement o)
    {
        var id = o.TryGetProperty("origClientOrderId", out var orig) && orig.GetString() is { Length: > 0 } origId ? origId : o.GetProperty("clientOrderId").GetString()!;
        var orderId = o.GetProperty("orderId").GetInt64();
        var status = o.GetProperty("status").GetString()!;
        var fills = o.TryGetProperty("fills", out var f)
            ? f.EnumerateArray().Select(x => new ExchangeTrade(x.GetProperty("tradeId").GetInt64(), orderId, Dec(x.GetProperty("price")), Dec(x.GetProperty("qty")),
                Dec(x.GetProperty("price")) * Dec(x.GetProperty("qty")), Dec(x.GetProperty("commission")), x.GetProperty("commissionAsset").GetString()!,
                o.TryGetProperty("transactTime", out var tt) ? DateTimeOffset.FromUnixTimeMilliseconds(tt.GetInt64()) : default, o.GetProperty("side").GetString() == "BUY")).ToList()
            : [];
        return new OrderState(o.GetProperty("symbol").GetString()!, id, orderId, OrderStatuses.FromExchange(status), status,
            o.TryGetProperty("origQty", out var q) ? Dec(q) : 0, o.TryGetProperty("executedQty", out var e) ? Dec(e) : 0,
            o.TryGetProperty("cummulativeQuoteQty", out var cq) ? Dec(cq) : 0,
            o.TryGetProperty("stopPrice", out var sp) && Dec(sp) is var stop && stop > 0 ? stop : null, fills);
    }

    private static SymbolRules ParseRules(JsonElement s)
    {
        PriceFilter? price = null;
        LotFilter? lot = null, marketLot = null;
        NotionalFilter? notional = null;
        PercentPriceBySide? percent = null;
        int? maxOrders = null, maxAlgo = null;
        foreach (var f in s.GetProperty("filters").EnumerateArray())
        {
            switch (f.GetProperty("filterType").GetString())
            {
                case "PRICE_FILTER":
                    price = new PriceFilter(Dec(f.GetProperty("minPrice")), Dec(f.GetProperty("maxPrice")), Dec(f.GetProperty("tickSize")));
                    break;
                case "LOT_SIZE":
                    lot = new LotFilter(Dec(f.GetProperty("minQty")), Dec(f.GetProperty("maxQty")), Dec(f.GetProperty("stepSize")));
                    break;
                case "MARKET_LOT_SIZE":
                    marketLot = new LotFilter(Dec(f.GetProperty("minQty")), Dec(f.GetProperty("maxQty")), Dec(f.GetProperty("stepSize")));
                    break;
                case "MIN_NOTIONAL":
                    notional ??= new NotionalFilter(Dec(f.GetProperty("minNotional")), 0, f.TryGetProperty("applyToMarket", out var a) && a.GetBoolean(), false);
                    break;
                case "NOTIONAL":
                    notional = new NotionalFilter(Dec(f.GetProperty("minNotional")), f.TryGetProperty("maxNotional", out var mx) ? Dec(mx) : 0,
                        f.TryGetProperty("applyMinToMarket", out var amin) && amin.GetBoolean(), f.TryGetProperty("applyMaxToMarket", out var amax) && amax.GetBoolean());
                    break;
                case "PERCENT_PRICE_BY_SIDE":
                    percent = new PercentPriceBySide(Dec(f.GetProperty("bidMultiplierUp")), Dec(f.GetProperty("bidMultiplierDown")),
                        Dec(f.GetProperty("askMultiplierUp")), Dec(f.GetProperty("askMultiplierDown")));
                    break;
                case "MAX_NUM_ORDERS":
                    maxOrders = f.GetProperty("maxNumOrders").GetInt32();
                    break;
                case "MAX_NUM_ALGO_ORDERS":
                    maxAlgo = f.GetProperty("maxNumAlgoOrders").GetInt32();
                    break;
            }
        }

        var types = s.GetProperty("orderTypes").EnumerateArray().Select(t => t.GetString()).Select(t => t switch
        {
            "LIMIT" => (OrderType?)OrderType.Limit,
            "MARKET" => OrderType.Market,
            "STOP_LOSS" => OrderType.StopLoss,
            "STOP_LOSS_LIMIT" => OrderType.StopLossLimit,
            _ => null,
        }).OfType<OrderType>().ToHashSet();
        return new SymbolRules(s.GetProperty("symbol").GetString()!, s.GetProperty("baseAsset").GetString()!, s.GetProperty("quoteAsset").GetString()!,
            s.GetProperty("status").GetString()!, types, price, lot, marketLot, notional, percent, maxOrders, maxAlgo);
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
        : e.GetDecimal();

    /// <summary>Plain decimal text without exponent or trailing zeros (exchange parameter format).</summary>
    public static string Text(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
