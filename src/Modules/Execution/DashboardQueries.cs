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
            SELECT id, run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason
            FROM trading.fills ORDER BY id DESC LIMIT $1
            """, ct, Limit(limit))]));
    }

    /// <summary>Recent L1 signals and L2 risk decisions with their reason codes (ADR-0018 §1).</summary>
    public async Task<string> DecisionsAsync(int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return Json(new JsonArray([.. await RowsAsync(connection, """
            SELECT e.run_id, e.bar_time, e.seq, e.event_time, e.sleeve, r.config -> 'sleeves' -> e.sleeve ->> 'name' AS strategy,
                   e.symbol, e.kind, e.value, e.detail, e.before, e.after
            FROM trading.engine_events e JOIN trading.paper_runs r ON r.id = e.run_id
            WHERE e.kind = ANY($1)
            ORDER BY e.bar_time DESC, e.run_id DESC, e.seq DESC LIMIT $2
            """, ct, SignalKinds.Concat(DecisionKinds).ToArray(), Limit(limit))]));
    }

    /// <summary>
    /// The chain of one fill (roadmap M9 criterion 1): fill → L2 risk decision (intent) → L1 signal → sleeve →
    /// frozen strategy (definition and structure hash, DSL) → cohort manifest hash and fields → research-window
    /// experiments (result hashes) → dataset snapshot.
    /// </summary>
    public async Task<string?> ProvenanceAsync(long fillId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var fill = (await RowsAsync(connection, """
            SELECT id, run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason
            FROM trading.fills WHERE id = $1
            """, ct, fillId)).SingleOrDefault();
        if (fill is null)
        {
            return null;
        }

        var runId = fill["run_id"]!.GetValue<long>();
        var sleeve = fill["sleeve"]!.GetValue<int>();
        var symbol = fill["symbol"]!.GetValue<string>();
        var barTime = fill["bar_time"]!.GetValue<DateTimeOffset>();
        var run = (await RunsAsync(connection, ct)).Single(r => r.Id == runId);
        var config = run.Config.Sleeves[sleeve];
        // Decisions are taken at a close and filled at the next open: the events of the fill's bar and the two before.
        var events = await RowsAsync(connection, """
            SELECT bar_time, seq, event_time, kind, value, detail, before, after FROM trading.engine_events
            WHERE run_id = $1 AND sleeve = $2 AND symbol = $3 AND bar_time BETWEEN $4 - interval '8 hours' AND $4
            ORDER BY bar_time, seq
            """, ct, runId, sleeve, symbol, barTime);
        JsonNode? Last(string[] kinds) => events.LastOrDefault(e => kinds.Contains(e["kind"]!.GetValue<string>()))?.DeepClone();
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
        return Json(new JsonObject
        {
            ["fill"] = fill,
            ["decision"] = Last(DecisionKinds),
            ["signal"] = Last(SignalKinds),
            ["events"] = new JsonArray([.. events]),
            ["sleeve"] = new JsonObject
            {
                ["runId"] = runId, ["sleeve"] = sleeve, ["name"] = config.Name, ["cohortId"] = config.CohortId, ["memberIndex"] = config.MemberIndex, ["capital"] = config.Capital,
            },
            ["strategy"] = new JsonObject
            {
                ["definitionHash"] = config.DefinitionHash,
                ["structureHash"] = strategy?.StructureHash,
                ["verified"] = strategy?.DefinitionHash == config.DefinitionHash,
                ["frozenAt"] = frozen?["recorded_at"]?.DeepClone(),
                ["definition"] = definition?.DeepClone(),
                ["lifecycle"] = new JsonArray([.. lifecycle]),
            },
            ["cohort"] = manifest,
            ["experiments"] = new JsonArray([.. experiments]),
            ["datasetSnapshots"] = new JsonArray(experiments.Select(e => e["dataset_snapshot_hash"]!.GetValue<string>())
                .Append(manifest?["datasetSnapshot"]?.GetValue<string>()).OfType<string>().Distinct().Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        });
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
                       o.client_order_id, e.status, e.executed_quantity, e.detail ->> 'reason' AS reason, e.recorded_at AS status_at
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

    /// <summary>Monthly AI cost by task class (settlements and open reservations), from ai.cost_ledger.</summary>
    public async Task<string> AiCostsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return Json(new JsonArray([.. await RowsAsync(connection, """
            SELECT to_char(date_trunc('month', at), 'YYYY-MM') AS month, coalesce(task_class, '(none)') AS task_class, job_kind,
                   coalesce(sum(amount_usd) FILTER (WHERE entry_kind = 'Settlement'), 0) AS settled_usd,
                   coalesce(sum(amount_usd) FILTER (WHERE entry_kind = 'Reservation'), 0) AS reserved_usd,
                   count(DISTINCT call_id) AS calls, sum(input_tokens) AS input_tokens, sum(output_tokens) AS output_tokens
            FROM ai.cost_ledger GROUP BY 1, 2, 3 ORDER BY 1 DESC, 2, 3
            """, ct)]));
    }

    /// <summary>Recent decision traces: task class, model, outcome, degraded flag, validator results; no prompt or answer text.</summary>
    public async Task<string> AiTracesAsync(int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return Json(new JsonArray([.. await RowsAsync(connection, """
            SELECT task_id, at, job_id, task_class, mode, provider, actual_model, prompt_version, calls, input_tokens, output_tokens,
                   latency_ms, estimated_usd, actual_usd, outcome, degraded, reason, validator_results, safety_violations
            FROM ai.decision_traces ORDER BY at DESC LIMIT $1
            """, ct, Limit(limit))]));
    }
}
