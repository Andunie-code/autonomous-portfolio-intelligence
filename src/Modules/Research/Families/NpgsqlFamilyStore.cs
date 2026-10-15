using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;

namespace PortfolioIntelligence.Research.Families;

/// <summary>PostgreSQL store over migration 0004 (research.trial_families, family_structures, trials, family_artifacts).</summary>
public sealed class NpgsqlFamilyStore(string connectionString) : IFamilyStore
{
    public async Task<(long TrialId, long FamilyId)?> FindTrialAsync(string definitionHash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT id, family_id FROM research.trials WHERE definition_hash = $1 AND research_cutoff = market.research_cutoff()", connection);
        command.Parameters.AddWithValue(definitionHash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    public async Task<long?> FamilyOfStructureAsync(string structureHash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT family_id FROM research.family_structures WHERE structure_hash = $1", connection);
        command.Parameters.AddWithValue(structureHash);
        return await command.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async IAsyncEnumerable<IReadOnlyList<RepresentativeArtifacts>> RepresentativesAsync(string snapshotHash, int batchSize, [EnumeratorCancellation] CancellationToken ct)
    {
        long after = 0;
        while (true)
        {
            await using var connection = await OpenAsync(ct);
            await using var command = new NpgsqlCommand("""
                SELECT a.family_id, a.symbol, a.entry_bitset, a.exposure
                FROM research.family_artifacts a
                WHERE a.snapshot_hash = $1
                  AND a.family_id IN (SELECT id FROM research.trial_families WHERE id > $2 ORDER BY id LIMIT $3)
                ORDER BY a.family_id, a.symbol
                """, connection);
            command.Parameters.AddWithValue(snapshotHash);
            command.Parameters.AddWithValue(after);
            command.Parameters.AddWithValue(batchSize);
            var families = new SortedDictionary<long, Dictionary<string, (byte[], sbyte[])>>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0);
                    if (!families.TryGetValue(id, out var pairs))
                    {
                        families[id] = pairs = new Dictionary<string, (byte[], sbyte[])>(StringComparer.Ordinal);
                    }

                    pairs[reader.GetString(1)] = ((byte[])reader[2], ((byte[])reader[3]).Select(b => unchecked((sbyte)b)).ToArray());
                }
            }

            // Families without trades on this snapshot have no artefact rows; they still advance the cursor.
            await using var next = new NpgsqlCommand("SELECT max(id) FROM (SELECT id FROM research.trial_families WHERE id > $1 ORDER BY id LIMIT $2) f", connection);
            next.Parameters.AddWithValue(after);
            next.Parameters.AddWithValue(batchSize);
            if (await next.ExecuteScalarAsync(ct) is not long last)
            {
                yield break;
            }

            yield return families.Select(f => new RepresentativeArtifacts(f.Key, new FamilyArtifacts(f.Value))).ToList();
            after = last;
        }
    }

    public async Task<long> CreateFamilyAsync(string structureHash, string definitionHash, string snapshotHash, FamilyArtifacts artifacts, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var family = new NpgsqlCommand(
            "INSERT INTO research.trial_families (representative_structure, representative_definition) VALUES ($1, $2) RETURNING id", connection, transaction);
        family.Parameters.AddWithValue(structureHash);
        family.Parameters.AddWithValue(definitionHash);
        var id = (long)(await family.ExecuteScalarAsync(ct))!;
        await using (var structure = new NpgsqlCommand(
            "INSERT INTO research.family_structures (structure_hash, family_id, assigned_by) VALUES ($1, $2, 'first-member')", connection, transaction))
        {
            structure.Parameters.AddWithValue(structureHash);
            structure.Parameters.AddWithValue(id);
            await structure.ExecuteNonQueryAsync(ct);
        }

        foreach (var (symbol, (entries, exposure)) in artifacts.Pairs)
        {
            await using var row = new NpgsqlCommand(
                "INSERT INTO research.family_artifacts (family_id, snapshot_hash, symbol, entry_bitset, exposure) VALUES ($1, $2, $3, $4, $5)", connection, transaction);
            row.Parameters.AddWithValue(id);
            row.Parameters.AddWithValue(snapshotHash);
            row.Parameters.AddWithValue(symbol);
            row.Parameters.AddWithValue(NpgsqlDbType.Bytea, entries);
            row.Parameters.AddWithValue(NpgsqlDbType.Bytea, exposure.Select(v => unchecked((byte)v)).ToArray());
            await row.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task AssignStructureAsync(string structureHash, long familyId, string assignedBy, string evidenceJson, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO research.family_structures (structure_hash, family_id, assigned_by, evidence) VALUES ($1, $2, $3, $4::jsonb)", connection);
        command.Parameters.AddWithValue(structureHash);
        command.Parameters.AddWithValue(familyId);
        command.Parameters.AddWithValue(assignedBy);
        command.Parameters.AddWithValue(evidenceJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> AddTrialAsync(TrialRequest request, string definitionHash, string structureHash, long familyId, double periodSharpe, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO research.trials (definition_hash, structure_hash, family_id, origin, strategy_name, definition, protocol_version, period_sharpe)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8) RETURNING id
            """, connection);
        command.Parameters.AddWithValue(definitionHash);
        command.Parameters.AddWithValue(structureHash);
        command.Parameters.AddWithValue(familyId);
        command.Parameters.AddWithValue(request.Origin switch
        {
            TrialOrigin.Llm => "llm",
            TrialOrigin.Grid => "grid",
            TrialOrigin.Human => "human",
            _ => "dev-assisted",
        });
        command.Parameters.AddWithValue(request.Definition.Name);
        command.Parameters.AddWithValue(Kernel.Dsl.DslCanonical.Canonicalize(request.Definition, Kernel.Dsl.CanonicalMode.Definition));
        command.Parameters.AddWithValue(request.ProtocolVersion);
        command.Parameters.AddWithValue(periodSharpe);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<double>> TrialSharpesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT period_sharpe FROM research.trials WHERE research_cutoff = market.research_cutoff() AND period_sharpe IS NOT NULL ORDER BY id", connection);
        var result = new List<double>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetDouble(0));
        }

        return result;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
