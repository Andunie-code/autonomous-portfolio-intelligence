using System.Net.Sockets;
using Npgsql;

// H-ENV probe (docs/m5-protocol.md §6.2, E8). Usage: probe <research|trading|evaluator|host> [--host <db host>] [--port <n>]
//   [--secrets <dir>] [--ops-db ops] [--holdout-dbs holdout,holdout_henv] [--cohort <id>] [--superuser <name>].
// Each line: "<test id> PASS|FAIL <check> <reason code>". It never prints rows, values or passwords; a negative
// check (connection or statement refused) always comes with a positive control from the same position.
var position = args.FirstOrDefault() ?? "";
string Option(string name, string fallback)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var host = Option("host", "postgres");
var port = int.Parse(Option("port", "5432"), System.Globalization.CultureInfo.InvariantCulture);
var secrets = Option("secrets", "/run/secrets");
var opsDb = Option("ops-db", "ops");
var holdoutDbs = Option("holdout-dbs", "holdout,holdout_henv").Split(',', StringSplitOptions.RemoveEmptyEntries);
var superuser = Option("superuser", "pi_dev");
var failures = 0;

string Connection(string database, string user) => new NpgsqlConnectionStringBuilder
{
    Host = host, Port = port, Database = database, Username = user, Pooling = false, Timeout = 5, CommandTimeout = 5,
    Password = (File.Exists(Path.Combine(secrets, user)) ? File.ReadAllText(Path.Combine(secrets, user)).Trim() : "none"),
}.ConnectionString;

void Report(string id, string check, bool pass, string reason)
{
    failures += pass ? 0 : 1;
    Console.WriteLine($"{id} {(pass ? "PASS" : "FAIL")} {check} {reason}");
}

async Task<string?> TryConnect(string database, string user, string? sql = null)
{
    try
    {
        await using var connection = new NpgsqlConnection(Connection(database, user));
        await connection.OpenAsync();
        if (sql is not null)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        return null;
    }
    catch (PostgresException e)
    {
        return e.SqlState;
    }
    catch (Exception e) when (e is NpgsqlException or SocketException or TimeoutException)
    {
        return "network";
    }
}

async Task Refused(string id, string check, string database, string user, string? sql = null, params string[] expected)
{
    var code = await TryConnect(database, user, sql);
    Report(id, check, code is not null && (expected.Length == 0 || expected.Contains(code)), code ?? "allowed");
}

async Task Allowed(string id, string check, string database, string user, string? sql = null)
{
    var code = await TryConnect(database, user, sql);
    Report(id, check, code is null, code ?? "ok");
}

