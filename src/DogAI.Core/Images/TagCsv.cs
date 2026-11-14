using System.Text;

namespace DogAI.Core.Images;

/// <summary>One tag kept from a tag file: the tag as the sites write it, how many posts have it, and its other spellings.</summary>
public sealed record TagRow(string Name, int Posts, IReadOnlyList<string> Spellings);

/// <summary>Thrown when a file is not a tag list DOG.ai can read. The message is meant for the owner.</summary>
public sealed class TagFileException(string message) : Exception(message);

/// <summary>
/// Reads a tag file of the a1111-sd-webui-tagcomplete project (<c>danbooru_e621_merged.csv</c> and files like it): one tag a
/// line, <c>name,category,posts,"other,spellings"</c>. The same rules make the bundled list (<c>tools/make-taglist.ps1</c>
/// calls <see cref="BuildBundledList"/>) and read the full list the owner may add on Models → Images, so both agree.
/// </summary>
/// <remarks>
/// Kept: Danbooru's general tags (category 0), e621's general tags (7) and e621's species tags (12), with at least
/// <see cref="MinPosts"/> posts. Left out: artists, characters, series, meta and lore tags (see <c>Images/Tags/README.md</c>).
/// The file is untrusted when the owner adds it: it is read strictly, up to <see cref="MaxBytes"/>, and a line that does
/// not have the expected shape rejects the whole file.
/// </remarks>
public static class TagCsv
{
    public const int MinPosts = 100;

    /// <summary>The largest tag file read. The pinned source is 6 MB.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private const int MaxNameLength = 80;
    private const int MaxSpellingLength = 60;

    /// <summary>Categories kept: Danbooru general, e621 general, e621 species.</summary>
    public static IReadOnlySet<int> Kept { get; } = new HashSet<int> { 0, 7, 12 };

    private const string NotATagList = "This file is not a tag list DOG.ai can read. It needs one tag a line: name, category, posts and other spellings, separated by commas (like danbooru_e621_merged.csv).";

    /// <summary>
    /// The tags a file keeps, in file order, without repeats. Throws <see cref="TagFileException"/> when the file is too large,
    /// a line has the wrong shape, or nothing in it is kept. <paramref name="excluded"/> leaves out any tag or other spelling
    /// whose fingerprint it lists.
    /// </summary>
    public static IReadOnlyList<TagRow> Read(TextReader reader, TagFingerprints? excluded = null, int minPosts = MinPosts)
    {
        var rows = new List<TagRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long chars = 0;
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            chars += line.Length + 1;
            if (chars > MaxBytes) throw new TagFileException($"This file is larger than a tag list should be (more than {MaxBytes / (1024 * 1024)} MB).");
            if (line.Length == 0) continue;
            if (number == 1 && line.Length > 0 && line[0] == '﻿') line = line[1..];
            if (Fields(line) is not [var rawName, var rawCategory, var rawPosts, var rawSpellings]
                || !int.TryParse(rawCategory, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var category)
                || !int.TryParse(rawPosts, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var posts))
                throw new TagFileException(NotATagList + $" Line {number} is not like that.");

            if (!Kept.Contains(category) || posts < minPosts) continue;
            var name = rawName.Trim();
            // A tag is one word as the sites write it: no spaces, tabs or control characters.
            if (name.Length == 0 || name.Length > MaxNameLength || HasSpaceOrControl(name)) continue;
            if (excluded is not null && excluded.Contains(name)) continue;
            if (!seen.Add(name)) continue;
            var spellings = rawSpellings.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 1 && s.Length <= MaxSpellingLength && !HasSpaceOrControl(s) && !s.StartsWith('/'))
                .Where(s => excluded is null || !excluded.Contains(s))
                .ToList();
            rows.Add(new TagRow(name, posts, spellings));
        }
        if (rows.Count == 0) throw new TagFileException(number == 0 ? "This file is empty." : NotATagList + " No tag in it was kept.");
        return rows;
    }

    /// <summary>Reads a tag file from disk, refusing one larger than <see cref="MaxBytes"/> before reading it.</summary>
    public static IReadOnlyList<TagRow> ReadFile(string path, TagFingerprints? excluded = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new TagFileException("The tag file is not there any more.");
        if (info.Length > MaxBytes) throw new TagFileException($"This file is larger than a tag list should be (more than {MaxBytes / (1024 * 1024)} MB).");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
        try
        {
            return Read(reader, excluded);
        }
        catch (DecoderFallbackException)
        {
            throw new TagFileException(NotATagList + " It is not UTF-8 text.");
        }
    }

    /// <summary>The bundled list's lines: a header, then tag, tab, posts, and (when it has any) tab and other spellings.</summary>
    public static void WriteList(TextWriter writer, IReadOnlyList<TagRow> rows, int minPosts = MinPosts)
    {
        writer.WriteLine("# Tags DOG.ai checks the chat model's picture tags against. Made by tools/make-taglist.ps1; do not edit by hand.");
        writer.WriteLine("# Source and licence: see README.md in this folder. Some tags are left out on purpose: see README.md.");
        writer.WriteLine($"# {rows.Count} tags with at least {minPosts} posts: tag, posts, other spellings.");
        foreach (var row in rows)
            writer.WriteLine(row.Spellings.Count > 0 ? $"{row.Name}\t{row.Posts}\t{string.Join(' ', row.Spellings)}" : $"{row.Name}\t{row.Posts}");
    }

    /// <summary>
    /// Makes the bundled list from a source file, leaving out every tag and other spelling listed in
    /// <paramref name="exclusionsPath"/>. Returns how many tags were written. Used by <c>tools/make-taglist.ps1</c>; it never
    /// prints or returns a left-out tag.
    /// </summary>
    public static int BuildBundledList(string sourcePath, string exclusionsPath, string targetPath)
    {
        using var exclusions = File.OpenText(exclusionsPath);
        var excluded = TagFingerprints.Read(exclusions);
        IReadOnlyList<TagRow> rows;
        using (var source = new StreamReader(sourcePath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false))
            rows = Read(source, excluded);
        using var target = new StreamWriter(targetPath, false, new UTF8Encoding(false));
        WriteList(target, rows);
        return rows.Count;
    }

    /// <summary>The comma-separated fields of one line; a field may be quoted, with "" for a quote inside. Null if the quoting is broken.</summary>
    internal static List<string>? Fields(string line)
    {
        var fields = new List<string>(4);
        var field = new StringBuilder();
        var i = 0;
        while (true)
        {
            field.Clear();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= line.Length) return null;
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    field.Append(line[i++]);
                }
                if (i < line.Length && line[i] != ',') return null;
            }
            else
            {
                while (i < line.Length && line[i] != ',')
                {
                    if (line[i] == '"') return null;
                    field.Append(line[i++]);
                }
            }
            fields.Add(field.ToString());
            if (fields.Count > 4) return null;
            if (i >= line.Length) return fields;
            i++; // the comma
        }
    }

    private static bool HasSpaceOrControl(string s) => s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned);
}

