namespace PortfolioIntelligence.Execution;

/// <summary>
/// In-memory <see cref="IPaperStore"/> for the parity replay (m5-protocol §5.1) on research data: the same trader
/// code path, without writing research-window replays into the operational tables.
/// </summary>
public sealed class InMemoryPaperStore(IReadOnlyList<FrozenRecord> frozen, IReadOnlyDictionary<string, string> definitions) : IPaperStore
{
    private readonly List<PaperRun> _runs = [];
    private readonly List<StepRecord> _steps = [];
    private readonly Dictionary<int, (long Fills, decimal Cash)> _ledger = [];

    public List<StepWrite> Written { get; } = [];

    public List<(bool Ok, object Detail)> Reconciliations { get; } = [];

    public List<(string Event, object Payload)> Audit { get; } = [];

    public Task<IReadOnlyList<FrozenRecord>> FrozenAsync(CancellationToken ct) => Task.FromResult(frozen);

    public Task<string?> DefinitionAsync(string definitionHash, CancellationToken ct) => Task.FromResult(definitions.GetValueOrDefault(definitionHash));

    public Task<PaperRun?> LatestRunAsync(CancellationToken ct) => Task.FromResult(_runs.LastOrDefault());

    public Task<PaperRun> CreateRunAsync(DateTimeOffset tradingStart, DateTimeOffset warmupStart, PaperRunConfig config, CancellationToken ct)
    {
        var run = new PaperRun(_runs.Count + 1, tradingStart, warmupStart, config);
        _runs.Add(run);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<StepRecord>> StepsAsync(long runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepRecord>>([.. _steps]);

    public Task<IReadOnlyDictionary<int, (long Fills, decimal Cash)>> LedgerAsync(long runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, (long, decimal)>>(new Dictionary<int, (long, decimal)>(_ledger));

    public Task<bool> KillSwitchEngagedAsync(CancellationToken ct) => Task.FromResult(false);

    public Task<IReadOnlyList<(long Id, string Action, int? Sleeve, string? Symbol)>> ActionsAfterAsync(long lastId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(long, string, int?, string?)>>([]);

    public Task<IReadOnlyDictionary<long, RiskConfigDocument>> RiskVersionsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<long, RiskConfigDocument>>(new Dictionary<long, RiskConfigDocument>());

    public Task AddStepAsync(PaperRun run, StepWrite step, CancellationToken ct)
    {
        _steps.Add(new StepRecord(step.BarTime, step.Inputs, step.StateHash));
        Written.Add(step);
        foreach (var fill in step.Fills)
        {
            var (fills, cash) = _ledger.GetValueOrDefault(fill.Sleeve);
            _ledger[fill.Sleeve] = (fills + 1, cash + (fill.Buy ? -fill.Notional : fill.Notional) - fill.Fee);
        }

        return Task.CompletedTask;
    }

    public Task AddReconciliationAsync(long runId, DateTimeOffset? barTime, bool ok, object detail, CancellationToken ct)
    {
        Reconciliations.Add((ok, detail));
        return Task.CompletedTask;
    }

    public Task AuditAsync(string actor, string @event, object payload, CancellationToken ct)
    {
        Audit.Add((@event, payload));
        return Task.CompletedTask;
    }
}
