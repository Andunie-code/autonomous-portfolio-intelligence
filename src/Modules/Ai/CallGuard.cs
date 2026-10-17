namespace PortfolioIntelligence.Ai;

public enum CallStatus
{
    Succeeded,
    Stopped,
    BudgetRefused,
    Failed,
}

public sealed record GuardedCall(
    CallStatus Status,
    AiResponse? Response,
    int Attempts,
    UsdAmount Estimated,
    UsdAmount Actual,
    TimeSpan Latency,
    string? Reason)
{
    public bool Succeeded => Status == CallStatus.Succeeded;
}

/// <summary>
/// The single path of every provider call (ADR-0010, architecture §3.4):
/// <list type="bullet">
/// <item>the AI kill switch is checked before every attempt, and polled while a call is in flight;</item>
/// <item>the estimated maximum cost is reserved against the call, job and monthly caps before sending;</item>
/// <item>at most <c>MaxRetries</c> retries, only for network errors, 429 and 5xx, with exponential backoff, seeded
/// jitter and <c>Retry-After</c>; 4xx is never retried; a per-call timeout;</item>
/// <item>automatic triggers: monthly cap reached, spend velocity, repeated cost deviation, circuit breaker.</item>
/// </list>
/// Concurrency is limited by a semaphore (initially 1).
/// </summary>
public sealed class AiCallGuard
{
    private readonly IAiProvider _provider;
    private readonly AiConfiguration _configuration;
    private readonly ICostLedger _ledger;
    private readonly AiKillSwitch _killSwitch;
    private readonly TimeProvider _time;
    private readonly Random _jitter;
    private readonly SemaphoreSlim _concurrency;
    private int _consecutiveFailures;
    private int _consecutiveDeviations;

    public AiCallGuard(IAiProvider provider, AiConfiguration configuration, ICostLedger ledger, AiKillSwitch killSwitch, TimeProvider time, int jitterSeed = 20261018)
    {
        _provider = provider;
        _configuration = configuration;
        _ledger = ledger;
        _killSwitch = killSwitch;
        _time = time;
        _jitter = new Random(jitterSeed);
        _concurrency = new SemaphoreSlim(configuration.Resilience.Concurrency, configuration.Resilience.Concurrency);
    }

    public AiKillSwitch KillSwitch => _killSwitch;

    public IAiProvider Provider => _provider;

