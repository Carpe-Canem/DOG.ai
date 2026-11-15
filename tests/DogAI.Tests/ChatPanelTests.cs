using System.Text.RegularExpressions;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Settings;
using DogAI.Core.Stories;

namespace DogAI.Tests;

/// <summary>M12: the side panels of chats and stories that fold away, the hidden side panel, and the editors in tabs.</summary>
public class ChatPanelTests
{
    [Fact]
    public void Every_panel_id_is_a_rememberable_name_and_unique_across_the_app()
    {
        var ids = ChatPanels.Layout.SelectMany(kv => kv.Value.Select(p => ChatPanels.Id(kv.Key, p.Panel)))
            .Concat(ChatPanels.Places.Select(ChatPanels.AsideId)).ToList();
        Assert.All(ids, id => Assert.True(InterfaceMemory.IsId(id), id));
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Other folds on the app's pages must not share an id with a side panel (they would open and close together).
        var others = SourceFoldIds();
        Assert.NotEmpty(others);
        Assert.Empty(others.Intersect(ids));
    }

    [Fact]
    public void Each_place_remembers_its_own_panels()
    {
        Assert.Equal("story-memory", ChatPanels.Id(ChatPanels.Story, ChatPanels.Memory));
        Assert.Equal("character-memory", ChatPanels.Id(ChatPanels.Character, ChatPanels.Memory));
        Assert.Throws<ArgumentException>(() => ChatPanels.Id(ChatPanels.General, ChatPanels.Members));
        Assert.Throws<ArgumentException>(() => ChatPanels.AsideId("models"));
    }

    [Fact]
    public void Panels_used_every_turn_start_open_and_the_rest_start_closed()
    {
        Assert.True(ChatPanels.OpensByDefault(ChatPanels.Character, ChatPanels.Style));
        Assert.True(ChatPanels.OpensByDefault(ChatPanels.Group, ChatPanels.Members));
        Assert.False(ChatPanels.OpensByDefault(ChatPanels.Character, ChatPanels.Memory));
        Assert.False(ChatPanels.OpensByDefault(ChatPanels.Story, ChatPanels.Style));
        // Every place keeps at least one panel open and at least one closed: the point is a shorter stack, not an empty one.
        foreach (var (place, panels) in ChatPanels.Layout)
        {
            Assert.Contains(panels, p => p.Open);
            Assert.Contains(panels, p => !p.Open);
            Assert.Equal(panels.Count, panels.Select(p => p.Panel).Distinct().Count());
        }
    }

    [Fact]
    public async Task A_hidden_side_panel_is_remembered_per_place()
    {
        var docs = new MemoryDocumentStore();
        var memory = new InterfaceMemory(docs);
        await memory.LoadAsync();
        Assert.True(memory.IsOpen(ChatPanels.AsideId(ChatPanels.Story)));
        await memory.SetOpenAsync(ChatPanels.AsideId(ChatPanels.Story), false);
        await memory.SetOpenAsync(ChatPanels.Id(ChatPanels.Story, ChatPanels.Memory), true, ChatPanels.OpensByDefault(ChatPanels.Story, ChatPanels.Memory));

        var again = new InterfaceMemory(docs);
        await again.LoadAsync();
        Assert.False(again.IsOpen(ChatPanels.AsideId(ChatPanels.Story)));
        Assert.True(again.IsOpen(ChatPanels.AsideId(ChatPanels.General)));
        Assert.True(again.IsOpen("story-memory", openByDefault: false));
        Assert.False(again.IsOpen("character-memory", ChatPanels.OpensByDefault(ChatPanels.Character, ChatPanels.Memory)));
    }

    [Theory]
    [InlineData(null, "None")]
    [InlineData("   \n  ", "None")]
    [InlineData("Answer in French.\nKeep it short.", "Answer in French.")]
    [InlineData("  It is raining.  ", "It is raining.")]
    public void A_text_box_summary_is_its_first_line(string? text, string expected) =>
        Assert.Equal(expected, ChatPanels.TextSummary(text));

