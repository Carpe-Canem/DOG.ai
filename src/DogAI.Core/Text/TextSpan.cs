using System.Text;
using System.Text.RegularExpressions;

namespace DogAI.Core.Text;

/// <summary>
/// Finds text selected on screen inside its markdown source. What is selected is the rendered text, so emphasis
/// marks are missing from it and line breaks may differ; the search allows for both.
/// </summary>
public static class TextSpan
{
    /// <summary>Where <paramref name="selection"/> sits in <paramref name="source"/>, or null when it cannot be found.</summary>
    public static (int Start, int Length)? Find(string source, string selection)
    {
        selection = selection.Trim();
        if (selection.Length == 0) return null;
        var exact = source.IndexOf(selection, StringComparison.Ordinal);
        if (exact >= 0) return (exact, selection.Length);

        var words = selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Between words: any white space, with markdown emphasis or quote marks that the screen does not show.
        const string marks = @"[*_~`>#]*";
        var pattern = new StringBuilder(marks);
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0) pattern.Append(marks).Append(@"\s+").Append(marks);
            // Inside a word, emphasis marks may sit between any two characters ("un*believ*able").
            pattern.Append(string.Join(@"[*_~`]*", words[i].Select(c => Regex.Escape(c.ToString()))));
        }
        pattern.Append(@"[*_~`]*");
        try
        {
            var match = Regex.Match(source, pattern.ToString(), RegexOptions.None, TimeSpan.FromMilliseconds(200));
            return match.Success ? (match.Index, match.Length) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
