using PortfolioIntelligence.Ai.Tests.Support;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>testing-strategy §6.3: the LLM emergency stop, its automatic triggers and the retry policy (fake provider, fake clock).</summary>
public class KillSwitchAndRetryTests
{
    private static readonly AiRequest Request = new(TestConfig.Model, "system", [new AiMessage(AiRole.User, "prompt")], null, null, 2000, TimeSpan.FromSeconds(120));

    [Fact]
    public async Task No_call_is_made_while_the_switch_is_on()
    {
        var rig = new RouterRig();
        await rig.KillSwitch.TripAsync(KillSwitchTrigger.Manual, "user:test", "test", default);

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(TaskOutcome.Stopped, result.Outcome);
        Assert.Empty(rig.Provider.Requests);
        Assert.Empty(rig.Ledger.Entries);
    }

    [Fact]
    public async Task Tripping_cancels_the_call_in_flight()
    {
        var rig = new RouterRig();
        rig.Provider.Blocks();

        var call = rig.Guard.CallAsync(Request, TaskClass.Summary, TestConfig.Research, default);
        await rig.Provider.InFlight.Task;
        await rig.KillSwitch.TripAsync(KillSwitchTrigger.Manual, "user:test", "stop now", default);
        var result = await call;

        Assert.Equal(CallStatus.Stopped, result.Status);
        Assert.Contains("kill switch", result.Reason);
        Assert.Single(rig.Provider.Requests); // no retry after the cancellation
        // A cancelled call keeps its reservation as cost: the provider may have billed it.
        Assert.Equal(result.Estimated, rig.Ledger.Entries.Single(e => e.Kind == LedgerEntryKind.Settlement).Amount);
    }

    [Fact]
    public async Task Watchdog_cancels_the_call_when_another_process_trips_the_switch()
    {
        var rig = new RouterRig();
        rig.Provider.Blocks();

        var call = rig.Guard.CallAsync(Request, TaskClass.Summary, TestConfig.Research, default);
        await rig.Provider.InFlight.Task;
        // Another process: the flag is written to the shared store without this process's in-flight registry.
        await rig.KillSwitchStore.AppendAsync(new KillSwitchEvent(rig.Time.GetUtcNow(), true, "user:other", KillSwitchTrigger.Manual, "elsewhere"), default);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var result = await call;

        Assert.Equal(CallStatus.Stopped, result.Status);
    }

    [Fact]
    public async Task Tripping_cancels_open_batch_jobs()
    {
        var rig = new RouterRig();
        var batch = await rig.Provider.SubmitBatchAsync([Request], default);
        using var registration = rig.KillSwitch.RegisterBatch(rig.Provider, batch);

        await rig.KillSwitch.TripAsync(KillSwitchTrigger.Manual, "user:test", "stop", default);

        Assert.Equal([batch], rig.Provider.CancelledBatches);
    }

    [Fact]
    public async Task Monthly_cap_reached_by_actual_spend_trips_the_switch()
    {
        var rig = new RouterRig(TestConfig.Configuration(callCap: 5m));
        await rig.SpendAsync(9.995m);
        rig.Provider.EstimatedInputTokens = 0;
        rig.Provider.Returns("x", input: 0, output: 2000); // 0.005 USD: reaches the 10 USD cap exactly

        await rig.Guard.CallAsync(Request, TaskClass.Summary, TestConfig.Research, default);

        Assert.Equal(KillSwitchTrigger.MonthlyCap, rig.KillSwitchStore.Events.Single().Trigger);
    }

    [Fact]
    public async Task Spend_velocity_above_20_percent_of_the_cap_in_60_minutes_trips_the_switch()
    {
        var rig = new RouterRig(TestConfig.Configuration(callCap: 5m));
        await rig.SpendAsync(1.999m, ago: TimeSpan.FromMinutes(30)); // 20 % of 10 USD = 2 USD
        rig.Provider.Returns("x", input: 1000, output: 1000);

        await rig.Guard.CallAsync(Request, TaskClass.Summary, new JobContext("job-velocity", JobKind.Qualification), default);

        Assert.Equal(KillSwitchTrigger.SpendVelocity, rig.KillSwitchStore.Events.Single().Trigger);
    }

