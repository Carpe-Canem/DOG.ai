using System.Text.Json;
using System.Text.RegularExpressions;
using DogAI.Core.Models;
using DogAI.Core.Search;
using DogAI.Core.Settings;

namespace DogAI.Tests;

/// <summary>
/// M23: the user guide in docs/guide says when it has fallen behind the app. Each page starts with hidden lines naming
/// the addresses it covers and the source files it describes (see docs/guide/README.md, "How this guide is kept").
/// </summary>
public class GuideTests
{
    /// <summary>
    /// A page not written yet carries a <c>&lt;!-- stub --&gt;</c> line. Since M23's last step every page is written, so
    /// a stub fails; a new page is written in the step that adds it.
    /// </summary>
    private const bool StubsAllowed = false;

    private static readonly string Root = FindRoot();
    private static readonly string Guide = Path.Combine(Root, "docs", "guide");

    [Fact]
    public void Every_page_is_in_the_contents_and_every_contents_link_is_a_page()
    {
        var contents = File.ReadAllText(Path.Combine(Guide, "README.md"));
        var linked = Regex.Matches(contents, @"\]\(([\w-]+\.md)\)").Select(m => m.Groups[1].Value).ToHashSet();
        var pages = Pages().Select(p => p.File).ToHashSet();
        None(pages.Except(linked), "pages missing from the contents");
        None(linked.Except(pages), "contents links to no page");
    }

    [Fact]
    public void Every_page_names_what_it_covers_and_its_sources()
    {
        foreach (var page in Pages())
        {
            Assert.True(page.Sources.Count > 0, $"{page.File} has no <!-- sources: … --> line.");
            Assert.True(page.Stub || !page.Text.Contains("<!-- stub -->"), page.File);
            if (!StubsAllowed) Assert.False(page.Stub, $"{page.File} is still a stub.");
        }
    }

