using Microsoft.Extensions.Logging.Abstractions;
using PortfolioIntelligence.HoldoutEvaluator;
using PortfolioIntelligence.Infrastructure.Logging;
using PortfolioIntelligence.Infrastructure.Persistence;

// Holdout evaluator host (M3: synthetic window data only). Configuration:
//   ConnectionStrings:HoldoutDb    holdout database (holdout_synth in M3)
//   ConnectionStrings:StatusRelay  ops database, used only through the status_relay role
//   Evaluator:SubmitToken / Evaluator:ReadStatusToken   API bearer tokens
//   Evaluator:SyntheticSeed        seed of the synthetic window source
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddPortfolioLogging(builder.Configuration);
var holdout = builder.Configuration.GetConnectionString("HoldoutDb") ?? throw new InvalidOperationException("ConnectionStrings:HoldoutDb is not set.");
var ops = builder.Configuration.GetConnectionString("StatusRelay");
var tokens = new EvaluatorTokens(builder.Configuration["Evaluator:SubmitToken"], builder.Configuration["Evaluator:ReadStatusToken"]);
var seed = int.TryParse(builder.Configuration["Evaluator:SyntheticSeed"], out var s) ? s : 1;

if (string.Equals(builder.Configuration["Evaluator:CreateDatabase"], "true", StringComparison.OrdinalIgnoreCase))
{
    // Synthetic mode only (M3): create holdout_synth on first start. The real holdout database is provisioned in M5.
    var target = new Npgsql.NpgsqlConnectionStringBuilder(holdout);
    var server = new Npgsql.NpgsqlConnectionStringBuilder(holdout) { Database = "postgres" };
    await using var connection = new Npgsql.NpgsqlConnection(server.ConnectionString);
    await connection.OpenAsync();
    await using var exists = new Npgsql.NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection);
    exists.Parameters.AddWithValue(target.Database!);
    if (await exists.ExecuteScalarAsync() is null)
    {
        await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{target.Database!.Replace("\"", "")}\"", connection);
        await create.ExecuteNonQueryAsync();
    }
}

await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(holdout, SqlMigrator.LoadEmbedded(typeof(HoldoutDatabase).Assembly), CancellationToken.None);

var database = new HoldoutDatabase(holdout, ops);
var relay = ops is null ? null : new StatusRelay(database);
var source = new SyntheticWindowSource(seed);
var clock = new SystemClock();
var prospective = new ProspectiveService(database, source, clock, relay);

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
