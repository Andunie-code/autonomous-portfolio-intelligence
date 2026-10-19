using Npgsql;

namespace PortfolioIntelligence.HoldoutEvaluator;

public sealed record SeedResult(long CohortId, DateTimeOffset RegisteredAt, DateTimeOffset StartsAt, DateTimeOffset WindowEnd, IReadOnlyList<(int Member, string Status, int Attempt)> Members);

/// <summary>
/// Kohort 0 seed exception (architecture §6.3, V13): the owner registers the cohort with the local commit time as
/// registered_at through results.seed_cohort; A_c and the window end are still derived by the database, limits and
/// family attempts still apply, and the event is audited. Used once in M3 (synthetic database) and once in M5.
/// M5 (m5-protocol §6.3, A-2): the manifest's own release (local-build) is registered as the seed release, and
/// <paramref name="evaluatingDigest"/>, the real evaluator image, is linked to it by an audited equivalence record
/// carrying the regression evidence (the M2 result hashes reproduced by that engine). The manifest is unchanged;
/// a second seed of the same manifest is rejected by the unique manifest hash.
/// </summary>
public static class CohortSeeder
{
    public static async Task<SeedResult> SeedAsync(HoldoutDatabase database, string manifestJson, DateTimeOffset registeredAt, StatusRelay? relay, CancellationToken ct,
        string? evaluatingDigest = null, string? equivalenceEvidenceJson = null)
    {
        var manifest = ManifestParser.Parse(manifestJson);
        await using var connection = await database.OpenAsync(null, ct);
        await using (var release = new NpgsqlCommand(
            "INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ($1, $2) ON CONFLICT (image_digest) DO NOTHING", connection))
        {
            release.Parameters.AddWithValue(manifest.ImageDigest);
            release.Parameters.AddWithValue(manifest.EngineSha);
            await release.ExecuteNonQueryAsync(ct);
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var seed = new NpgsqlCommand("SELECT results.seed_cohort($1, $2, $3::jsonb, $4, $5, $6, $7, $8, $9)", connection, transaction);
        seed.Parameters.AddWithValue(registeredAt);
        seed.Parameters.AddWithValue(manifest.CohortMonths);
        seed.Parameters.AddWithValue(manifest.CanonicalJson);
        seed.Parameters.AddWithValue(manifest.Hash);
        seed.Parameters.AddWithValue(manifest.ProtocolVersion);
        seed.Parameters.AddWithValue(manifest.ProtocolComplete);
        seed.Parameters.AddWithValue(manifest.Underpowered);
        seed.Parameters.AddWithValue(manifest.EngineSha);
        seed.Parameters.AddWithValue(manifest.ImageDigest);
        var id = (long)(await seed.ExecuteScalarAsync(ct))!;
        await RegistryService.InsertMembersAsync(connection, transaction, id, manifest, ct);
        await using (var end = new NpgsqlCommand("SELECT results.end_seed()", connection, transaction))
        {
            await end.ExecuteNonQueryAsync(ct);
        }

        if (evaluatingDigest is not null && evaluatingDigest != manifest.ImageDigest)
        {
            if (string.IsNullOrWhiteSpace(equivalenceEvidenceJson))
            {
                throw new RegistryRejectedException("an equivalence record needs regression evidence");
            }

            await using var equivalence = new NpgsqlCommand(
                "INSERT INTO results.release_equivalences (from_digest, to_digest, actor, evidence) VALUES ($1, $2, 'user', $3::jsonb)", connection, transaction);
            equivalence.Parameters.AddWithValue(manifest.ImageDigest);
            equivalence.Parameters.AddWithValue(evaluatingDigest);
            equivalence.Parameters.AddWithValue(equivalenceEvidenceJson);
            await equivalence.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        if (relay is not null)
        {
            await relay.PublishAsync(id, "sealed", null, ct);
        }

        await using var read = new NpgsqlCommand("""
            SELECT c.registered_at, c.a_c, c.window_end, v.member_index, v.status, m.attempt
            FROM results.cohort_manifests c
            JOIN results.validation_status v ON v.cohort_id = c.cohort_id
            JOIN results.cohort_members m ON m.cohort_id = c.cohort_id AND m.member_index = v.member_index
            WHERE c.cohort_id = $1 ORDER BY v.member_index
            """, connection);
        read.Parameters.AddWithValue(id);
        var members = new List<(int, string, int)>();
        DateTimeOffset registered = default, start = default, windowEnd = default;
        await using var reader = await read.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            (registered, start, windowEnd) = (reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
            members.Add((reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5)));
        }

        return new SeedResult(id, registered, start, windowEnd, members);
    }
}
