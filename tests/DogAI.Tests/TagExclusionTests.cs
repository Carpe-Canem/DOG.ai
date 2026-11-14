using System.Text;
using DogAI.Core.Images;
using DogAI.Engine.Images;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// Tags left out of the bundled list on purpose (the owner's decision, 2026-11-14): the tag file rules shared by the build
/// script and the app, the fingerprint list, the tag check dropping left-out tags, and the full list the owner may add.
/// Made-up tags only: the left-out tags themselves are never written in tests. Checks against the real source file run
/// only when <c>DOGAI_TEST_TAG_CSV</c> points at it, and they report counts, never tags.
/// </summary>
public sealed class TagExclusionTests(ITestOutputHelper output)
{
    private static string Csv(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static IReadOnlyList<TagRow> Read(string text, TagFingerprints? excluded = null) => TagCsv.Read(new StringReader(text), excluded);

    // ---- Reading a tag file ----

    [Fact]
    public void A_tag_file_keeps_general_and_species_tags_with_enough_posts()
    {
        var rows = Read(Csv(
            "plain_tag,0,5000,\"plain_tags,plaintag\"",
            "artist_name,1,9000,",
            "series_name,3,9000,",
            "character_name,4,9000,",
            "meta_tag,5,9000,",
            "e621_tag,7,300,",
            "e621_species,12,150,specie_alias",
            "lore_tag,15,9000,",
            "rare_tag,0,99,",
            "just_enough,0,100,"));
        Assert.Equal(["plain_tag", "e621_tag", "e621_species", "just_enough"], rows.Select(r => r.Name));
        Assert.Equal(["plain_tags", "plaintag"], rows[0].Spellings);
        Assert.Equal(5000, rows[0].Posts);
    }

    [Fact]
    public void A_tag_file_is_read_with_the_build_scripts_rules()
    {
        var rows = Read(Csv(
            "﻿first,0,500,",
            "  spaced_name  ,0,500,\" a_b , c ,/slash,with space,x,ok_one\"",
            "two words,0,500,",
            "tab\there,0,500,",
            "\"quoted_\"\"name\"\"\",0,500,\"say_\"\"hi\"\"\"",
            "first,0,900,",
            new string('n', 81) + ",0,500,",
            "emoticon_^_^,0,500,"));
        Assert.Equal(["first", "spaced_name", "quoted_\"name\"", "emoticon_^_^"], rows.Select(r => r.Name));
        // Spellings: trimmed; one character, spaces and ones starting with "/" left out.
        Assert.Equal(["a_b", "ok_one"], rows[1].Spellings);
        Assert.Equal(["say_\"hi\""], rows[2].Spellings);
        Assert.Equal(500, rows[0].Posts); // the first of a repeated tag wins
    }

    [Theory]
    [InlineData("name,0,500")]                 // three fields
    [InlineData("name,0,500,,extra")]          // five fields
    [InlineData("name,general,500,")]          // category not a number
    [InlineData("name,0,many,")]               // posts not a number
    [InlineData("name,0,-5,")]                 // posts with a sign
    [InlineData("\"name,0,500,")]              // quote never closed
    [InlineData("na\"me,0,500,")]              // quote inside a plain field
    [InlineData("<html><body>Not found</body></html>")]
    public void A_file_that_is_not_a_tag_list_is_refused_whole(string bad)
    {
        var e = Assert.Throws<TagFileException>(() => Read(Csv("good_tag,0,500,", bad, "other_tag,0,500,")));
        Assert.Contains("not a tag list", e.Message);
        Assert.Contains("Line 2", e.Message);
    }

    [Fact]
    public void An_empty_file_or_one_with_nothing_kept_is_refused()
    {
        Assert.Contains("empty", Assert.Throws<TagFileException>(() => Read("")).Message);
        Assert.Contains("No tag in it was kept", Assert.Throws<TagFileException>(() => Read(Csv("artist_name,1,9000,", "rare,0,3,"))).Message);
    }

    [Fact]
    public void A_file_larger_than_a_tag_list_is_refused()
    {
        // Read stops at the cap without reading on.
        var e = Assert.Throws<TagFileException>(() => TagCsv.Read(new EndlessTags()));
        Assert.Contains("larger than a tag list", e.Message);

        // From disk, the size is checked before anything is read.
        var path = Path.Combine(Path.GetTempPath(), "dogai-big-tags-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using (var f = File.Create(path)) f.SetLength(TagCsv.MaxBytes + 1);
            Assert.Contains("larger than a tag list", Assert.Throws<TagFileException>(() => TagCsv.ReadFile(path)).Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_that_is_not_utf8_text_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), "dogai-bin-tags-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE, 0x00]);
            Assert.Contains("not a tag list", Assert.Throws<TagFileException>(() => TagCsv.ReadFile(path)).Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_written_list_reads_back_as_the_same_tags()
    {
        var rows = Read(Csv("plain_tag,0,5000,\"plain_tags,plaintag\"", "lonely_tag,7,300,"));
        var text = new StringWriter();
        TagCsv.WriteList(text, rows);
        var lines = text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Count(l => l.StartsWith('#')));
        Assert.Contains("# 2 tags with at least 100 posts", lines[2]);
        Assert.Equal("plain_tag\t5000\tplain_tags plaintag", lines[3]);
        Assert.Equal("lonely_tag\t300", lines[4]);

        var list = TagList.Read(new StringReader(text.ToString()));
        Assert.Equal(["plain tag"], list.Resolve("plaintag"));
        Assert.True(list.Contains("lonely tag"));
    }

    // ---- Fingerprints ----

    [Fact]
    public void A_fingerprint_is_the_sha256_of_the_tag_in_prompt_form()
    {
        // SHA-256 of the UTF-8 text "made up tag".
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("made up tag")));
        Assert.Equal(expected, TagFingerprints.Of("made up tag"));
        Assert.Equal(expected, TagFingerprints.Of("Made_Up_Tag"));
        Assert.Equal(expected, TagFingerprints.Of("  made   up_tag "));
        Assert.Equal(64, expected.Length);
        Assert.NotEqual(expected, TagFingerprints.Of("made up tags"));
    }

    [Fact]
    public void A_fingerprint_list_holds_only_fingerprints()
    {
        var hash = TagFingerprints.Of("made up tag");
        var list = TagFingerprints.Read(new StringReader($"# a comment\n\n{hash.ToUpperInvariant()}\n"));
        Assert.Equal(1, list.Count);
        Assert.True(list.Contains("made_up_tag"));
        Assert.Throws<FormatException>(() => TagFingerprints.Read(new StringReader("made up tag\n")));
        Assert.Throws<FormatException>(() => TagFingerprints.Read(new StringReader(hash[..63] + "\n")));
    }

    [Fact]
    public void A_listed_tag_and_a_listed_spelling_are_left_out_of_a_built_list()
    {
        var excluded = TagFingerprints.OfHashes([TagFingerprints.Of("hidden thing"), TagFingerprints.Of("rude spelling")]);
        var rows = Read(Csv(
            "clean_thing,0,5000,\"clean_things,rude_spelling\"",
            "hidden_thing,0,9000,\"hidden_things,other_name\"",
            "Hidden_Thing,7,500,"), excluded);
        Assert.Equal(["clean_thing"], rows.Select(r => r.Name));
        Assert.Equal(["clean_things"], rows[0].Spellings);
    }

    // ---- The tag check and left-out tags ----

    /// <summary>A small list as built: "hidden thing" and "rude spelling" were left out and are known only by fingerprint.</summary>
    private static TagList Built() => TagList.Of(
            "dress\t9000", "red dress\t5000", "hidden things\t10", "thing\t4000", "pebble\t3000", "red pebble\t100", "long hair\t9000",
            "sweater\t1000", "smile\t9000\tsmiling", "river pebbles\t200")
        .Excluding(TagFingerprints.OfHashes([TagFingerprints.Of("hidden thing"), TagFingerprints.Of("rude spelling"), TagFingerprints.Of("pebbles")]));

    [Theory]
    [InlineData("hidden thing")]
    [InlineData("Hidden_Thing")]
    [InlineData("(hidden thing:1.3)")]
    [InlineData("wearing a hidden thing")]   // filler words
    [InlineData("hidden-thing")]             // another spelling worth trying
    [InlineData("hiddenthing")]
    [InlineData("hidden thing dress")]       // inside a longer tag
    [InlineData("red hidden thing")]
    [InlineData("rude spelling")]
    [InlineData("pebbles")]
    public void A_left_out_tag_is_dropped_and_never_put_right_into_a_clean_one(string written)
    {
        var list = Built();
        Assert.Empty(list.Resolve(written));
        var (kept, notes) = list.Check([written + ", smile"]);
        Assert.Equal(["smile"], kept);
        Assert.Empty(notes.Corrected);
    }

    [Fact]
    public void A_left_out_tag_does_not_take_clean_tags_with_it()
    {
        var list = Built();
        // Real tags stay, even when a left-out tag is a spelling of them away.
        Assert.Equal(["pebble"], list.Resolve("pebble"));
        Assert.Equal(["red pebble"], list.Resolve("red pebble"));
        Assert.Equal(["dress"], list.Resolve("dress"));
        Assert.Equal(["smile"], list.Resolve("smiling"));
        // A clean word whose plural was left out still works inside a longer tag.
        Assert.Equal(["pebble"], list.Resolve("tattered pebble"));
        // A clean tag's plural, though its last word alone was left out, is the clean tag.
        Assert.Equal(["red pebble"], list.Resolve("red pebbles"));
        // A clean tag that shares a word with a left-out one survives an extra describing word.
        Assert.Equal(["river pebbles"], list.Resolve("river pebbles"));
        Assert.Equal(["river pebbles"], list.Resolve("tattered river pebbles"));
        Assert.Empty(list.Resolve("pebbles dress"));
        // Near misses of clean tags are still put right.
        Assert.Equal(["sweater"], list.Resolve("sweatr"));
        Assert.Equal(["long hair"], list.Resolve("long-hair"));
    }

    [Fact]
    public void The_full_list_brings_left_out_tags_back()
    {
        var list = Built();
        var full = list.With(Read(Csv("hidden_thing,0,9000,\"rude_spelling\"", "pebbles,0,500,", "new_tag,7,300,\"newtag\"")));
        Assert.Equal(["hidden thing"], full.Resolve("hidden thing"));
        Assert.Equal(["hidden thing"], full.Resolve("rude spelling"));
        Assert.Equal(["pebbles"], full.Resolve("pebbles"));
        Assert.Equal(["new tag"], full.Resolve("newtag"));
        Assert.Equal(list.Count + 3, full.Count);
        // The list it was made from is unchanged.
        Assert.Empty(list.Resolve("hidden thing"));
    }

    // ---- Looks on a card from someone else ----

    [Fact]
    public void Only_left_out_tags_count_as_left_out()
    {
        var list = Built();
        Assert.True(list.LeavesOut("hidden thing"));
        Assert.True(list.LeavesOut("Hidden_Thing"));
        Assert.True(list.LeavesOut("hidden thing dress"));
        // Written as one word it is still that tag (the tag check drops such a word as unknown; here it would stay).
        Assert.True(list.LeavesOut("hiddenthing"));
        Assert.True(list.LeavesOut("red hiddenthing"));
        // A clean tag, an unknown word and a clean tag that shares a word with a left-out one are not.
        Assert.False(list.LeavesOut("red dress"));
        Assert.False(list.LeavesOut("made up word"));
        Assert.False(list.LeavesOut("river pebbles"));
        Assert.False(list.LeavesOut(""));
        // A list that leaves nothing out (an empty one, or the full list the owner added) never says so.
        Assert.False(TagList.Empty.LeavesOut("hidden thing"));
        Assert.False(list.With(Read(Csv("hidden_thing,0,9000,"))).LeavesOut("hidden thing"));
    }

    [Fact]
    public void Looks_from_an_imported_card_lose_left_out_tags_and_nothing_else()
    {
        var looks = new Looks("1girl, silver hair, hidden thing, hiddenthing, (hidden thing:1.2), wearing a hidden thing, hidden thing dress, " +
                              "red dress, pebbles, river pebbles, made up word", "people/mara.safetensors", 0.8);
        var (kept, leftOut) = looks.ForImport(Built());
        // Unknown words stay: only what is on the left-out list goes.
        Assert.Equal("1girl, silver hair, red dress, river pebbles, made up word", kept.Tags);
        Assert.Equal(6, leftOut);
        Assert.Equal("people/mara.safetensors", kept.Lora);
        Assert.Equal(0.8, kept.LoraStrength);

        // With the full list added, the card's looks come in whole, as the planner's own tags do.
        var full = Built().With(Read(Csv("hidden_thing,0,9000,", "pebbles,0,500,")));
        var (whole, none) = looks.ForImport(full);
        Assert.Equal(looks, whole);
        Assert.Equal(0, none);

        // Looks without any such tag are handed back untouched.
        var clean = new Looks("1girl, long hair, red dress");
        Assert.Equal((clean, 0), clean.ForImport(Built()));
    }

    [Fact]
    public void An_imported_card_is_checked_once_and_the_owner_is_told_how_many_went()
    {
        var card = new DogAI.Core.Characters.Character { Name = "Mara", Description = "A scout." };
        new Looks("1girl, hidden thing, long hair", "people/mara.safetensors").SaveTo(card);
        var imported = DogAI.Core.Characters.CharacterCard.FromJson(DogAI.Core.Characters.CharacterCard.ToV2Json(card));

        Assert.Equal(1, Looks.CheckImported(imported, Built()));
        Assert.Equal("1girl, long hair", Looks.Of(imported).Tags);
        Assert.Equal("people/mara.safetensors", Looks.Of(imported).Lora);
        // A second look finds nothing more.
        Assert.Equal(0, Looks.CheckImported(imported, Built()));

        // Looks that were nothing but such tags are gone from the card altogether.
        var only = new DogAI.Core.Characters.Character { Name = "Odd" };
        new Looks("hidden thing, pebbles").SaveTo(only);
        Assert.Equal(2, Looks.CheckImported(only, Built()));
        Assert.Null(DogAI.Core.Characters.CharacterCard.LooksJson(only));

        // A card without looks is left exactly as it was.
        var plain = DogAI.Core.Characters.CharacterCard.FromJson("""{ "name": "Plain", "description": "d", "personality": "p", "scenario": "s", "first_mes": "f", "mes_example": "e" }""");
        var before = plain.ExtraJson;
        Assert.Equal(0, Looks.CheckImported(plain, Built()));
        Assert.Equal(before, plain.ExtraJson);

        // The note says how many, never which.
        Assert.StartsWith("1 of this card’s Looks tag was left out", Looks.ImportNote(1));
        Assert.StartsWith("3 of this card’s Looks tags were left out", Looks.ImportNote(3));
        Assert.DoesNotContain("hidden", Looks.ImportNote(3));
    }

    // ---- The bundled list ----

    [Fact]
    public void The_bundled_list_holds_no_left_out_tag_or_spelling()
    {
        var excluded = TagFingerprints.Bundled;
        Assert.True(excluded.Count > 1000, $"only {excluded.Count} fingerprints are bundled");
        Assert.Same(excluded, TagList.Bundled.Excluded);

        using var stream = typeof(TagList).Assembly.GetManifestResourceStream(TagList.ResourceName)!;
        using var reader = new StreamReader(stream);
        int lines = 0, found = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            lines++;
            var parts = line.Split('\t');
            if (excluded.Contains(parts[0])) found++;
            if (parts.Length > 2) found += parts[2].Split(' ').Count(excluded.Contains);
        }
        Assert.True(lines > 25000, $"only {lines} tags are bundled");
        Assert.Equal(0, found);
    }

