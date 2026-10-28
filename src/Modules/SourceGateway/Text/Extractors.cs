using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Util;

namespace PortfolioIntelligence.SourceGateway.Text;

/// <summary>Text taken from a document before normalisation; <see cref="Hidden"/> counts characters dropped as hidden.</summary>
public sealed record ExtractedText(string Text, int Hidden, int Pages, bool Truncated, string ExtractorVersion);

/// <summary>Raised for an unreadable, encrypted or oversized document; nothing from it is used.</summary>
public sealed class UnreadableDocumentException(string message) : Exception(message);

/// <summary>
/// PDF text with PdfPig (m7-protocol Â§3): at most the configured pages; encrypted or broken files are refused. Letters that
/// would not be visible are dropped and counted: invisible rendering modes, fonts under 1 pt, glyphs off the page and
/// near-white fill. Words are rebuilt from the kept letters and laid out top to bottom, left to right.
/// </summary>
public static class PdfTextExtractor
{
    public const string Version = "pdf-text v1 (PdfPig 0.1.16)";

    public static ExtractedText Extract(byte[] pdf, int maxPages, int maxCharacters)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = false, SkipMissingFonts = true });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new UnreadableDocumentException("PDF could not be opened: " + e.GetType().Name);
        }

        using (document)
        {
            if (document.IsEncrypted)
            {
                throw new UnreadableDocumentException("Encrypted PDFs are not read.");
            }

            var text = new StringBuilder();
            var hidden = 0;
            var pages = Math.Min(document.NumberOfPages, maxPages);
            for (var number = 1; number <= pages; number++)
            {
                Page page;
                try
                {
                    page = document.GetPage(number);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    throw new UnreadableDocumentException($"PDF page {number} could not be read: {e.GetType().Name}");
                }

                var visible = new List<Letter>(page.Letters.Count);
                foreach (var letter in page.Letters)
                {
                    if (IsVisible(letter, page))
                    {
                        visible.Add(letter);
                    }
                    else
                    {
                        hidden += letter.Value.Length;
                    }
                }

                AppendPage(text, visible);
                text.Append("\n\n");
                if (text.Length >= maxCharacters)
                {
                    return new ExtractedText(text.ToString(0, maxCharacters), hidden, number, true, Version);
                }
            }

            return new ExtractedText(text.ToString(), hidden, pages, document.NumberOfPages > maxPages, Version);
        }
    }

    public static bool IsVisible(Letter letter, Page page)
    {
        if (letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip)
        {
            return false;
        }

        if (letter.PointSize < 1)
        {
            return false;
        }

        var box = letter.BoundingBox;
        if (box.Right < 0 || box.Left > page.Width || box.Top < 0 || box.Bottom > page.Height)
        {
            return false;
        }

        var (r, g, b) = letter.FillColor?.ToRGBValues() ?? (0, 0, 0);
        return !(r >= 0.95 && g >= 0.95 && b >= 0.95);
    }

    private static void AppendPage(StringBuilder text, IReadOnlyList<Letter> letters)
    {
        var words = DefaultWordExtractor.Instance.GetWords(letters)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        // Lines by baseline (top first), words left to right.
        foreach (var line in words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.0)).OrderByDescending(g => g.Key))
        {
            text.Append(string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            text.Append('\n');
        }
    }
}

/// <summary>
/// HTML text with AngleSharp (m7-protocol Â§3): scripts, styles, templates, frames, SVG and elements hidden by attribute or
/// inline style are removed and counted; block elements become line breaks. External CSS is not applied (known limit).
/// </summary>
public static class HtmlTextExtractor
{
    public const string Version = "html-text v1 (AngleSharp 1.8.4)";

    private static readonly string[] Dropped = ["script", "style", "noscript", "template", "iframe", "svg", "object", "embed", "head"];

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "li", "ul", "ol", "table", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "br", "dd", "dt",
    };

    public static ExtractedText Extract(string html, int maxCharacters)
    {
        var document = new HtmlParser().ParseDocument(html);
        var hidden = 0;
        foreach (var element in document.QuerySelectorAll(string.Join(',', Dropped)).ToList())
        {
            hidden += element.TextContent.Length;
            element.Remove();
        }

        foreach (var element in document.All.Where(IsHidden).ToList())
        {
            if (element.Parent is not null)
            {
                hidden += element.TextContent.Length;
                element.Remove();
            }
        }

        var text = new StringBuilder();
        Walk(document.Body ?? document.DocumentElement, text);
        var truncated = text.Length > maxCharacters;
        return new ExtractedText(truncated ? text.ToString(0, maxCharacters) : text.ToString(), hidden, 1, truncated, Version);
    }

    public static bool IsHidden(IElement element)
    {
        if (element.HasAttribute("hidden") || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var style = (element.GetAttribute("style") ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return style.Contains("display:none", StringComparison.Ordinal) || style.Contains("visibility:hidden", StringComparison.Ordinal)
            || style.Contains("font-size:0", StringComparison.Ordinal) || style.Contains("opacity:0;", StringComparison.Ordinal) || style.EndsWith("opacity:0", StringComparison.Ordinal);
    }

    private static void Walk(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                text.Append(t.Data);
            }
            else if (child is IElement e)
            {
                var block = Blocks.Contains(e.LocalName);
                if (block)
                {
                    text.Append("\n\n");
                }

                Walk(e, text);
                if (block)
                {
                    text.Append("\n\n");
                }
            }
        }
    }
}
