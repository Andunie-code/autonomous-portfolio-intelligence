using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.HoldoutEvaluator;

public sealed class RegistryRejectedException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class EarlyEvaluationException(string message) : Exception(message);

/// <summary>
/// Database access with role separation. M5 (ADR-0013 §2): each role connects with its own login
/// (<paramref name="roleConnectionStrings"/>, eval_registry, eval_diagnostic, eval_prospective); the owner connection
/// (eval_owner) is used for migrations, administration and owner-level audit only. Without a role login (tests,
/// synthetic databases) the role is emulated with SET ROLE on the owner connection. Every statement runs with that
/// role's grants either way (SET ROLE to one's own role is a no-op for a real login).
/// </summary>
public sealed class HoldoutDatabase(string holdoutConnectionString, string? opsConnectionString, IReadOnlyDictionary<string, string>? roleConnectionStrings = null)
{
    internal string ConnectionString => holdoutConnectionString;

    public string DatabaseName => new NpgsqlConnectionStringBuilder(holdoutConnectionString).Database ?? string.Empty;

    public async Task<NpgsqlConnection> OpenAsync(string? role, CancellationToken ct) =>
        await Open(role is not null && roleConnectionStrings?.TryGetValue(role, out var login) == true ? login : holdoutConnectionString, role, ct);

    public async Task<NpgsqlConnection> OpenOpsAsync(string role, CancellationToken ct) =>
        await Open(opsConnectionString ?? throw new InvalidOperationException("No ops connection for the status relay."), role, ct);

    private static async Task<NpgsqlConnection> Open(string connectionString, string? role, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        if (role is not null)
        {
            await using var set = new NpgsqlCommand($"SET ROLE {role}", connection);
            await set.ExecuteNonQueryAsync(ct);
        }

        return connection;
    }

    /// <summary>Owner-level audit entry (rejections and disclosures).</summary>
    public async Task AuditAsync(string @event, long? cohortId, object payload, CancellationToken ct)
    {
        await using var connection = await OpenAsync(null, ct);
        await using var command = new NpgsqlCommand("SELECT results.audit($1, $2, $3::jsonb)", connection);
        command.Parameters.AddWithValue(@event);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)cohortId ?? DBNull.Value);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Writes the disclosure gate in the ops database as status_relay (D24): sealed at registration, revealed after the result.</summary>
