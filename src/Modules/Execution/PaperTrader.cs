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
/// </summary>
public sealed class PaperTrader(IPaperStore store, ICandleSource candles, CostModel costs, TimeSpan? warmup = null)
{
    public static readonly TimeSpan Warmup = TimeSpan.FromDays(45);
    private readonly Dictionary<long, (int Steps, PortfolioEngine Engine)> _cache = [];
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
        var runs = (await store.RunsAsync(ct)).ToList();

        // A cohort in PaperTrading without a run gets its own (ADR-0019 §3); members that join later are not added.
        var covered = runs.SelectMany(r => r.Config.Sleeves.Select(s => s.CohortId)).ToHashSet();
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

        if (runs.Count == 0)
        {
            return new CycleOutcome(0, false, false, true, [.. degraded, "no-paper-strategies"]);
        }

        var bars = new HashSet<DateTimeOffset>();
        bool catchUp = false, stale = false, reconciliationOk = true;
        foreach (var run in runs)
        {
            var outcome = await ProcessRunAsync(run, now, degraded, bars, ct);
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
    /// Indicator warm-up of a new run: the longest indicator warm-up of its strategies (BTC-sourced ones included)
    /// plus one bar and one day, in whole days, and never less than <see cref="Warmup"/> (ADR-0019 §4).
    /// </summary>
    public static TimeSpan WarmupFor(IEnumerable<DslStrategy> strategies)
    {
        var bars = strategies.SelectMany(s => s.Source.Indicators).Select(DslStrategy.Warmup).DefaultIfEmpty(0).Max() + 1;
        var needed = TimeSpan.FromDays(Math.Ceiling((CandleInterval.FourHours.FixedLength!.Value * bars + TimeSpan.FromDays(1)).TotalDays));
        return needed > Warmup ? needed : Warmup;
    }

    /// <summary>One run's cycle; <paramref name="bars"/> collects the bar times processed by any run.</summary>
    private async Task<CycleOutcome> ProcessRunAsync(PaperRun run, DateTimeOffset now, List<string> degraded, HashSet<DateTimeOffset> bars, CancellationToken ct)
    {
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
        var cached = _cache.TryGetValue(run.Id, out var c) && c.Steps == steps.Count && (steps.Count == 0 || c.Engine.StateHash() == steps[^1].StateHash);
        // A rebuild needs every candle since the warm-up start; a cached engine only the new bars and the correlation window.
        var from = cached && steps.Count > 0 ? Max(run.WarmupStart, steps[^1].BarTime - CorrelationLookback) : run.WarmupStart;
        var series = await candles.ReadAsync(run.Config.Symbols, interval, from, now, ct);
        var byTime = series.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.OpenTime));
        var engine = cached ? c.Engine : new PortfolioEngine(new PortfolioSetup(
            run.Config.Sleeves.Select((s, i) => new SleeveSpec(s.Name, strategies[i], s.Capital)).ToList(),
            costs, Risk(riskVersions, 0, run), run.Config.Symbols, interval, run.TradingStart, run.Config.Experimental.ToHashSet(), RecordEvents: true));

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
        for (var t = steps.Count > 0 ? steps[^1].BarTime + length : run.WarmupStart; t + length <= now; t += length)
        {
            var present = run.Config.Symbols.Where(s => byTime[s].ContainsKey(t)).ToList();
            var late = now - (t + length) > maxLag;
            if (present.Count == 0 && !late)
            {
                break; // wait for data
            }

            var killSwitch = await store.KillSwitchEngagedAsync(ct);
            var actions = await store.ActionsAfterAsync(lastActionId, ct);
            lastActionId = actions.Count > 0 ? actions[^1].Id : lastActionId;
            var riskVersion = riskVersions.Keys.DefaultIfEmpty(0).Max();
            var disabled = run.Config.Sleeves
                .Select((s, i) => (i, State: frozen.Single(f => f.CohortId == s.CohortId && f.MemberIndex == s.MemberIndex).State))
                .Where(x => x.State != "PaperTrading")
                .Select(x => x.i)
                .ToList();
            var blocked = killSwitch ? "kill-switch" : !reconciliationOk ? "reconciliation" : late ? "stale-data" : null;
            stale |= late;
            var inputs = new StepInputs(
                blocked,
                disabled,
                actions.Where(a => a is { Action: "close-position", Sleeve: not null, Symbol: not null } && (a.RunId is null || a.RunId == run.Id))
                    .Select(a => new ManualClose(a.Sleeve!.Value, a.Symbol!)).ToList(),
                actions.Any(a => a.Action == "close-all"),
                present,
                lastActionId,
                riskVersion);

            var eventsBefore = engine.Events.Count;
            var fillsBefore = engine.Fills.Count;
            engine.SetRisk(Risk(riskVersions, riskVersion, run));
            engine.Step(t, Bars(byTime, present, t), Reference(byTime, present, t), inputs.ToContext());
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

        if (reconciliationOk)
        {
            _cache[run.Id] = (steps.Count + processed, engine);
        }
        else
        {
            _cache.Remove(run.Id);
        }

        return new CycleOutcome(processed, processed > 1, stale, reconciliationOk, []);
    }

    private static readonly TimeSpan CorrelationLookback = TimeSpan.FromDays(40);

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