/// <summary>
/// Tags left out of the bundled list on purpose, kept only as SHA-256 fingerprints so the words themselves are not in
/// DOG.ai's source (the owner's decision, 2026-11-14). A fingerprint is the SHA-256 of the tag in the form prompts use
/// (<see cref="TagList.Normalise"/>: lower case, spaces for underscores), as lower-case hex. The list is
/// <c>Images/Tags/excluded-tags.txt</c>; <c>tools/tag-fingerprints.ps1</c> adds to it.
/// </summary>
public sealed class TagFingerprints
{
    public const string ResourceName = "DogAI.Core.Images.Tags.excluded-tags.txt";

    private readonly HashSet<string> _hashes;

    private TagFingerprints(HashSet<string> hashes) => _hashes = hashes;

    public static TagFingerprints None { get; } = new([]);

    private static readonly Lazy<TagFingerprints> BundledSet = new(() =>
    {
        using var stream = typeof(TagFingerprints).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return None;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Read(reader);
    });

    /// <summary>The fingerprints shipped with DOG.ai.</summary>
    public static TagFingerprints Bundled => BundledSet.Value;

    public int Count => _hashes.Count;

    /// <summary>The fingerprint of a tag in any of its spellings ("Some_Tag" and "some tag" give the same one).</summary>
    public static string Of(string tag) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(TagList.Normalise(tag))));

    /// <summary>One fingerprint a line (64 hex characters); lines starting with # and blank lines are skipped, anything else is refused.</summary>
    public static TagFingerprints Read(TextReader reader)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#') continue;
            if (text.Length != 64 || !text.All(char.IsAsciiHexDigit))
                throw new FormatException($"Line {number} of the fingerprint list is not a SHA-256 fingerprint.");
            hashes.Add(text.ToLowerInvariant());
        }
        return new TagFingerprints(hashes);
    }

    /// <summary>A set made in code from fingerprints, for tests.</summary>
    public static TagFingerprints OfHashes(IEnumerable<string> hashes) => new(hashes.Select(h => h.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal));

    public bool Contains(string tag) => _hashes.Count > 0 && _hashes.Contains(Of(tag));

    public bool ContainsHash(string hash) => _hashes.Contains(hash.ToLowerInvariant());
}
