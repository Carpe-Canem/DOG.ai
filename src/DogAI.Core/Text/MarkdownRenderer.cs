using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DogAI.Core.Text;

/// <summary>
/// A piece of a rendered message: either ready-made HTML, or a code block kept as plain text
/// so the UI can draw it with its own copy button.
/// </summary>
public sealed record MarkdownSegment(string? Html, string? Code, string? Language)
{
    public bool IsCode => Code is not null;
}

/// <summary>
/// Turns markdown from a model (untrusted text) into safe HTML. Raw HTML in the source is shown
/// as text, never passed through, and links can only point at http, https or mailto addresses.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    /// <param name="citations">How many numbered sources the text may cite. "[2]" for 1..citations becomes a small button
    /// (class "cite", data-cite="2") that the chat screen opens; other bracketed numbers stay as text.</param>
    public static IReadOnlyList<MarkdownSegment> Render(string? markdown, int citations = 0)
    {
        var segments = new List<MarkdownSegment>();
        if (string.IsNullOrEmpty(markdown)) return segments;

        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!IsSafeLink(link.Url)) link.Url = "#";
            // Remote images would be blocked by the app's security policy; show them as links instead.
            if (link.IsImage)
            {
                link.IsImage = false;
                if (link.FirstChild is null) link.AppendChild(new LiteralInline("image"));
            }
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);

        void Flush()
        {
            renderer.Writer.Flush();
            var html = writer.ToString();
            writer.GetStringBuilder().Clear();
            if (citations > 0) html = MarkCitations(html, citations);
            if (!string.IsNullOrWhiteSpace(html)) segments.Add(new MarkdownSegment(html, null, null));
        }

        foreach (var block in document)
        {
            if (block is FencedCodeBlock code)
            {
                Flush();
                var language = string.IsNullOrWhiteSpace(code.Info) ? null : code.Info.Trim();
                segments.Add(new MarkdownSegment(null, code.Lines.ToString(), language));
            }
            else
            {
                renderer.Render(block);
            }
        }
        Flush();
        return segments;
    }

    /// <summary>
    /// Turns "[2]" or "[1, 3]" in rendered text into cite buttons. Tags and inline code are left alone, and only numbers
    /// of real sources change, so the markup added is always our own.
    /// </summary>
    internal static string MarkCitations(string html, int citations)
    {
        var parts = TagOrCode().Split(html);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('<')) continue;
            parts[i] = Citation().Replace(parts[i], m =>
            {
                var numbers = m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
                if (numbers.Any(n => n < 1 || n > citations)) return m.Value;
                return string.Concat(numbers.Select(n => $"<button type=\"button\" class=\"cite\" data-cite=\"{n}\" title=\"Show source {n}\">{n}</button>"));
            });
        }
        return string.Concat(parts);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(<code>.*?</code>|<[^>]*>)", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex TagOrCode();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,2}(?:\s*,\s*\d{1,2})*)\]")]
    private static partial System.Text.RegularExpressions.Regex Citation();

    /// <summary>
    /// Removes HTML tags from third-party markdown (model cards), keeping the text between them, so
    /// cards written with HTML read cleanly instead of showing tags. Fenced code is left alone.
    /// The result still goes through <see cref="Render"/>, which never passes HTML through.
    /// </summary>
    public static string StripHtmlTags(string markdown)
    {
        var output = new System.Text.StringBuilder(markdown.Length);
        var inFence = false;
        var blank = false;
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var fence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            if (fence) inFence = !inFence;
            var text = line;
            if (!inFence && !fence)
            {
                text = Tag().Replace(Comment().Replace(line, ""), "");
                // Indented HTML would otherwise turn into a code block once its tags are gone.
                if (text != line) text = text.Trim();
                var isBlank = text.Trim().Length == 0;
                if (isBlank && blank) continue;
                blank = isBlank;
            }
            output.Append(text).Append('\n');
        }
        return output.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"</?[A-Za-z][^<>]*>")]
    private static partial System.Text.RegularExpressions.Regex Tag();

    [System.Text.RegularExpressions.GeneratedRegex(@"<!--.*?-->")]
    private static partial System.Text.RegularExpressions.Regex Comment();

    private static bool IsSafeLink(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith('#')) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
    }
}
