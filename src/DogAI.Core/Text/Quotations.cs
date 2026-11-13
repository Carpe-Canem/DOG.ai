using System.Text;

namespace DogAI.Core.Text;

/// <summary>A run of one paragraph: narration, or a quotation (its marks included).</summary>
public readonly record struct QuotePart(string Text, bool Quoted);

/// <summary>
/// Finds quotations in prose. The rules are shared by the speech colours (<see cref="MarkdownRenderer"/>, which reads
/// rendered HTML) and by reading aloud (which reads plain text), so both find the same quotations in a paragraph and
/// a line gets the same speaker in its colour and its voice.
/// </summary>
public static class Quotations
{
    /// <summary>How a straight quotation mark appears in rendered HTML.</summary>
    internal const string Entity = "&quot;";

    /// <summary>The quotation mark at <paramref name="i"/> and how many characters it takes, or null.</summary>
    /// <param name="html">The text is rendered HTML, where a straight mark is written as an entity.</param>
    internal static (char Mark, int Length)? MarkAt(string text, int i, bool html) => text[i] switch
    {
        '“' or '”' or '«' or '»' => (text[i], 1),
        '"' when !html => ('"', 1),
        '&' when html && string.CompareOrdinal(text, i, Entity, 0, Entity.Length) == 0 => ('"', Entity.Length),
        _ => null,
    };

    /// <summary>The mark that ends a quotation opened by the mark at <paramref name="i"/>, or null when it opens none.</summary>
    internal static char? CloserFor(char mark, string text, int i, int length) => mark switch
    {
        '“' => '”',
        '«' => '»',
        '"' when OpensStraight(text, i, length) => '"',
        _ => null,
    };

    /// <summary>Models mix curly and straight marks, so either one ends a quotation opened with the other.</summary>
    internal static bool Ends(char closer, char mark) => closer == '»' ? mark == '»' : mark is '”' or '"';

    /// <summary>A straight mark starts speech only at the start of a word, so 5" tall and stray closing marks stay plain.</summary>
    private static bool OpensStraight(string text, int i, int length)
    {
        var after = i + length;
        if (after < text.Length && char.IsWhiteSpace(text[after])) return false;
        return i == 0 || char.IsWhiteSpace(text[i - 1]) || "([{—–-".Contains(text[i - 1]);
    }

    /// <summary>Cuts one paragraph of plain text into narration and quotations. A quotation left open ends with the paragraph.</summary>
    public static IReadOnlyList<QuotePart> Split(string paragraph)
    {
        var parts = new List<QuotePart>();
        var run = new StringBuilder();
        char? closer = null;
        var i = 0;
        while (i < paragraph.Length)
        {
            if (MarkAt(paragraph, i, html: false) is not { } found)
            {
                run.Append(paragraph[i++]);
                continue;
            }
            var mark = paragraph.Substring(i, found.Length);
            if (closer is null)
            {
                if (CloserFor(found.Mark, paragraph, i, found.Length) is { } end)
                {
                    if (run.Length > 0) parts.Add(new QuotePart(run.ToString(), false));
                    run.Clear();
                    closer = end;
                }
                run.Append(mark);
            }
            else
            {
                run.Append(mark);
                if (Ends(closer.Value, found.Mark))
                {
                    parts.Add(new QuotePart(run.ToString(), true));
                    run.Clear();
                    closer = null;
                }
            }
            i += found.Length;
        }
        if (run.Length > 0 || closer is not null) parts.Add(new QuotePart(run.ToString(), closer is not null));
        return parts;
    }
}
