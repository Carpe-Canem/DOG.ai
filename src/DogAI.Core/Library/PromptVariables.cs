using System.Text.RegularExpressions;

namespace DogAI.Core.Library;

/// <summary>One blank in a saved prompt.</summary>
public sealed record PromptVariable(string Name, string Default);

/// <summary>
/// Blanks in a saved prompt are written [[name]] or [[name|default text]]. Double square brackets were chosen because
/// character cards already use {{char}} and {{user}}, and single braces and brackets turn up in code and markdown.
/// </summary>
public static partial class PromptVariables
{
    [GeneratedRegex(@"\[\[\s*([^\[\]|\r\n]{1,60}?)\s*(?:\|([^\[\]\r\n]*))?\]\]")]
    private static partial Regex Blank();

    /// <summary>The distinct blanks in the order they first appear. A repeated name keeps its first default.</summary>
    public static IReadOnlyList<PromptVariable> Find(string text)
    {
        var found = new List<PromptVariable>();
        foreach (Match m in Blank().Matches(text))
        {
            var name = m.Groups[1].Value.Trim();
            if (found.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new PromptVariable(name, m.Groups[2].Success ? m.Groups[2].Value.Trim() : ""));
        }
        return found;
    }

    /// <summary>The prompt with each blank replaced by its value. A blank with no value takes its default, else becomes empty.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        // A name written several times takes the default of its first appearance, as <see cref="Find"/> reports it.
        var defaults = Find(text);
        return Blank().Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            foreach (var (key, value) in values)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value.Length > 0) return value;
            return defaults.First(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Default;
        });
    }
}
