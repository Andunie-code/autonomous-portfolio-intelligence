using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.HoldoutEvaluator;
using PortfolioIntelligence.Infrastructure.Logging;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.Kernel.Dsl;

// Holdout evaluator host (M5, docs/m5-protocol.md §6, §10 E1). Configuration:
//   ConnectionStrings:HoldoutDb            holdout database as eval_owner (migrations, administration, audit)
//   ConnectionStrings:HoldoutRegistry / HoldoutDiagnostic / HoldoutProspective   per-role logins
//   ConnectionStrings:StatusRelay          ops database as status_relay (insert into research.cohort_status only)
//   Evaluator:SecretsDirectory             Docker secret files: login passwords by user name, API tokens
//   Evaluator:SubmitToken / ReadStatusToken (or the files submit_token / read_status_token in the secrets directory)
//   Evaluator:WindowSource                 archive (default) | synthetic (only for holdout_synth and holdout_henv)
//   Evaluator:ImageDigest                  digest of the running image (set by scripts/m5-deploy.ps1)
//   Evaluator:CacheDirectory               verified archive files (the evaluator's own volume)
// Commands (the user's interactive runs; none is reachable through the API):
//   serve (default) | migrate | register-release <digest> <engineSha> | release-check <digest>
//   henv-selftest | henv-mark-verified <digest> <reportHash> | seed-cohort0 <manifest> <registeredAt> <evidence.json>
//   export-records <file>
var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "serve";
var builder = WebApplication.CreateBuilder(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray());
builder.Logging.AddPortfolioLogging(builder.Configuration);
var configuration = builder.Configuration;
var secrets = configuration["Evaluator:SecretsDirectory"];
var holdout = WithPassword(configuration.GetConnectionString("HoldoutDb") ?? throw new InvalidOperationException("ConnectionStrings:HoldoutDb is not set."));
var ops = configuration.GetConnectionString("StatusRelay") is { } relayConnection ? WithPassword(relayConnection) : null;
var roles = new Dictionary<string, string>();
foreach (var (key, role) in new[] { ("HoldoutRegistry", "eval_registry"), ("HoldoutDiagnostic", "eval_diagnostic"), ("HoldoutProspective", "eval_prospective") })
{
    if (configuration.GetConnectionString(key) is { } value)
    {
        roles[role] = WithPassword(value);
    }
}

var database = new HoldoutDatabase(holdout, ops, roles);
var relay = ops is null ? null : new StatusRelay(database);
var clock = new SystemClock();
var imageDigest = configuration["Evaluator:ImageDigest"];
var operands = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Skip(1).ToArray();

await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(holdout, SqlMigrator.LoadEmbedded(typeof(HoldoutDatabase).Assembly), CancellationToken.None);
switch (command)
{
    case "migrate":
        Console.WriteLine($"Holdout database {database.DatabaseName} migrated.");
        return 0;
    case "register-release" when operands.Length == 2:
        await Owner("INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ($1, $2) ON CONFLICT (image_digest) DO NOTHING", operands[0], operands[1]);
        Console.WriteLine($"EvaluatorRelease {operands[0]} ({operands[1]}) registered.");
        return 0;
    case "release-check" when operands.Length == 1:
    {
        // H20: the running image digest has a release record.
        await using var connection = await database.OpenAsync(null, CancellationToken.None);
        await using var check = new NpgsqlCommand("SELECT count(*) FROM results.evaluator_releases WHERE image_digest = $1", connection);
        check.Parameters.AddWithValue(operands[0]);
        var found = (long)(await check.ExecuteScalarAsync())! == 1;
        Console.WriteLine($"H20 {(found ? "PASS" : "FAIL")} release-record");
        return found ? 0 : 1;
    }
    case "henv-selftest":
        return await SelfTest.RunAsync(database, Console.Out, CancellationToken.None);
    case "henv-mark-verified" when operands.Length == 2:
        if (!Confirm($"Set HoldoutEnvironmentVerified for {operands[0]} with H-ENV report {operands[1]}?"))
        {
            return 3;
        }

        await Owner("INSERT INTO results.environment_flags (verified, actor, image_digest, report_hash) VALUES (true, 'user', $1, $2)", operands[0], operands[1]);
        Console.WriteLine("HoldoutEnvironmentVerified set and audited.");
        return 0;
    case "seed-cohort0" when operands.Length == 3:
    {
        if (imageDigest is null)
        {
            throw new InvalidOperationException("Evaluator:ImageDigest is required to link Kohort 0 to the evaluating release.");
        }

        var registeredAt = DateTimeOffset.Parse(operands[1], CultureInfo.InvariantCulture).ToUniversalTime();
        var evidence = await File.ReadAllTextAsync(operands[2]);
        if (!Confirm($"Seed Kohort 0 with registered_at {registeredAt:O} and link it to {imageDigest}?"))
        {
            return 3;
        }

        var seeded = await CohortSeeder.SeedAsync(database, await File.ReadAllTextAsync(operands[0]), registeredAt, relay, CancellationToken.None, imageDigest, evidence);
        Console.WriteLine($"Kohort 0 registered as cohort {seeded.CohortId}: registered_at {seeded.RegisteredAt:O}, A_0 {seeded.StartsAt:O}, window end {seeded.WindowEnd:O}");
        foreach (var (member, status, attempt) in seeded.Members)
        {
            Console.WriteLine($"  member {member}: {status} (attempt {attempt})");
        }

        return 0;
    }
    case "export-records" when operands.Length == 1:
        await RecordExport.WriteAsync(database, operands[0], CancellationToken.None);
        Console.WriteLine($"Evaluation records exported (no window data): {operands[0]}");
        return 0;
    case "serve":
        break;
    default:
        Console.Error.WriteLine("Unknown command or arguments; see the header of Program.cs.");
        return 1;
}

