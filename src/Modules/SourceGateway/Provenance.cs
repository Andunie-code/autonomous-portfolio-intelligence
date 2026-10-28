using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>What the Extractor reads of a document: the PDF text, or the API abstract when no PDF may be fetched (U-M7-6).</summary>
public enum DocumentText
{
    Pdf,
    Abstract,
    Html,
}

/// <summary>
/// Provenance of one source document (ADR-0022 Â§7, sources.source_documents): what was fetched, when, its content and text
/// hashes, the arXiv version and its dates (both before C for arXiv, U-M7-2; also a database CHECK), the extractor and
/// normaliser versions, and how many characters were dropped as hidden or removed by the normaliser.
/// </summary>
public sealed record SourceDocument(
    long Id,
    string Url,
    string CanonicalUrl,
    SourceKind Kind,
    TrustTier Tier,
    DocumentText TextKind,
    string? ArxivId,
    int? ArxivVersion,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? VersionAt,
    DateTimeOffset FetchedAt,
    string ContentSha256,
    string TextSha256,
    long Bytes,
    string ContentType,
    string ExtractorVersion,
    string NormalizerVersion,
    int HiddenCharacters,
    int RemovedCharacters,
    bool Truncated)
{
    /// <summary>Hidden text or removed control characters were found; the flag travels with every finding (m7-protocol Â§3).</summary>
    public bool Suspicious => HiddenCharacters > 0 || RemovedCharacters > 0;

    public bool IsBeforeCutoff => Kind is not (SourceKind.ArxivApi or SourceKind.ArxivPdf or SourceKind.ArxivAbstract)
        || (PublishedAt < ResearchCutoff.C && VersionAt < ResearchCutoff.C);
}

/// <summary>
/// One finding (sources.findings): the Extractor's claim and quote with the deterministic verdict. Rejected findings are
/// kept with their reason; only verified ones reach the Researcher or a hypothesis (m7-protocol Â§4.3).
/// </summary>
public sealed record Finding(
    long Id,
    long DocumentId,
    Guid TraceId,
    string Kind,
    string Claim,
    string Quote,
    int? QuoteStart,
    int? QuoteEnd,
    IReadOnlyList<string> Numbers,
    string Relevance,
    bool Verified,
    string? RejectionReason);

/// <summary>A verified finding as the Researcher sees it: no raw text, body or URL content (m7-protocol Â§8.1).</summary>
public sealed record FindingView(long Id, string Kind, string Claim, string Quote, string Source, TrustTier Tier, DateTimeOffset? VersionAt, bool Suspicious);

/// <summary>A research request recorded by the Researcher's RequestSourceResearch tool; processed only by the user's run.</summary>
public sealed record ResearchRequest(long Id, DateTimeOffset At, string Topic, IReadOnlyList<string> Categories, string Status);

public interface ISourceStore
{
    Task<long> AddDocumentAsync(SourceDocument document, CancellationToken ct);

    Task<IReadOnlyList<SourceDocument>> DocumentsAsync(CancellationToken ct);

    Task<long> AddFindingAsync(Finding finding, CancellationToken ct);

    Task<IReadOnlyList<Finding>> FindingsAsync(long documentId, CancellationToken ct);

    /// <summary>Verified findings whose claim or quote contains every term (case-insensitive), newest first.</summary>
    Task<IReadOnlyList<FindingView>> SearchVerifiedAsync(IReadOnlyList<string> terms, string? kind, int limit, CancellationToken ct);

    /// <summary>The subset of <paramref name="findingIds"/> that exist, are verified and come from a pre-cutoff document, with its latest version date.</summary>
    Task<(IReadOnlySet<long> Valid, DateTimeOffset? LatestVersionAt)> UsableForHypothesisAsync(IReadOnlyList<long> findingIds, CancellationToken ct);

    Task<long> AddResearchRequestAsync(string topic, IReadOnlyList<string> categories, string status, CancellationToken ct);
}

public sealed class InMemorySourceStore : ISourceStore
{
    private readonly List<SourceDocument> _documents = [];
    private readonly List<Finding> _findings = [];
    private readonly List<ResearchRequest> _requests = [];

    public IReadOnlyList<ResearchRequest> Requests => _requests.ToList();

