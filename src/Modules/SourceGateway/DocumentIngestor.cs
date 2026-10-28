using System.Text;
using PortfolioIntelligence.SourceGateway.Text;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>A recorded document and the normalised text the Extractor and the quote verifier both use.</summary>
public sealed record IngestedDocument(SourceDocument Document, NormalizedText Text, string? PdfSkippedReason);

/// <summary>
/// Turns a pre-cutoff arXiv record into a recorded <see cref="SourceDocument"/> (m7-protocol §2.5, §3). The PDF of exactly
/// that version is used when robots.txt allows it; otherwise (or if it cannot be read) the API abstract is used (U-M7-6).
/// The text is normalised and hashed; the record goes to the store, whose database CHECK refuses a post-cutoff version.
/// </summary>
public sealed class DocumentIngestor(ArxivClient arxiv, ISourceStore store, SourceConfiguration configuration)
{
    public async Task<IngestedDocument> IngestAsync(ArxivEntry entry, FetchedSource feed, CancellationToken ct)
    {
        if (!entry.IsBeforeCutoff)
        {
            throw new SourcePolicyViolationException($"{entry.VersionedId} was submitted on or after the research cutoff; not recorded (U-M7-2).");
        }

        string? skipped = null;
        try
        {
            var pdf = await arxiv.GetPdfAsync(entry, ct);
            var extracted = PdfTextExtractor.Extract(pdf.Body, configuration.MaxPdfPages, configuration.MaxExtractedCharacters);
            return await RecordAsync(entry, pdf, DocumentText.Pdf, extracted, ct);
        }
        catch (Exception e) when (e is SourceUnavailableException or UnreadableDocumentException or SourceTooLargeException)
        {
            skipped = e.Message;
        }

        var abstractText = new ExtractedText(entry.Title + "\n\n" + entry.Summary, 0, 0, false, "arxiv-atom-abstract v1");
        var ingested = await RecordAsync(entry, feed, DocumentText.Abstract, abstractText, ct);
        return ingested with { PdfSkippedReason = skipped };
    }

    private async Task<IngestedDocument> RecordAsync(ArxivEntry entry, FetchedSource source, DocumentText kind, ExtractedText extracted, CancellationToken ct)
    {
        var normalized = TextNormalizer.Normalize(extracted.Text);
        var document = new SourceDocument(0, source.Uri.AbsoluteUri, entry.AbstractUri.AbsoluteUri, source.Target.Kind, source.Target.Source.Tier, kind,
            entry.Id, entry.Version, entry.Published, entry.Updated, source.FetchedAt, source.ContentSha256,
            SourceCache.Sha256(Encoding.UTF8.GetBytes(normalized.Text)), source.Body.LongLength, source.ContentType ?? "", extracted.ExtractorVersion,
            TextNormalizer.Version, extracted.Hidden, normalized.Removed, extracted.Truncated);
        var id = await store.AddDocumentAsync(document, ct);
        return new IngestedDocument(document with { Id = id }, normalized, null);
    }
}
