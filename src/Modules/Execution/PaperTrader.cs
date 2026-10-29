using System.Globalization;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Radar;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Execution;

/// <summary>Source of closed live candles for the paper engine (market.live_candles in production).</summary>
public interface ICandleSource
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<Candle>>> ReadAsync(IReadOnlyList<string> symbols, CandleInterval interval, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public sealed record CycleOutcome(int BarsProcessed, bool CatchUp, bool Stale, bool ReconciliationOk, IReadOnlyList<string> Degraded);

/// <summary>
/// Deterministic paper trading (m5-protocol §2, ADR-0018 §4, ADR-0019 §3). One run per cohort: a cohort whose
/// members reach PaperTrading gets its own run; existing runs never change. Each cycle, for every run: rebuild the
/// engine by replaying the recorded steps (the state hash of every step must match: restart and reconciliation),
/// check the ledger, then process every newly closed bar in order with its operational inputs and persist it
/// atomically. Within one process each run's engine is kept between cycles (no replay when the step log is
/// unchanged); a new instance is a restart. <paramref name="warmup"/> overrides the indicator warm-up before the
/// trading start (by default <see cref="WarmupFor"/>: at least 45 days).
/// With <paramref name="testnet"/> (M6, m6-protocol §4): a cohort with TestnetActive members also gets a Testnet run,
/// a copy of its paper run's sleeves (capital scaled to 5,000 USDT, A-4 v1.1) that runs the same engine with the
/// exchange's fills; the paper run goes on unchanged. Each cycle first maintains the Testnet orders and reconciles the account, then steps the runs.
/// With <paramref name="allocator"/> and <paramref name="allocatorStore"/> (M8, ADR-0023): in shadow mode each paper
/// run has a shadow engine stepped after the real step with the allocator's overrides (real orders never change); in
/// active mode the real step is taken in two phases and the overrides of Validated sleeves are applied and recorded.
/// </summary>
public sealed class PaperTrader(IPaperStore store, ICandleSource candles, CostModel costs, TimeSpan? warmup = null, TestnetExecutor? testnet = null,
    IAllocationAdvisor? allocator = null, IAllocatorStore? allocatorStore = null)
{
    /// <summary>Lifecycle states whose sleeve trades in a paper run: a TestnetActive strategy keeps its paper shadow (U-M6-4).</summary>
    public static readonly IReadOnlySet<string> PaperStates = new HashSet<string> { "PaperTrading", "TestnetActive" };

    public const string TestnetState = "TestnetActive";

    public static readonly TimeSpan Warmup = TimeSpan.FromDays(45);
    private readonly Dictionary<long, (int Steps, PortfolioEngine Engine)> _cache = [];
    private readonly Dictionary<long, (int Steps, PortfolioEngine Engine)> _shadowCache = [];
    private readonly Dictionary<(string ManifestHash, DateTimeOffset Bar), IReadOnlyList<AllocationOverride>> _activeOverrides = [];
    private readonly HashSet<long> _warned = [];
    public const double CorrelationThreshold = 0.70;
    public const string ReferenceSymbol = "BTCUSDT";

    /// <summary>The engine debits notional + fee in one operation, the ledger in two rows: they agree to 1e-12 USDT.</summary>
    public const decimal LedgerTolerance = 0.000000000001m;

    /// <summary>Verified strategies for the frozen members in PaperTrading (V14: a hash mismatch is not run).</summary>
    public async Task<(IReadOnlyList<(FrozenRecord Record, DslStrategy Strategy, OperationalManifest Manifest)> Members, IReadOnlyList<string> Rejected)> LoadMembersAsync(Func<FrozenRecord, bool> include, CancellationToken ct)
    {
        var members = new List<(FrozenRecord, DslStrategy, OperationalManifest)>();
        var rejected = new List<string>();
        foreach (var record in (await store.FrozenAsync(ct)).Where(include))
        {
            try
            {
                var manifest = CohortManifestReader.Read(record.Manifest);
                if (manifest.Hash != record.ManifestHash)
                {
                    throw new StrategyHashMismatchException(record.ManifestHash, manifest.Hash);
                }

                var expected = manifest.Members.Single(m => m.Index == record.MemberIndex).DefinitionHash;
                var definition = await store.DefinitionAsync(record.DefinitionHash, ct) ?? throw new StrategyHashMismatchException(expected, "missing");
                members.Add((record, StrategyHashGuard.LoadVerified(definition, expected), manifest));
            }
            catch (Exception e) when (e is StrategyHashMismatchException or FormatException)
            {
                rejected.Add(record.Name);
                await store.AuditAsync("trading", "strategy_hash_mismatch", new { record.CohortId, record.MemberIndex, record.Name, error = e.Message }, ct);
            }
        }

        return (members, rejected);
    }

    public async Task<CycleOutcome> ProcessAsync(DateTimeOffset now, CancellationToken ct)
    {
        var degraded = new List<string>();
        var closed = await store.ClosedRunsAsync(ct);
        var runs = (await store.RunsAsync(ct)).Where(r => !closed.Contains(r.Id)).ToList();

        // A cohort in PaperTrading without a run gets its own (ADR-0019 §3); members that join later are not added.
        var covered = runs.Where(r => !r.Config.IsTestnet).SelectMany(r => r.Config.Sleeves.Select(s => s.CohortId)).ToHashSet();
        var (members, rejected) = await LoadMembersAsync(r => r.State == "PaperTrading" && !covered.Contains(r.CohortId), ct);
        degraded.AddRange(rejected.Select(r => $"strategy-hash-mismatch:{r}"));
        foreach (var cohort in members.GroupBy(m => m.Record.CohortId).OrderBy(g => g.Key))
        {
            var group = cohort.ToList();
            var start = NextBoundary(now);
            var lastAction = await store.LastActionIdAsync(ct);
            var run = await store.CreateRunAsync(start, start - (warmup ?? WarmupFor(group.Select(m => m.Strategy))), Config(group, lastAction == 0 ? null : lastAction), ct);
            await store.AuditAsync("trading", "paper_run_created", new { run.Id, cohortId = cohort.Key, run.TradingStart, run.WarmupStart, configHash = run.Config.Hash() }, ct);
            runs.Add(run);
        }

        TestnetReconciliation? testnetCheck = null;
        if (testnet is not null)
        {
            try
            {
                testnetCheck = await PrepareTestnetAsync(runs, now, degraded, ct);
            }
            catch (ExchangeException e) when (e is not ExchangeIpBannedException)
            {
                degraded.Add("exchange-unavailable");
                await store.AuditAsync("trading", "exchange_unavailable", new { error = e.GetType().Name }, ct);
            }
        }

        if (runs.Count == 0)
        {
            return new CycleOutcome(0, false, false, true, [.. degraded, "no-paper-strategies"]);
        }

        var bars = new HashSet<DateTimeOffset>();
        bool catchUp = false, stale = false, reconciliationOk = true;
        foreach (var run in runs)
        {
            if (run.Config.IsTestnet && (testnet is null || testnetCheck is null))
            {
                degraded.Add("exchange-unavailable"); // Testnet disabled or unreachable: the Testnet run waits; paper goes on
                continue;
            }

            CycleOutcome outcome;
            try
            {
                outcome = await ProcessRunAsync(run, now, degraded, bars, testnetCheck, ct);
            }
            catch (ExchangeException e) when (run.Config.IsTestnet && e is not ExchangeIpBannedException)
            {
                degraded.Add("exchange-unavailable");
                await store.AuditAsync("trading", "exchange_unavailable", new { runId = run.Id, error = e.GetType().Name }, ct);
                continue;
            }

            catchUp |= outcome.CatchUp;
            stale |= outcome.Stale;
            reconciliationOk &= outcome.ReconciliationOk;
        }

        if (stale)
        {
            degraded.Add("stale-data");
        }

        if (await store.KillSwitchEngagedAsync(ct))
        {
            degraded.Add("kill-switch");
        }

        return new CycleOutcome(bars.Count, catchUp, stale, reconciliationOk, degraded.Distinct().ToList());
    }

    /// <summary>
    /// Exchange maintenance outside the hourly cycle (m6-protocol §5.5): with the kill switch engaged, the open entry
    /// orders of every Testnet run are cancelled at once; protective stops stay. Returns the number of Testnet runs.
    /// </summary>
    public async Task<int> MaintainTestnetAsync(CancellationToken ct)
    {
        if (testnet is null)
        {
            return 0;
        }

        var closed = await store.ClosedRunsAsync(ct);
        var open = (await store.RunsAsync(ct)).Where(r => r.Config.IsTestnet && !closed.Contains(r.Id)).ToList();
        if (open.Count == 0)
        {
            return 0;
        }

        await testnet.PrepareAsync(open.SelectMany(r => r.Config.Symbols).Distinct(StringComparer.Ordinal).ToList(), ct);
        var killSwitch = await store.KillSwitchEngagedAsync(ct);
        foreach (var run in open)
        {
            var steps = await store.StepsAsync(run.Id, ct);
            var length = CandleInterval.FromCode(run.Config.Interval).FixedLength!.Value;
            await testnet.MaintainAsync(run, steps.Count > 0 ? steps[^1].BarTime + length : run.WarmupStart, killSwitch, ct);
        }

        return open.Count;
    }

    /// <summary>
    /// Indicator warm-up of a new run: the longest indicator warm-up of its strategies (BTC-sourced ones included)
    /// plus one bar and one day, in whole days, and never less than <see cref="Warmup"/> (ADR-0019 §4).
    /// </summary>
    public static TimeSpan WarmupFor(IEnumerable<DslStrategy> strategies)
    {
        var bars = strategies.SelectMany(s => s.Source.Indicators).Select(DslStrategy.Warmup).DefaultIfEmpty(0).Max() + 1;
        var needed = TimeSpan.FromDays(Math.Ceiling((CandleInterval.FourHours.FixedLength!.Value * bars + TimeSpan.FromDays(1)).TotalDays));
        return needed > Warmup ? needed : Warmup;
    }

    /// <summary>
    /// Testnet part of a cycle (m6-protocol §3.2, §4): rules and clock, a requested rebaseline (closes the open Testnet
    /// runs), a Testnet run with a balance baseline for every cohort with TestnetActive members, maintenance of every
    /// Testnet run's orders, then the account-wide reconciliation. Null when there is no Testnet run.
    /// </summary>
    private async Task<TestnetReconciliation?> PrepareTestnetAsync(List<PaperRun> runs, DateTimeOffset now, List<string> degraded, CancellationToken ct)
    {
        var frozen = await store.FrozenAsync(ct);
        var active = frozen.Where(f => f.State == TestnetState).Select(f => f.CohortId).Distinct().Order().ToList();
        var open = runs.Where(r => r.Config.IsTestnet).ToList();
        if (active.Count == 0 && open.Count == 0)
        {
            return null;
        }

        var symbols = runs.Where(r => open.Contains(r) || active.Any(c => r.Config.Sleeves.Any(s => s.CohortId == c)))
            .SelectMany(r => r.Config.Symbols).Distinct(StringComparer.Ordinal).ToList();
        await testnet!.PrepareAsync(symbols, ct);

        // Rebaseline (§4.4): the user's action closes the open Testnet runs; new runs and a new baseline follow.
        var sources = await testnet.BaselineSourcesAsync(ct);
        var rebaseline = (await store.ActionsAfterAsync(0, ct)).LastOrDefault(a => a.Action == "testnet-rebaseline" && !sources.Contains($"rebaseline:{a.Id}"));
        string? source = null;
        if (rebaseline is not null)
        {
            foreach (var run in open)
            {
                await store.CloseRunAsync(run.Id, $"rebaseline:{rebaseline.Id}", ct);
                runs.Remove(run);
            }

            await store.AuditAsync("trading", "testnet_runs_closed", new { runs = open.Select(r => r.Id).ToList(), actionId = rebaseline.Id }, ct);
            open.Clear();
            source = $"rebaseline:{rebaseline.Id}";
        }

        var coveredTestnet = open.SelectMany(r => r.Config.Sleeves.Select(s => s.CohortId)).ToHashSet();
        foreach (var cohortId in active.Where(c => !coveredTestnet.Contains(c)))
        {
            var paper = runs.LastOrDefault(r => !r.Config.IsTestnet && r.Config.Sleeves.Any(s => s.CohortId == cohortId));
            var (members, rejected) = await LoadMembersAsync(r => r.CohortId == cohortId, ct);
            if (paper is null || rejected.Count > 0)
            {
                continue; // no paper run to mirror, or a hash alarm
            }

            var start = NextBoundary(now);
            var lastAction = await store.LastActionIdAsync(ct);
            var config = paper.Config.ForTestnet(lastAction == 0 ? null : lastAction);
            var run = await store.CreateRunAsync(start, start - (warmup ?? WarmupFor(members.Select(m => m.Strategy))), config, ct);
            await testnet.BaselineAsync(run.Id, source ?? $"run-start:{run.Id}", await testnet.AccountAsync(ct), ct);
            await store.AuditAsync("trading", "testnet_run_created", new { run.Id, cohortId, paperRunId = paper.Id, run.TradingStart, configHash = run.Config.Hash() }, ct);
            runs.Add(run);
            open.Add(run);
        }

        if (open.Count == 0)
        {
            return null;
        }

        var killSwitch = await store.KillSwitchEngagedAsync(ct);
        var unknown = 0;
        foreach (var run in open)
        {
            var steps = await store.StepsAsync(run.Id, ct);
            var length = CandleInterval.FromCode(run.Config.Interval).FixedLength!.Value;
            unknown += await testnet.MaintainAsync(run, steps.Count > 0 ? steps[^1].BarTime + length : run.WarmupStart, killSwitch, ct);
        }

        if (unknown > 0)
        {
            degraded.Add("order-unknown");
        }

        var check = await testnet.ReconcileAsync(open, ct);
        if (!check.Ok)
        {
            degraded.Add(check.Reason == "testnet-reset-suspected" ? "testnet-reset-suspected" : "testnet-reconciliation");
            await store.AuditAsync("trading", "testnet_reconciliation_failed", check.Detail, ct);
        }

        return check;
    }

    /// <summary>One run's cycle; <paramref name="bars"/> collects the bar times processed by any run.</summary>
    private async Task<CycleOutcome> ProcessRunAsync(PaperRun run, DateTimeOffset now, List<string> degraded, HashSet<DateTimeOffset> bars, TestnetReconciliation? testnetCheck, CancellationToken ct)
    {
        var onTestnet = run.Config.IsTestnet;
        var strategies = new List<IStrategy>();
        foreach (var sleeve in run.Config.Sleeves)
        {
            var (loaded, rejected) = await LoadMembersAsync(r => r.CohortId == sleeve.CohortId && r.MemberIndex == sleeve.MemberIndex, ct);
            if (rejected.Count > 0 || loaded.Count != 1 || loaded[0].Strategy.DefinitionHash != sleeve.DefinitionHash)
            {
                // The run cannot be rebuilt without every sleeve: nothing is processed and the alarm is raised.
                degraded.Add($"strategy-hash-mismatch:{sleeve.Name}");
                return new CycleOutcome(0, false, false, false, []);
            }

            strategies.Add(loaded[0].Strategy);
        }

        var interval = CandleInterval.FromCode(run.Config.Interval);
        var length = interval.FixedLength!.Value;
        var riskVersions = await store.RiskVersionsAsync(ct);
        var steps = await store.StepsAsync(run.Id, ct);
        var reconciliationOk = true;
        var allocation = allocatorStore is null || allocator is null ? AllocatorState.Disabled : await allocatorStore.StateAsync(ct);
        var cached = _cache.TryGetValue(run.Id, out var c) && c.Steps == steps.Count && (steps.Count == 0 || c.Engine.StateHash() == steps[^1].StateHash);
        // A rebuild needs every candle since the warm-up start; a cached engine only the new bars and the correlation
        // window (with the allocator: the radar's SMA300 window of the BTC regime).
        var lookback = allocation.Mode == AllocatorMode.Disabled ? CorrelationLookback : RegimeLookback;
        var from = cached && steps.Count > 0 ? Max(run.WarmupStart, steps[^1].BarTime - lookback) : run.WarmupStart;
        var series = await candles.ReadAsync(run.Config.Symbols, interval, from, now, ct);
        var byTime = series.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.OpenTime));
        PortfolioEngine NewEngine() => new(new PortfolioSetup(
            run.Config.Sleeves.Select((s, i) => new SleeveSpec(s.Name, strategies[i], s.Capital)).ToList(),
            costs, Risk(riskVersions, 0, run), run.Config.Symbols, interval, run.TradingStart, run.Config.Experimental.ToHashSet(), RecordEvents: true,
            ExternalFills: onTestnet));
        var engine = cached ? c.Engine : NewEngine();

        // 1. Rebuild from the step log; every step must reproduce its recorded state hash.
        foreach (var step in cached ? [] : steps)
        {
            engine.SetRisk(Risk(riskVersions, step.Inputs.RiskVersion, run));
            engine.Step(step.BarTime, Bars(byTime, step.Inputs.Symbols, step.BarTime), Reference(byTime, step.Inputs.Symbols, step.BarTime), step.Inputs.ToContext());
            if (reconciliationOk && engine.StateHash() != step.StateHash)
            {
                reconciliationOk = false;
                await store.AddReconciliationAsync(run.Id, step.BarTime, false, new { reason = "state-hash", expected = step.StateHash, actual = engine.StateHash() }, ct);
            }
        }

        // 2. Ledger reconciliation (m5-protocol §2.5).
        var ledger = await store.LedgerAsync(run.Id, ct);
        for (var k = 0; k < run.Config.Sleeves.Count && reconciliationOk; k++)
        {
            var (fills, cash) = ledger.GetValueOrDefault(k);
            var expectedFills = engine.Fills.Count(f => f.Sleeve == k);
            if (fills != expectedFills || Math.Abs(run.Config.Sleeves[k].Capital + cash - engine.Cash(k)) > LedgerTolerance || engine.Cash(k) < 0)
            {
                reconciliationOk = false;
                await store.AddReconciliationAsync(run.Id, steps.LastOrDefault()?.BarTime, false, new { reason = "ledger", sleeve = k, fills, expectedFills, ledgerCash = run.Config.Sleeves[k].Capital + cash, engineCash = engine.Cash(k) }, ct);
            }
        }

        // Testnet: the account-wide check of the exchange orders and balances (m6-protocol §4.3).
        if (onTestnet && reconciliationOk && testnetCheck is { Ok: false })
        {
            reconciliationOk = false;
            await store.AddReconciliationAsync(run.Id, steps.LastOrDefault()?.BarTime, false, testnetCheck.Detail, ct);
        }

        if (reconciliationOk && steps.Count > 0)
        {
            await store.AddReconciliationAsync(run.Id, steps[^1].BarTime, true, new { steps = steps.Count, fills = engine.Fills.Count }, ct);
        }
        else if (!reconciliationOk)
        {
            degraded.Add("reconciliation");
        }

        // 3. New closed bars, in order.
        var processed = 0;
        var stale = false;
        var lastActionId = steps.LastOrDefault()?.Inputs.LastActionId ?? run.Config.ActionsAfter ?? 0;
        var maxLag = TimeSpan.FromTicks(length.Ticks * Risk(riskVersions, riskVersions.Keys.DefaultIfEmpty(0).Max(), run).MaxDataLagBars);
        var frozen = await store.FrozenAsync(ct);
        var shadow = allocation.Mode == AllocatorMode.Shadow && !onTestnet && reconciliationOk
            ? await ShadowEngineAsync(run, steps, NewEngine, riskVersions, interval, byTime, degraded, ct)
            : null;
        IReadOnlySet<long> validated = allocation.Mode == AllocatorMode.Active ? await allocatorStore!.ValidatedCohortsAsync(ct) : new HashSet<long>();
        for (var t = steps.Count > 0 ? steps[^1].BarTime + length : run.WarmupStart; t + length <= now; t += length)
        {
            var present = run.Config.Symbols.Where(s => byTime[s].ContainsKey(t)).ToList();
            var late = now - (t + length) > maxLag;
            if (present.Count == 0 && !late)
            {
                break; // wait for data
            }

            // Testnet: the orders sent for this bar's open must be resolved before the step can apply their fills.
            if (onTestnet && await testnet!.PendingAsync(run, t - length, ct))
            {
                degraded.Add("orders-pending");
                break;
            }

            var killSwitch = await store.KillSwitchEngagedAsync(ct);
            var actions = await store.ActionsAfterAsync(lastActionId, ct);
            lastActionId = actions.Count > 0 ? actions[^1].Id : lastActionId;
            var riskVersion = riskVersions.Keys.DefaultIfEmpty(0).Max();
            var disabled = run.Config.Sleeves
                .Select((s, i) => (i, State: frozen.Single(f => f.CohortId == s.CohortId && f.MemberIndex == s.MemberIndex).State))
                .Where(x => onTestnet ? x.State != TestnetState : !PaperStates.Contains(x.State))
                .Select(x => x.i)
                .ToList();
            var external = onTestnet ? await testnet!.ExternalAsync(run, engine, t, ct) : null;
            var blocked = killSwitch ? "kill-switch" : !reconciliationOk ? "reconciliation" : late ? "stale-data"
                : external?.Unprotected is { Count: > 0 } ? "unprotected" : null;
            stale |= late;
            var inputs = new StepInputs(
                blocked,
                disabled,
                actions.Where(a => a is { Action: "close-position", Sleeve: not null, Symbol: not null } && (a.RunId is null || a.RunId == run.Id))
                    .Select(a => new ManualClose(a.Sleeve!.Value, a.Symbol!)).ToList(),
                actions.Any(a => a.Action == "close-all"),
                present,
                lastActionId,
                riskVersion,
                external);

            var eventsBefore = engine.Events.Count;
            var fillsBefore = engine.Fills.Count;
            var live = now - (t + length) < length;
            engine.SetRisk(Risk(riskVersions, riskVersion, run));
            if (allocation.Mode == AllocatorMode.Active)
            {
                // Active mode (ADR-0023 §4): exits and stops in the first phase, then the overrides of Validated sleeves only.
                var candidates = engine.BeginStep(t, Bars(byTime, present, t), Reference(byTime, present, t), inputs.ToContext());
                var applied = await ActiveOverridesAsync(run, engine, t, t + length, live, candidates, validated, byTime, ct);
                engine.CompleteStep(applied);
                inputs = inputs with { Allocation = applied };
            }
            else
            {
                engine.Step(t, Bars(byTime, present, t), Reference(byTime, present, t), inputs.ToContext());
            }
            var positions = engine.Positions().ToList();
            var balances = run.Config.Sleeves.Select((_, k) =>
            {
                var held = positions.Where(p => p.Sleeve == k).ToList();
                return (k, engine.Cash(k), engine.Cash(k) + held.Sum(p => p.Quantity * p.LastClose),
                    (object)held.Select(p => new { p.Symbol, p.EntryTime, p.EntryPrice, p.Quantity, p.Stop, p.LastClose }).ToList());
            }).ToList();
            var document = riskVersions.TryGetValue(riskVersion, out var stored) ? stored : run.Config.InitialRisk;
            var threshold = (double?)document.ClusterCapThreshold ?? CorrelationThreshold;
            var correlation = Correlation(run, byTime, t, t + length, positions, engine.TotalEquity, threshold);
            await store.AddStepAsync(run, new StepWrite(
                t, inputs, engine.StateHash(), engine.Events.Skip(eventsBefore).ToList(), engine.Fills.Skip(fillsBefore).ToList(), balances, correlation), ct);
            processed++;
            bars.Add(t);
            if (shadow is not null)
            {
                await ShadowStepAsync(run, shadow, engine, inputs, t, t + length, live, Risk(riskVersions, riskVersion, run), Bars(byTime, present, t), Reference(byTime, present, t), allocation, byTime, ct);
            }

            if (onTestnet)
            {
                // The step's orders go out now, for the next open (outbox, m6-protocol §3.1); stops follow the holdings.
                await testnet!.SubmitAsync(run, engine, t, now, await store.KillSwitchEngagedAsync(ct) ? "kill-switch" : null, ct);
            }

            // P1 warning (informational): the largest cluster share crosses the warning level.
            var above = correlation is not null && document.ClusterWarningShare is { } warning && correlation.MaxClusterShare >= (double)warning;
            if (above && !_warned.Contains(run.Id))
            {
                await store.AuditAsync("trading", "correlation_warning", new { runId = run.Id, barTime = t, share = Math.Round(correlation!.MaxClusterShare, 4), warning = document.ClusterWarningShare }, ct);
            }

            if (above)
            {
                _warned.Add(run.Id);
            }
            else
            {
                _warned.Remove(run.Id);
            }
        }

        if (onTestnet && engine.LastStep is { } lastStep)
        {
            // Idempotent: sends what a crash after the last step left unsent, retries entries within their bar and
            // re-places missing stops; then reports positions still without a stop (m6-protocol §5.3).
            await testnet!.SubmitAsync(run, engine, lastStep, now, await store.KillSwitchEngagedAsync(ct) ? "kill-switch" : !reconciliationOk ? "reconciliation" : null, ct);
            if ((await testnet.UnprotectedAsync(run, engine, ct)).Count > 0)
            {
                degraded.Add("unprotected-stop");
            }
        }

        if (reconciliationOk)
        {
            _cache[run.Id] = (steps.Count + processed, engine);
        }
        else
        {
            _cache.Remove(run.Id);
        }

        if (shadow is not null)
        {
            _shadowCache[run.Id] = (steps.Count + processed, shadow);
        }
        else
        {
            _shadowCache.Remove(run.Id);
        }

        return new CycleOutcome(processed, processed > 1, stale, reconciliationOk, []);
    }

    private static readonly TimeSpan CorrelationLookback = TimeSpan.FromDays(40);

    /// <summary>The radar's SMA300 of 4h BTC candles needs 50 days; 60 leave a margin (m8-protocol §5.1).</summary>
    private static readonly TimeSpan RegimeLookback = TimeSpan.FromDays(60);

    /// <summary>
    /// The run's shadow engine, in step with the real run: the cached one, or a rebuild that replays every recorded step
    /// with the stored shadow overrides instead of the real ones (no model call). A stored shadow state hash that the
    /// rebuild does not reproduce is an alarm; the shadow is then skipped for this cycle.
    /// </summary>
    private async Task<PortfolioEngine?> ShadowEngineAsync(PaperRun run, IReadOnlyList<StepRecord> steps, Func<PortfolioEngine> create,
        IReadOnlyDictionary<long, RiskConfigDocument> riskVersions, CandleInterval interval, Dictionary<string, Dictionary<DateTimeOffset, Candle>> recent,
        List<string> degraded, CancellationToken ct)
    {
        if (_shadowCache.TryGetValue(run.Id, out var cachedShadow) && cachedShadow.Steps == steps.Count)
        {
            return cachedShadow.Engine;
        }

        var stored = (await allocatorStore!.ShadowStepsAsync(run.Id, ct)).ToDictionary(s => s.BarTime);
        var byTime = steps.Count == 0 ? recent
            : (await candles.ReadAsync(run.Config.Symbols, interval, run.WarmupStart, steps[^1].BarTime + interval.FixedLength!.Value, ct))
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.OpenTime));
        var shadow = create();
        foreach (var step in steps)
        {
            var overrides = stored.TryGetValue(step.BarTime, out var s) && s.Overrides.Count > 0 ? s.Overrides : null;
            shadow.SetRisk(Risk(riskVersions, step.Inputs.RiskVersion, run));
            shadow.Step(step.BarTime, Bars(byTime, step.Inputs.Symbols, step.BarTime), Reference(byTime, step.Inputs.Symbols, step.BarTime),
                step.Inputs.ToContext() with { Allocation = overrides });
            if (s is not null && shadow.StateHash() != s.StateHash)
            {
                degraded.Add("allocator-shadow");
                await store.AuditAsync("trading", "allocator_shadow_mismatch", new { runId = run.Id, barTime = step.BarTime, expected = s.StateHash, actual = shadow.StateHash() }, ct);
                return null;
            }
        }

        return shadow;
    }

    /// <summary>One shadow step (m8-protocol §4.1): same bar and operational inputs as the real step, the allocator's own overrides.</summary>
    private async Task ShadowStepAsync(PaperRun run, PortfolioEngine shadow, PortfolioEngine real, StepInputs inputs, DateTimeOffset t, DateTimeOffset close, bool live,
        RiskConfig risk, IReadOnlyDictionary<string, Candle> barsAt, Candle? reference, AllocatorState allocation,
        Dictionary<string, Dictionary<DateTimeOffset, Candle>> byTime, CancellationToken ct)
    {
        shadow.SetRisk(risk);
        var candidates = shadow.BeginStep(t, barsAt, reference, inputs.ToContext() with { Allocation = null });
        var decision = await AdviseAsync(Request(run, shadow, close, AllocatorMode.Shadow, live, candidates, allocation.ProtocolHash, byTime, t), ct);
        shadow.CompleteStep(decision.Overrides.Count == 0 ? null : decision.Overrides);
        await allocatorStore!.AddDecisionAsync(
            new ProposalWrite(run.Id, t, ProposalWrite.ShadowRole, decision.Outcome, decision.TaskId, allocation.ProtocolHash, decision.Items, decision.SafetyEvents),
            new ShadowStep(run.Id, t, allocation.ProtocolHash, decision.Overrides, shadow.StateHash(), shadow.TotalEquity, real.TotalEquity), ct);
    }

    /// <summary>
    /// Overrides of an active allocator for one real step (ADR-0023 §4): only Validated sleeves; a Testnet run reuses its
    /// paper run's decision of the same bar. Null means T11.
    /// </summary>
    private async Task<IReadOnlyList<AllocationOverride>?> ActiveOverridesAsync(PaperRun run, PortfolioEngine engine, DateTimeOffset t, DateTimeOffset close, bool live,
        IReadOnlyList<EntryCandidate> candidates, IReadOnlySet<long> validated, Dictionary<string, Dictionary<DateTimeOffset, Candle>> byTime, CancellationToken ct)
    {
        var eligible = run.Config.Sleeves.Select((s, k) => (s, k)).Where(x => validated.Contains(x.s.CohortId)).Select(x => x.k).ToHashSet();
        IReadOnlyList<AllocationOverride> applied;
        if (run.Config.IsTestnet)
        {
            applied = _activeOverrides.GetValueOrDefault((run.Config.ManifestHash, t)) ?? [];
        }
        else
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var mine = candidates.Where(c => eligible.Contains(c.Sleeve)).ToList();
            var decision = mine.Count == 0
                ? AllocationDecision.Fallback(AllocationOutcome.NotValidated)
                : await AdviseAsync(Request(run, engine, close, AllocatorMode.Active, live, mine, null, byTime, t), ct);
            applied = decision.Overrides;
            _activeOverrides[(run.Config.ManifestHash, t)] = applied;
            await allocatorStore!.AddDecisionAsync(
                new ProposalWrite(run.Id, t, ProposalWrite.ActiveRole, decision.Outcome, decision.TaskId, null, decision.Items, decision.SafetyEvents), null, ct);
        }

        // Defence in depth: whatever the advisor returned, only Validated sleeves are ever adjusted.
        var filtered = applied.Where(o => eligible.Contains(o.Sleeve)).ToList();
        return filtered.Count == 0 ? null : filtered;
    }

    /// <summary>The allocator call; any failure of the allocator path is a deterministic fallback, never a failed trading cycle.</summary>
    private async Task<AllocationDecision> AdviseAsync(AllocationRequest request, CancellationToken ct)
    {
        try
        {
            return await allocator!.AdviseAsync(request, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await store.AuditAsync("trading", "allocator_error", new { request.RunKey, request.DecisionTime, error = e.GetType().Name }, ct);
            return AllocationDecision.Fallback(AllocationOutcome.ProviderError);
        }
    }

    /// <summary>The read-only packet input of one decision (m8-protocol §5.1): candidates, holdings, limit summary and BTC regime.</summary>
    private static AllocationRequest Request(PaperRun run, PortfolioEngine engine, DateTimeOffset close, AllocatorMode mode, bool live, IReadOnlyList<EntryCandidate> candidates,
        string? protocolHash, Dictionary<string, Dictionary<DateTimeOffset, Candle>> byTime, DateTimeOffset t)
    {
        var equity = engine.TotalEquity;
        decimal Share(decimal value) => equity > 0 ? value / equity : 0;
        var exits = engine.PendingOrders().Where(o => !o.Entry).Select(o => (o.Sleeve, o.Symbol)).ToHashSet();
        var positions = engine.Positions().ToList();
        var holdings = positions.Select(p => new AllocationHolding(p.Sleeve, p.Symbol, Share(p.Quantity * p.LastClose), exits.Contains((p.Sleeve, p.Symbol)))).ToList();
        var experimental = run.Config.Experimental.ToHashSet();
        var limits = new AllocationLimits(
            Share(positions.Sum(p => p.Quantity * p.LastClose)),
            Share(positions.Where(p => experimental.Contains(p.Symbol)).Sum(p => p.Quantity * p.LastClose)),
            positions.Count, engine.Risk.Settings.MaxOpenPositions, engine.Risk.Settings.MaxAssetWeight);
        string? regime = null;
        if (live && candidates.Count > 0 && byTime.TryGetValue(ReferenceSymbol, out var btc))
        {
            var r = SignalRadar.Regime(btc.Values.Where(b => b.OpenTime <= t).OrderBy(b => b.OpenTime).TakeLast(SignalRadar.Bars).ToList());
            regime = r.Sma300DistancePct is { } pct ? string.Create(CultureInfo.InvariantCulture, $"BTCUSDT {r.State}, close {pct}% from SMA300") : $"BTCUSDT {r.State}";
        }

        return new AllocationRequest($"run-{run.Id}", close, mode, live, run.Config.Sleeves.Select(s => s.Name).ToList(), candidates, holdings, limits, regime, protocolHash);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>The first interval boundary (4h, UTC) strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextBoundary(DateTimeOffset now)
    {
        var length = CandleInterval.FourHours.FixedLength!.Value;
        var ticks = (now.UtcTicks / length.Ticks + 1) * length.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>Configuration of a new run for the members of one cohort (they share the manifest).</summary>
    private static PaperRunConfig Config(IReadOnlyList<(FrozenRecord Record, DslStrategy Strategy, OperationalManifest Manifest)> members, long? actionsAfter)
    {
        var symbols = members.SelectMany(m => m.Manifest.Pairs.Select(p => p.Symbol)).Distinct().ToList();
        var experimental = members.SelectMany(m => m.Manifest.ExperimentalSymbols).Distinct().Order(StringComparer.Ordinal).ToList();
        var interval = members.Select(m => m.Manifest.Interval.Code).Distinct().Single();
        var capital = Math.Round(PaperRunConfig.NominalCapital / members.Count, 8);
        return new PaperRunConfig(
            members[0].Record.ManifestHash,
            members.Select(m => new SleeveConfig(m.Record.CohortId, m.Record.MemberIndex, m.Record.Name, m.Strategy.DefinitionHash, capital)).ToList(),
            symbols,
            experimental,
            interval,
            symbols.Contains(ReferenceSymbol) ? ReferenceSymbol : null,
            "cost-model v1",
            InitialRisk(members[0].Manifest),
            actionsAfter);
    }

    /// <summary>
    /// What the live feed must cover (m5-protocol §2.1): the symbols of every run and of every cohort in
    /// PaperTrading still waiting for its run, from the earliest warm-up start. Reads the manifests only (no audit).
    /// </summary>
    public async Task<(IReadOnlyList<string> Symbols, DateTimeOffset WarmupStart)?> FeedPlanAsync(DateTimeOffset now, CancellationToken ct)
    {
        var runs = await store.RunsAsync(ct);
        var covered = runs.SelectMany(r => r.Config.Sleeves.Select(s => s.CohortId)).ToHashSet();
        var symbols = runs.SelectMany(r => r.Config.Symbols).ToList();
        var starts = runs.Select(r => r.WarmupStart).ToList();
        foreach (var cohort in (await store.FrozenAsync(ct)).Where(f => f.State == "PaperTrading" && !covered.Contains(f.CohortId)).GroupBy(f => f.CohortId))
        {
            OperationalManifest manifest;
            try
            {
                manifest = CohortManifestReader.Read(cohort.First().Manifest);
            }
            catch (FormatException)
            {
                continue; // the cycle's member load raises the hash alarm
            }

            var strategies = manifest.Members.Where(m => cohort.Any(f => f.MemberIndex == m.Index)).Select(m => DslStrategy.FromJson(m.DefinitionJson));
            symbols.AddRange(manifest.Pairs.Select(p => p.Symbol));
            starts.Add(NextBoundary(now) - (warmup ?? WarmupFor(strategies)));
        }

        return starts.Count == 0 ? null : (symbols.Distinct().ToList(), starts.Min());
    }

    /// <summary>The RiskConfig of a version; version 0 is the manifest risk block with the D22 cap and D7 freshness rule.</summary>
    private static RiskConfig Risk(IReadOnlyDictionary<long, RiskConfigDocument> versions, long version, PaperRun run) =>
        (versions.TryGetValue(version, out var document) ? document : run.Config.InitialRisk).ToConfig();

    private static IReadOnlyDictionary<string, Candle> Bars(Dictionary<string, Dictionary<DateTimeOffset, Candle>> byTime, IReadOnlyList<string> symbols, DateTimeOffset t)
    {
        var bars = new Dictionary<string, Candle>();
        foreach (var symbol in symbols)
        {
            if (byTime.TryGetValue(symbol, out var series) && series.TryGetValue(t, out var bar))
            {
                bars[symbol] = bar;
            }
        }

        return bars;
    }

    private static Candle? Reference(Dictionary<string, Dictionary<DateTimeOffset, Candle>> byTime, IReadOnlyList<string> symbols, DateTimeOffset t) =>
        symbols.Contains(ReferenceSymbol) && byTime.TryGetValue(ReferenceSymbol, out var series) && series.TryGetValue(t, out var bar) ? bar : null;

    /// <summary>Correlation monitoring (m5-protocol §2.7): informational; computed from day-end closes up to the close.</summary>
    /// <summary>
    /// RiskConfig of a new paper run: the manifest risk block with the D22 cap and D7 freshness rule, and the binding P1
    /// correlation-cluster cap with its warning level (U, 2026-10-20).
    /// </summary>
    public static RiskConfigDocument InitialRisk(OperationalManifest manifest) =>
        RiskConfigDocument.From(new RiskConfig(manifest.Risk, ClusterCap: RiskConfig.P1ClusterCap), RiskConfig.P1ClusterWarning);

    private static CorrelationSnapshot? Correlation(PaperRun run, Dictionary<string, Dictionary<DateTimeOffset, Candle>> byTime, DateTimeOffset barTime, DateTimeOffset close, IReadOnlyList<PositionView> positions, decimal equity, double threshold)
    {
        var asOf = DateOnly.FromDateTime(close.UtcDateTime).AddDays(-1);
        var dayEnd = run.Config.Symbols.ToDictionary(
            s => s,
            s => (IReadOnlyDictionary<DateOnly, decimal>)byTime[s].Values
                .Where(c => c.CloseTime <= close && c.CloseTime.TimeOfDay == TimeSpan.Zero)
                .ToDictionary(c => DateOnly.FromDateTime(c.OpenTime.UtcDateTime), c => c.Close));
        var (symbols, matrix) = CorrelationClusters.Matrix(run.Config.Symbols, dayEnd, asOf, InverseVolatilityAllocation.VolatilityDays);
        if (symbols.Count == 0 || equity <= 0)
        {
            return null;
        }

        var clusters = CorrelationClusters.Assign(run.Config.Symbols, dayEnd, asOf, InverseVolatilityAllocation.VolatilityDays, threshold);
        var exposure = run.Config.Symbols.ToDictionary(s => s, s => (double)(positions.Where(p => p.Symbol == s).Sum(p => p.Quantity * p.LastClose) / equity));
        var weights = symbols.Select(s => exposure[s]).ToList();
        var maxShare = clusters.GroupBy(c => c.Value).Select(g => g.Sum(c => exposure[c.Key])).DefaultIfEmpty(0).Max();
        return new CorrelationSnapshot(barTime, threshold, clusters, CorrelationClusters.EffectiveCount(weights, matrix), maxShare);
    }
}