    public async Task<GuardedCall> CallAsync(AiRequest request, TaskClass? task, JobContext job, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            return await CallCoreAsync(request, task, job, ct);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<GuardedCall> CallCoreAsync(AiRequest request, TaskClass? task, JobContext job, CancellationToken ct)
    {
        var spend = _configuration.Spend;
        var resilience = _configuration.Resilience;
        var estimated = _provider.EstimateMaxCost(request);
        var actualTotal = UsdAmount.Zero;
        var started = _time.GetTimestamp();
        string? lastFailure = null;
        for (var attempt = 1; attempt <= resilience.MaxRetries + 1; attempt++)
        {
            if (await _killSwitch.IsTrippedAsync(ct))
            {
                return new GuardedCall(CallStatus.Stopped, null, attempt - 1, estimated, actualTotal, _time.GetElapsedTime(started), "AI kill switch is on");
            }

            var callId = Guid.NewGuid();
            var reservation = await _ledger.TryReserveAsync(
                new ReservationRequest(callId, job, task, _provider.Name, request.Model, estimated, _configuration.Prices.Version),
                spend.MonthlyCap, spend.JobCap(job.Kind), spend.CallCap, _time.GetUtcNow(), ct);
            if (reservation != ReservationStatus.Reserved)
            {
                if (reservation == ReservationStatus.MonthlyCapExceeded)
                {
                    await _killSwitch.TripAsync(KillSwitchTrigger.MonthlyCap, AiKillSwitch.SystemActor,
                        $"monthly cap {spend.MonthlyCap} reached (reservation {estimated} refused)", CancellationToken.None);
                }

                return new GuardedCall(CallStatus.BudgetRefused, null, attempt - 1, estimated, actualTotal, _time.GetElapsedTime(started), reservation.ToString());
            }

            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var timeout = new CancellationTokenSource(request.Timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Token, timeout.Token);
            using var registration = _killSwitch.RegisterInFlight(call);
            using var watchdog = new CancellationTokenSource();
            var poll = WatchAsync(call, watchdog.Token);
            try
            {
                var response = await _provider.GenerateAsync(request, linked.Token);
                await watchdog.CancelAsync();
                await poll;
                var actual = _configuration.Prices.Cost(_provider.Name, request.Model, response.Usage);
                actualTotal += actual;
                await _ledger.SettleAsync(callId, actual, response.Usage, _time.GetUtcNow(), CancellationToken.None);
                _consecutiveFailures = 0;
                await AfterSuccessAsync(job, estimated, actual);
                return new GuardedCall(CallStatus.Succeeded, response, attempt, estimated, actualTotal, _time.GetElapsedTime(started), null);
            }
            catch (AiProviderException e)
            {
                await watchdog.CancelAsync();
                await poll;
                // A failed call is settled at zero; the monthly reconciliation catches any provider-side charge.
                await _ledger.SettleAsync(callId, UsdAmount.Zero, new AiUsage(0, 0), _time.GetUtcNow(), CancellationToken.None);
                lastFailure = e.StatusCode is { } status ? $"HTTP {status}: {e.Message}" : e.Message;
                if (!e.Retryable)
                {
                    return new GuardedCall(CallStatus.Failed, null, attempt, estimated, actualTotal, _time.GetElapsedTime(started), lastFailure);
                }

                if (await FailureAsync(lastFailure))
                {
                    return new GuardedCall(CallStatus.Stopped, null, attempt, estimated, actualTotal, _time.GetElapsedTime(started), "circuit breaker opened");
                }

                if (attempt <= resilience.MaxRetries)
                {
                    await Task.Delay(Backoff(attempt, e.RetryAfter), _time, ct);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await watchdog.CancelAsync();
                await poll;
                // Cancelled by the kill switch or timed out: the provider may have billed it, so the reservation stands.
                await _ledger.SettleAsync(callId, estimated, new AiUsage(0, 0), _time.GetUtcNow(), CancellationToken.None);
                actualTotal += estimated;
                if (call.IsCancellationRequested)
                {
                    return new GuardedCall(CallStatus.Stopped, null, attempt, estimated, actualTotal, _time.GetElapsedTime(started), "cancelled by the AI kill switch");
                }

                lastFailure = $"timeout after {request.Timeout.TotalSeconds:0} s";
                if (await FailureAsync(lastFailure))
                {
                    return new GuardedCall(CallStatus.Stopped, null, attempt, estimated, actualTotal, _time.GetElapsedTime(started), "circuit breaker opened");
                }

                if (attempt <= resilience.MaxRetries)
                {
                    await Task.Delay(Backoff(attempt, null), _time, ct);
                }
            }
            catch (OperationCanceledException)
            {
                await watchdog.CancelAsync();
                await poll;
                await _ledger.SettleAsync(callId, estimated, new AiUsage(0, 0), _time.GetUtcNow(), CancellationToken.None);
                throw;
            }
        }

        return new GuardedCall(CallStatus.Failed, null, resilience.MaxRetries + 1, estimated, actualTotal, _time.GetElapsedTime(started), "retries exhausted: " + lastFailure);
    }

    /// <summary>Delay before retry <paramref name="attempt"/>: Retry-After if given (capped), else base·2^(attempt−1) plus jitter in [0, base).</summary>
    public TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var resilience = _configuration.Resilience;
        var cap = TimeSpan.FromSeconds(resilience.MaxRetryAfterSeconds);
        if (retryAfter is { } after)
        {
            return after > cap ? cap : after;
        }

        var seconds = resilience.BackoffBaseSeconds * Math.Pow(2, attempt - 1) + _jitter.NextDouble() * resilience.BackoffBaseSeconds;
        return TimeSpan.FromSeconds(Math.Min(seconds, resilience.MaxRetryAfterSeconds));
    }

    private async Task WatchAsync(CancellationTokenSource call, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(_configuration.Resilience.KillSwitchPollSeconds);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, stop);
                if (await _killSwitch.IsTrippedAsync(stop))
                {
                    await call.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The call finished.
        }
    }

    private async Task<bool> FailureAsync(string reason)
    {
        if (++_consecutiveFailures < _configuration.Resilience.CircuitBreakerFailures)
        {
            return false;
        }

        await _killSwitch.TripAsync(KillSwitchTrigger.CircuitBreaker, AiKillSwitch.SystemActor,
            $"{_consecutiveFailures} consecutive provider failures; last: {reason}", CancellationToken.None);
        return true;
    }

    private async Task AfterSuccessAsync(JobContext job, UsdAmount estimated, UsdAmount actual)
    {
        var spend = _configuration.Spend;
        _consecutiveDeviations = actual > estimated * spend.CostDeviationFactor ? _consecutiveDeviations + 1 : 0;
        if (_consecutiveDeviations >= spend.CostDeviationConsecutive)
        {
            await _killSwitch.TripAsync(KillSwitchTrigger.CostDeviation, AiKillSwitch.SystemActor,
                $"actual cost above {spend.CostDeviationFactor}× the estimate in {_consecutiveDeviations} consecutive calls", CancellationToken.None);
            return;
        }

        var summary = await _ledger.SummaryAsync(_time.GetUtcNow(), job.JobId, spend.VelocityWindow, CancellationToken.None);
        if (summary.Window > spend.VelocityCap)
        {
            await _killSwitch.TripAsync(KillSwitchTrigger.SpendVelocity, AiKillSwitch.SystemActor,
                $"spend in the last {spend.VelocityWindowMinutes} min {summary.Window} above {spend.VelocityCap}", CancellationToken.None);
        }
        else if (summary.MonthToDate >= spend.MonthlyCap)
        {
            await _killSwitch.TripAsync(KillSwitchTrigger.MonthlyCap, AiKillSwitch.SystemActor,
                $"monthly cap {spend.MonthlyCap} reached", CancellationToken.None);
        }
    }
}
