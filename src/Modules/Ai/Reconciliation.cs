using System.Globalization;
using System.Text;

namespace PortfolioIntelligence.Ai;

/// <summary>The provider invoice the user reads from the billing console (TRY) and the exchange rate used.</summary>
public sealed record InvoiceInput(decimal AmountTry, decimal UsdTryRate);

/// <summary>
/// Monthly reconciliation (ADR-0010): ledger totals at list prices by task class, job and day, compared with the
/// provider invoice. A difference above 10 % flags the price table for an update (the event is then audited).
/// </summary>
public sealed record ReconciliationReport(
    int Year,
    int Month,
    string PriceVersion,
    UsdAmount LedgerTotal,
    int Calls,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<(string Key, UsdAmount Amount)> ByTask,
    IReadOnlyList<(string Key, UsdAmount Amount)> ByJob,
    IReadOnlyList<(string Key, UsdAmount Amount)> ByDay,
    InvoiceInput? Invoice)
{
    public const decimal Tolerance = 0.10m;

    public UsdAmount? InvoiceUsd => Invoice is { UsdTryRate: > 0 } invoice ? new UsdAmount(invoice.AmountTry / invoice.UsdTryRate) : null;

    /// <summary>(invoice − ledger) / ledger; null without an invoice or with an empty ledger.</summary>
    public decimal? Difference => InvoiceUsd is { } usd && LedgerTotal.Value > 0 ? (usd.Value - LedgerTotal.Value) / LedgerTotal.Value : null;

    public bool PriceTableReviewNeeded => Difference is { } d ? Math.Abs(d) > Tolerance : InvoiceUsd is { Value: > 0 } && LedgerTotal.Value == 0;

    public static ReconciliationReport Build(IEnumerable<LedgerEntry> entries, int year, int month, string priceVersion, InvoiceInput? invoice)
    {
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);
        var all = entries.ToList();
        var settled = all.Where(e => e.Kind == LedgerEntryKind.Settlement).ToDictionary(e => e.CallId);
        var calls = LedgerMath.Committed(all)
            .Where(c => c.Reservation.At >= start && c.Reservation.At < end)
            .Select(c => (c.Reservation, c.Committed, Settlement: settled.GetValueOrDefault(c.Reservation.CallId)))
            .ToList();

        static IReadOnlyList<(string, UsdAmount)> Group<T>(IEnumerable<(LedgerEntry Reservation, UsdAmount Committed, LedgerEntry? Settlement)> items, Func<LedgerEntry, T> key) =>
            items.GroupBy(i => key(i.Reservation)).Select(g => (Convert.ToString(g.Key, CultureInfo.InvariantCulture) ?? "-", g.Aggregate(UsdAmount.Zero, (s, i) => s + i.Committed)))
                .OrderBy(g => g.Item1, StringComparer.Ordinal).ToList();

        return new ReconciliationReport(year, month, priceVersion,
            calls.Aggregate(UsdAmount.Zero, (s, c) => s + c.Committed),
            calls.Count,
            calls.Sum(c => c.Settlement?.InputTokens ?? 0),
            calls.Sum(c => c.Settlement?.OutputTokens ?? 0),
            Group(calls, r => r.Task?.ToString() ?? "(none)"),
            Group(calls, r => r.JobId),
            Group(calls, r => r.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            invoice);
    }

    public string ToMarkdown()
    {
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"# AI harcama mutabakatı — {Year:0000}-{Month:00}");
        s.AppendLine();
        s.AppendLine(CultureInfo.InvariantCulture, $"- Fiyat tablosu: `{PriceVersion}` (liste fiyatı, USD)");
        s.AppendLine(CultureInfo.InvariantCulture, $"- Ledger toplamı: **{LedgerTotal}** · çağrı {Calls} · girdi token {InputTokens} · çıktı token {OutputTokens}");
        if (Invoice is { } invoice)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"- Fatura: {invoice.AmountTry:0.00} TRY, kur {invoice.UsdTryRate:0.0000} → {InvoiceUsd}");
            s.AppendLine(Difference is { } d
                ? string.Create(CultureInfo.InvariantCulture, $"- Fark: {d:P1} → {(PriceTableReviewNeeded ? "**%10'u aşıyor: fiyat tablosu gözden geçirilmeli**" : "tolerans içinde")}")
                : $"- Fark: hesaplanamadı{(PriceTableReviewNeeded ? " → **ledger boş ama fatura var: gözden geçirilmeli**" : "")}");
        }
        else
        {
            s.AppendLine("- Fatura: girilmedi (mutabakat bekliyor)");
        }

        foreach (var (title, rows) in new[] { ("Görev sınıfı", ByTask), ("İş", ByJob), ("Gün", ByDay) })
        {
            s.AppendLine();
            s.AppendLine(CultureInfo.InvariantCulture, $"| {title} | Tutar |");
            s.AppendLine("|---|---|");
            foreach (var (key, amount) in rows)
            {
                s.AppendLine(CultureInfo.InvariantCulture, $"| {key} | {amount} |");
            }
        }

        return s.ToString();
    }
}