    [Fact]
    public void A_long_text_box_summary_is_cut()
    {
        var summary = ChatPanels.TextSummary(new string('a', 200));
        Assert.Equal(ChatPanels.SummaryLength, summary.Length);
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void Other_summaries()
    {
        Assert.Equal("None", ChatPanels.DocumentsSummary(0, 0));
        Assert.Equal("1 file", ChatPanels.DocumentsSummary(1, 0));
        Assert.Equal("3 files, 1 collection", ChatPanels.DocumentsSummary(3, 1));
        Assert.Equal("2 collections", ChatPanels.DocumentsSummary(0, 2));
        Assert.Equal("Off", ChatPanels.MemorySummary(false, true));
        Assert.Equal("On", ChatPanels.MemorySummary(true, false));
        Assert.Equal("On · summary written", ChatPanels.MemorySummary(true, true));
        Assert.Null(ChatPanels.OutlineSummary(0, 0));
        Assert.Equal("Chapter 1 of 5", ChatPanels.OutlineSummary(0, 5));
        Assert.Equal("Chapter 5 of 5", ChatPanels.OutlineSummary(9, 5));
    }

    [Fact]
    public void Editor_tabs_are_distinct_and_the_story_kind_never_takes_away_the_open_tab()
    {
        Assert.Equal(EditorTabs.Character.Count, EditorTabs.Character.Select(t => t.Id).Distinct().Count());
        var adventure = EditorTabs.Story(StoryKind.Interactive);
        var written = EditorTabs.Story(StoryKind.Authored);
        Assert.Equal(adventure.Select(t => t.Id), written.Select(t => t.Id));
        Assert.Equal("Adventure rules", adventure.Single(t => t.Id == EditorTabs.Plot).Title);
        Assert.Equal("Outline", written.Single(t => t.Id == EditorTabs.Plot).Title);
        Assert.Equal(EditorTabs.Basics, EditorTabs.Character[0].Id);
        Assert.Equal(EditorTabs.Basics, adventure[0].Id);
    }

    [Fact]
    public void A_problem_on_saving_opens_the_tab_of_its_field()
    {
        // The name and the picture are both on Basics; the editor switches there when either stops a save.
        Assert.Equal(EditorTabs.Basics, EditorTabs.ForCharacterProblem(EditorTabs.CharacterProblem.NoName));
        Assert.Equal(EditorTabs.Basics, EditorTabs.ForCharacterProblem(EditorTabs.CharacterProblem.Picture));
        Assert.Equal(EditorTabs.Messages, EditorTabs.Known(EditorTabs.Character, EditorTabs.Messages));
        Assert.Equal(EditorTabs.Basics, EditorTabs.Known(EditorTabs.Character, "nonsense"));
        Assert.Equal(EditorTabs.Basics, EditorTabs.Known(EditorTabs.Character, null));
    }

    [Fact]
    public void Every_editor_tab_has_its_part_in_the_editor()
    {
        var ui = Path.Combine(SolutionRoot(), "src", "DogAI.UI", "Pages");
        var character = File.ReadAllText(Path.Combine(ui, "CharacterEditor.razor"));
        var story = File.ReadAllText(Path.Combine(ui, "StoryEditor.razor"));
        foreach (var name in new[] { "Basics", "Personality", "Messages", "LooksAndVoice", "Advanced" })
            Assert.Contains($"data-tab=\"@EditorTabs.{name}\"", character);
        foreach (var name in new[] { "Basics", "Cast", "WorldAndStyle", "Plot" })
            Assert.Contains($"data-tab=\"@EditorTabs.{name}\"", story);
        Assert.Equal(EditorTabs.Character.Count, Regex.Matches(character, "class=\"editor-tab\"").Count);
        Assert.Equal(EditorTabs.Story(StoryKind.Interactive).Count, Regex.Matches(story, "class=\"editor-tab\"").Count);
    }

    [Fact]
    public void The_narrow_drawer_opens_and_closes_without_touching_the_hidden_side_panel()
    {
        var drawer = new SidePanelDrawer();
        var changes = 0;
        drawer.Changed += () => changes++;
        Assert.False(drawer.Open);
        drawer.Close();
        Assert.Equal(0, changes);
        drawer.Show();
        drawer.Show();
        Assert.True(drawer.Open);
        Assert.Equal(1, changes);
        drawer.Toggle();
        Assert.False(drawer.Open);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Every_page_with_a_side_panel_can_open_it_in_a_narrow_window()
    {
        // Below 1050 px the side panel is reached only through the drawer: AsideRegion carries its head and the
        // AsideToggle in the page's heading carries the button that opens it. A bare aside would be out of reach.
        var pages = Path.Combine(SolutionRoot(), "src", "DogAI.UI", "Pages");
        var withAside = 0;
        foreach (var file in Directory.EnumerateFiles(pages, "*.razor"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            Assert.DoesNotContain("SectionName=\"aside\"", text);
            if (!text.Contains("<AsideRegion")) continue;
            withAside++;
            Assert.True(text.Contains("<AsideToggle"), $"{name} has a side panel but no button to open it");
        }
        Assert.True(withAside >= 6, $"only {withAside} pages with a side panel found");
    }

    /// <summary>
    /// A variable declared in a top-level <c>@if</c> ("_controls is { } controls") or a top-level <c>@{ }</c> belongs
    /// to the whole page. Once a handler uses it, the compiler hangs every lambda of the page, the regions' content
    /// included, off an object made anew on each render; the layout keys a region's content by that lambda, takes it
    /// for new and rebuilds it. Seen on the Images page: its prompt boxes lost the focus after every key.
    /// </summary>
    [Fact]
    public void A_page_that_fills_regions_declares_no_variable_at_its_top()
    {
        var ui = Path.Combine(SolutionRoot(), "src", "DogAI.UI");
        var pages = 0;
        foreach (var file in Directory.EnumerateFiles(ui, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var lines = File.ReadAllLines(file);
            if (!lines.Any(l => l.Contains("<SectionContent"))) continue;
            pages++;
            var name = Path.GetFileName(file);
            for (var i = 0; i < lines.Length && !lines[i].StartsWith("@code", StringComparison.Ordinal); i++)
                Assert.False(DeclaresAtTheTop(lines[i]),
                    $"{name} line {i + 1} declares a variable for the whole page; test the field and declare the variable inside the block: {lines[i].Trim()}");
        }
        Assert.True(pages >= 14, $"only {pages} pages that fill regions found");
    }

    [Theory]
    [InlineData("@if (!_kept && _ready && Missing() is null && _controls is { } controls && _card is { } card)", true)]
    [InlineData("@if (_viewing is { Length: > 0 } viewed)", true)]
    [InlineData("@if (_open is FoundPassage hit)", true)]
    [InlineData("@if (int.TryParse(_typed, out var number))", true)]
    [InlineData("@if (_map.TryGetValue(id, out string? name) && _ready)", true)]
    [InlineData("@switch (_state is { } s ? s.Kind : 0)", true)]
    [InlineData("@{ var shown = Session.Selected; }", true)]
    [InlineData("@{", true)]
    [InlineData("@if (_kept)", false)]
    [InlineData("@if (!_kept && _ready && Missing() is null && _controls is not null && _card is not null)", false)]
    [InlineData("@if (_count is > 0 and < 5 || _name is null or \"\")", false)]
    [InlineData("@if (_open is not FoundPassage)", false)]
    [InlineData("    @if (_error is { } error)", false)] // inside a block: it belongs to that block
    [InlineData("else if (Missing() is { } missing)", false)] // an else's own statement: it does not reach the page
    [InlineData("@foreach (var c in _checkpoints)", false)]
    public void What_counts_as_a_variable_at_the_top_of_a_page(string line, bool declares) =>
        Assert.Equal(declares, DeclaresAtTheTop(line));

    private static bool DeclaresAtTheTop(string line)
    {
        if (line.StartsWith("@{", StringComparison.Ordinal)) return true;
        if (!line.StartsWith("@if (", StringComparison.Ordinal) && !line.StartsWith("@switch (", StringComparison.Ordinal)) return false;
        return Regex.IsMatch(line, @"\bis\s+\{[^{}]*\}\s+(?!and\b|or\b)[A-Za-z_]\w*")
            || Regex.IsMatch(line, @"\bis\s+(?!not\b|null\b|var\b)[A-Za-z_][\w.<>?]*\s+(?!and\b|or\b|null\b)[A-Za-z_]\w*")
            || Regex.IsMatch(line, @"\bis\s+var\s+[A-Za-z_]\w*")
            || Regex.IsMatch(line, @"\bout\s+[A-Za-z_][\w.<>?]*\s+[A-Za-z_]\w*");
    }

    private static string SolutionRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    /// <summary>The literal ids of every Fold on the app's pages (side panels build theirs from ChatPanels).</summary>
    private static List<string> SourceFoldIds()
    {
        var ui = Path.Combine(SolutionRoot(), "src", "DogAI.UI");
        return Directory.EnumerateFiles(ui, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "<Fold\\s+Id=\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value))
            .ToList();
    }
}
