using System.Globalization;
using System.Text;

namespace PortfolioIntelligence.SourceGateway.Text;

/// <summary>
/// Normalised text and, for every character, its offset in the original text (so a verified quote maps back to the source).
/// <see cref="Removed"/> counts deleted control, zero-width and bidi characters and soft hyphens.
/// </summary>
public sealed record NormalizedText(string Text, IReadOnlyList<int> OriginalOffsets, int Removed)
{
    /// <summary>The text with paragraph breaks as spaces; same length and offsets. Quote matching uses this form.</summary>
    public string Flat => Text.Replace('\n', ' ');

    /// <summary>Original span [start, end) of the normalised span [start, start + length).</summary>
    public (int Start, int End) OriginalSpan(int start, int length) =>
        (OriginalOffsets[start], OriginalOffsets[start + length - 1] + 1);
}

/// <summary>
/// <c>text-normalizer v1</c> (m7-protocol §3), applied both to the Extractor input and to quote verification so both see
/// the same text:
/// <list type="bullet">
/// <item>compatibility forms per character (ligatures, fullwidth ASCII, Unicode spaces, ellipsis, superscript digits) with an
/// explicit table: the build uses invariant globalisation, where <c>string.Normalize</c> has no NFKC data (ADR-0022 note);</item>
/// <item>C0/C1 controls (except line breaks and tabs), zero-width characters, bidi controls and soft hyphens removed;</item>
/// <item>dash variants become <c>-</c>, quote variants <c>"</c> and <c>'</c>;</item>
/// <item>a line-end hyphen followed by a lower-case letter on the next line is joined (hyphenation);</item>
/// <item>whitespace runs become one space, or a paragraph break (a blank line) when the run holds one; the result is a fixed point.</item>
/// </list>
/// </summary>
public static class TextNormalizer
{
    public const string Version = "text-normalizer v1";

    public static NormalizedText Normalize(string original)
    {
        // Pass 1: per-character mapping, keeping the original offset of every produced character.
        var units = new List<(char C, int At)>(original.Length);
        var removed = 0;
        for (var i = 0; i < original.Length; i++)
        {
            var c = original[i];
            if (char.IsHighSurrogate(c) && i + 1 < original.Length && char.IsLowSurrogate(original[i + 1]))
            {
                units.Add((original[i], i));
                units.Add((original[i + 1], i + 1));
                i++;
                continue;
            }

            if (IsRemoved(c))
            {
                removed++;
                continue;
            }

            var mapped = Map(c);
            if (mapped is { } single)
            {
                units.Add((single, i));
                continue;
            }

            foreach (var n in Compatibility(c))
            {
                units.Add((n, i));
            }
        }

        // Pass 2: hyphenation joins and whitespace runs.
        var text = new StringBuilder(units.Count);
        var offsets = new List<int>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var (c, at) = units[i];
            if (c == '-' && TryHyphenation(units, i, out var next))
            {
                i = next - 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                var newlines = 0;
                var j = i;
                for (; j < units.Count && char.IsWhiteSpace(units[j].C); j++)
                {
                    newlines += units[j].C == '\n' ? 1 : 0;
                }

                if (text.Length > 0 && j < units.Count)
                {
                    // A blank line stays a paragraph break ("\n\n"), so normalising normalised text changes nothing.
                    text.Append(newlines >= 2 ? "\n\n" : " ");
                    offsets.Add(at);
                    if (newlines >= 2)
                    {
                        offsets.Add(at);
                    }
                }

                i = j - 1;
                continue;
            }

            text.Append(c);
            offsets.Add(at);
        }

        return new NormalizedText(text.ToString(), offsets, removed);
    }

    private static bool TryHyphenation(List<(char C, int At)> units, int i, out int next)
    {
        // "-" then horizontal space, one line break, horizontal space, then a lower-case letter: join the word.
        next = i + 1;
        var sawBreak = false;
        while (next < units.Count && char.IsWhiteSpace(units[next].C))
        {
            if (units[next].C == '\n')
            {
                if (sawBreak)
                {
                    return false;
                }

                sawBreak = true;
            }

            next++;
        }

        return sawBreak && i > 0 && char.IsLetter(units[i - 1].C) && next < units.Count && char.IsLower(units[next].C);
    }

    private static bool IsRemoved(char c)
    {
        if (c is '\n' or '\t' or '\r')
        {
            return false;
        }

        return char.IsControl(c)
            || c is '­' or '​' or '‌' or '‍' or '‎' or '‏' or '⁠' or '﻿'
            || c is >= '‪' and <= '‮'
            || c is >= '⁦' and <= '⁩'
            || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;
    }

    /// <summary>The compatibility decompositions that occur in extracted papers; any other character is kept as it is.</summary>
    private static string Compatibility(char c) => c switch
    {
        'ﬀ' => "ff",
        'ﬁ' => "fi",
        'ﬂ' => "fl",
        'ﬃ' => "ffi",
        'ﬄ' => "ffl",
        'ﬅ' or 'ﬆ' => "st",
        >= '！' and <= '～' => ((char)(c - 0xFEE0)).ToString(),
        ' ' or (>= ' ' and <= ' ') or ' ' or ' ' or '　' => " ",
        '․' => ".",
        '‥' => "..",
        '…' => "...",
        '¹' => "1",
        '²' => "2",
        '³' => "3",
        '⁰' => "0",
        >= '⁴' and <= '⁹' => ((char)('4' + (c - '⁴'))).ToString(),
        _ => c.ToString(),
    };

    private static char? Map(char c) => c switch
    {
        '\r' => ' ',
        >= '‐' and <= '―' or '−' => '-',
        '“' or '”' or '„' or '‟' or '«' or '»' or '″' => '"',
        '‘' or '’' or '‚' or '‛' or '′' => '\'',
        _ => null,
    };
}
