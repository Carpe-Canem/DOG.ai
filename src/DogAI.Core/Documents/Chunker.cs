using System.Text.RegularExpressions;

namespace DogAI.Core.Documents;

/// <summary>A passage cut from a document: where it is, which page and heading it falls under.</summary>
public sealed record Chunk(int Start, int Length, int? Page, string? Heading, string Text);

/// <summary>
/// Cuts a document into overlapping passages of a few hundred words, at paragraph boundaries where possible, so each
/// passage can be searched and quoted on its own. A heading starts a new passage, and every passage knows the heading
/// it falls under. Sizes are in characters (about four to a token), well inside what the search model reads at once.
/// </summary>
public static partial class Chunker
{
    /// <summary>Aim for passages about this long (roughly 300 tokens).</summary>
    public const int Target = 1200;

    /// <summary>Never longer than this; one character per token still fits the search model's window.</summary>
    public const int Max = 1500;

    /// <summary>How much of the end of one passage is repeated at the start of the next, so a sentence cut in two is still found.</summary>
    public const int Overlap = 300;

    public static IReadOnlyList<Chunk> Split(ExtractedText document)
    {
        var text = document.Text;
        var pieces = Pieces(text, document.HasMarkdownHeadings);
        var chunks = new List<Chunk>();
        var i = 0;
        while (i < pieces.Count)
        {
            var start = pieces[i].Start;
            var end = pieces[i].End;
            var j = i + 1;
            // A heading belongs with what follows it, so a passage never ends on one.
            while (j < pieces.Count)
            {
                var next = pieces[j];
                if (next.IsHeading && end - start >= Target / 3 && !pieces[j - 1].IsHeading) break;
                if (next.End - start > Target && !pieces[j - 1].IsHeading) break;
                if (next.End - start > Max) break;
                end = next.End;
                j++;
            }
            var body = text[start..end].Trim();
            if (body.Length > 0)
                chunks.Add(new Chunk(start, end - start, document.PageAt(start), HeadingAt(text, pieces, start, document.HasMarkdownHeadings), body));
            if (j >= pieces.Count) break;

            // Start the next passage a little before this one ended, at a piece boundary, unless a heading starts there.
            var back = j;
            while (back - 1 > i && !pieces[j].IsHeading && end - pieces[back - 1].Start <= Overlap) back--;
            i = back > i ? back : j;
        }
        return chunks;
    }

    private readonly record struct Piece(int Start, int End, bool IsHeading);

    /// <summary>Paragraphs, with over-long ones cut at sentence ends (or at spaces, as a last resort).</summary>
    private static List<Piece> Pieces(string text, bool markdownHeadings)
    {
        var pieces = new List<Piece>();
        foreach (Match m in Paragraph().Matches(text))
        {
            var start = m.Index;
            var end = m.Index + m.Length;
            var isHeading = markdownHeadings && Heading().IsMatch(m.Value.Split('\n')[0]);
            if (end - start <= Target)
            {
                pieces.Add(new Piece(start, end, isHeading));
                continue;
            }
            // Long paragraph: split into sentences, then pack them up to the target size.
            var pos = start;
            while (pos < end)
            {
                var limit = Math.Min(end, pos + Target);
                var cut = limit;
                if (limit < end)
                {
                    cut = LastBreak(text, pos, limit, SentenceEnd()) ?? LastBreak(text, pos, limit, Space()) ?? limit;
                }
                pieces.Add(new Piece(pos, cut, false));
                pos = cut;
                while (pos < end && char.IsWhiteSpace(text[pos])) pos++;
            }
        }
        return pieces;
    }

    /// <summary>The last place in [from, to) just after a match of <paramref name="pattern"/>, past the first third.</summary>
    private static int? LastBreak(string text, int from, int to, Regex pattern)
    {
        int? best = null;
        foreach (Match m in pattern.Matches(text[from..to]))
        {
            var at = from + m.Index + m.Length;
            if (at - from >= (to - from) / 3) best = at;
        }
        return best;
    }

    /// <summary>The nearest heading at or before <paramref name="offset"/>, with its parents ("Setup › Windows").</summary>
    private static string? HeadingAt(string text, List<Piece> pieces, int offset, bool markdownHeadings)
    {
        if (!markdownHeadings) return null;
        var trail = new List<(int Level, string Title)>();
        foreach (var piece in pieces)
        {
            if (piece.Start > offset) break;
            if (!piece.IsHeading) continue;
            var line = text[piece.Start..piece.End].Split('\n')[0];
            var m = Heading().Match(line);
            if (!m.Success) continue;
            var level = m.Groups[1].Value.Length;
            trail.RemoveAll(h => h.Level >= level);
            trail.Add((level, m.Groups[2].Value.Trim().TrimEnd('#').Trim()));
        }
        return trail.Count == 0 ? null : string.Join(" › ", trail.Select(h => h.Title));
    }

    [GeneratedRegex(@"[^\n]+(?:\n[^\n]+)*")]
    private static partial Regex Paragraph();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"[.!?。！？][""')\]]*\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