public sealed class StatusRelay(HoldoutDatabase database)
{
    public async Task PublishAsync(long cohortId, string status, object? summary, CancellationToken ct)
    {
        await using var connection = await database.OpenOpsAsync("status_relay", ct);
        await using var command = new NpgsqlCommand("INSERT INTO research.cohort_status (cohort_id, status, summary) VALUES ($1, $2, $3::jsonb)", connection);
        command.Parameters.AddWithValue(cohortId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, summary is null ? DBNull.Value : JsonSerializer.Serialize(summary));
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed record Registration(long CohortId, DateTimeOffset RegisteredAt, DateTimeOffset StartsAt, DateTimeOffset WindowEnd, IReadOnlyList<int> Attempts, bool ProtocolComplete);

/// <summary>Cohort registration (eval_registry). Times, attempts and limits come from the database (V13, D21).</summary>
public sealed class RegistryService(HoldoutDatabase database, StatusRelay? relay)
{
    public async Task<Registration> RegisterAsync(string manifestJson, CancellationToken ct)
    {
        var manifest = ManifestParser.Parse(manifestJson);
        try
        {
            await using var connection = await database.OpenAsync("eval_registry", ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO results.cohort_manifests (l_c_months, manifest, manifest_hash, protocol_version, protocol_complete, underpowered, engine_sha, image_digest)
                VALUES ($1, $2::jsonb, $3, $4, $5, $6, $7, $8) RETURNING cohort_id, registered_at, a_c, window_end
                """, connection, transaction);
            insert.Parameters.AddWithValue(manifest.CohortMonths);
            insert.Parameters.AddWithValue(manifest.CanonicalJson);
            insert.Parameters.AddWithValue(manifest.Hash);
            insert.Parameters.AddWithValue(manifest.ProtocolVersion);
            insert.Parameters.AddWithValue(manifest.ProtocolComplete);
            insert.Parameters.AddWithValue(manifest.Underpowered);
            insert.Parameters.AddWithValue(manifest.EngineSha);
            insert.Parameters.AddWithValue(manifest.ImageDigest);
            long id;
            DateTimeOffset registered, start, end;
            await using (var reader = await insert.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                id = reader.GetInt64(0);
                registered = reader.GetFieldValue<DateTimeOffset>(1);
                start = reader.GetFieldValue<DateTimeOffset>(2);
                end = reader.GetFieldValue<DateTimeOffset>(3);
            }

            var attempts = await InsertMembersAsync(connection, transaction, id, manifest, ct);
            await transaction.CommitAsync(ct);
            if (relay is not null)
            {
                await relay.PublishAsync(id, "sealed", null, ct);
            }

            return new Registration(id, registered, start, end, attempts, manifest.ProtocolComplete);
        }
        catch (PostgresException e)
        {
            await database.AuditAsync("manifest-rejected", null, new { manifest.Hash, reason = e.MessageText }, ct);
            throw new RegistryRejectedException(e.MessageText, e);
        }
    }

    internal static async Task<List<int>> InsertMembersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CohortManifest manifest, CancellationToken ct)
    {
        var attempts = new List<int>();
        for (var i = 0; i < manifest.Strategies.Count; i++)
        {
            var strategy = manifest.Strategies[i].Strategy;
            await using var member = new NpgsqlCommand("""
                INSERT INTO results.cohort_members (cohort_id, member_index, strategy_name, definition_hash, structure_hash)
                VALUES ($1, $2, $3, $4, $5) RETURNING attempt
                """, connection, transaction);
            member.Parameters.AddWithValue(id);
            member.Parameters.AddWithValue(i + 1);
            member.Parameters.AddWithValue(strategy.Name);
            member.Parameters.AddWithValue(strategy.DefinitionHash);
            member.Parameters.AddWithValue(strategy.StructureHash);
            attempts.Add((int)(await member.ExecuteScalarAsync(ct))!);
        }

        return attempts;
    }
}

public sealed record DiagnosticRequest(string DefinitionJson, IReadOnlyList<string> Symbols, DateTimeOffset? ModelKnowledgeCutoff);

/// <summary>Diagnostic evaluation on [C, A) (eval_diagnostic). Research sees only the verdict (H10); metrics stay in the holdout database.</summary>
public sealed class DiagnosticService(HoldoutDatabase database, IWindowSource source, IClock clock, double maxDrawdown = 0.25)
{
    public async Task<string> EvaluateAsync(DiagnosticRequest request, CancellationToken ct)
    {
        var strategy = DslStrategy.FromJson(request.DefinitionJson);
        var start = ResearchCutoff.C;
        var end = new DateTimeOffset((clock.UtcNow - TimeSpan.FromDays(3)).UtcDateTime.Date, TimeSpan.Zero);
        if (end <= start.AddDays(30))
        {
            throw new RegistryRejectedException("diagnostic window too short");
        }

        var symbols = request.Symbols.Distinct(StringComparer.Ordinal).ToList();
        var loaded = source.Load(symbols.Append("BTCUSDT").Distinct(StringComparer.Ordinal).ToList(), start - ProspectiveEvaluation.WarmUp, end);
        var seed = BitConverter.ToInt32(Convert.FromHexString(strategy.DefinitionHash[..8]));
        var outcome = DiagnosticEvaluation.Evaluate(strategy, loaded, symbols, start, end, maxDrawdown, seed);
        try
        {
            await using var connection = await database.OpenAsync("eval_diagnostic", ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO results.diagnostic_results (structure_hash, definition_hash, window_start, window_end, verdict, knowledge_overlap, metrics, result_hash)
                VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8)
                """, connection, transaction);
            insert.Parameters.AddWithValue(strategy.StructureHash);
            insert.Parameters.AddWithValue(strategy.DefinitionHash);
            insert.Parameters.AddWithValue(start);
            insert.Parameters.AddWithValue(end);
            insert.Parameters.AddWithValue(outcome.Verdict);
            insert.Parameters.AddWithValue(request.ModelKnowledgeCutoff is { } k && k > start);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(new { outcome.Metric, outcome.Upper, outcome.MaxDrawdown, outcome.Trades }));
            insert.Parameters.AddWithValue(outcome.ResultHash);
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return outcome.Verdict;
        }
        catch (PostgresException e)
        {
            await database.AuditAsync("diagnostic-rejected", null, new { strategy.StructureHash, reason = e.MessageText }, ct);
            throw new RegistryRejectedException(e.MessageText, e);
        }
    }
}

/// <summary>
/// Prospective evaluation (eval_prospective). Only the scheduler starts it, and only after window end + publication
/// buffer by the injected clock (application layer of V4; the database trigger is the second layer). There is no
/// "evaluate now". Re-evaluations need evidence and reuse the same manifest, window, data and digest (V6).
/// </summary>
/// <paramref name="imageDigest"/> is the running evaluator release (M5); null means the cohort's own release (tests).
public sealed class ProspectiveService(HoldoutDatabase database, IWindowSource source, IClock clock, StatusRelay? relay, string? imageDigest = null)
{
    public static readonly TimeSpan PublicationBuffer = TimeSpan.FromDays(3);