    [Fact]
    public async Task Spend_older_than_the_window_does_not_count_for_velocity()
    {
        var rig = new RouterRig(TestConfig.Configuration(callCap: 5m));
        await rig.SpendAsync(1.999m, ago: TimeSpan.FromMinutes(61));
        rig.Provider.Returns("x", input: 1000, output: 1000);

        await rig.Guard.CallAsync(Request, TaskClass.Summary, new JobContext("job-velocity", JobKind.Qualification), default);

        Assert.Empty(rig.KillSwitchStore.Events);
    }

    [Fact]
    public async Task Three_consecutive_calls_above_twice_the_estimate_trip_the_switch()
    {
        var rig = new RouterRig();
        rig.Provider.EstimatedInputTokens = 0;
        var small = Request with { MaxOutputTokens = 10 }; // estimate 0.000025 USD
        for (var i = 0; i < 3; i++)
        {
            rig.Provider.Returns("x", input: 0, output: 21); // actual 2.1 × the estimate
        }

        await rig.Guard.CallAsync(small, null, TestConfig.Research, default);
        await rig.Guard.CallAsync(small, null, TestConfig.Research, default);
        Assert.Empty(rig.KillSwitchStore.Events);
        await rig.Guard.CallAsync(small, null, TestConfig.Research, default);

        Assert.Equal(KillSwitchTrigger.CostDeviation, rig.KillSwitchStore.Events.Single().Trigger);
    }

    [Fact]
    public async Task Circuit_breaker_trips_the_switch_after_five_consecutive_failures()
    {
        var rig = new RouterRig();
        for (var i = 0; i < 6; i++)
        {
            rig.Provider.Throws(503, retryAfter: TimeSpan.Zero);
        }

        var first = await rig.Guard.CallAsync(Request, null, TestConfig.Research, default);
        Assert.Equal(CallStatus.Failed, first.Status);
        var second = await rig.Guard.CallAsync(Request, null, TestConfig.Research, default);

        Assert.Equal(CallStatus.Stopped, second.Status);
        Assert.Equal(5, rig.Provider.Requests.Count);
        Assert.Equal(KillSwitchTrigger.CircuitBreaker, rig.KillSwitchStore.Events.Single().Trigger);
    }

