using Npgsql;

namespace PortfolioIntelligence.HoldoutEvaluator.Tests.Support;

/// <summary>Seed cohorts (the owner-only Kohort 0 exception path) with a past window, so evaluation can be exercised.</summary>
public static class Seeding
{
    public static readonly DateTimeOffset PastRegistration = new(2023, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static async Task<long> SeedAsync(HoldoutDatabase db, string manifestJson, DateTimeOffset? registeredAt = null)
    {
        var manifest = ManifestParser.Parse(manifestJson);
        await using var connection = await db.OpenAsync(null, default);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var seed = new NpgsqlCommand("SELECT results.seed_cohort($1, $2, $3::jsonb, $4, $5, $6, $7, $8, $9)", connection, transaction);
        seed.Parameters.AddWithValue(registeredAt ?? PastRegistration);
        seed.Parameters.AddWithValue(manifest.CohortMonths);
        seed.Parameters.AddWithValue(manifest.CanonicalJson);
        seed.Parameters.AddWithValue(manifest.Hash);
        seed.Parameters.AddWithValue(manifest.ProtocolVersion);
        seed.Parameters.AddWithValue(manifest.ProtocolComplete);
        seed.Parameters.AddWithValue(manifest.Underpowered);
        seed.Parameters.AddWithValue(manifest.EngineSha);
        seed.Parameters.AddWithValue(manifest.ImageDigest);
        var id = (long)(await seed.ExecuteScalarAsync())!;
        await RegistryService.InsertMembersAsync(connection, transaction, id, manifest, default);
        await using (var end = new NpgsqlCommand("SELECT results.end_seed()", connection, transaction))
        {
            await end.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return id;
    }

    /// <summary>Seeds a past cohort and runs its prospective evaluation, so it no longer counts as open.</summary>
    public static async Task<long> SeedClosedAsync(HoldoutDatabase db, string manifestJson, FixedClock clock, int seed = 1)
    {
        var id = await SeedAsync(db, manifestJson);
        await new ProspectiveService(db, new SyntheticWindowSource(seed), clock, null).RunDueAsync(default);
        return id;
    }
}
