using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using DogAI.Core.Chat;
using DogAI.Core.Text;

namespace DogAI.Core.Stories;

/// <summary>Writes the branch on screen as Markdown, a single HTML page, or an EPUB book. Nothing in them points outside the file.</summary>
public static class StoryExport
{
    /// <param name="Heading">The chapter name; null when the story has no chapters.</param>
    public sealed record Section(string? Heading, IReadOnlyList<Block> Blocks);

    /// <param name="IsAction">True for what the player did (interactive stories); false for story text.</param>
    public sealed record Block(string Text, bool IsAction);

    /// <summary>
    /// The story text in reading order. An authored story is split at its chapters and its directions are left out;
    /// an interactive one is a single section with the player's actions between the passages.
    /// </summary>
    public static IReadOnlyList<Section> Sections(Story story, IReadOnlyList<ChatMessage> path)
    {
        var sections = new List<Section>();
        var blocks = new List<Block>();
        string? heading = null;
        int? chapter = null;

        void Close()
        {
            if (blocks.Count > 0) sections.Add(new Section(heading, blocks));
            blocks = [];
        }

        foreach (var m in path)
        {
            if (m.Role == ChatRole.Assistant)
            {
                if (m.Content.Trim().Length > 0) blocks.Add(new Block(m.Content.Trim(), false));
            }
            else if (story.Kind == StoryKind.Interactive)
            {
                var text = m.Content.Trim();
                if (StoryTurn.Of(m).Roll is { } roll) text += $" (rolled {roll}: {StoryPrompt.DiceLabel(roll)})";
                if (text.Length > 0) blocks.Add(new Block(text, true));
            }
            else if (story.Outline.Count > 0)
            {
                var next = Math.Clamp(StoryTurn.Of(m).Chapter ?? chapter ?? 0, 0, story.Outline.Count - 1);
                if (next == chapter) continue;
                Close();
                chapter = next;
                heading = StoryPrompt.ChapterName(story, next);
            }
        }
        Close();
        return sections;
    }

    public static string ToMarkdown(string title, Story story, IReadOnlyList<ChatMessage> path)
    {
        var text = new StringBuilder("# ").Append(title.Trim()).Append("\n\n");
        foreach (var section in Sections(story, path))
        {
            if (section.Heading is not null) text.Append("## ").Append(section.Heading).Append("\n\n");
            foreach (var block in section.Blocks)
                text.Append(block.IsAction ? "> " + block.Text.ReplaceLineEndings("\n> ") : block.Text).Append("\n\n");
        }
        return text.ToString().TrimEnd() + "\n";
    }

    private const string Css =
        "body{max-width:40em;margin:2em auto;padding:0 1em;font:1.1em/1.6 Georgia,Cambria,serif;color:#222;background:#fff}" +
        "h1{font-size:1.8em;text-align:center;margin:1.5em 0}h2{font-size:1.3em;margin:2em 0 1em}" +
        "p{margin:0 0 .9em}.action{font-style:italic;color:#555;border-left:3px solid #bbb;padding-left:.8em;margin:1.2em 0}" +
        "pre{white-space:pre-wrap}";

