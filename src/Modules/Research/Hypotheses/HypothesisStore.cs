using Npgsql;
using PortfolioIntelligence.Researcher;
using PortfolioIntelligence.SourceGateway;

namespace PortfolioIntelligence.Research.Hypotheses;

/// <summary>
/// research.hypotheses (migration 0006, append-only): one record per hypothesis and protocol version. A hypothesis written
/// from findings (M7) is recorded only if every finding exists, is verified and comes from a pre-cutoff document
/// (m7-protocol §8.2, U-M7-2); its knowledge cutoff is then at least the latest source version date. The database
/// trigger of migration 0010 enforces the same rule.
/// </summary>
public sealed class HypothesisStore(string connectionString, ISourceStore? sources = null)
{
    /// <summary>Inserts the record, or returns the existing id for the same key and protocol (a re-run is not a new hypothesis).</summary>
    public async Task<long> EnsureAsync(Hypothesis hypothesis, HypothesisProvenance provenance, string protocolVersion, CancellationToken ct)
    {
        var findings = hypothesis.SourceFindingIds ?? [];
        var cutoff = provenance.KnowledgeCutoff;
        if (findings.Count > 0)
        {
            if (sources is null)
            {
                throw new InvalidOperationException($"Hypothesis {hypothesis.Key} names source findings but no source store is configured.");
            }

            var (valid, latest) = await sources.UsableForHypothesisAsync(findings, ct);
            var unusable = findings.Where(f => !valid.Contains(f)).ToList();
            if (unusable.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Hypothesis {hypothesis.Key}: findings {string.Join(", ", unusable)} are missing, unverified or from a post-cutoff document.");
            }

            if (latest is { } at && (cutoff is null || DateOnly.FromDateTime(at.UtcDateTime) > cutoff))
            {
                cutoff = DateOnly.FromDateTime(at.UtcDateTime);
            }
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO research.hypotheses (hypothesis_key, protocol_version, title, origin, author, knowledge_cutoff,
                knowledge_overlaps_diagnostic, approved_by, structure_hash, source_finding_ids)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (hypothesis_key, protocol_version) DO NOTHING
            RETURNING id
            """, connection);
        insert.Parameters.AddWithValue(hypothesis.Key);
        insert.Parameters.AddWithValue(protocolVersion);
        insert.Parameters.AddWithValue(hypothesis.Title);
        insert.Parameters.AddWithValue(provenance.Origin);
        insert.Parameters.AddWithValue(provenance.Author);
        insert.Parameters.AddWithValue((object?)cutoff ?? DBNull.Value);
        insert.Parameters.AddWithValue(provenance.KnowledgeOverlapsDiagnostic);
        insert.Parameters.AddWithValue((object?)provenance.ApprovedBy ?? DBNull.Value);
        insert.Parameters.AddWithValue(hypothesis.StructureHash);
        insert.Parameters.AddWithValue(findings.Count > 0 ? (object)findings.ToArray() : DBNull.Value);
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
