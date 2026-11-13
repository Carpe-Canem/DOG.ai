using System.Text;
using System.Text.RegularExpressions;

namespace DogAI.Core.Models;

/// <summary>What one model card says a model is and does, as plain text.</summary>
/// <param name="Repo">The repository whose card this came from.</param>
/// <param name="Intro">One or two paragraphs describing the model.</param>
/// <param name="Highlights">What the card lists as the model's strengths, if it has such a list.</param>
public sealed record ModelAbout(string Repo, string? Intro, IReadOnlyList<string> Highlights);

/// <summary>
/// Reads Hugging Face model cards (README.md). Cards are written by strangers, so everything here
/// returns plain text: no HTML, no links, no images. Most GGUF repositories only describe their
/// files; <see cref="Summarize"/> returns null for those so the caller can try the original model.
/// </summary>
public static partial class ModelCard
{
    /// <summary>The metadata block at the top of a card, and the rest.</summary>
    public static (string FrontMatter, string Body) Split(string text)
    {
        var trimmed = text.TrimStart('﻿');
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return ("", text);
        var end = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return ("", text);
        var after = trimmed.IndexOf('\n', end + 4);
        var front = trimmed[3..end];
        return (front, after < 0 ? "" : trimmed[(after + 1)..].TrimStart());
    }