var tokens = new EvaluatorTokens(Secret("Evaluator:SubmitToken", "submit_token"), Secret("Evaluator:ReadStatusToken", "read_status_token"));
var source = WindowSourceFor(database.DatabaseName);
var prospective = new ProspectiveService(database, source, clock, relay, imageDigest);
var app = builder.Build();
EvaluatorApi.Map(app, new RegistryService(database, relay), new DiagnosticService(database, source, clock), database, tokens);

// The scheduler is the only trigger of prospective evaluation (no "evaluate now" endpoint).
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
    do
    {
        try
        {
            await prospective.RunDueAsync(app.Lifetime.ApplicationStopping);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            app.Logger.LogError(e, "Scheduled prospective evaluation failed");
        }
    }
    while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping));
});

await app.RunAsync();
return 0;

IWindowSource WindowSourceFor(string databaseName)
{
    if (string.Equals(configuration["Evaluator:WindowSource"], "synthetic", StringComparison.Ordinal))
    {
        // Synthetic data never reaches the authoritative registry.
        if (!databaseName.StartsWith("holdout_synth", StringComparison.Ordinal) && databaseName != "holdout_henv")
        {
            throw new InvalidOperationException($"The synthetic window source is not allowed for database {databaseName}.");
        }

        return new SyntheticWindowSource(int.TryParse(configuration["Evaluator:SyntheticSeed"], CultureInfo.InvariantCulture, out var seed) ? seed : 1);
    }

    var http = new HttpClient(new ArchiveWindowGuard(ArchiveWindowGuard.CreateNetworkHandler())) { Timeout = TimeSpan.FromMinutes(2) };
    return new ArchiveWindowSource(http, clock, configuration["Evaluator:CacheDirectory"]);
}

string? Secret(string key, string file) =>
    configuration[key] is { Length: > 0 } value ? value
    : secrets is not null && File.Exists(Path.Combine(secrets, file)) ? File.ReadAllText(Path.Combine(secrets, file)).Trim() : null;

string WithPassword(string connectionString)
{
    var csb = new NpgsqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrEmpty(csb.Password) && secrets is not null && csb.Username is { } user && File.Exists(Path.Combine(secrets, user)))
    {
        csb.Password = (File.ReadAllText(Path.Combine(secrets, user)).Trim());
    }

    return csb.ConnectionString;
}

static bool Confirm(string question)
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Refused: this command needs an interactive confirmation (input is redirected).");
        return false;
    }

    Console.Write($"{question} Type 'yes' to continue: ");
    return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.Ordinal);
}

async Task Owner(string sql, params object[] parameters)
{
    await using var connection = await database.OpenAsync(null, CancellationToken.None);
    await using var statement = new NpgsqlCommand(sql, connection);
    foreach (var p in parameters)
    {
        statement.Parameters.AddWithValue(p);
    }

    await statement.ExecuteNonQueryAsync();
}

/// <summary>Backup of the evaluation records (m5-protocol §7.4): manifests, members, releases, flags, runs and results; no candles.</summary>
internal static class RecordExport
{
    public static readonly string[] Tables =
    [
        "results.evaluator_releases", "results.release_equivalences", "results.environment_flags", "results.cohort_manifests",
        "results.cohort_members", "results.prospective_runs", "results.prospective_results", "results.diagnostic_results", "results.audit_log",
    ];

    public static async Task WriteAsync(HoldoutDatabase database, string path, CancellationToken ct)
    {
        var document = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using var connection = await database.OpenAsync(null, ct);
        foreach (var table in Tables)
        {
            await using var command = new NpgsqlCommand($"SELECT coalesce(json_agg(t ORDER BY 1), '[]'::json)::text FROM {table} t", connection);
            document[table] = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!).RootElement.Clone();
        }

        var json = JsonSerializer.Serialize(document);
        await File.WriteAllTextAsync(path, json, ct);
        await database.AuditAsync("records-exported", null, new { tables = Tables, hash = DslCanonical.Sha256(json) }, ct);
    }
}