const string Privilege = "42501";
const string Rejected = "28000"; // pg_hba reject / no entry
switch (position)
{
    case "research":
        // Research.Host position (ops + eval networks).
        await Allowed("H6", "control-research_app-reaches-ops", opsDb, "research_app", "SELECT 1");
        foreach (var db in holdoutDbs)
        {
            foreach (var user in new[] { "research_app", "eval_registry", "eval_prospective" })
            {
                await Refused("H6", $"{user}-to-{db}-refused", db, user);
            }
        }

        await Refused("H21", "status_relay-outside-the-holdout-network-refused", opsDb, "status_relay", null, Rejected, "28P01", "network");
        foreach (var table in new[] { "market.live_candles", "trading.fills", "trading.steps", "trading.correlation_snapshots" })
        {
            await Refused("H6b", $"research_app-reads-{table}-refused", opsDb, "research_app", $"SELECT 1 FROM {table} LIMIT 1", Privilege);
        }

        await Allowed("H6b", "unrevealed-cohorts-absent-from-paper-summary", opsDb, "research_app",
            "DO $$ BEGIN IF EXISTS (SELECT 1 FROM research.revealed_paper_summary s WHERE s.cohort_id NOT IN (SELECT cohort_id FROM research.revealed_cohorts)) THEN RAISE EXCEPTION 'unrevealed'; END IF; END $$");
        await Refused("H25", "research_app-writes-cohort_status-refused", opsDb, "research_app",
            "INSERT INTO research.cohort_status (cohort_id, status) VALUES (-1, 'sealed')", Privilege);
        break;

    case "trading":
        // Trading.Host position (ops network only): the evaluator is unreachable (H14).
        Report("H14", "control-postgres-reachable", await Tcp("postgres", 5432), "tcp");
        Report("H14", "evaluator-unreachable", !await Tcp("holdout-evaluator", 8080), "network");
        break;

    case "evaluator":
        // HoldoutEvaluator position (its static IP on the holdout network).
        foreach (var db in holdoutDbs.Take(1))
        {
            await Allowed("H21", $"control-eval_registry-reaches-{db}", db, "eval_registry", "SELECT 1");
        }

        foreach (var user in new[] { "eval_registry", "eval_diagnostic", "eval_prospective", "eval_owner" })
        {
            await Refused("H21", $"{user}-to-ops-refused", opsDb, user);
        }

        await Allowed("H21", "control-status_relay-connects-to-ops", opsDb, "status_relay");
        foreach (var sql in new[]
                 {
                     "SELECT 1 FROM research.cohort_status LIMIT 1",
                     "SELECT 1 FROM market.candles LIMIT 1",
                     "SELECT 1 FROM trading.fills LIMIT 1",
                     "INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ('henv', 'henv', '{}', repeat('0', 64))",
                     "INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES (true, 'henv', 'henv')",
                 })
        {
            await Refused("H21", $"status_relay-{sql.Split(' ')[0].ToLowerInvariant()}-{sql.Split(' ')[sql.StartsWith("SELECT", StringComparison.Ordinal) ? 3 : 2]}-refused", opsDb, "status_relay", sql, Privilege);
        }

        if (Option("cohort", "") is { Length: > 0 } cohort)
        {
            // H22 on the real holdout database: an early prospective import is refused by the trigger (nothing is written).
            await Refused("H22", "early-prospective-import-refused", holdoutDbs[0], "eval_prospective",
                $"INSERT INTO prospective.candles (cohort_id, symbol, open_time, open, high, low, close, quote_volume) VALUES ({long.Parse(cohort, System.Globalization.CultureInfo.InvariantCulture)}, 'BTCUSDT', now(), 1, 1, 1, 1, 1)", "P0001");
        }

        await Acl("H26", holdoutDbs[0], "eval_registry");
        break;

    case "host":
        // The host through the loopback-published port (H5, H23, H26).
        await Allowed("H5", "control-research_app-reaches-ops", opsDb, "research_app", "SELECT 1");
        foreach (var db in holdoutDbs)
        {
            foreach (var user in new[] { "research_app", "eval_registry", "eval_owner", superuser })
            {
                await Refused("H5", $"{user}-to-{db}-refused", db, user, null, Rejected, "28P01", "network");
            }
        }

        await Refused("H5", "superuser-to-ops-refused", opsDb, superuser, null, Rejected, "28P01", "network");
        Report("H5", "evaluator-port-not-published", !await Tcp("127.0.0.1", 55480), "tcp");
        foreach (var user in new[] { "research_app", "market_import" })
        {
            await Refused("H23", $"{user}-writes-risk-config-refused", opsDb, user,
                "INSERT INTO trading.risk_config_versions (actor, reason, config, config_hash) VALUES ('henv', 'henv', '{\"experimentalPairsMaxExposure\": 1}', repeat('0', 64))", Privilege);
        }

        await Refused("H23", "trading_app-from-the-host-refused", opsDb, "trading_app", null, Rejected, "28P01", "network");
        await Acl("H26", opsDb, "research_app");
        break;

    default:
        Console.Error.WriteLine("usage: probe <research|trading|evaluator|host> [options]");
        return 2;
}

return failures == 0 ? 0 : 1;

static async Task<bool> Tcp(string target, int targetPort)
{
    try
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(target, targetPort, timeout.Token);
        return true;
    }
    catch (Exception e) when (e is SocketException or OperationCanceledException)
    {
        return false;
    }
}

// H26: CONNECT is not granted to PUBLIC on any project database (pg_database is a shared catalog).
async Task Acl(string id, string database, string user)
{
    try
    {
        await using var connection = new NpgsqlConnection(Connection(database, user));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT datname FROM pg_database
            WHERE datname IN ('ops', 'holdout', 'holdout_henv', 'holdout_synth')
              AND (datacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(datacl) a WHERE a.grantee = 0 AND a.privilege_type = 'CONNECT'))
            """, connection);
        var open = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            open.Add(reader.GetString(0));
        }

        Report(id, "no-public-connect", open.Count == 0, open.Count == 0 ? "ok" : "public:" + string.Join("+", open));
    }
    catch (Exception e) when (e is NpgsqlException or SocketException)
    {
        Report(id, "no-public-connect", false, "unreachable");
    }
}