    /// <summary>
    /// The one model this card says it was made from (<c>base_model</c>). Null when there is none, or
    /// several (a merge), since no single card would describe it.
    /// </summary>
    public static string? BaseModel(string frontMatter)
    {
        var lines = frontMatter.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = BaseModelLine().Match(lines[i]);
            if (!m.Success) continue;
            var inline = m.Groups[1].Value.Trim().Trim('"', '\'');
            if (inline.StartsWith('['))
            {
                var items = inline.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return items.Length == 1 ? Clean(items[0]) : null;
            }
            if (inline.Length > 0) return Clean(inline);
            var list = lines.Skip(i + 1).TakeWhile(l => l.TrimStart().StartsWith('-')).ToList();
            return list.Count == 1 ? Clean(list[0].TrimStart()[1..]) : null;
        }
        return null;
    }

    /// <summary>The model a search result was made from, from its tags (<c>base_model:quantized:x</c> and similar).</summary>
    public static string? BaseModelFromTags(IEnumerable<string>? tags)
    {
        if (tags is null) return null;
        var bases = tags.Where(t => t.StartsWith("base_model:", StringComparison.OrdinalIgnoreCase))
            .Select(t => t["base_model:".Length..])
            .Select(t => t.Split(':') is [var kind, var repo] && kind is "quantized" or "finetune" or "adapter" or "merge" ? repo : t)
            .Where(t => !t.Contains(':'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return bases.Count == 1 ? Clean(bases[0]) : null;
    }

    private static string? Clean(string repo)
    {
        repo = repo.Trim().Trim('"', '\'');
        return RepoId().IsMatch(repo) ? repo : null;
    }

    /// <summary>
    /// What is known about a model: its own card's description, then the card of the model it was made
    /// from while the description so far has no list of strengths (a GGUF copy → an uncensored edit →
    /// the original). At most two descriptions and four cards. A card that cannot be read ends the walk;
    /// only a failure on the first card is passed on.
    /// </summary>
    public static async Task<IReadOnlyList<ModelAbout>> DescribeAsync(string repo, Func<string, CancellationToken, Task<string>> readCard, CancellationToken ct = default)
    {
        var found = new List<ModelAbout>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? next = repo;
        for (var hop = 0; next is not null && hop < 4 && seen.Add(next); hop++)
        {
            string raw;
            try
            {
                raw = await readCard(next, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (hop > 0 && e is not OperationCanceledException)
            {
                break;
            }
            var (front, body) = Split(raw);
            if (Summarize(next, body) is { } about) found.Add(about);
            if (found.Count >= 2 || found.Any(a => a.Highlights.Count > 0)) break;
            next = BaseModel(front);
        }
        return found;
    }

    /// <summary>The card's own description of the model, or null when it only describes files.</summary>
    public static ModelAbout? Summarize(string repo, string body)
    {
        var intro = new List<string>();
        var highlights = new List<string>();
        var heading = "";
        var skipSection = false;
        var listOpen = false; // the previous paragraph ended with ':' or we are in a highlights section

        foreach (var block in Blocks(body))
        {
            switch (block)
            {
                case Heading h:
                    // A card that opens with "… Quantizations of X" is about files; anything after is advice on picking one.
                    if (heading.Length == 0 && intro.Count == 0 && h.Text.Contains("quantization", StringComparison.OrdinalIgnoreCase)) return null;
                    heading = h.Text;
                    // A top-level heading is a title ("Read our guide", the model name), not a section to skip.
                    skipSection = h.Level > 1 && SkipHeading().IsMatch(heading);
                    listOpen = HighlightHeading().IsMatch(heading);
                    break;
                case Paragraph p when !skipSection:
                    var text = Inline(p.Text);
                    if (!Usable(text)) { listOpen = false; break; }
                    if (text.EndsWith(':')) { listOpen = true; break; }
                    if (BoldLead().IsMatch(p.Text) && intro.Count > 0) { if (highlights.Count < MaxHighlights) highlights.Add(text); break; }
                    if (intro.Count < 2 && intro.Sum(s => s.Length) < 450) intro.Add(text);
                    listOpen = HighlightHeading().IsMatch(heading);
                    break;
                case Bullets b when !skipSection && listOpen && highlights.Count == 0:
                    foreach (var item in b.Items.Select(Inline).Where(i => Usable(i) && !SpecLine().IsMatch(i)))
                        if (highlights.Count < MaxHighlights) highlights.Add(item);
                    listOpen = false;
                    break;
                case Bullets:
                    listOpen = false;
                    break;
            }
            if (intro.Count >= 2 && highlights.Count > 0) break;
        }

        return intro.Count == 0 && highlights.Count == 0 ? null : new ModelAbout(repo, intro.Count == 0 ? null : string.Join("\n\n", intro), highlights);
    }

    private const int MaxHighlights = 6;

    /// <summary>Real prose: long enough to say something, and not about files, downloads or donations.</summary>
    private static bool Usable(string text) =>
        text.Length >= 40 && !Boilerplate().IsMatch(text) && text.Count(char.IsLetter) > text.Length / 2;

    private abstract record Block;
    private sealed record Heading(int Level, string Text) : Block;
    private sealed record Paragraph(string Text) : Block;
    private sealed record Bullets(List<string> Items) : Block;

    /// <summary>Headings, paragraphs and bullet lists. Code, tables, quotes, images and HTML are dropped.</summary>
    private static IEnumerable<Block> Blocks(string body)
    {
        var lines = HtmlComment().Replace(body.Replace("\r", ""), "").Split('\n');
        var para = new StringBuilder();
        List<string>? items = null;
        var inFence = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var bullet = BulletLine().Match(raw);
            var isText = line.Length > 0 && !line.StartsWith('<') && !line.StartsWith('|') && !line.StartsWith('>')
                         && !line.StartsWith("![", StringComparison.Ordinal) && !Rule().IsMatch(line) && !line.StartsWith('#');

            // Close what is open when the kind of block changes.
            if (para.Length > 0 && (!isText || bullet.Success))
            {
                yield return new Paragraph(para.ToString());
                para.Clear();
            }
            // Blank lines between items do not end a list; any other line does.
            if (items is not null && !bullet.Success && line.Length > 0 && !(isText && raw.StartsWith("  ", StringComparison.Ordinal)))
            {
                yield return new Bullets(items);
                items = null;
            }

            if (line.StartsWith('#'))
                yield return new Heading(line.Length - line.TrimStart('#').Length, Inline(line.TrimStart('#')));
            else if (bullet.Success)
            {
                // Only top-level items: nested ones are details (layer counts and the like).
                if (bullet.Groups[1].Length <= 1) (items ??= []).Add(bullet.Groups[2].Value);
            }
            else if (isText && items is null)
                para.Append(para.Length > 0 ? " " : "").Append(line);
        }
        if (para.Length > 0) yield return new Paragraph(para.ToString());
        if (items is not null) yield return new Bullets(items);
    }

    /// <summary>Markdown inline syntax and stray HTML turned into the words a reader would see.</summary>
    private static string Inline(string text)
    {
        text = Image().Replace(text, "");
        text = Link().Replace(text, "$1");
        text = Tag().Replace(text, "");
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        text = Emphasis().Replace(text, "$1");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ").Trim();
        return text.TrimEnd(' ', ' ');
    }

    [GeneratedRegex(@"^\s*base_model\s*:(.*)$")]
    private static partial Regex BaseModelLine();

    [GeneratedRegex(@"^[A-Za-z0-9][\w.\-]*/[\w.\-]+$")]
    private static partial Regex RepoId();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.+)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^([-*_]\s*){3,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>]{0,500}>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<![\w*])\*([^*\n]+)\*(?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\s*\*\*[^*]{2,60}\*\*\s*[:–—-]")]
    private static partial Regex BoldLead();

    /// <summary>A specification line ("Number of Layers: 64"), not a strength.</summary>
    [GeneratedRegex(@"^[\w\s()\-]{2,40}:\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex SpecLine();

    [GeneratedRegex(@"gguf|quant|imatrix|llama\.?cpp|lm ?studio|ollama|provided files|download|unsloth|patreon|ko-?fi|donat|sponsor|storage limit|support (my|our) work|huggingface-cli|see below|\b\d+-bit\b|this repo(sitory)? (contains|provides|hosts)|converted (to|from)|run (them|it) (in|with)|discord|weights and configuration files|hf\.co|https?://|repack|\bI?Q\d_\w*|\bTQ\d|\b(i|we) (would like to )?thank|thank you|special thanks|thanks (go )?to (my|our|the team)|refer to|for more (details|information)|blog post|unmute|join (us|our)", RegexOptions.IgnoreCase)]
    private static partial Regex Boilerplate();

    [GeneratedRegex(@"usage|use with|how to (run|use)|quick ?start|prompt (format|template)|download|provided (files|quants)|files|quantization|quants|citation|cite|licen[cs]e|donat|support|sponsor|benchmark|evaluation|performance|results|install|inference|deploy|serving|requirements|changelog|update|patch|warning|limitation|contact|acknowledg|best practices|parameters|sampling|settings|chat template|ollama|llama\.?cpp|lm ?studio|run(ning)? (it|the model|locally)|which file|choos|embed/output|\barm\b|\bavx|credits", RegexOptions.IgnoreCase)]
    private static partial Regex SkipHeading();

    [GeneratedRegex(@"highlight|feature|capabilit|strength|key |what'?s new|why ", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightHeading();
}
