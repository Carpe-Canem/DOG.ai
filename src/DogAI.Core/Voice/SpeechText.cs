using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DogAI.Core.Voice;

/// <summary>
/// Turns a reply's markdown into the words to say: the text of paragraphs, headings, lists and quotes, without
/// markup symbols, code blocks, citation numbers or web addresses.
/// </summary>
public static partial class SpeechText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    /// <summary>Short inline code (a name, a key) is read out; longer code is skipped.</summary>
    private const int SpokenCodeLength = 32;

    public static string ForSpeech(string? markdown) => Tidy(string.Join(" ", Lines(markdown)));

    /// <summary>The words to say, one entry per paragraph, heading, list item or table row.</summary>
    public static IReadOnlyList<string> Paragraphs(string? markdown) =>
        Lines(markdown).Select(Tidy).Where(line => line.Length > 0).ToList();

    private static List<string> Lines(string? markdown)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown)) return lines;
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var block in document) AppendBlock(block, lines);
        return lines;
    }

    private static void AppendBlock(Block block, List<string> lines)
    {
        switch (block)
        {
            case CodeBlock:
            case ThematicBreakBlock:
                return;
            case Table table:
                foreach (var row in table.OfType<TableRow>())
                {
                    var cells = row.OfType<TableCell>().Select(c =>
                    {
                        var cell = new List<string>();
                        foreach (var inner in c) AppendBlock(inner, cell);
                        return Tidy(string.Join(" ", cell)).TrimEnd('.');
                    }).Where(c => c.Length > 0);
                    Sentence(lines, string.Join(", ", cells));
                }
                return;
            case LeafBlock leaf:
                if (leaf.Inline is null) return;
                var line = new StringBuilder();
                AppendInlines(leaf.Inline, line);
                Sentence(lines, line.ToString());
                return;
            case ContainerBlock container:
                foreach (var child in container) AppendBlock(child, lines);
                return;
        }
    }

    /// <summary>Ends each block like a sentence so the voice pauses between headings, list items and paragraphs.</summary>
    private static void Sentence(List<string> lines, string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        lines.Add(EndsSentence(line) ? line : line + ".");
    }

    private static bool EndsSentence(string line)
    {
        var last = line.TrimEnd('"', '\'', ')', '”', '’', '*', '_').LastOrDefault();
        return last is '.' or '!' or '?' or '…' or ':' or ';' or ',' or '。' or '！' or '？';
    }

    private static void AppendInlines(ContainerInline container, StringBuilder text)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    if (code.Content.Length <= SpokenCodeLength) text.Append(code.Content);
                    break;
                case AutolinkInline:
                    break;
                case LinkInline { IsImage: true }:
                    break;
                case LinkInline link:
                    AppendInlines(link, text);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.ToString());
                    break;
                case ContainerInline inner:
                    AppendInlines(inner, text);
                    break;
                default:
                    text.Append(inline.ToString());
                    break;
            }
        }
    }

    private static string Tidy(string text)
    {
        text = Url().Replace(text, "");
        text = Citation().Replace(text, "");
        // Markup a parser leaves behind in half-written text, and symbols voices read out letter by letter.
        text = Symbols().Replace(text, " ");
        text = Spaces().Replace(text, " ");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        return text.Trim();
    }

    // A web address, without the full stop or bracket that may follow it.
    [GeneratedRegex(@"\b(?:https?://|www\.)\S*[^\s.,;:!?)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\s?\[\d{1,3}\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"[*_~`#|<>\[\]{}\\^]+")]
    private static partial Regex Symbols();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([.,!?;:…])")]
    private static partial Regex SpaceBeforePunctuation();
}
