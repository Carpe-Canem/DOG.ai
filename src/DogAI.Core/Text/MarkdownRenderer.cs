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
/// Someone whose spoken lines can be told apart: a character with speaker colour <paramref name="Tone"/> (1 to
/// <see cref="SpeakerTones.Count"/>), or, with no tone, a name that only helps the guess (their lines stay plain).
/// </summary>
public sealed record Speaker(string Name, int? Tone, Pronoun Pronoun = Pronoun.Unknown);

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
        .UseMathematics()
        .DisableHtml()
        .Build();

    /// <param name="citations">How many numbered sources the text may cite. "[2]" for 1..citations becomes a small button
    /// (class "cite", data-cite="2") that the chat screen opens; other bracketed numbers stay as text.</param>
    /// <param name="speech">Roleplay and story text: quoted lines are wrapped in span.say and emphasis outside a quotation
    /// (an action written between asterisks) becomes em.act, so the UI can colour them.</param>
    /// <param name="speakers">Stories: the characters whose lines get their own colour, when a line can be pinned to one.</param>
    public static IReadOnlyList<MarkdownSegment> Render(string? markdown, int citations = 0, bool speech = false, IReadOnlyList<Speaker>? speakers = null)
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
            if (speech) html = MarkSpeech(html, speakers);
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

    private const string SayClose = "</span>";

    private static readonly HashSet<string> BlockTags =
        ["p", "li", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "td", "th", "tr", "table", "blockquote", "div", "hr"];

    /// <summary>Stands in for a quotation's speaker class until the whole paragraph has been read.</summary>
    private const char ToneMark = '\u0001';

    /// <summary>
    /// Marks spoken lines and actions in rendered text. Each run of text inside a quotation gets its own span.say, closed
    /// before any tag and reopened after it, so the markup added is always well formed whatever the model wrote. A
    /// quotation left open ends with its paragraph. Code and formulas are left alone. With <paramref name="speakers"/>,
    /// a line <see cref="SpeakerGuess"/> can pin to one of them also gets that speaker's tone-N class.
    /// </summary>
    internal static string MarkSpeech(string html, IReadOnlyList<Speaker>? speakers = null)
    {
        var state = new SpeechState();
        var inMath = false;
        foreach (var part in TagOrCode().Split(html))
        {
            if (part.Length == 0) continue;
            if (part[0] != '<')
            {
                if (inMath) state.Output.Append(part);
                else state.AppendText(part);
                continue;
            }
            if (part.StartsWith("<code>", StringComparison.Ordinal))
            {
                state.Output.Append(part);
                continue;
            }
            if (inMath)
            {
                if (part is "</span>" or "</div>") inMath = false;
            }
            else if (part.Contains("class=\"math", StringComparison.Ordinal))
            {
                inMath = true;
            }
            if (BlockTags.Contains(TagName(part))) state.EndBlock();
            state.Output.Append(part == "<em>" && state.Closer is null ? "<em class=\"act\">" : part);
        }
        state.EndBlock();
        return state.Finish(speakers);
    }

    /// <summary>The work of <see cref="MarkSpeech"/>: the marked HTML, and each paragraph's narration and quotations.</summary>
    private sealed class SpeechState
    {
        public readonly System.Text.StringBuilder Output = new();
        public char? Closer;
        private readonly List<List<QuotePart>> _blocks = [];
        private List<QuotePart> _block = [];
        private readonly System.Text.StringBuilder _run = new();
        private int _quotes;

        private void OpenSay() => Output.Append("<span class=\"say").Append(ToneMark).Append(_quotes - 1).Append(ToneMark).Append("\">");

        public void AppendText(string text)
        {
            var open = Closer is not null;
            if (open) OpenSay();
            var i = 0;
            while (i < text.Length)
            {
                if (Quotations.MarkAt(text, i, html: true) is not { } found)
                {
                    Output.Append(text[i]);
                    _run.Append(text[i++]);
                    continue;
                }
                var token = text.Substring(i, found.Length);
                if (Closer is null)
                {
                    if (Quotations.CloserFor(found.Mark, text, i, found.Length) is { } end)
                    {
                        FlushRun(quoted: false);
                        _quotes++;
                        OpenSay();
                        Closer = end;
                        open = true;
                    }
                    Output.Append(token);
                    _run.Append(found.Mark);
                }
                else
                {
                    Output.Append(token);
                    _run.Append(found.Mark);
                    if (Quotations.Ends(Closer.Value, found.Mark))
                    {
                        Output.Append(SayClose);
                        FlushRun(quoted: true);
                        Closer = null;
                        open = false;
                    }
                }
                i += found.Length;
            }
            if (open) Output.Append(SayClose);
        }

        private void FlushRun(bool quoted)
        {
            if (_run.Length > 0 || quoted) _block.Add(new QuotePart(System.Net.WebUtility.HtmlDecode(_run.ToString()), quoted));
            _run.Clear();
        }

        /// <summary>A paragraph (or list item, heading, cell) ends; so does any quotation left open in it.</summary>
        public void EndBlock()
        {
            if (Closer is not null || _run.Length > 0) FlushRun(Closer is not null);
            Closer = null;
            if (_block.Count > 0) _blocks.Add(_block);
            _block = [];
        }

        public string Finish(IReadOnlyList<Speaker>? speakers)
        {
            var tones = new List<int?>();
            var guess = speakers is { Count: > 0 } ? new SpeakerGuess(speakers.Select(s => new Person(s.Name, s.Pronoun)).ToList()) : null;
            foreach (var block in _blocks)
            {
                if (guess is null || guess.IsEmpty) tones.AddRange(block.Where(p => p.Quoted).Select(_ => (int?)null));
                else tones.AddRange(guess.Assign(block).Select(s => s is { } n ? speakers![n].Tone : null));
            }
            var html = Output.ToString();
            return ToneMarks().Replace(html, m =>
            {
                var q = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return q < tones.Count && tones[q] is { } tone ? $" tone-{tone}" : "";
            });
        }
    }

    private static string TagName(string tag)
    {
        var start = tag.Length > 1 && tag[1] == '/' ? 2 : 1;
        var end = start;
        while (end < tag.Length && char.IsAsciiLetterOrDigit(tag[end])) end++;
        return tag[start..end].ToLowerInvariant();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(<code>.*?</code>|<[^>]*>)", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex TagOrCode();

    [System.Text.RegularExpressions.GeneratedRegex(@"\u0001(\d+)\u0001")]
    private static partial System.Text.RegularExpressions.Regex ToneMarks();

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
