using Npgsql;
using PortfolioIntelligence.Researcher;

namespace PortfolioIntelligence.Research.Hypotheses;

/// <summary>research.hypotheses (migration 0006, append-only): one record per hypothesis and protocol version.</summary>
public sealed class HypothesisStore(string connectionString)
{
    /// <summary>Inserts the record, or returns the existing id for the same key and protocol (a re-run is not a new hypothesis).</summary>
    public async Task<long> EnsureAsync(Hypothesis hypothesis, HypothesisProvenance provenance, string protocolVersion, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO research.hypotheses (hypothesis_key, protocol_version, title, origin, author, knowledge_cutoff,
                knowledge_overlaps_diagnostic, approved_by, structure_hash)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (hypothesis_key, protocol_version) DO NOTHING
            RETURNING id
            """, connection);
        insert.Parameters.AddWithValue(hypothesis.Key);
        insert.Parameters.AddWithValue(protocolVersion);
        insert.Parameters.AddWithValue(hypothesis.Title);
        insert.Parameters.AddWithValue(provenance.Origin);
        insert.Parameters.AddWithValue(provenance.Author);
        insert.Parameters.AddWithValue((object?)provenance.KnowledgeCutoff ?? DBNull.Value);
        insert.Parameters.AddWithValue(provenance.KnowledgeOverlapsDiagnostic);
        insert.Parameters.AddWithValue((object?)provenance.ApprovedBy ?? DBNull.Value);
        insert.Parameters.AddWithValue(hypothesis.StructureHash);
        if (await insert.ExecuteScalarAsync(ct) is long id)
        {
            return id;
        }

        await using var select = new NpgsqlCommand(
            "SELECT id, structure_hash FROM research.hypotheses WHERE hypothesis_key = $1 AND protocol_version = $2", connection);
        select.Parameters.AddWithValue(hypothesis.Key);
        select.Parameters.AddWithValue(protocolVersion);
        await using var reader = await select.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetString(1) != hypothesis.StructureHash)
        {
            throw new InvalidOperationException($"Hypothesis {hypothesis.Key} is recorded with another structure under {protocolVersion}.");
        }

        return reader.GetInt64(0);
    }
}
