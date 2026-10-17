namespace PortfolioIntelligence.Ai;

/// <summary>Why the switch changed: the four automatic triggers of ADR-0010, a manual trip, or the manual clear.</summary>
public enum KillSwitchTrigger
{
    MonthlyCap,
    SpendVelocity,
    CostDeviation,
    CircuitBreaker,
    Manual,
    ManualClear,
}

/// <summary>One append-only row of ai.kill_switch_events. The latest row is the switch state.</summary>
public sealed record KillSwitchEvent(DateTimeOffset At, bool Tripped, string Actor, KillSwitchTrigger Trigger, string Reason);

public interface IKillSwitchStore
{
    Task<KillSwitchEvent?> LatestAsync(CancellationToken ct);

    Task AppendAsync(KillSwitchEvent killSwitchEvent, CancellationToken ct);
}

/// <summary>Append-only audit sink for AI events (audit.events).</summary>
public interface IAiAudit
{
    Task RecordAsync(string actor, string @event, object payload, CancellationToken ct);
}

/// <summary>
/// LLM emergency stop (ADR-0010, architecture §3.4): a persistent flag checked before every call and every retry.
/// Tripping cancels the calls in flight in this process and the open batch jobs; other processes see the flag on
/// their next check or watchdog poll. It is cleared only manually by a user, with a reason, and audited. It is
/// independent of the trading kill switch: stopping the LLM never stops trading.
/// </summary>
public sealed class AiKillSwitch(IKillSwitchStore store, IAiAudit audit, TimeProvider time)
{
    public const string SystemActor = "system";

    private readonly Lock _gate = new();
    private readonly HashSet<CancellationTokenSource> _inFlight = [];
    private readonly Dictionary<string, IAiProvider> _batches = new(StringComparer.Ordinal);

    public async Task<bool> IsTrippedAsync(CancellationToken ct) => (await store.LatestAsync(ct))?.Tripped ?? false;

    public Task<KillSwitchEvent?> StateAsync(CancellationToken ct) => store.LatestAsync(ct);

    public async Task TripAsync(KillSwitchTrigger trigger, string actor, string reason, CancellationToken ct)
    {
        if (trigger == KillSwitchTrigger.ManualClear)
        {
            throw new ArgumentException("A trip cannot use the clear trigger.", nameof(trigger));
        }

        if (!await IsTrippedAsync(ct))
        {
            await store.AppendAsync(new KillSwitchEvent(time.GetUtcNow(), true, actor, trigger, reason), ct);
            await audit.RecordAsync(actor, "ai-kill-switch-tripped", new { trigger = trigger.ToString(), reason }, ct);
        }

        await CancelOutstandingAsync();
    }

    /// <summary>The only way to clear the switch: a user actor (<c>user:&lt;name&gt;</c>) and a non-empty reason.</summary>
    public async Task ClearAsync(string actor, string reason, CancellationToken ct)
    {
        if (!actor.StartsWith("user:", StringComparison.Ordinal) || actor.Length <= "user:".Length)
        {
            throw new InvalidOperationException("The AI kill switch is cleared only manually by a user (actor 'user:<name>').");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Clearing the AI kill switch needs a reason.");
        }

        if (!await IsTrippedAsync(ct))
        {
            return;
        }

        await store.AppendAsync(new KillSwitchEvent(time.GetUtcNow(), false, actor, KillSwitchTrigger.ManualClear, reason), ct);
        await audit.RecordAsync(actor, "ai-kill-switch-cleared", new { reason }, ct);
    }

    /// <summary>Registers a call in flight; it is cancelled when the switch trips. Dispose when the call ends.</summary>
    public IDisposable RegisterInFlight(CancellationTokenSource call)
    {
        lock (_gate)
        {
            _inFlight.Add(call);
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _inFlight.Remove(call);
            }
        });
    }

    /// <summary>Registers an open batch job; the switch cancels it through its provider. Dispose when it finishes.</summary>
    public IDisposable RegisterBatch(IAiProvider provider, string batchId)
    {
        lock (_gate)
        {
            _batches[batchId] = provider;
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _batches.Remove(batchId);
            }
        });
    }

    private async Task CancelOutstandingAsync()
    {
        List<CancellationTokenSource> calls;
        List<KeyValuePair<string, IAiProvider>> batches;
        lock (_gate)
        {
            calls = _inFlight.ToList();
            batches = _batches.ToList();
            _batches.Clear();
        }

        foreach (var call in calls)
        {
            try
            {
                await call.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The call ended between the snapshot and the cancellation.
            }
        }

        foreach (var (batchId, provider) in batches)
        {
            await provider.CancelBatchAsync(batchId, CancellationToken.None);
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dispose();
            }
        }
    }
}

public sealed class InMemoryKillSwitchStore : IKillSwitchStore
{
    private readonly List<KillSwitchEvent> _events = [];

    public IReadOnlyList<KillSwitchEvent> Events => _events.ToList();

    public Task<KillSwitchEvent?> LatestAsync(CancellationToken ct) => Task.FromResult(_events.LastOrDefault());

    public Task AppendAsync(KillSwitchEvent killSwitchEvent, CancellationToken ct)
    {
        _events.Add(killSwitchEvent);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAiAudit : IAiAudit
{
    private readonly List<(string Actor, string Event, object Payload)> _events = [];

    public IReadOnlyList<(string Actor, string Event, object Payload)> Events => _events.ToList();

    public Task RecordAsync(string actor, string @event, object payload, CancellationToken ct)
    {
        _events.Add((actor, @event, payload));
        return Task.CompletedTask;
    }
}