    [Fact]
    public void The_bundled_fingerprint_list_is_only_fingerprints()
    {
        using var stream = typeof(TagList).Assembly.GetManifestResourceStream(TagFingerprints.ResourceName)!;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            Assert.True(line.StartsWith('#') || (line.Length == 64 && line.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')), "a line that is not a fingerprint");
    }

    /// <summary>
    /// With the real source file: the app's rules rebuild the shipped list exactly (so the build script and the app agree),
    /// and every left-out tag and spelling in it is dropped by the bundled tag check rather than corrected into a clean tag.
    /// </summary>
    [Fact]
    public void With_the_source_file_every_left_out_tag_is_dropped()
    {
        if (Environment.GetEnvironmentVariable("DOGAI_TEST_TAG_CSV") is not { Length: > 0 } csv || !File.Exists(csv))
        {
            output.WriteLine("Skipped: DOGAI_TEST_TAG_CSV is not set.");
            return;
        }

        // The shipped list is what the rules make from the pinned file.
        var rebuilt = new StringWriter();
        TagCsv.WriteList(rebuilt, TagCsv.ReadFile(csv, TagFingerprints.Bundled));
        using (var stream = typeof(TagList).Assembly.GetManifestResourceStream(TagList.ResourceName)!)
        using (var reader = new StreamReader(stream))
            Assert.Equal(reader.ReadToEnd().ReplaceLineEndings(), rebuilt.ToString().ReplaceLineEndings());

        // Every tag the rules would keep, and its spellings, whose fingerprint is listed.
        var all = TagCsv.ReadFile(csv);
        var excluded = TagFingerprints.Bundled;
        var written = new List<string>();
        foreach (var row in all)
        {
            var rowExcluded = excluded.Contains(row.Name);
            if (rowExcluded) written.Add(row.Name);
            foreach (var spelling in row.Spellings)
                if (rowExcluded || excluded.Contains(spelling)) written.Add(spelling);
        }
        Assert.True(written.Count > 1000);

        var list = TagList.Bundled;
        int kept = 0, wrongHashes = 0;
        foreach (var w in written)
        {
            foreach (var form in new[] { w, w.Replace('_', ' '), w.Replace('_', ' ').ToUpperInvariant() })
            {
                var resolved = list.Resolve(form);
                if (resolved.Count == 0) continue;
                // A spelling of a left-out tag that is itself a clean tag (not listed) is allowed through.
                if (!excluded.Contains(form)) continue;
                kept++;
                if (wrongHashes++ < 20) output.WriteLine("Not dropped, by fingerprint: " + TagFingerprints.Of(form) + " → " + string.Join(", ", resolved.Select(TagFingerprints.Of)));
            }
        }
        output.WriteLine($"{written.Count} left-out tags and spellings checked in three forms; {kept} not dropped.");
        Assert.Equal(0, kept);
    }

    /// <summary>A reader that never ends: one long line after another.</summary>
    private sealed class EndlessTags : TextReader
    {
        private readonly string _line = "made_up_tag,0,500," + new string('x', 1_000_000);
        public override string ReadLine() => _line;
    }
}

/// <summary>The full tag list on disk: added, read at start, refused when it is not a tag list, removed. Changes <see cref="TagList.Current"/>, so not run beside other tests that use it.</summary>
[Collection(nameof(CurrentTagList))]
public sealed class ExtraTagsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-extra-tags-" + Guid.NewGuid().ToString("N"));

    public ExtraTagsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TagList.UseExtra(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Source(string text)
    {
        var path = Path.Combine(_root, "chosen-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task A_chosen_tag_file_is_copied_in_used_read_at_start_and_removed()
    {
        var extra = new ExtraTags();
        await extra.LoadAsync(_root);
        Assert.False(extra.IsOn);
        Assert.Same(TagList.Bundled, TagList.Current);

        var source = Source("made_up_extra_tag,0,5000,\"madeupextratag\"\r\nother_made_up_tag,7,300,\r\nartist_made_up,1,9000,\r\n");
        await extra.AddAsync(_root, source);
        Assert.True(extra.IsOn);
        Assert.Equal(2, extra.Count);
        Assert.Null(extra.Problem);
        Assert.True(File.Exists(ExtraTags.PathIn(_root)));
        Assert.Equal(Path.Combine(_root, "tags", "full-tag-list.csv"), ExtraTags.PathIn(_root));
        Assert.Equal(["made up extra tag"], TagList.Current.Resolve("madeupextratag"));
        Assert.Empty(TagList.Bundled.Resolve("made up extra tag"));

        // A new start finds it.
        TagList.UseExtra(null);
        var atStart = new ExtraTags();
        await atStart.LoadAsync(_root);
        Assert.True(atStart.IsOn);
        Assert.True(TagList.Current.Contains("other made up tag"));

        await atStart.RemoveAsync(_root);
        Assert.False(atStart.IsOn);
        Assert.False(File.Exists(ExtraTags.PathIn(_root)));
        Assert.Same(TagList.Bundled, TagList.Current);
    }

    [Fact]
    public async Task A_file_that_is_not_a_tag_list_is_refused_and_nothing_is_copied()
    {
        var extra = new ExtraTags();
        var e = await Assert.ThrowsAsync<TagFileException>(() => extra.AddAsync(_root, Source("<html>not a tag list</html>\r\n")));
        Assert.Contains("not a tag list", e.Message);
        Assert.False(extra.IsOn);
        Assert.False(File.Exists(ExtraTags.PathIn(_root)));
        Assert.Same(TagList.Bundled, TagList.Current);
    }

    [Fact]
    public async Task A_damaged_file_in_the_folder_is_reported_and_not_used()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ExtraTags.PathIn(_root))!);
        File.WriteAllText(ExtraTags.PathIn(_root), "good_tag,0,500,\r\nbroken line\r\n");
        var extra = new ExtraTags();
        await extra.LoadAsync(_root);
        Assert.False(extra.IsOn);
        Assert.Contains("could not be used", extra.Problem);
        Assert.Same(TagList.Bundled, TagList.Current);

        await extra.RemoveAsync(_root);
        Assert.Null(extra.Problem);
        Assert.False(File.Exists(ExtraTags.PathIn(_root)));
    }

    [Fact]
    public void The_pinned_download_is_the_reviewed_file_from_an_allowed_host()
    {
        Assert.True(DogAI.Core.Net.OriginPolicy.IsAllowedHubUri(new Uri(ExtraTags.SourceUrl)));
        Assert.Contains(ExtraTags.SourceCommit, ExtraTags.SourceUrl);
        Assert.EndsWith("/" + ExtraTags.SourceFile, ExtraTags.SourceUrl);
        Assert.Equal(6_024_803, ExtraTags.SourceBytes);
        Assert.Equal("aa9eab8435562f86d7ac0d81f2f7bedcba9f6b93a806010708702406bba85e18", ExtraTags.SourceSha256);
    }
}

[CollectionDefinition(nameof(CurrentTagList), DisableParallelization = true)]
public sealed class CurrentTagList;