    [Fact]
    public void Every_screen_and_settings_category_has_a_page()
    {
        var covered = Pages().SelectMany(p => p.Covers).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var screens = QuickJump.Screens.Select(s => s.Route)
            .Concat(SettingsCatalog.Categories.Select(c => $"settings/{c.Id}"));
        None(screens.Where(s => !covered.Contains(s)), "screens no page covers");
        // And nothing is covered that the app does not have.
        var known = screens.Concat(ModelsPlaces.Groups.SelectMany(g => g.Places).Select(p => ModelsPlaces.Address(p.Id)))
            .Concat(["models/pictures/loras", "models/pictures/tags", "models/downloads", "settings"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        None(covered.Where(c => !known.Contains(c)), "covered addresses the app does not have");
    }

    [Fact]
    public void Every_screen_file_belongs_to_a_page()
    {
        var globs = Pages().SelectMany(p => p.Sources).Select(Glob).ToList();
        var ui = Path.Combine(Root, "src", "DogAI.UI");
        var files = Directory.GetFiles(ui, "*.razor", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => f.Contains("/Pages/") || f.Contains("/Components/") || f.Contains("/Layout/"));
        None(files.Where(f => !globs.Any(g => g.IsMatch(f))), "screen files no page lists in its sources");
    }

    [Fact]
    public void Every_source_line_names_files_that_exist()
    {
        var all = Directory.GetFiles(Path.Combine(Root, "src"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).ToList();
        foreach (var page in Pages())
            foreach (var source in page.Sources)
                Assert.True(all.Any(Glob(source).IsMatch), $"{page.File}: {source} matches no file.");
    }

    [Fact]
    public void Every_picture_and_link_exists_and_every_picture_is_used()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in Pages().Append(new Page("README.md", File.ReadAllText(Path.Combine(Guide, "README.md")))))
            foreach (Match m in Regex.Matches(page.Text, @"\]\(([^)#\s]+)(?:#[^)]*)?\)"))
            {
                var target = m.Groups[1].Value;
                if (target.Contains("://")) continue;
                var full = Path.GetFullPath(Path.Combine(Guide, target));
                Assert.True(File.Exists(full), $"{page.File} links to {target}, which is not there.");
                used.Add(full);
            }
        var pictures = Directory.Exists(Path.Combine(Guide, "img"))
            ? Directory.GetFiles(Path.Combine(Guide, "img"), "*.png", SearchOption.AllDirectories) : [];
        None(pictures.Where(p => !used.Contains(Path.GetFullPath(p))).Select(p => Path.GetRelativePath(Guide, p)), "pictures no page shows");
    }

    [Fact]
    public void Every_picture_is_in_the_shot_list_and_every_shot_belongs_to_a_page()
    {
        var shots = Shots();
        var pages = Pages().Select(p => Path.GetFileNameWithoutExtension(p.File)).ToHashSet();
        None(shots.Where(s => !pages.Contains(s.Page)).Select(s => s.Name), "shots of a page that does not exist");
        Assert.Equal(shots.Count, shots.Select(s => s.Name).Distinct().Count());
        var listed = shots.Select(s => $"{s.Page}/{s.Name}.png").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pictures = Directory.Exists(Path.Combine(Guide, "img"))
            ? Directory.GetFiles(Path.Combine(Guide, "img"), "*.png", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(Path.Combine(Guide, "img"), p).Replace('\\', '/')) : [];
        None(pictures.Where(p => !listed.Contains(p)), "pictures not in tools/guide-shots.json");
        Assert.All(shots, s => Assert.Contains(s.Kind, new[] { "plain", "online", "live", "window", "kept" }));
        Assert.All(shots, s => Assert.Contains(s.Folder, new[] { "empty", "made-up", "none" }));
        // The lock screen can only be taken on a folder that is locked.
        Assert.All(shots.Where(s => s.Kind == "window"), s => Assert.True(s.Locked, $"{s.Name} is a window shot without a lock."));
    }

    [Fact]
    public void Every_id_the_shot_list_names_is_in_a_razor_file()
    {
        var razor = string.Join("\n", Directory.GetFiles(Path.Combine(Root, "src", "DogAI.UI"), "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        // Ids built in code: id="@($"default-{m}")" makes every id that starts default-.
        var built = Regex.Matches(razor, @"id=""@\(\$""([\w-]+-)\{").Select(m => m.Groups[1].Value).ToList();
        foreach (var shot in Shots())
            foreach (var id in shot.Selectors.SelectMany(s => Regex.Matches(s, @"#([\w-]+)").Select(m => m.Groups[1].Value)))
                Assert.True(razor.Contains($"id=\"{id}\"") || razor.Contains($"Id=\"{id}\"") || razor.Contains($"\"{id}\"") ||
                    built.Any(p => id.StartsWith(p, StringComparison.Ordinal)), $"{shot.Name} names #{id}, which no .razor file has.");
    }

    [Fact]
    public void Models_places_are_named_as_the_app_names_them()
    {
        var names = ModelsPlaces.Groups.SelectMany(g => g.Places).Select(p => ModelsPlaces.Name(p.Id))
            .Concat(ModelsPlaces.Groups.Select(g => $"Models › {g.Title}"))
            .Concat([ModelsPlaces.LorasName, ModelsPlaces.TagsName, ModelsPlaces.PicturesName + " › Checkpoints"])
            .OrderByDescending(n => n.Length).ToList();
        foreach (var page in Pages())
            foreach (Match m in Regex.Matches(page.Text, "Models › "))
            {
                var rest = page.Text[m.Index..];
                var name = names.FirstOrDefault(n => rest.StartsWith(n, StringComparison.Ordinal) && !rest[n.Length..].StartsWith(" ›"));
                Assert.True(name is not null, $"{page.File} names a place in Models the app does not have: {rest[..Math.Min(60, rest.Length)]}");
            }
    }

    [Fact]
    public void A_written_settings_page_names_every_setting()
    {
        var page = Pages().Single(p => p.File == "settings-and-help.md");
        if (page.Stub) return;
        None(SettingsCatalog.Entries.Where(e => !page.Text.Contains(e.Title, StringComparison.Ordinal)).Select(e => e.Title), "settings the Settings page does not name");
    }

    private static void None(IEnumerable<string> found, string what)
    {
        var list = found.ToList();
        Assert.True(list.Count == 0, $"{what}: {string.Join(", ", list)}");
    }

    // --- Reading the guide -------------------------------------------------------------------------------------------

    private sealed record Page(string File, string Text)
    {
        public IReadOnlyList<string> Covers => Lines("covers");
        public IReadOnlyList<string> Sources => Lines("sources");
        public bool Stub => Text.Contains("<!-- stub -->");

        private IReadOnlyList<string> Lines(string key) => Regex.Matches(Text, $@"<!--\s*{key}:\s*(.*?)\s*-->", RegexOptions.Singleline)
            .SelectMany(m => Regex.Split(m.Groups[1].Value, @",(?![^{]*\})|\n").Select(s => s.Trim()).Where(s => s.Length > 0))
            .ToList();
    }

    private static IReadOnlyList<Page> Pages() => Directory.GetFiles(Guide, "*.md")
        .Where(f => Path.GetFileName(f) != "README.md")
        .Select(f => new Page(Path.GetFileName(f), File.ReadAllText(f))).ToList();

    private sealed record Shot(string Name, string Page, string Folder, string Kind, IReadOnlyList<string> Selectors, bool Locked);

    private static IReadOnlyList<Shot> Shots()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools", "guide-shots.json")));
        return doc.RootElement.GetProperty("shots").EnumerateArray().Select(s =>
        {
            var selectors = new List<string>();
            foreach (var key in new[] { "marks", "volatile" })
                if (s.TryGetProperty(key, out var list)) selectors.AddRange(list.EnumerateArray().Select(e => e.GetString()!));
            if (s.TryGetProperty("waitFor", out var w)) selectors.Add(w.GetString()!);
            foreach (var part in new[] { "steps", "after" })
                if (s.TryGetProperty(part, out var steps))
                    foreach (var step in steps.EnumerateArray())
                        foreach (var key in new[] { "mouse", "osclick" })
                            if (step.TryGetProperty(key, out var sel)) selectors.Add(sel.GetString()!);
            return new Shot(s.GetProperty("name").GetString()!, s.GetProperty("page").GetString()!,
                s.GetProperty("folder").GetString()!, s.GetProperty("kind").GetString()!, selectors, s.TryGetProperty("lock", out _));
        }).ToList();
    }

    /// <summary>A sources glob as a regex over paths from the repository root: ** any folders, * within a name, {a,b} either.</summary>
    private static Regex Glob(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*")
            .Replace(@"\{", "(").Replace(@"}", ")").Replace(",", "|");
        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DogAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DogAI.sln was not found above the test folder.");
    }
}