    public static string ToHtml(string title, Story story, IReadOnlyList<ChatMessage> path)
    {
        var html = new StringBuilder("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n<title>")
            .Append(Escape(title.Trim())).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n<h1>")
            .Append(Escape(title.Trim())).Append("</h1>\n");
        foreach (var section in Sections(story, path))
        {
            if (section.Heading is not null) html.Append("<h2>").Append(Escape(section.Heading)).Append("</h2>\n");
            foreach (var block in section.Blocks) html.Append(BlockHtml(block));
        }
        return html.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>An EPUB 3 book: a title page, then one file per chapter (one in all when the story has no chapters).</summary>
    public static byte[] ToEpub(string title, Story story, IReadOnlyList<ChatMessage> path, DateTime? now = null)
    {
        title = title.Trim().Length == 0 ? "Untitled story" : title.Trim();
        var sections = Sections(story, path);
        var chapters = sections.Select((s, i) => (File: $"chapter-{i + 1}.xhtml", Name: s.Heading ?? (sections.Count == 1 ? title : $"Part {i + 1}"), Section: s)).ToList();
        var stamp = (now ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        string Page(string name, string body) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"en\" xml:lang=\"en\">\n" +
            $"<head><meta charset=\"utf-8\" /><title>{Escape(name)}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\" /></head>\n" +
            $"<body>\n{body}</body>\n</html>\n";

        var opf = new StringBuilder(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">\n" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n")
            .Append($"<dc:identifier id=\"id\">urn:uuid:{Guid.NewGuid()}</dc:identifier>\n")
            .Append($"<dc:title>{Escape(title)}</dc:title>\n<dc:language>en</dc:language>\n")
            .Append($"<meta property=\"dcterms:modified\">{stamp}</meta>\n</metadata>\n<manifest>\n")
            .Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\" />\n")
            .Append("<item id=\"css\" href=\"style.css\" media-type=\"text/css\" />\n")
            .Append("<item id=\"title\" href=\"title.xhtml\" media-type=\"application/xhtml+xml\" />\n");
        for (var i = 0; i < chapters.Count; i++)
            opf.Append($"<item id=\"c{i + 1}\" href=\"{chapters[i].File}\" media-type=\"application/xhtml+xml\" />\n");
        opf.Append("</manifest>\n<spine>\n<itemref idref=\"title\" />\n");
        for (var i = 0; i < chapters.Count; i++) opf.Append($"<itemref idref=\"c{i + 1}\" />\n");
        opf.Append("</spine>\n</package>\n");

        var nav = new StringBuilder("<nav epub:type=\"toc\"><h1>Contents</h1><ol>\n");
        foreach (var chapter in chapters) nav.Append($"<li><a href=\"{chapter.File}\">{Escape(chapter.Name)}</a></li>\n");
        nav.Append("</ol></nav>\n");

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The format requires this entry first and uncompressed.
            Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add(zip, "META-INF/container.xml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\" /></rootfiles>\n</container>\n");
            Add(zip, "OEBPS/content.opf", opf.ToString());
            Add(zip, "OEBPS/nav.xhtml", Page("Contents", nav.ToString()));
            Add(zip, "OEBPS/style.css", Css);
            Add(zip, "OEBPS/title.xhtml", Page(title, $"<h1>{Escape(title)}</h1>\n"));
            foreach (var chapter in chapters)
            {
                var head = chapter.Section.Heading is { } h ? $"<h2>{Escape(h)}</h2>\n" : "";
                var body = head + string.Concat(chapter.Section.Blocks.Select(BlockHtml));
                // An e-reader refuses a page that is not well-formed XML; fall back to plain paragraphs if the markdown produced one.
                if (!IsXml("<div>" + body + "</div>"))
                    body = head + string.Concat(chapter.Section.Blocks.Select(b => PlainHtml(b.Text, b.IsAction)));
                Add(zip, "OEBPS/" + chapter.File, Page(chapter.Name, body));
            }
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var entry = zip.CreateEntry(name, level).Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        entry.Write(bytes, 0, bytes.Length);
    }

    private static string BlockHtml(Block block)
    {
        if (block.IsAction) return PlainHtml(block.Text, true);
        var html = new StringBuilder();
        foreach (var segment in MarkdownRenderer.Render(block.Text))
            html.Append(segment.IsCode ? "<pre><code>" + Escape(segment.Code!) + "</code></pre>\n" : segment.Html);
        return html.ToString();
    }

    private static string PlainHtml(string text, bool action) =>
        string.Concat(text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => (action ? "<p class=\"action\">" : "<p>") + Escape(p).Replace("\n", "<br />") + "</p>\n"));

    private static bool IsXml(string markup)
    {
        try
        {
            XDocument.Parse(markup);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
