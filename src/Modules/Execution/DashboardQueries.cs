using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution;

/// <summary>
/// Read-only queries of the M9a dashboard (ADR-0020 §4) as ops_api: portfolio overview, sleeves and positions,
/// fills, decisions, the provenance chain of a fill, system health and the AI cost ledger and traces. It writes
/// nothing, and it never reads the holdout database or a secret. Paper mode has no separate order or intent table:
/// the L2 engine event (Approved, Clipped, Rejected, Canceled) is the intent with its risk decision (ADR-0018).
/// </summary>
public sealed class DashboardQueries(string connectionString)
{
    public const int MaxRows = 200;

    private static readonly string[] SignalKinds = ["EntrySignal", "ExitSignal"];
    private static readonly string[] DecisionKinds = ["Approved", "Clipped", "Rejected", "Canceled"];
    private static readonly string[] AllocatorKinds = ["AllocatorAdjusted", "AllocatorIgnored"];

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<List<JsonObject>> RowsAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = new NpgsqlCommand($"SELECT coalesce(json_agg(q), '[]'::json)::text FROM ({sql}) q", connection);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }

        var array = JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsArray();
        var rows = array.Select(n => n!.AsObject()).ToList();
        array.Clear(); // detach the rows, so they can be placed in another document
        return rows;
    }

    /// <summary>
    /// The ops schema is older than this build: a missing grant, table or column (42501, 42P01, 42703), usually
    /// because a migration (0008: the dashboard's grants and ops_actions.run_id) has not been applied yet.
    /// </summary>
    public static bool IsSchemaBehind(PostgresException e) => e.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn;

    public const string SchemaBehindMessage = "The ops database schema is older than Trading.Host (migration 0008 not applied?): run scripts/m5-migrate.ps1.";

    /// <summary>A section the schema cannot serve yet is left out and named in <paramref name="unavailable"/>, so the rest still renders.</summary>
    private static async Task<List<JsonObject>> OptionalRowsAsync(NpgsqlConnection connection, string section, JsonArray unavailable, string sql, CancellationToken ct)
    {
        try
        {
            return await RowsAsync(connection, sql, ct);
        }
        catch (PostgresException e) when (IsSchemaBehind(e))
        {
            unavailable.Add(section);
            return [];
        }
    }

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static int Limit(int? limit) => Math.Clamp(limit ?? 50, 1, MaxRows);

    private sealed record RunRow(long Id, DateTimeOffset TradingStart, PaperRunConfig Config);

    private static async Task<List<RunRow>> RunsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT id, trading_start, config::text FROM trading.paper_runs ORDER BY id", connection);
        var runs = new List<RunRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runs.Add(new RunRow(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), PaperRunConfig.Parse(reader.GetString(2))));
        }

        return runs;
    }

    private static async Task<RiskConfigDocument?> CurrentRiskAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT coalesce((SELECT config::text FROM trading.risk_config_versions ORDER BY id DESC LIMIT 1),
                            (SELECT (config -> 'initialRisk')::text FROM trading.paper_runs ORDER BY id DESC LIMIT 1))
            """, connection);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<RiskConfigDocument>(json, PaperJson.Options) : null;
    }

    /// <summary>Latest balance snapshot rows of every run: run, sleeve, cash, equity, positions, bar time.</summary>
    private static Task<List<JsonObject>> LatestBalancesAsync(NpgsqlConnection connection, CancellationToken ct) => RowsAsync(connection, """
        SELECT b.run_id, b.sleeve, b.cash, b.equity, b.positions, b.bar_time
        FROM trading.balance_snapshots b
        WHERE b.bar_time = (SELECT max(bar_time) FROM trading.balance_snapshots WHERE run_id = b.run_id)
        ORDER BY b.run_id, b.sleeve
        """, ct);

    private static decimal D(JsonNode? node) => node is null ? 0m : node.GetValue<decimal>();

    /// <summary>
    /// Portfolio overview: per run and in total, cash, equity, realised and unrealised PnL, drawdown from the run's
    /// peak, exposure, experimental-pair exposure and the correlation clusters' exposure against the D29 cap.
    /// </summary>
    public async Task<string> OverviewAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var runs = await RunsAsync(connection, ct);
        var balances = await LatestBalancesAsync(connection, ct);
        var peaks = (await RowsAsync(connection, """
            SELECT t.run_id, max(t.total) AS peak FROM (
              SELECT b.run_id, b.bar_time, sum(b.equity) AS total FROM trading.balance_snapshots b
              JOIN trading.paper_runs r ON r.id = b.run_id WHERE b.bar_time >= r.trading_start GROUP BY b.run_id, b.bar_time) t
            GROUP BY t.run_id
            """, ct)).ToDictionary(r => r["run_id"]!.GetValue<long>(), r => D(r["peak"]));
        var correlation = (await RowsAsync(connection, """
            SELECT DISTINCT ON (run_id) run_id, bar_time, threshold, clusters, effective_count, max_cluster_share
            FROM trading.correlation_snapshots ORDER BY run_id, bar_time DESC
            """, ct)).ToDictionary(r => r["run_id"]!.GetValue<long>());
        var risk = await CurrentRiskAsync(connection, ct);

        var result = new JsonArray();
        decimal capitalTotal = 0, cashTotal = 0, equityTotal = 0, notionalTotal = 0, unrealisedTotal = 0;
        foreach (var run in runs)
        {
            var rows = balances.Where(b => b["run_id"]!.GetValue<long>() == run.Id).ToList();
            var capital = run.Config.Sleeves.Sum(s => s.Capital);
            // A run that has not processed its first candle holds its starting capital in cash.
            var cash = rows.Count == 0 ? capital : rows.Sum(r => D(r["cash"]));
            var equity = rows.Count == 0 ? capital : rows.Sum(r => D(r["equity"]));
            var positions = rows.SelectMany(r => r["positions"]!.AsArray().Select(p => p!.AsObject())).ToList();
            decimal Notional(JsonObject p) => D(p["quantity"]) * D(p["lastClose"]);
            var notional = positions.Sum(Notional);
            var unrealised = positions.Sum(p => D(p["quantity"]) * (D(p["lastClose"]) - D(p["entryPrice"])));
            var experimental = positions.Where(p => run.Config.Experimental.Contains(p["symbol"]!.GetValue<string>())).Sum(Notional);
            var peak = Math.Max(peaks.GetValueOrDefault(run.Id), capital);
            var clusters = new JsonArray();
            if (correlation.TryGetValue(run.Id, out var c))
            {
                var bySymbol = positions.GroupBy(p => p["symbol"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Sum(Notional));
                foreach (var cluster in c["clusters"]!.AsObject().GroupBy(kv => kv.Value!.GetValue<int>()).OrderBy(g => g.Key))
                {
                    var symbols = cluster.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
                    var share = equity <= 0 ? 0 : symbols.Sum(s => bySymbol.GetValueOrDefault(s)) / equity;
                    clusters.Add(new JsonObject
                    {
                        ["cluster"] = cluster.Key,
                        ["symbols"] = new JsonArray(symbols.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                        ["exposure"] = Math.Round(share, 6),
                    });
                }
            }

            result.Add(new JsonObject
            {
                ["runId"] = run.Id,
                ["venue"] = run.Config.IsTestnet ? PaperRunConfig.TestnetVenue : "paper",
                ["cohortIds"] = new JsonArray(run.Config.Sleeves.Select(s => s.CohortId).Distinct().Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                ["manifestHash"] = run.Config.ManifestHash,
                ["tradingStart"] = run.TradingStart,
                ["barTime"] = rows.Count == 0 ? null : rows[0]["bar_time"]!.DeepClone(),
                ["awaitingFirstBar"] = rows.Count == 0,
                ["capital"] = capital,
                ["cash"] = Math.Round(cash, 8),
                ["equity"] = Math.Round(equity, 8),
                ["pnl"] = Math.Round(equity - capital, 8),
                ["unrealizedPnl"] = Math.Round(unrealised, 8),
                ["realizedPnl"] = Math.Round(equity - capital - unrealised, 8),
                ["drawdown"] = peak <= 0 ? 0 : Math.Round(1 - equity / peak, 6),
                ["exposure"] = equity <= 0 ? 0 : Math.Round(notional / equity, 6),
                ["experimentalExposure"] = equity <= 0 ? 0 : Math.Round(experimental / equity, 6),
                ["openPositions"] = positions.Count,
                ["clusters"] = clusters,
                ["maxClusterShare"] = c?["max_cluster_share"]?.DeepClone(),
                ["clusterThreshold"] = c?["threshold"]?.DeepClone(),
            });
            if (run.Config.IsTestnet)
            {
                continue; // paper totals only: Testnet runs have their own, smaller capital (A-4 v1.1)
            }

            capitalTotal += capital;
            cashTotal += cash;
            equityTotal += equity;
            notionalTotal += notional;
            unrealisedTotal += unrealised;
        }

        return Json(new JsonObject
        {
            ["total"] = new JsonObject
            {
                ["capital"] = capitalTotal,
                ["cash"] = Math.Round(cashTotal, 8),
                ["equity"] = Math.Round(equityTotal, 8),
                ["pnl"] = Math.Round(equityTotal - capitalTotal, 8),
                ["unrealizedPnl"] = Math.Round(unrealisedTotal, 8),
                ["realizedPnl"] = Math.Round(equityTotal - capitalTotal - unrealisedTotal, 8),
                ["exposure"] = equityTotal <= 0 ? 0 : Math.Round(notionalTotal / equityTotal, 6),
            },
            ["limits"] = risk is null ? null : new JsonObject
            {
                ["maxTotalExposure"] = risk.MaxTotalExposure,
                ["maxAssetWeight"] = risk.MaxAssetWeight,
                ["maxDrawdown"] = risk.MaxDrawdown,
                ["maxPositionLoss"] = risk.MaxPositionLoss,
                ["experimentalPairsMaxExposure"] = risk.ExperimentalPairsMaxExposure,
                ["clusterCapThreshold"] = risk.ClusterCapThreshold,
                ["clusterCapMaxExposure"] = risk.ClusterCapMaxExposure,
                ["clusterWarningShare"] = risk.ClusterWarningShare,
            },
            ["runs"] = result,
        });
    }

    /// <summary>
    /// Every sleeve: cohort, lifecycle state, capital, equity, PnL, fills and open positions with the effective stop
    /// and whether the D25 hard stop (entry × (1 − maxPositionLoss)) or the strategy stop binds.
    /// </summary>
    public async Task<string> SleevesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var runs = await RunsAsync(connection, ct);
        var balances = await LatestBalancesAsync(connection, ct);
        var states = (await RowsAsync(connection, """
            SELECT f.cohort_id, f.member_index, f.name,
                   coalesce((SELECT l.to_state FROM research.lifecycle_events l WHERE l.frozen_id = f.id ORDER BY l.id DESC LIMIT 1), 'Candidate') AS state
            FROM research.frozen_strategies f
            """, ct)).ToDictionary(r => (r["cohort_id"]!.GetValue<long>(), r["member_index"]!.GetValue<int>()), r => r["state"]!.GetValue<string>());
        var fills = (await RowsAsync(connection, "SELECT run_id, sleeve, count(*) AS n FROM trading.fills GROUP BY run_id, sleeve", ct))
            .ToDictionary(r => (r["run_id"]!.GetValue<long>(), r["sleeve"]!.GetValue<int>()), r => r["n"]!.GetValue<long>());
        var risk = await CurrentRiskAsync(connection, ct);
        var maxLoss = risk?.MaxPositionLoss ?? 0.10m;

        var result = new JsonArray();
        foreach (var run in runs)
        {
            for (var k = 0; k < run.Config.Sleeves.Count; k++)
            {
                var sleeve = run.Config.Sleeves[k];
                var row = balances.FirstOrDefault(b => b["run_id"]!.GetValue<long>() == run.Id && b["sleeve"]!.GetValue<int>() == k);
                var equity = row is null ? sleeve.Capital : D(row["equity"]);
                var positions = new JsonArray();
                foreach (var p in row?["positions"]!.AsArray().Select(n => n!.AsObject()) ?? [])
                {
                    var entry = D(p["entryPrice"]);
                    var stop = D(p["stop"]);
                    var last = D(p["lastClose"]);
                    var hard = entry * (1 - maxLoss);
                    positions.Add(new JsonObject
                    {
                        ["symbol"] = p["symbol"]!.DeepClone(),
                        ["entryTime"] = p["entryTime"]!.DeepClone(),
                        ["entryPrice"] = entry,
                        ["quantity"] = D(p["quantity"]),
                        ["lastClose"] = last,
                        ["returnPct"] = entry == 0 ? null : Math.Round((last / entry - 1) * 100, 4),
                        ["stop"] = stop,
                        ["hardStop"] = Math.Round(hard, 8),
                        ["stopSource"] = Math.Abs(stop - hard) <= Math.Abs(entry) * 0.000001m ? "hard-stop-d25" : "strategy",
                        ["stopDistancePct"] = last == 0 ? null : Math.Round((last - stop) / last * 100, 4),
                    });
                }

                result.Add(new JsonObject
                {
                    ["runId"] = run.Id,
                    ["sleeve"] = k,
                    ["name"] = sleeve.Name,
                    ["cohortId"] = sleeve.CohortId,
                    ["memberIndex"] = sleeve.MemberIndex,
                    ["definitionHash"] = sleeve.DefinitionHash,
                    ["state"] = states.GetValueOrDefault((sleeve.CohortId, sleeve.MemberIndex), "unknown"),
                    ["capital"] = sleeve.Capital,
                    ["cash"] = row is null ? sleeve.Capital : D(row["cash"]),
                    ["equity"] = equity,
                    ["pnl"] = Math.Round(equity - sleeve.Capital, 8),
                    ["fills"] = fills.GetValueOrDefault((run.Id, k)),
                    ["positions"] = positions,
                });
            }
        }

        return Json(result);
    }

    public async Task<string> FillsAsync(int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return Json(new JsonArray([.. await RowsAsync(connection, """
            SELECT f.id, f.run_id, p.venue, f.bar_time, f.sleeve, f.strategy, f.cohort_id, f.symbol, f.side, f.fill_time, f.price, f.quantity, f.fee, f.reason
            FROM trading.fills f JOIN trading.paper_runs p ON p.id = f.run_id ORDER BY f.id DESC LIMIT $1
            """, ct, Limit(limit))]));
    }

    /// <summary>Recent L1 signals and L2 risk decisions, with the allocator's adjustments (ADR-0023), and their reason codes (ADR-0018 §1).</summary>
    public async Task<string> DecisionsAsync(int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return Json(new JsonArray([.. await RowsAsync(connection, """
            SELECT e.run_id, r.venue, e.bar_time, e.seq, e.event_time, e.sleeve, r.config -> 'sleeves' -> e.sleeve ->> 'name' AS strategy,
                   e.symbol, e.kind, e.value, e.detail, e.before, e.after
            FROM trading.engine_events e JOIN trading.paper_runs r ON r.id = e.run_id
            WHERE e.kind = ANY($1)
            ORDER BY e.bar_time DESC, e.run_id DESC, e.seq DESC LIMIT $2
            """, ct, SignalKinds.Concat(DecisionKinds).Concat(AllocatorKinds).ToArray(), Limit(limit))]));
    }

    /// <summary>
    /// The chain of one fill (roadmap M9 criterion 1): fill (and, on Testnet, its exchange order) → L2 risk decision
    /// (intent) with the allocator node → L1 signal → sleeve → frozen strategy (definition and structure hash, DSL) →
    /// cohort manifest hash and fields → research-window experiments (result hashes) → dataset snapshot.
    /// </summary>
    public async Task<string?> ProvenanceAsync(long fillId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var fill = (await RowsAsync(connection, """
            SELECT f.id, f.run_id, p.venue, f.bar_time, f.sleeve, f.strategy, f.cohort_id, f.symbol, f.side, f.fill_time, f.price, f.quantity, f.fee, f.reason
            FROM trading.fills f JOIN trading.paper_runs p ON p.id = f.run_id WHERE f.id = $1
            """, ct, fillId)).SingleOrDefault();
        if (fill is null)
        {
            return null;
        }

        var runId = fill["run_id"]!.GetValue<long>();
        var sleeve = fill["sleeve"]!.GetValue<int>();
        var symbol = fill["symbol"]!.GetValue<string>();
        var barTime = fill["bar_time"]!.GetValue<DateTimeOffset>();
        // A Testnet fill is applied in the step of its exchange trade (step_bar); its order names the decision bar.
        var orderId = (await RowsAsync(connection, """
            SELECT o.id FROM trading.exchange_orders o JOIN trading.exchange_fills x ON x.order_id = o.id
            WHERE o.run_id = $1 AND o.sleeve = $2 AND o.symbol = $3 AND x.step_bar = $4 AND lower(o.side) = $5
            ORDER BY o.id DESC LIMIT 1
            """, ct, runId, sleeve, symbol, barTime, fill["side"]!.GetValue<string>())).SingleOrDefault()?["id"]?.GetValue<long>();
        var order = orderId is { } id ? await OrderAsync(connection, id, ct) : null;
        // Decisions are taken at a close and filled at the next open: without an order, the fill's bar is the anchor.
        var anchor = order?["decision_bar"]?.GetValue<DateTimeOffset>() ?? barTime;
        return Json(await ChainAsync(connection, new JsonObject { ["fill"] = fill, ["order"] = order }, runId, sleeve, symbol, anchor, ct));
    }

    /// <summary>
    /// The chain of one Testnet exchange order (M9, m9-m10-protocol §2.1), found by its clientOrderId or by the
    /// exchange's order id: the order with its status history and trades, the fills it produced, then the same chain
    /// as a fill, anchored at the order's decision bar.
    /// </summary>
    public async Task<string?> OrderProvenanceAsync(string id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var orderId = (await RowsAsync(connection, """
            SELECT id FROM (
              SELECT o.id, 0 AS pass FROM trading.exchange_orders o WHERE o.client_order_id = $1
              UNION ALL
              SELECT e.order_id, 1 FROM trading.exchange_order_events e WHERE e.exchange_order_id = $2) q
            ORDER BY pass, id DESC LIMIT 1
            """, ct, id, long.TryParse(id, out var exchangeId) ? exchangeId : -1L)).SingleOrDefault()?["id"]?.GetValue<long>();
        if (orderId is not { } found)
        {
            return null;
        }

        var order = (await OrderAsync(connection, found, ct))!;
        var runId = order["run_id"]!.GetValue<long>();
        var sleeve = order["sleeve"]!.GetValue<int>();
        var symbol = order["symbol"]!.GetValue<string>();
        var fills = await RowsAsync(connection, """
            SELECT f.id, f.run_id, f.bar_time, f.sleeve, f.strategy, f.cohort_id, f.symbol, f.side, f.fill_time, f.price, f.quantity, f.fee, f.reason
            FROM trading.fills f
            WHERE f.run_id = $1 AND f.sleeve = $2 AND f.symbol = $3 AND f.side = lower($4)
              AND f.bar_time IN (SELECT x.step_bar FROM trading.exchange_fills x WHERE x.order_id = $5)
            ORDER BY f.id
            """, ct, runId, sleeve, symbol, order["side"]!.GetValue<string>(), found);
        var result = new JsonObject { ["order"] = order, ["fills"] = new JsonArray([.. fills]) };
        return Json(await ChainAsync(connection, result, runId, sleeve, symbol, order["decision_bar"]!.GetValue<DateTimeOffset>(), ct));
    }

    /// <summary>One exchange order: its fields, the exchange's order id, status history (reason only) and trades.</summary>
    private static async Task<JsonObject?> OrderAsync(NpgsqlConnection connection, long orderId, CancellationToken ct)
    {
        var order = (await RowsAsync(connection, """
            SELECT o.id, o.created_at, o.run_id, o.sleeve, o.symbol, o.purpose, o.side, o.order_type, o.time_in_force, o.quantity, o.price, o.stop_price,
                   o.decision_bar, o.attempt, o.client_order_id, o.replaces, o.sent_at, trading.order_status(o.id) AS status,
                   (SELECT max(e.exchange_order_id) FROM trading.exchange_order_events e WHERE e.order_id = o.id) AS exchange_order_id
            FROM trading.exchange_orders o WHERE o.id = $1
            """, ct, orderId)).SingleOrDefault();
        if (order is null)
        {
            return null;
        }

        order["history"] = new JsonArray([.. await RowsAsync(connection, """
            SELECT recorded_at, status, exchange_order_id, executed_quantity, detail ->> 'reason' AS reason
            FROM trading.exchange_order_events WHERE order_id = $1 ORDER BY id
            """, ct, orderId)]);
        order["trades"] = new JsonArray([.. await RowsAsync(connection, """
            SELECT trade_id, trade_time, step_bar, price, quantity, quote_quantity, commission, commission_asset
            FROM trading.exchange_fills WHERE order_id = $1 ORDER BY id
            """, ct, orderId)]);
        return order;
    }

    /// <summary>
    /// The allocator at the decision bar (ADR-0023): the applied adjustment (T11 weight → applied weight and
    /// multiplier), an ignored item or a veto, and the stored proposal items of this sleeve and symbol (shadow or
    /// active) with their decision trace (models, outcome, degraded, cost; no text). Null when the allocator took no part.
    /// </summary>
    private static async Task<JsonObject?> AllocatorNodeAsync(NpgsqlConnection connection, long runId, int sleeve, string symbol, List<JsonObject> events, DateTimeOffset decisionBar, CancellationToken ct)
    {
        JsonObject? At(Func<JsonObject, bool> match) =>
            events.LastOrDefault(e => match(e) && e["bar_time"]!.GetValue<DateTimeOffset>() == decisionBar);
        var adjusted = At(e => e["kind"]!.GetValue<string>() == "AllocatorAdjusted");
        var ignored = At(e => e["kind"]!.GetValue<string>() == "AllocatorIgnored");
        var veto = At(e => e["kind"]!.GetValue<string>() == "Rejected" && e["detail"]?.GetValue<string>() == "allocator-veto");
        var proposals = await RowsAsync(connection, """
            SELECT bar_time, role, outcome, task_id, protocol_hash, items, jsonb_array_length(safety) AS safety_events
            FROM trading.allocator_proposals WHERE run_id = $1 AND bar_time = $2 ORDER BY id
            """, ct, runId, decisionBar);
        var items = new JsonArray();
        foreach (var proposal in proposals)
        {
            var item = proposal["items"]?.AsArray().Select(n => n?.AsObject())
                .FirstOrDefault(i => i?["sleeve"]?.GetValue<int>() == sleeve && i["symbol"]?.GetValue<string>() == symbol);
            if (item is null)
            {
                continue;
            }

            JsonObject? trace = null;
            if (proposal["task_id"]?.GetValue<string>() is { } task && Guid.TryParse(task, out var taskId))
            {
                trace = (await RowsAsync(connection, """
                    SELECT task_id, task_class, requested_model, actual_model, outcome, degraded, latency_ms, input_tokens, output_tokens, actual_usd
                    FROM ai.decision_traces WHERE task_id = $1
                    """, ct, taskId)).SingleOrDefault();
            }

            items.Add(new JsonObject
            {
                ["barTime"] = proposal["bar_time"]!.DeepClone(),
                ["role"] = proposal["role"]!.DeepClone(),
                ["outcome"] = proposal["outcome"]!.DeepClone(),
                ["taskId"] = proposal["task_id"]?.DeepClone(),
                ["protocolHash"] = proposal["protocol_hash"]?.DeepClone(),
                ["safetyEvents"] = proposal["safety_events"]?.DeepClone(),
                ["item"] = item.DeepClone(),
                ["trace"] = trace,
            });
        }

        if (adjusted is null && ignored is null && veto is null && items.Count == 0)
        {
            return null;
        }

        var t11 = adjusted?["before"]?.GetValue<decimal>() ?? 0m;
        decimal? multiplier = t11 > 0 && adjusted?["after"]?.GetValue<decimal>() is { } appliedWeight ? Math.Round(appliedWeight / t11, 6) : null;
        return new JsonObject
        {
            ["band"] = AllocatorBand.Version,
            ["applied"] = adjusted is null ? null : new JsonObject
            {
                ["t11Weight"] = adjusted["before"]?.DeepClone(),
                ["appliedWeight"] = adjusted["after"]?.DeepClone(),
                ["multiplier"] = multiplier,
            },
            ["ignored"] = ignored?.DeepClone(),
            ["veto"] = veto?.DeepClone(),
            ["proposals"] = items,
        };
    }

    /// <summary>
    /// The rest of the chain from (run, sleeve, symbol) at the anchor bar: the engine events of the anchor and the two
    /// bars before it, the last L2 decision and L1 signal, the allocator node, then sleeve → strategy → manifest →
    /// experiments → dataset snapshots. Written into <paramref name="result"/> after its own fields.
    /// </summary>
    private static async Task<JsonObject> ChainAsync(NpgsqlConnection connection, JsonObject result, long runId, int sleeve, string symbol, DateTimeOffset anchor, CancellationToken ct)
    {
        var run = (await RunsAsync(connection, ct)).Single(r => r.Id == runId);
        var config = run.Config.Sleeves[sleeve];
        var events = await RowsAsync(connection, """
            SELECT bar_time, seq, event_time, kind, value, detail, before, after FROM trading.engine_events
            WHERE run_id = $1 AND sleeve = $2 AND symbol = $3 AND bar_time BETWEEN $4 - interval '8 hours' AND $4
            ORDER BY bar_time, seq
            """, ct, runId, sleeve, symbol, anchor);
        JsonNode? Last(string[] kinds) => events.LastOrDefault(e => kinds.Contains(e["kind"]!.GetValue<string>()))?.DeepClone();
        var decision = Last(DecisionKinds);
        var frozen = (await RowsAsync(connection, """
            SELECT f.id, f.cohort_id, f.member_index, f.name, f.definition_hash, f.manifest_hash, f.manifest, f.recorded_at
            FROM research.frozen_strategies f WHERE f.cohort_id = $1 AND f.member_index = $2
            """, ct, config.CohortId, config.MemberIndex)).SingleOrDefault();
        var lifecycle = frozen is null ? [] : await RowsAsync(connection,
            "SELECT from_state, to_state, actor, recorded_at FROM research.lifecycle_events WHERE frozen_id = $1 ORDER BY id", ct, frozen["id"]!.GetValue<long>());
        var definition = (await RowsAsync(connection, "SELECT definition FROM research.strategy_versions WHERE definition_hash = $1", ct, config.DefinitionHash))
            .SingleOrDefault()?["definition"];
        var experiments = await RowsAsync(connection, """
            SELECT experiment_id, created_at, variant, protocol_version, protocol_commit, dataset_snapshot_hash, engine_git_sha, result_hash
            FROM research.frozen_provenance WHERE cohort_id = $1 AND member_index = $2 ORDER BY experiment_id
            """, ct, config.CohortId, config.MemberIndex);

        JsonObject? manifest = null;
        if (frozen?["manifest"]?.GetValue<string>() is { } text && JsonNode.Parse(text) is JsonObject m)
        {
            manifest = new JsonObject
            {
                ["manifestHash"] = frozen["manifest_hash"]!.DeepClone(),
                ["cohort"] = m["cohort"]?.DeepClone(),
                ["protocol"] = m["protocol"]?["version"]?.DeepClone(),
                ["protocolCommit"] = m["protocol"]?["commit"]?.DeepClone(),
                ["engineSha"] = m["engineSha"]?.DeepClone(),
                ["imageDigest"] = m["imageDigest"]?.DeepClone(),
                ["universe"] = m["universe"]?.DeepClone(),
                ["datasetSnapshot"] = m["datasetSnapshot"]?.DeepClone(),
                ["exemptions"] = m["exemptions"]?.DeepClone(),
                ["underpowered"] = m["power"]?["underpowered"]?.DeepClone(),
            };
        }

        var strategy = definition is null ? null : DslStrategy.FromJson(definition.ToJsonString());
        result["decision"] = decision;
        result["signal"] = Last(SignalKinds);
        result["allocator"] = await AllocatorNodeAsync(connection, runId, sleeve, symbol, events, decision?["bar_time"]?.GetValue<DateTimeOffset>() ?? anchor, ct);
        result["events"] = new JsonArray([.. events]);
        result["sleeve"] = new JsonObject
        {
            ["runId"] = runId, ["venue"] = run.Config.IsTestnet ? PaperRunConfig.TestnetVenue : "paper", ["sleeve"] = sleeve, ["name"] = config.Name,
            ["cohortId"] = config.CohortId, ["memberIndex"] = config.MemberIndex, ["capital"] = config.Capital,
        };
        result["strategy"] = new JsonObject
        {
            ["definitionHash"] = config.DefinitionHash,
            ["structureHash"] = strategy?.StructureHash,
            ["verified"] = strategy?.DefinitionHash == config.DefinitionHash,
            ["frozenAt"] = frozen?["recorded_at"]?.DeepClone(),
            ["definition"] = definition?.DeepClone(),
            ["lifecycle"] = new JsonArray([.. lifecycle]),
        };
        result["cohort"] = manifest;
        result["experiments"] = new JsonArray([.. experiments]);
        result["datasetSnapshots"] = new JsonArray(experiments.Select(e => e["dataset_snapshot_hash"]!.GetValue<string>())
            .Append(manifest?["datasetSnapshot"]?.GetValue<string>()).OfType<string>().Distinct().Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        return result;
    }

    /// <summary>
    /// System health: trading and AI kill switches, recent cycles and late cycles (> 60 s), reconciliation, cohort
    /// status relayed by the evaluator (sealed or revealed only) and recent audit events (type, time, actor; no payload).
    /// HoldoutEnvironmentVerified lives in the holdout database, which ops_api cannot read (architecture §6.2).
    /// The AI kill switch and the audit trail need migration 0008's grants; without them those sections are listed
    /// in "unavailable" and the trading health still renders.
    /// </summary>
    public async Task<string> SystemAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        JsonNode? First(List<JsonObject> rows) => rows.FirstOrDefault()?.DeepClone();
        var unavailable = new JsonArray();
        var aiKillSwitch = First(await OptionalRowsAsync(connection, "aiKillSwitch", unavailable, "SELECT tripped, actor, trigger_kind, reason, at FROM ai.kill_switch_events ORDER BY id DESC LIMIT 1", ct));
        var audit = await OptionalRowsAsync(connection, "audit", unavailable, "SELECT id, recorded_at, actor, event FROM audit.events ORDER BY id DESC LIMIT 30", ct);
        return Json(new JsonObject
        {
            ["tradingKillSwitch"] = First(await RowsAsync(connection, "SELECT engaged, actor, reason, recorded_at FROM trading.kill_switch_events ORDER BY id DESC LIMIT 1", ct)),
            ["aiKillSwitch"] = aiKillSwitch,
            ["cycles"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT scheduled_for, started_at, finished_at, extract(epoch FROM finished_at - started_at) * 1000 AS duration_ms,
                       extract(epoch FROM started_at - scheduled_for) AS lag_seconds, bars_processed, catch_up, stale, outcome, detail -> 'degraded' AS degraded
                FROM trading.cycle_metrics ORDER BY id DESC LIMIT 24
                """, ct)]),
            ["lateCycles7d"] = First(await RowsAsync(connection,
                "SELECT count(*) AS n FROM trading.cycle_metrics WHERE finished_at > now() - interval '7 days' AND finished_at - scheduled_for > interval '60 seconds'", ct))?["n"]?.DeepClone(),
            ["reconciliation"] = First(await RowsAsync(connection, "SELECT run_id, checked_at, ok FROM trading.reconciliation_runs ORDER BY id DESC LIMIT 1", ct)),
            ["reconciliationFailures7d"] = First(await RowsAsync(connection,
                "SELECT count(*) AS n FROM trading.reconciliation_runs WHERE NOT ok AND checked_at > now() - interval '7 days'", ct))?["n"]?.DeepClone(),
            ["cohortStatus"] = new JsonArray([.. await RowsAsync(connection,
                "SELECT DISTINCT ON (cohort_id) cohort_id, status, recorded_at FROM research.cohort_status ORDER BY cohort_id, id DESC", ct)]),
            ["holdoutEnvironmentVerified"] = "not readable from ops (holdout database; see the H-ENV report)",
            ["audit"] = new JsonArray([.. audit]),
            ["unavailable"] = unavailable,
            ["unavailableReason"] = unavailable.Count == 0 ? null : SchemaBehindMessage,
        });
    }

    /// <summary>
    /// Testnet execution (m6-protocol §7): the Testnet runs, outbox counts by purpose and state, Unknown orders, each
    /// position's protective stop on the exchange (or none), recent orders with their reason, the latest Testnet
    /// reconciliation, rules snapshot and balance baseline, and the L3 Testnet deviation categories (ADR-0014, ADR-0015).
    /// </summary>
    public async Task<string> TestnetAsync(bool enabled, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var runs = (await RunsAsync(connection, ct)).Where(r => r.Config.IsTestnet).ToList();
        var ids = runs.Select(r => r.Id).ToArray();
        var balances = (await LatestBalancesAsync(connection, ct)).Where(b => ids.Contains(b["run_id"]!.GetValue<long>())).ToList();
        var stops = await RowsAsync(connection, """
            SELECT o.run_id, o.sleeve, o.symbol, o.order_type, o.stop_price, o.quantity, o.client_order_id, trading.order_status(o.id) AS status, o.created_at
            FROM trading.exchange_orders o WHERE o.purpose = 'Stop' AND o.run_id = ANY($1)
              AND trading.order_status(o.id) IN ('Accepted', 'PartiallyFilled', 'Unknown', 'Submitting', 'CancelRequested')
            ORDER BY o.id
            """, ct, ids);
        var positions = new JsonArray();
        foreach (var row in balances)
        {
            var runId = row["run_id"]!.GetValue<long>();
            var sleeve = row["sleeve"]!.GetValue<int>();
            var run = runs.Single(r => r.Id == runId);
            foreach (var p in row["positions"]!.AsArray().Select(n => n!.AsObject()))
            {
                var symbol = p["symbol"]!.GetValue<string>();
                var stop = stops.LastOrDefault(s => s["run_id"]!.GetValue<long>() == runId && s["sleeve"]!.GetValue<int>() == sleeve && s["symbol"]!.GetValue<string>() == symbol);
                var accepted = stop?["status"]?.GetValue<string>() is "Accepted" or "PartiallyFilled";
                positions.Add(new JsonObject
                {
                    ["runId"] = runId,
                    ["sleeve"] = sleeve,
                    ["strategy"] = run.Config.Sleeves[sleeve].Name,
                    ["cohortId"] = run.Config.Sleeves[sleeve].CohortId,
                    ["symbol"] = symbol,
                    ["quantity"] = p["quantity"]!.DeepClone(),
                    ["engineStop"] = p["stop"]!.DeepClone(),
                    ["exchangeStop"] = stop?["stop_price"]?.DeepClone(),
                    ["stopType"] = stop?["order_type"]?.DeepClone(),
                    ["stopStatus"] = stop?["status"]?.DeepClone() ?? "none",
                    ["protected"] = accepted,
                });
            }
        }

        return Json(new JsonObject
        {
            ["enabled"] = enabled,
            ["runs"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT p.id AS run_id, p.trading_start, p.created_at, c.closed_at, c.reason AS closed_reason,
                       (SELECT jsonb_agg(DISTINCT (x ->> 'cohortId')::bigint) FROM jsonb_array_elements(p.config -> 'sleeves') x) AS cohort_ids
                FROM trading.paper_runs p LEFT JOIN trading.run_closures c ON c.run_id = p.id WHERE p.venue = 'testnet' ORDER BY p.id
                """, ct)]),
            ["outbox"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT o.purpose, trading.order_status(o.id) AS status, count(*) AS n FROM trading.exchange_orders o GROUP BY 1, 2 ORDER BY 1, 2
                """, ct)]),
            ["unknown"] = First(await RowsAsync(connection,
                "SELECT count(*) AS n FROM trading.exchange_orders o WHERE trading.order_status(o.id) IN ('Unknown', 'Submitting')", ct))?["n"]?.DeepClone(),
            ["positions"] = positions,
            ["unprotected"] = positions.Count(p => p!["protected"]!.GetValue<bool>() == false),
            ["recentOrders"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT o.id, o.created_at, o.run_id, o.sleeve, o.symbol, o.purpose, o.side, o.order_type, o.quantity, o.price, o.stop_price, o.attempt,
                       o.client_order_id, (SELECT max(x.exchange_order_id) FROM trading.exchange_order_events x WHERE x.order_id = o.id) AS exchange_order_id,
                       e.status, e.executed_quantity, e.detail ->> 'reason' AS reason, e.recorded_at AS status_at
                FROM trading.exchange_orders o
                JOIN LATERAL (SELECT * FROM trading.exchange_order_events x WHERE x.order_id = o.id ORDER BY x.id DESC LIMIT 1) e ON true
                ORDER BY o.id DESC LIMIT 50
                """, ct)]),
            ["reconciliation"] = First(await RowsAsync(connection, """
                SELECT r.run_id, r.checked_at, r.ok, r.detail ->> 'reason' AS reason FROM trading.reconciliation_runs r
                JOIN trading.paper_runs p ON p.id = r.run_id WHERE p.venue = 'testnet' ORDER BY r.id DESC LIMIT 1
                """, ct)),
            ["rules"] = First(await RowsAsync(connection, "SELECT recorded_at, rules_hash FROM trading.exchange_symbol_rules ORDER BY id DESC LIMIT 1", ct)),
            ["baseline"] = First(await RowsAsync(connection, "SELECT recorded_at, run_id, source FROM trading.testnet_baselines ORDER BY id DESC LIMIT 1", ct)),
            ["deviations"] = First(await RowsAsync(connection, TestnetDeviations.Sql, ct)),
        });

        static JsonNode? First(List<JsonObject> rows) => rows.FirstOrDefault()?.DeepClone();
    }

    /// <summary>D30 progress of every frozen strategy (m6-protocol §6.1, §7): days, bars, critical errors, outage ratio, eligibility.</summary>
    public async Task<string> PromotionAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return JsonSerializer.Serialize(new
        {
            minimumDays = Promotion.MinimumDays,
            minimumBars = Promotion.MinimumBars,
            strategies = await Promotion.ReadAsync(connection, now, null, ct),
        }, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// AI cost from ai.cost_ledger (roadmap M9 criterion 3; m9-m10-protocol §2.3): a call counts its settlement when
    /// settled, otherwise its open reservation, in the UTC month of its reservation (as the ledger's cap check,
    /// LedgerMath). Rows by month × task class × job kind, month subtotals, all-time totals per task class, this
    /// month's jobs against their caps, and this month's use of the monthly cap.
    /// </summary>
    public async Task<string> AiCostsAsync(AiBudget budget, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var month = AiSpend.MonthStart(now);
        var used = await AiSpend.CommittedSinceAsync(connection, month, ct);
        var jobs = await RowsAsync(connection, $"""
            {AiSpend.Committed}
            SELECT job_id, job_kind, sum(committed_usd) AS committed_usd, count(*) AS calls FROM c WHERE at >= $1 GROUP BY 1, 2 ORDER BY 3 DESC, 1
            """, ct, month);
        foreach (var job in jobs)
        {
            var share = budget.JobShares.GetValueOrDefault(job["job_kind"]!.GetValue<string>());
            job["cap_usd"] = share > 0 ? budget.MonthlyCapUsd * share : null;
        }

        return Json(new JsonObject
        {
            ["month"] = month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            ["monthlyCapUsd"] = budget.MonthlyCapUsd,
            ["monthUsedUsd"] = used,
            ["monthShare"] = budget.MonthlyCapUsd > 0 ? Math.Round(used / budget.MonthlyCapUsd, 6) : null,
            ["warningShare"] = AlarmThresholds.BudgetWarningShare,
            ["rows"] = new JsonArray([.. await RowsAsync(connection, $"""
                {AiSpend.Committed}
                SELECT to_char(at AT TIME ZONE 'UTC', 'YYYY-MM') AS month, task_class, job_kind, sum(committed_usd) AS committed_usd,
                       sum(settled_usd) AS settled_usd, sum(open_usd) AS open_usd, count(*) AS calls, sum(input_tokens) AS input_tokens, sum(output_tokens) AS output_tokens
                FROM c GROUP BY 1, 2, 3 ORDER BY 1 DESC, 2, 3
                """, ct)]),
            ["months"] = new JsonArray([.. await RowsAsync(connection, $"""
                {AiSpend.Committed}
                SELECT to_char(at AT TIME ZONE 'UTC', 'YYYY-MM') AS month, sum(committed_usd) AS committed_usd, sum(open_usd) AS open_usd, count(*) AS calls
                FROM c GROUP BY 1 ORDER BY 1 DESC
                """, ct)]),
            ["taskClasses"] = new JsonArray([.. await RowsAsync(connection, $"""
                {AiSpend.Committed}
                SELECT task_class, sum(committed_usd) AS committed_usd, count(*) AS calls, sum(input_tokens) AS input_tokens, sum(output_tokens) AS output_tokens
                FROM c GROUP BY 1 ORDER BY 1
                """, ct)]),
            ["jobs"] = new JsonArray([.. jobs]),
        });
    }

    /// <summary>
    /// Allocator (m8-protocol §4, D32): the mode and its history, decision counts by role and outcome, the latest
    /// shadow and real values per run, and recent decisions with T11 and proposed weights. For the user only (it shows
    /// shadow values); no model text.
    /// </summary>
    public async Task<string> AllocatorAsync(int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var modes = await RowsAsync(connection, """
            SELECT id, recorded_at, from_mode, to_mode, protocol_hash, qualification_id, reason
            FROM trading.allocator_mode_events ORDER BY id DESC LIMIT 20
            """, ct);
        return Json(new JsonObject
        {
            ["mode"] = modes.Count > 0 ? modes[0]["to_mode"]!.GetValue<string>() : nameof(AllocatorMode.Disabled),
            ["band"] = new JsonObject { ["version"] = AllocatorBand.Version, ["low"] = AllocatorBand.Low, ["high"] = AllocatorBand.High },
            ["modes"] = new JsonArray([.. modes]),
            ["counts"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT role, outcome, count(*) AS decisions, count(*) FILTER (WHERE jsonb_array_length(safety) > 0) AS safety_events
                FROM trading.allocator_proposals GROUP BY role, outcome ORDER BY role, outcome
                """, ct)]),
            ["shadow"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT DISTINCT ON (run_id) run_id, bar_time, protocol_hash, shadow_equity, real_equity, shadow_equity - real_equity AS difference
                FROM trading.allocator_shadow_steps ORDER BY run_id, bar_time DESC
                """, ct)]),
            ["decisions"] = new JsonArray([.. await RowsAsync(connection, """
                SELECT run_id, bar_time, role, outcome, task_id, items, safety
                FROM trading.allocator_proposals WHERE outcome NOT IN ('no-candidates', 'disabled') ORDER BY bar_time DESC, id DESC LIMIT $1
                """, ct, Limit(limit))]),
        });
    }

    /// <summary>The computed alarms (M10, ADR-0024; catalogue m9-m10-protocol §3.2) at <paramref name="now"/>.</summary>
    public async Task<string> AlarmsAsync(AiBudget budget, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return AlarmEvaluator.Document(AlarmEvaluator.Evaluate(await AlarmEvaluator.ReadAsync(connection, now, ct), budget, now), now);
    }

    /// <summary>
    /// Recent decision traces (roadmap M9 criterion 2): task class, requested and actual model, degraded flag, the
    /// validators that failed (names and count only), safety violation count, latency, tokens, estimated and actual
    /// cost. No prompt, answer or validator message text.
    /// </summary>
    public async Task<string> AiTracesAsync(int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return Json(new JsonArray([.. await RowsAsync(connection, """
            SELECT task_id, at, job_id, task_class, mode, provider, requested_model, actual_model, prompt_version, calls, input_tokens, output_tokens,
                   latency_ms, estimated_usd, actual_usd, outcome, degraded, reason,
                   jsonb_array_length(validator_results) AS validator_failures,
                   (SELECT coalesce(jsonb_agg(DISTINCT split_part(v, ':', 1)), '[]'::jsonb) FROM jsonb_array_elements_text(validator_results) v) AS failed_validators,
                   jsonb_array_length(safety_violations) AS safety_violations
            FROM ai.decision_traces ORDER BY at DESC LIMIT $1
            """, ct, Limit(limit))]));
    }
}
