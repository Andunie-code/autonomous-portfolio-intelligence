using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution;

/// <summary>The current allocator mode (latest mode event; Disabled without one) and its shadow protocol.</summary>
public sealed record AllocatorState(AllocatorMode Mode, string? ProtocolHash, long EventId)
{
    public static AllocatorState Disabled { get; } = new(AllocatorMode.Disabled, null, 0);
}

/// <summary>One decision of a run and bar: the shadow engine's or the real run's in active mode (m8-protocol §5.4).</summary>
public sealed record ProposalWrite(long RunId, DateTimeOffset BarTime, string Role, string Outcome, Guid? TaskId, string? ProtocolHash,
    IReadOnlyList<AllocationItem> Items, IReadOnlyList<string> Safety)
{
    public const string ShadowRole = "shadow";

    public const string ActiveRole = "active";
}

/// <summary>One shadow-engine step: its overrides (replayed on restart), state hash and the shadow and real values.</summary>
public sealed record ShadowStep(long RunId, DateTimeOffset BarTime, string? ProtocolHash, IReadOnlyList<AllocationOverride> Overrides, string StateHash, decimal ShadowEquity, decimal RealEquity);

/// <summary>Allocator persistence (migration 0011), as trading_app. Everything is append-only.</summary>
public interface IAllocatorStore
{
    Task<AllocatorState> StateAsync(CancellationToken ct);

    /// <summary>Cohorts whose latest revealed outcome is Validated (research.cohort_status, written only by status_relay).</summary>
    Task<IReadOnlySet<long>> ValidatedCohortsAsync(CancellationToken ct);

    Task<IReadOnlyList<ShadowStep>> ShadowStepsAsync(long runId, CancellationToken ct);

    /// <summary>Writes the decision, the shadow step (shadow role) and, for a safety event, the audit row in one transaction.</summary>
    Task AddDecisionAsync(ProposalWrite proposal, ShadowStep? shadow, CancellationToken ct);
}

public sealed class AllocatorStore(string connectionString) : IAllocatorStore
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>The current mode; Disabled while migration 0011 is not applied, so an older database keeps trading with T11.</summary>
    public async Task<AllocatorState> StateAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id, to_mode, protocol_hash FROM trading.allocator_mode_events ORDER BY id DESC LIMIT 1", connection);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new AllocatorState(Enum.Parse<AllocatorMode>(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(0))
                : AllocatorState.Disabled;
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InsufficientPrivilege)
        {
            return AllocatorState.Disabled;
        }
    }

    public async Task<IReadOnlySet<long>> ValidatedCohortsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT cohort_id FROM (
              SELECT DISTINCT ON (cohort_id) cohort_id, summary ->> 'outcome' AS outcome
              FROM research.cohort_status WHERE status = 'revealed' ORDER BY cohort_id, id DESC) s
            WHERE outcome = 'Validated'
            """, connection);
        var result = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task<IReadOnlyList<ShadowStep>> ShadowStepsAsync(long runId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT bar_time, protocol_hash, overrides::text, state_hash, shadow_equity, real_equity
            FROM trading.allocator_shadow_steps WHERE run_id = $1 ORDER BY bar_time
            """, connection);
        command.Parameters.AddWithValue(runId);
        var result = new List<ShadowStep>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ShadowStep(runId, reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                JsonSerializer.Deserialize<List<AllocationOverride>>(reader.GetString(2), PaperJson.Options)!, reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5)));
        }

        return result;
    }

    public async Task AddDecisionAsync(ProposalWrite proposal, ShadowStep? shadow, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO trading.allocator_proposals (run_id, bar_time, role, outcome, task_id, protocol_hash, items, safety)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(proposal.RunId);
            command.Parameters.AddWithValue(proposal.BarTime);
            command.Parameters.AddWithValue(proposal.Role);
            command.Parameters.AddWithValue(proposal.Outcome);
            command.Parameters.AddWithValue((object?)proposal.TaskId ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)proposal.ProtocolHash ?? DBNull.Value);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(proposal.Items, PaperJson.Options));
            command.Parameters.AddWithValue(JsonSerializer.Serialize(proposal.Safety, PaperJson.Options));
            await command.ExecuteNonQueryAsync(ct);
        }

        if (shadow is not null)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO trading.allocator_shadow_steps (run_id, bar_time, protocol_hash, overrides, state_hash, shadow_equity, real_equity)
                VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7)
                """, connection, transaction);
            command.Parameters.AddWithValue(shadow.RunId);
            command.Parameters.AddWithValue(shadow.BarTime);
            command.Parameters.AddWithValue((object?)shadow.ProtocolHash ?? DBNull.Value);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(shadow.Overrides, PaperJson.Options));
            command.Parameters.AddWithValue(shadow.StateHash);
            command.Parameters.AddWithValue(shadow.ShadowEquity);
            command.Parameters.AddWithValue(shadow.RealEquity);
            await command.ExecuteNonQueryAsync(ct);
        }

        if (proposal.Safety.Count > 0)
        {
            var json = JsonSerializer.Serialize(new { proposal.RunId, proposal.BarTime, proposal.Role, proposal.TaskId, events = proposal.Safety }, PaperJson.Options);
            await using var command = new NpgsqlCommand("INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ('trading', 'allocator_safety_event', $1::jsonb, $2)", connection, transaction);
            command.Parameters.AddWithValue(json);
            command.Parameters.AddWithValue(DslCanonical.Sha256(json));
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}

/// <summary>In-memory allocator store for replay and tests.</summary>
public sealed class InMemoryAllocatorStore : IAllocatorStore
{
    private readonly List<ProposalWrite> _proposals = [];
    private readonly List<ShadowStep> _shadow = [];

    public AllocatorState State { get; set; } = AllocatorState.Disabled;

    public HashSet<long> Validated { get; } = [];

    public IReadOnlyList<ProposalWrite> Proposals => _proposals;

    public IReadOnlyList<ShadowStep> Shadow => _shadow;

    public Task<AllocatorState> StateAsync(CancellationToken ct) => Task.FromResult(State);

    public Task<IReadOnlySet<long>> ValidatedCohortsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<long>>(Validated.ToHashSet());

    public Task<IReadOnlyList<ShadowStep>> ShadowStepsAsync(long runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShadowStep>>(_shadow.Where(s => s.RunId == runId).OrderBy(s => s.BarTime).ToList());

    public Task AddDecisionAsync(ProposalWrite proposal, ShadowStep? shadow, CancellationToken ct)
    {
        if (_proposals.Any(p => p.RunId == proposal.RunId && p.BarTime == proposal.BarTime && p.Role == proposal.Role))
        {
            throw new InvalidOperationException("Duplicate allocator decision.");
        }

        _proposals.Add(proposal);
        if (shadow is not null)
        {
            _shadow.Add(shadow);
        }

        return Task.CompletedTask;
    }
}
