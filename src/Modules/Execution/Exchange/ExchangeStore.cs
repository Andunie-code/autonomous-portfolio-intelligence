using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution.Exchange;

/// <summary>An order of the outbox with its latest event (m6-protocol §2.1).</summary>
public sealed record OutboxOrder(
    long Id,
    long RunId,
    int Sleeve,
    string Symbol,
    OrderPurpose Purpose,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    DateTimeOffset DecisionBar,
    int Attempt,
    string ClientOrderId,
    DateTimeOffset? SentAt,
    OrderStatus Status,
    long? ExchangeOrderId,
    decimal ExecutedQuantity,
    string? Reason,
    int UnknownQueries)
{
    public bool IsTerminal => OrderStatuses.IsTerminal(Status);
}

/// <summary>A recorded exchange trade of one of the system's orders, with its order's sleeve and purpose.</summary>
public sealed record OutboxFill(long Id, long OrderId, long RunId, int Sleeve, string Symbol, OrderPurpose Purpose, OrderSide Side, long TradeId, decimal Price, decimal Quantity,
    decimal QuoteQuantity, decimal Commission, string CommissionAsset, DateTimeOffset TradeTime, DateTimeOffset StepBar);

public sealed record TestnetBaseline(long Id, long RunId, string Source, long LastFillId, IReadOnlyDictionary<string, decimal> Balances);