    [Fact]
    public async Task Switch_is_cleared_only_manually_by_a_user_with_a_reason_and_audited()
    {
        var rig = new RouterRig();
        await rig.KillSwitch.TripAsync(KillSwitchTrigger.CircuitBreaker, AiKillSwitch.SystemActor, "test", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.KillSwitch.ClearAsync(AiKillSwitch.SystemActor, "auto", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.KillSwitch.ClearAsync("user:", "no name", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.KillSwitch.ClearAsync("user:ilhan", " ", default));
        Assert.True(await rig.KillSwitch.IsTrippedAsync(default));

        // Time passing or spend dropping out of the window never clears it.
        rig.Time.Advance(TimeSpan.FromDays(40));
        Assert.True(await rig.KillSwitch.IsTrippedAsync(default));

        await rig.KillSwitch.ClearAsync("user:ilhan", "investigated: provider outage", default);

        Assert.False(await rig.KillSwitch.IsTrippedAsync(default));
        Assert.Equal(["ai-kill-switch-tripped", "ai-kill-switch-cleared"], rig.Audit.Events.Select(e => e.Event));
        Assert.Equal(KillSwitchTrigger.ManualClear, rig.KillSwitchStore.Events[^1].Trigger);
    }

    [Fact]
    public async Task Client_errors_are_never_retried()
    {
        var rig = new RouterRig();
        rig.Provider.Throws(400);

        var result = await rig.Guard.CallAsync(Request, null, TestConfig.Research, default);

        Assert.Equal(CallStatus.Failed, result.Status);
        Assert.Single(rig.Provider.Requests);
    }

    [Fact]
    public async Task Retries_stop_after_two()
    {
        var rig = new RouterRig();
        rig.Provider.Throws(500, TimeSpan.Zero).Throws(502, TimeSpan.Zero).Throws(503, TimeSpan.Zero);

        var result = await rig.Guard.CallAsync(Request, null, TestConfig.Research, default);

        Assert.Equal(CallStatus.Failed, result.Status);
        Assert.Equal(3, rig.Provider.Requests.Count);
        Assert.Contains("retries exhausted", result.Reason);
    }

    [Fact]
    public async Task Retry_after_is_respected()
    {
        var rig = new RouterRig();
        rig.Provider.Throws(429, TimeSpan.FromSeconds(7)).Returns("x");

        var call = rig.Guard.CallAsync(Request, null, TestConfig.Research, default);
        var retriedAfter = await AdvanceUntil(rig, () => rig.Provider.Requests.Count == 2, TimeSpan.FromSeconds(0.5));
        var result = await call;

        Assert.Equal(CallStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.True(retriedAfter >= TimeSpan.FromSeconds(7), $"retried after {retriedAfter}");
    }

    [Fact]
    public async Task Network_errors_are_retried_with_exponential_backoff_and_jitter()
    {
        var rig = new RouterRig();

        var first = rig.Guard.Backoff(1, null);
        var second = rig.Guard.Backoff(2, null);
        var capped = rig.Guard.Backoff(1, TimeSpan.FromMinutes(5));

        Assert.InRange(first.TotalSeconds, 1, 2);
        Assert.InRange(second.TotalSeconds, 2, 3);
        Assert.Equal(TimeSpan.FromSeconds(60), capped);
        rig.Provider.Throws(null, network: true).Returns("x");
        var call = rig.Guard.CallAsync(Request, null, TestConfig.Research, default);
        var retriedAfter = await AdvanceUntil(rig, () => rig.Provider.Requests.Count == 2, TimeSpan.FromSeconds(0.25));

        Assert.Equal(CallStatus.Succeeded, (await call).Status);
        Assert.True(retriedAfter >= TimeSpan.FromSeconds(1), $"retried after {retriedAfter}");
    }

    [Fact]
    public async Task Kill_switch_is_checked_before_each_retry()
    {
        var rig = new RouterRig();
        rig.Provider.Throws(503, TimeSpan.FromSeconds(1)).Returns("x");

        var call = rig.Guard.CallAsync(Request, null, TestConfig.Research, default);
        await WaitUntil(() => rig.Provider.Requests.Count == 1);
        await rig.KillSwitch.TripAsync(KillSwitchTrigger.Manual, "user:test", "between attempts", default);
        await AdvanceUntil(rig, () => call.IsCompleted, TimeSpan.FromSeconds(0.5));
        var result = await call;

        Assert.Equal(CallStatus.Stopped, result.Status);
        Assert.Single(rig.Provider.Requests);
    }

    [Fact]
    public async Task Timeout_cancels_the_call_and_counts_as_a_retryable_failure()
    {
        var rig = new RouterRig();
        rig.Provider.Blocks().Returns("x");

        var call = rig.Guard.CallAsync(Request, null, TestConfig.Research, default);
        await rig.Provider.InFlight.Task;
        rig.Time.Advance(TimeSpan.FromSeconds(120)); // timeout; the 2 s watchdog polls find the switch off
        for (var i = 0; i < 100 && !call.IsCompleted; i++)
        {
            await Task.Delay(10);
            rig.Time.Advance(TimeSpan.FromSeconds(1)); // backoff, whenever the retry delay is registered
        }

        var result = await call;

        Assert.Equal(CallStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    /// <summary>
    /// Advances the fake clock in steps until <paramref name="condition"/> holds; returns the fake time advanced.
    /// Steps before the guard registers its delay (continuations of CancelAsync run on the thread pool) only add time.
    /// </summary>
    private static async Task<TimeSpan> AdvanceUntil(RouterRig rig, Func<bool> condition, TimeSpan step)
    {
        var advanced = TimeSpan.Zero;
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
            if (!condition())
            {
                rig.Time.Advance(step);
                advanced += step;
            }
        }

        Assert.True(condition());
        return advanced;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
