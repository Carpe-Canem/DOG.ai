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
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<MarkdownSegment> Render(string? markdown)
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

    private static bool IsSafeLink(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith('#')) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
    }
}