/// <summary>
/// The append-only outbox of migration 0009 (ADR-0002, ADR-0021 §2), as trading_app: an order row and its first
/// event are written in one transaction before anything is sent; later states are new events; trades are unique by
/// (symbol, trade id).
/// </summary>
public sealed class ExchangeStore(string connectionString)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private const string OrderColumns = """
        o.id, o.run_id, o.sleeve, o.symbol, o.purpose, o.side, o.order_type, o.quantity, o.price, o.stop_price, o.decision_bar, o.attempt,
        o.client_order_id, o.sent_at, e.status, e.exchange_order_id, coalesce(e.executed_quantity, 0), e.detail ->> 'reason',
        (SELECT count(*) FROM trading.exchange_order_events u WHERE u.order_id = o.id AND u.detail ->> 'query' = 'not-found-after-horizon')::int
        FROM trading.exchange_orders o
        JOIN LATERAL (SELECT * FROM trading.exchange_order_events x WHERE x.order_id = o.id ORDER BY x.id DESC LIMIT 1) e ON true
        """;

    private static OutboxOrder ReadOrder(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), Enum.Parse<OrderPurpose>(r.GetString(4)), Enum.Parse<OrderSide>(r.GetString(5)),
        Enum.Parse<OrderType>(r.GetString(6)), r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9),
        r.GetFieldValue<DateTimeOffset>(10), r.GetInt32(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
        Enum.Parse<OrderStatus>(r.GetString(14)), r.IsDBNull(15) ? null : r.GetInt64(15), r.GetDecimal(16), r.IsDBNull(17) ? null : r.GetString(17), r.GetInt32(18));

    /// <summary>Orders of the given runs (all runs when null), oldest first.</summary>
    public async Task<IReadOnlyList<OutboxOrder>> OrdersAsync(IReadOnlyCollection<long>? runIds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT {OrderColumns} WHERE $1::bigint[] IS NULL OR o.run_id = ANY($1) ORDER BY o.id", connection);
        command.Parameters.AddWithValue(runIds is null ? DBNull.Value : runIds.ToArray());
        var result = new List<OutboxOrder>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadOrder(reader));
        }

        return result;
    }

    public async Task<OutboxOrder> OrderAsync(string clientOrderId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT {OrderColumns} WHERE o.client_order_id = $1", connection);
        command.Parameters.AddWithValue(clientOrderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadOrder(reader) : throw new InvalidOperationException($"No order {clientOrderId}.");
    }

    /// <summary>
    /// Writes an order and its first event (Submitting before sending, or LocalRejected) in one transaction. The
    /// database refuses a second active order for the same intent (duplicate guard of migration 0009).
    /// </summary>
    public async Task<long> CreateAsync(long runId, int sleeve, OrderPurpose purpose, DateTimeOffset decisionBar, int attempt, OrderDraft order,
        OrderStatus first, DateTimeOffset? sentAt, string? replaces, object detail, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        long id;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO trading.exchange_orders (run_id, sleeve, symbol, purpose, side, order_type, time_in_force, quantity, price, stop_price,
                decision_bar, attempt, client_order_id, sent_at, replaces)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15) RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(runId);
            insert.Parameters.AddWithValue(sleeve);
            insert.Parameters.AddWithValue(order.Symbol);
            insert.Parameters.AddWithValue(purpose.ToString());
            insert.Parameters.AddWithValue(order.Side.ToString());
            insert.Parameters.AddWithValue(order.Type.ToString());
            insert.Parameters.AddWithValue((object?)order.TimeInForce ?? DBNull.Value);
            insert.Parameters.AddWithValue(Math.Max(order.Quantity, 0));
            insert.Parameters.AddWithValue((object?)order.Price ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)order.StopPrice ?? DBNull.Value);
            insert.Parameters.AddWithValue(decisionBar);
            insert.Parameters.AddWithValue(attempt);
            insert.Parameters.AddWithValue(order.ClientOrderId);
            insert.Parameters.AddWithValue((object?)sentAt ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)replaces ?? DBNull.Value);
            id = (long)(await insert.ExecuteScalarAsync(ct))!;
        }

        await InsertEvent(connection, transaction, id, first, null, null, detail, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task AddEventAsync(long orderId, OrderStatus status, long? exchangeOrderId, decimal? executed, object detail, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await InsertEvent(connection, null, orderId, status, exchangeOrderId, executed, detail, ct);
    }

    private static async Task InsertEvent(NpgsqlConnection connection, NpgsqlTransaction? transaction, long orderId, OrderStatus status, long? exchangeOrderId, decimal? executed, object detail, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO trading.exchange_order_events (order_id, status, exchange_order_id, executed_quantity, detail) VALUES ($1, $2, $3, $4, $5::jsonb)
            """, connection, transaction);
        insert.Parameters.AddWithValue(orderId);
        insert.Parameters.AddWithValue(status.ToString());
        insert.Parameters.AddWithValue((object?)exchangeOrderId ?? DBNull.Value);
        insert.Parameters.AddWithValue((object?)executed ?? DBNull.Value);
        insert.Parameters.AddWithValue(JsonSerializer.Serialize(detail, PaperJson.Options));
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Records trades of an order; already recorded trades are ignored. Returns how many were new.</summary>
    public async Task<int> AddFillsAsync(long orderId, IEnumerable<ExchangeTrade> trades, Func<ExchangeTrade, DateTimeOffset> stepBar, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var inserted = 0;
        foreach (var t in trades)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO trading.exchange_fills (order_id, symbol, trade_id, price, quantity, quote_quantity, commission, commission_asset, trade_time, step_bar)
                SELECT $1, o.symbol, $2, $3, $4, $5, $6, $7, $8, $9 FROM trading.exchange_orders o WHERE o.id = $1
                ON CONFLICT (symbol, trade_id) DO NOTHING
                """, connection);
            insert.Parameters.AddWithValue(orderId);
            insert.Parameters.AddWithValue(t.TradeId);
            insert.Parameters.AddWithValue(t.Price);
            insert.Parameters.AddWithValue(t.Quantity);
            insert.Parameters.AddWithValue(t.QuoteQuantity);
            insert.Parameters.AddWithValue(t.Commission);
            insert.Parameters.AddWithValue(t.CommissionAsset);
            insert.Parameters.AddWithValue(t.Time);
            insert.Parameters.AddWithValue(stepBar(t));
            inserted += await insert.ExecuteNonQueryAsync(ct);
        }

        return inserted;
    }

    /// <summary>Fills of the given runs (all when null) with an id above <paramref name="afterId"/>, in id order.</summary>
    public async Task<IReadOnlyList<OutboxFill>> FillsAsync(IReadOnlyCollection<long>? runIds, long afterId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT f.id, f.order_id, o.run_id, o.sleeve, f.symbol, o.purpose, o.side, f.trade_id, f.price, f.quantity, f.quote_quantity, f.commission,
                   f.commission_asset, f.trade_time, f.step_bar
            FROM trading.exchange_fills f JOIN trading.exchange_orders o ON o.id = f.order_id
            WHERE ($1::bigint[] IS NULL OR o.run_id = ANY($1)) AND f.id > $2 ORDER BY f.id
            """, connection);
        command.Parameters.AddWithValue(runIds is null ? DBNull.Value : runIds.ToArray());
        command.Parameters.AddWithValue(afterId);
        var result = new List<OutboxFill>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new OutboxFill(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                Enum.Parse<OrderPurpose>(reader.GetString(5)), Enum.Parse<OrderSide>(reader.GetString(6)), reader.GetInt64(7), reader.GetDecimal(8),
                reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetString(12), reader.GetFieldValue<DateTimeOffset>(13),
                reader.GetFieldValue<DateTimeOffset>(14)));
        }

        return result;
    }

    public async Task<string?> LatestRulesHashAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT rules_hash FROM trading.exchange_symbol_rules ORDER BY id DESC LIMIT 1", connection);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task AddRulesAsync(IReadOnlyDictionary<string, SymbolRules> rules, string hash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO trading.exchange_symbol_rules (rules, rules_hash) VALUES ($1::jsonb, $2)", connection);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(rules.OrderBy(r => r.Key, StringComparer.Ordinal).ToDictionary(r => r.Key, r => r.Value.Canonical()), PaperJson.Options));
        command.Parameters.AddWithValue(hash);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TestnetBaseline?> LatestBaselineAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id, run_id, source, last_fill_id, balances::text FROM trading.testnet_baselines ORDER BY id DESC LIMIT 1", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new TestnetBaseline(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                JsonSerializer.Deserialize<Dictionary<string, decimal>>(reader.GetString(4), PaperJson.Options)!)
            : null;
    }

    /// <summary>Baseline sources already used (run-start:&lt;run&gt;, rebaseline:&lt;action&gt;).</summary>
    public async Task<IReadOnlySet<string>> BaselineSourcesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT source FROM trading.testnet_baselines", connection);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task AddBaselineAsync(long runId, string source, IReadOnlyDictionary<string, decimal> balances, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(balances.OrderBy(b => b.Key, StringComparer.Ordinal).ToDictionary(b => b.Key, b => b.Value), PaperJson.Options);
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO trading.testnet_baselines (run_id, source, last_fill_id, balances, balances_hash)
            VALUES ($1, $2, (SELECT coalesce(max(id), 0) FROM trading.exchange_fills), $3::jsonb, $4)
            """, connection);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(source);
        command.Parameters.AddWithValue(json);
        command.Parameters.AddWithValue(DslCanonical.Sha256(json));
        await command.ExecuteNonQueryAsync(ct);
    }
}