    public Task<long> AddDocumentAsync(SourceDocument document, CancellationToken ct)
    {
        if (!document.IsBeforeCutoff)
        {
            throw new InvalidOperationException("source_documents: an arXiv version on or after the research cutoff is refused.");
        }

        var id = _documents.Count + 1L;
        _documents.Add(document with { Id = id });
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<SourceDocument>> DocumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceDocument>>(_documents.ToList());

    public Task<long> AddFindingAsync(Finding finding, CancellationToken ct)
    {
        var id = _findings.Count + 1L;
        _findings.Add(finding with { Id = id });
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<Finding>> FindingsAsync(long documentId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Finding>>(_findings.Where(f => f.DocumentId == documentId).ToList());

    public Task<IReadOnlyList<FindingView>> SearchVerifiedAsync(IReadOnlyList<string> terms, string? kind, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FindingView>>(_findings
            .Where(f => f.Verified && (kind is null || f.Kind == kind)
                        && terms.All(t => f.Claim.Contains(t, StringComparison.OrdinalIgnoreCase) || f.Quote.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Id).Take(limit)
            .Select(f => View(f, _documents.Single(d => d.Id == f.DocumentId))).ToList());

    public Task<(IReadOnlySet<long> Valid, DateTimeOffset? LatestVersionAt)> UsableForHypothesisAsync(IReadOnlyList<long> findingIds, CancellationToken ct)
    {
        var usable = _findings.Where(f => findingIds.Contains(f.Id) && f.Verified)
            .Select(f => (f.Id, Document: _documents.Single(d => d.Id == f.DocumentId)))
            .Where(x => x.Document.IsBeforeCutoff).ToList();
        return Task.FromResult<(IReadOnlySet<long>, DateTimeOffset?)>((usable.Select(x => x.Id).ToHashSet(), usable.Max(x => x.Document.VersionAt)));
    }

    public Task<long> AddResearchRequestAsync(string topic, IReadOnlyList<string> categories, string status, CancellationToken ct)
    {
        var id = _requests.Count + 1L;
        _requests.Add(new ResearchRequest(id, DateTimeOffset.UtcNow, topic, categories, status));
        return Task.FromResult(id);
    }

    internal static FindingView View(Finding f, SourceDocument d) =>
        new(f.Id, f.Kind, f.Claim, f.Quote, d.ArxivId is { } id ? $"arXiv:{id}v{d.ArxivVersion}" : d.CanonicalUrl, d.Tier, d.VersionAt, d.Suspicious);
}

/// <summary>sources.* (migration 0010, append-only) for the research_app login.</summary>
public sealed class NpgsqlSourceStore(string connectionString) : ISourceStore
{
    public async Task<long> AddDocumentAsync(SourceDocument d, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO sources.source_documents (url, canonical_url, kind, tier, text_kind, arxiv_id, arxiv_version, published_at, version_at, fetched_at,
                content_sha256, text_sha256, bytes, content_type, extractor_version, normalizer_version, hidden_characters, removed_characters, truncated)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19) RETURNING id
            """, connection);
        foreach (var value in new object?[] { d.Url, d.CanonicalUrl, d.Kind.ToString(), d.Tier.ToString(), d.TextKind.ToString(), d.ArxivId, d.ArxivVersion,
                     d.PublishedAt, d.VersionAt, d.FetchedAt, d.ContentSha256, d.TextSha256, d.Bytes, d.ContentType, d.ExtractorVersion, d.NormalizerVersion,
                     d.HiddenCharacters, d.RemovedCharacters, d.Truncated })
        {
            command.Parameters.AddWithValue(value ?? DBNull.Value);
        }

        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<SourceDocument>> DocumentsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT {DocumentColumns} FROM sources.source_documents ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SourceDocument>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadDocument(reader, 0));
        }

        return result;
    }

    public async Task<long> AddFindingAsync(Finding f, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO sources.findings (document_id, trace_id, kind, claim, quote, quote_start, quote_end, numbers, relevance, verified, rejection_reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id
            """, connection);
        foreach (var value in new object?[] { f.DocumentId, f.TraceId, f.Kind, f.Claim, f.Quote, f.QuoteStart, f.QuoteEnd, JsonSerializer.Serialize(f.Numbers), f.Relevance, f.Verified, f.RejectionReason })
        {
            command.Parameters.AddWithValue(value ?? DBNull.Value);
        }

        command.Parameters[7].NpgsqlDbType = NpgsqlDbType.Jsonb;
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<Finding>> FindingsAsync(long documentId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, document_id, trace_id, kind, claim, quote, quote_start, quote_end, numbers::text, relevance, verified, rejection_reason
            FROM sources.findings WHERE document_id = $1 ORDER BY id
            """, connection);
        command.Parameters.AddWithValue(documentId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<Finding>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new Finding(reader.GetInt64(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(8))!, reader.GetString(9), reader.GetBoolean(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return result;
    }

    public async Task<IReadOnlyList<FindingView>> SearchVerifiedAsync(IReadOnlyList<string> terms, string? kind, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand($"""
            SELECT f.id, f.kind, f.claim, f.quote, {DocumentColumnsOf("d")}
            FROM sources.findings f JOIN sources.source_documents d ON d.id = f.document_id
            WHERE f.verified AND ($1::text IS NULL OR f.kind = $1)
              AND NOT EXISTS (SELECT 1 FROM unnest($2::text[]) t WHERE strpos(lower(f.claim), lower(t)) = 0 AND strpos(lower(f.quote), lower(t)) = 0)
            ORDER BY f.id DESC LIMIT $3
            """, connection);
        command.Parameters.AddWithValue((object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue(terms.ToArray());
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FindingView>();
        while (await reader.ReadAsync(ct))
        {
            var document = ReadDocument(reader, 4);
            var finding = new Finding(reader.GetInt64(0), document.Id, Guid.Empty, reader.GetString(1), reader.GetString(2), reader.GetString(3), null, null, [], "", true, null);
            result.Add(InMemorySourceStore.View(finding, document));
        }

        return result;
    }

    public async Task<(IReadOnlySet<long> Valid, DateTimeOffset? LatestVersionAt)> UsableForHypothesisAsync(IReadOnlyList<long> findingIds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT f.id, d.version_at FROM sources.findings f JOIN sources.source_documents d ON d.id = f.document_id
            WHERE f.id = ANY($1) AND f.verified AND (d.arxiv_id IS NULL OR (d.published_at < $2 AND d.version_at < $2))
            """, connection);
        command.Parameters.AddWithValue(findingIds.ToArray());
        command.Parameters.AddWithValue(ResearchCutoff.C);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var valid = new HashSet<long>();
        DateTimeOffset? latest = null;
        while (await reader.ReadAsync(ct))
        {
            valid.Add(reader.GetInt64(0));
            if (!reader.IsDBNull(1) && (latest is null || reader.GetFieldValue<DateTimeOffset>(1) > latest))
            {
                latest = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        return (valid, latest);
    }

    public async Task<long> AddResearchRequestAsync(string topic, IReadOnlyList<string> categories, string status, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO sources.research_requests (topic, categories, status) VALUES ($1, $2, $3) RETURNING id", connection);
        command.Parameters.AddWithValue(topic);
        command.Parameters.AddWithValue(categories.ToArray());
        command.Parameters.AddWithValue(status);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private const string DocumentColumns = """
        id, url, canonical_url, kind, tier, text_kind, arxiv_id, arxiv_version, published_at, version_at, fetched_at, content_sha256, text_sha256,
        bytes, content_type, extractor_version, normalizer_version, hidden_characters, removed_characters, truncated
        """;

    private static string DocumentColumnsOf(string alias) =>
        string.Join(", ", DocumentColumns.Split(',', StringSplitOptions.TrimEntries).Select(c => alias + "." + c));

    private static SourceDocument ReadDocument(NpgsqlDataReader r, int o) => new(
        r.GetInt64(o), r.GetString(o + 1), r.GetString(o + 2), Enum.Parse<SourceKind>(r.GetString(o + 3)), Enum.Parse<TrustTier>(r.GetString(o + 4)),
        Enum.Parse<DocumentText>(r.GetString(o + 5)), r.IsDBNull(o + 6) ? null : r.GetString(o + 6), r.IsDBNull(o + 7) ? null : r.GetInt32(o + 7),
        r.IsDBNull(o + 8) ? null : r.GetFieldValue<DateTimeOffset>(o + 8), r.IsDBNull(o + 9) ? null : r.GetFieldValue<DateTimeOffset>(o + 9),
        r.GetFieldValue<DateTimeOffset>(o + 10), r.GetString(o + 11), r.GetString(o + 12), r.GetInt64(o + 13), r.GetString(o + 14), r.GetString(o + 15),
        r.GetString(o + 16), r.GetInt32(o + 17), r.GetInt32(o + 18), r.GetBoolean(o + 19));

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
