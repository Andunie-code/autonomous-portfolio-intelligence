namespace PortfolioIntelligence.Ai;

public enum LedgerEntryKind
{
    Reservation,
    Settlement,
}

/// <summary>One append-only cost ledger row (ai.cost_ledger). Amounts in USD at list prices.</summary>
public sealed record LedgerEntry(
    DateTimeOffset At,
    Guid CallId,
    string JobId,
    JobKind JobKind,
    TaskClass? Task,
    LedgerEntryKind Kind,
    UsdAmount Amount,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    string PriceVersion);

public sealed record ReservationRequest(Guid CallId, JobContext Job, TaskClass? Task, string Provider, string Model, UsdAmount Amount, string PriceVersion);

public enum ReservationStatus
{
    Reserved,
    MonthlyCapExceeded,
    JobCapExceeded,
    CallCapExceeded,
}

/// <summary>Committed spend: a call counts its settlement when settled, otherwise its open reservation.</summary>
public sealed record SpendSummary(UsdAmount MonthToDate, UsdAmount Job, UsdAmount Window);

/// <summary>
/// Cost ledger with reservation (ADR-0010 T9): a call reserves <see cref="IAiProvider.EstimateMaxCost"/> before it is
/// sent and is settled with the actual list-price cost afterwards. Cap checks and the reservation happen atomically.
/// </summary>
public interface ICostLedger
{
    Task<ReservationStatus> TryReserveAsync(ReservationRequest request, UsdAmount monthlyCap, UsdAmount jobCap, UsdAmount callCap, DateTimeOffset now, CancellationToken ct);

    Task SettleAsync(Guid callId, UsdAmount actual, AiUsage usage, DateTimeOffset now, CancellationToken ct);

    Task<SpendSummary> SummaryAsync(DateTimeOffset now, string jobId, TimeSpan window, CancellationToken ct);

    Task<IReadOnlyList<LedgerEntry>> EntriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public static class LedgerMath
{
    public static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Committed amount per call: settlement if present, else reservation.</summary>
    public static IEnumerable<(LedgerEntry Reservation, UsdAmount Committed)> Committed(IEnumerable<LedgerEntry> entries)
    {
        var list = entries.ToList();
        var settled = list.Where(e => e.Kind == LedgerEntryKind.Settlement).ToDictionary(e => e.CallId, e => e.Amount);
        foreach (var reservation in list.Where(e => e.Kind == LedgerEntryKind.Reservation))
        {
            yield return (reservation, settled.TryGetValue(reservation.CallId, out var actual) ? actual : reservation.Amount);
        }
    }

    public static SpendSummary Summary(IEnumerable<LedgerEntry> entries, DateTimeOffset now, string jobId, TimeSpan window)
    {
        var committed = Committed(entries).ToList();
        var month = MonthStart(now);
        return new SpendSummary(
            Sum(committed.Where(c => c.Reservation.At >= month)),
            Sum(committed.Where(c => c.Reservation.JobId == jobId)),
            Sum(committed.Where(c => c.Reservation.At > now - window)));
    }

    public static ReservationStatus Check(SpendSummary current, UsdAmount amount, UsdAmount monthlyCap, UsdAmount jobCap, UsdAmount callCap)
    {
        if (amount > callCap)
        {
            return ReservationStatus.CallCapExceeded;
        }

        if (current.MonthToDate + amount > monthlyCap)
        {
            return ReservationStatus.MonthlyCapExceeded;
        }

        return current.Job + amount > jobCap ? ReservationStatus.JobCapExceeded : ReservationStatus.Reserved;
    }

    private static UsdAmount Sum(IEnumerable<(LedgerEntry Reservation, UsdAmount Committed)> items) =>
        items.Aggregate(UsdAmount.Zero, (total, item) => total + item.Committed);
}

/// <summary>In-memory ledger for tests and dry runs.</summary>
public sealed class InMemoryCostLedger : ICostLedger
{
    private readonly List<LedgerEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LedgerEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public Task<ReservationStatus> TryReserveAsync(ReservationRequest request, UsdAmount monthlyCap, UsdAmount jobCap, UsdAmount callCap, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var status = LedgerMath.Check(LedgerMath.Summary(_entries, now, request.Job.JobId, TimeSpan.Zero), request.Amount, monthlyCap, jobCap, callCap);
            if (status == ReservationStatus.Reserved)
            {
                _entries.Add(new LedgerEntry(now, request.CallId, request.Job.JobId, request.Job.Kind, request.Task, LedgerEntryKind.Reservation,
                    request.Amount, request.Provider, request.Model, 0, 0, request.PriceVersion));
            }

            return Task.FromResult(status);
        }
    }

    public Task SettleAsync(Guid callId, UsdAmount actual, AiUsage usage, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var reservation = _entries.Single(e => e.CallId == callId && e.Kind == LedgerEntryKind.Reservation);
            if (_entries.Any(e => e.CallId == callId && e.Kind == LedgerEntryKind.Settlement))
            {
                throw new InvalidOperationException($"Call {callId} is already settled.");
            }

            _entries.Add(reservation with { At = now, Kind = LedgerEntryKind.Settlement, Amount = actual, InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens });
        }

        return Task.CompletedTask;
    }

    public Task<SpendSummary> SummaryAsync(DateTimeOffset now, string jobId, TimeSpan window, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(LedgerMath.Summary(_entries, now, jobId, window));
        }
    }

    public Task<IReadOnlyList<LedgerEntry>> EntriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LedgerEntry>>(_entries.Where(e => e.At >= from && e.At < to).ToList());
        }
    }
}
