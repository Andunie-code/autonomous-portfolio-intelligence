using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Logging;
using PortfolioIntelligence.Infrastructure.Persistence;

// Applies the embedded SQL migrations to the database in ConnectionStrings:Ops.
// scripts/dev-up.ps1 supplies it through the ConnectionStrings__Ops environment variable.
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

await using var services = new ServiceCollection()
    .AddLogging(logging => logging.AddPortfolioLogging(configuration))
    .AddSingleton<SqlMigrator>()
    .BuildServiceProvider();

var logger = services.GetRequiredService<ILogger<Program>>();
var connectionString = configuration.GetConnectionString("Ops");
if (string.IsNullOrWhiteSpace(connectionString))
{
    logger.LogError("ConnectionStrings:Ops is not set.");
    return 1;
}

try
{
    var migrations = SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly);
    var result = await services.GetRequiredService<SqlMigrator>()
        .MigrateAsync(connectionString, migrations, CancellationToken.None);
    logger.LogInformation(
        "Migrations complete. Applied {Applied}, already applied {AlreadyApplied}.",
        result.Applied.Count, result.AlreadyApplied.Count);
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Migration failed.");
    return 1;
}
