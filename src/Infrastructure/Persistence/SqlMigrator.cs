using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PortfolioIntelligence.Infrastructure.Persistence;

public sealed record Migration(int Version, string Name, string Sql)
{
    // Computed on access (not stored) so a copy made with `with { Sql = ... }` can never carry a stale checksum.
    public string Checksum => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
}

public sealed record MigrationResult(IReadOnlyList<Migration> Applied, IReadOnlyList<Migration> AlreadyApplied);

/// <summary>
/// Applies plain SQL migrations in version order, each in its own transaction.
/// Plain SQL (rather than an ORM migration tool) because the schema relies on roles, grants,
/// views and CHECK constraints (ADR-0006, ADR-0009) that are clearest when written directly.
/// An applied migration whose file has since changed is an error, never silently re-applied.
/// </summary>
public sealed partial class SqlMigrator
{
    // Arbitrary constant; serializes concurrent migrator runs against the same database.
    private const long AdvisoryLockKey = 7_316_052_119;

    private readonly ILogger<SqlMigrator> _logger;

    public SqlMigrator(ILogger<SqlMigrator> logger)
    {
        _logger = logger;
    }

    public static IReadOnlyList<Migration> LoadEmbedded(Assembly assembly)
    {
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = MigrationResourceName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(new Migration(int.Parse(match.Groups["version"].Value), match.Groups["name"].Value, reader.ReadToEnd()));
        }

        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}.");
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }

    public async Task<MigrationResult> MigrateAsync(string connectionString, IReadOnlyList<Migration> migrations, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS public.schema_migrations (
                    version     integer     PRIMARY KEY,
                    name        text        NOT NULL,
                    checksum    text        NOT NULL,
                    applied_at  timestamptz NOT NULL DEFAULT now()
                )
                """, cancellationToken);

            var applied = await ReadAppliedAsync(connection, cancellationToken);
            var newlyApplied = new List<Migration>();
            var alreadyApplied = new List<Migration>();

            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out var checksum))
                {
                    if (!string.Equals(checksum, migration.Checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration {migration.Version} ({migration.Name}) was modified after it was applied. " +
                            "Add a new migration instead of editing an applied one.");
                    }

                    alreadyApplied.Add(migration);
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, migration.Sql, cancellationToken, transaction);
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO public.schema_migrations (version, name, checksum) VALUES ($1, $2, $3)", connection, transaction))
                {
                    record.Parameters.AddWithValue(migration.Version);
                    record.Parameters.AddWithValue(migration.Name);
                    record.Parameters.AddWithValue(migration.Checksum);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                newlyApplied.Add(migration);
                _logger.LogInformation("Applied migration {Version} {Name}", migration.Version, migration.Name);
            }

            return new MigrationResult(newlyApplied, alreadyApplied);
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, string>();
        await using var command = new NpgsqlCommand("SELECT version, checksum FROM public.schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Resource names look like "<root namespace>.Persistence.Migrations.0001_initial_schemas.sql".
    [GeneratedRegex(@"\.Migrations\.(?<version>\d{4})_(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex MigrationResourceName();
}