    private sealed record Cohort(long Id, DateTimeOffset StartsAt, DateTimeOffset WindowEnd, CohortManifest Manifest, List<int> Attempts);

    public async Task<IReadOnlyList<long>> RunDueAsync(CancellationToken ct)
    {
        var due = new List<long>();
        await using (var connection = await database.OpenAsync("eval_prospective", ct))
        {
            await using var command = new NpgsqlCommand("""
                SELECT c.cohort_id FROM results.cohort_manifests c
                WHERE c.window_end + interval '3 days' <= $1
                  AND NOT EXISTS (SELECT 1 FROM results.prospective_runs r WHERE r.cohort_id = c.cohort_id)
                ORDER BY c.cohort_id
                """, connection);
            command.Parameters.AddWithValue(clock.UtcNow);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                due.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in due)
        {
            await EvaluateAsync(id, "initial", null, null, ct);
        }

        return due;
    }

    /// <summary>Re-evaluation for an evidenced Invalid cause (architecture §6.5); started by the user with a reason.</summary>
    public Task<long> RerunAsync(long cohortId, string reason, string evidenceJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson) || evidenceJson.Trim() is "{}" or "null")
        {
            throw new RegistryRejectedException("re-evaluation requires evidence");
        }

        return EvaluateAsync(cohortId, reason, evidenceJson, rerun: true, ct);
    }

    private async Task<long> EvaluateAsync(long cohortId, string reason, string? evidenceJson, bool? rerun, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync("eval_prospective", ct);
        var cohort = await LoadAsync(connection, cohortId, ct);
        if (clock.UtcNow < cohort.WindowEnd + PublicationBuffer)
        {
            throw new EarlyEvaluationException($"cohort {cohortId}: window closes at {cohort.WindowEnd + PublicationBuffer:O}");
        }

        long? original = null;
        if (rerun == true)
        {
            await using var find = new NpgsqlCommand("SELECT run_id FROM results.prospective_runs WHERE cohort_id = $1 AND reason = 'initial'", connection);
            find.Parameters.AddWithValue(cohortId);
            original = await find.ExecuteScalarAsync(ct) as long? ?? throw new RegistryRejectedException("no initial run to re-evaluate");
        }

        var symbols = cohort.Manifest.Pairs.Select(p => p.Symbol).Append("BTCUSDT").Distinct(StringComparer.Ordinal).ToList();
        var candles = source.Load(symbols, cohort.StartsAt - ProspectiveEvaluation.WarmUp, cohort.WindowEnd);
        var (accepted, _) = WindowQuality.Check(candles.Where(kv => cohort.Manifest.Pairs.Any(p => p.Symbol == kv.Key)).ToDictionary(), cohort.StartsAt, cohort.WindowEnd);
        var metrics = ProspectiveEvaluation.Measure(cohort.Manifest, candles, cohort.StartsAt, cohort.WindowEnd);
        var decisions = ProspectiveEvaluation.Decide(cohort.Manifest, metrics, cohort.Attempts, accepted).ToList();
        var digest = imageDigest ?? cohort.Manifest.ImageDigest;
        bool verified;
        await using (var flag = new NpgsqlCommand("SELECT results.environment_verified($1)", connection))
        {
            flag.Parameters.AddWithValue(digest);
            verified = (bool)(await flag.ExecuteScalarAsync(ct))!;
        }

        for (var i = 0; i < decisions.Count; i++)
        {
            if (decisions[i].Status == "Validated" && !verified)
            {
                decisions[i] = decisions[i] with { Status = "Invalid", Detail = "unverified environment" };
            }
        }

        var dataDigest = WindowQuality.Digest(candles);
        var resultHash = ProspectiveEvaluation.ResultHash(metrics, decisions);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (rerun != true)
        {
            await CopyCandlesAsync(connection, cohortId, candles, ct);
        }

        await using var run = new NpgsqlCommand("""
            INSERT INTO results.prospective_runs (cohort_id, reason, rerun_of, evidence, manifest_hash, image_digest, data_digest, result_hash)
            VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7, $8) RETURNING run_id
            """, connection, transaction);
        run.Parameters.AddWithValue(cohortId);
        run.Parameters.AddWithValue(reason);
        run.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)original ?? DBNull.Value);
        run.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)evidenceJson ?? DBNull.Value);
        run.Parameters.AddWithValue(cohort.Manifest.Hash);
        run.Parameters.AddWithValue(digest);
        run.Parameters.AddWithValue(dataDigest);
        run.Parameters.AddWithValue(resultHash);
        long runId;
        try
        {
            runId = (long)(await run.ExecuteScalarAsync(ct))!;
            foreach (var d in decisions)
            {
                await using var result = new NpgsqlCommand("""
                    INSERT INTO results.prospective_results (run_id, cohort_id, member_index, status, detail, summary)
                    VALUES ($1, $2, $3, $4, $5, $6::jsonb)
                    """, connection, transaction);
                result.Parameters.AddWithValue(runId);
                result.Parameters.AddWithValue(cohortId);
                result.Parameters.AddWithValue(d.MemberIndex);
                result.Parameters.AddWithValue(d.Status);
                result.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)d.Detail ?? DBNull.Value);
                result.Parameters.AddWithValue(JsonSerializer.Serialize(d.RoundedSummary()));
                await result.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = new NpgsqlCommand("SELECT results.audit('disclosure', $1, $2::jsonb)", connection, transaction))
            {
                audit.Parameters.AddWithValue(cohortId);
                audit.Parameters.AddWithValue(JsonSerializer.Serialize(new { runId, resultHash, members = decisions.Select(d => d.RoundedSummary()) }));
                await audit.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (PostgresException e)
        {
            throw new RegistryRejectedException(e.MessageText, e);
        }

        if (relay is not null)
        {
            await relay.PublishAsync(cohortId, "revealed", decisions.Select(d => d.RoundedSummary()).ToList(), ct);
        }

        return runId;
    }

    private static async Task<Cohort> LoadAsync(NpgsqlConnection connection, long cohortId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT a_c, window_end, manifest::text FROM results.cohort_manifests WHERE cohort_id = $1", connection);
        command.Parameters.AddWithValue(cohortId);
        DateTimeOffset start, end;
        string json;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                throw new RegistryRejectedException($"unknown cohort {cohortId}");
            }

            start = reader.GetFieldValue<DateTimeOffset>(0);
            end = reader.GetFieldValue<DateTimeOffset>(1);
            json = reader.GetString(2);
        }

        var attempts = new List<int>();
        await using var members = new NpgsqlCommand("SELECT attempt FROM results.cohort_members WHERE cohort_id = $1 ORDER BY member_index", connection);
        members.Parameters.AddWithValue(cohortId);
        await using (var reader = await members.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                attempts.Add(reader.GetInt32(0));
            }
        }

        return new Cohort(cohortId, start, end, ManifestParser.Parse(json), attempts);
    }

    private static async Task CopyCandlesAsync(NpgsqlConnection connection, long cohortId, IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY prospective.candles (cohort_id, symbol, open_time, open, high, low, close, quote_volume) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (symbol, list) in candles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var c in list)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(cohortId, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(symbol, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(c.OpenTime, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(c.Open, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.High, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.Low, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.Close, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.QuoteVolume, NpgsqlDbType.Numeric, ct);
            }
        }

        await writer.CompleteAsync(ct);
    }
}
