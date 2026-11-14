using System.Text.RegularExpressions;
using DogAI.Core.Models;
using DogAI.Core.Settings;

namespace DogAI.Tests;

/// <summary>M10: what screens remember about their layout, the default model per job, and the Settings categories and search.</summary>
public class InterfaceTests
{
    [Fact]
    public async Task Sections_are_remembered_and_forgotten_when_back_at_their_default()
    {
        var docs = new MemoryDocumentStore();
        var memory = new InterfaceMemory(docs);
        await memory.LoadAsync();
        Assert.True(memory.IsOpen("library-folders"));
        Assert.False(memory.IsOpen("library-folders", openByDefault: false));

        await memory.SetOpenAsync("library-folders", false);
        await memory.SetOpenAsync("story-panel", true, openByDefault: false);

        var again = new InterfaceMemory(docs);
        await again.LoadAsync();
        Assert.False(again.IsOpen("library-folders"));
        Assert.True(again.IsOpen("story-panel", openByDefault: false));

        // Opened again: back at its default, so it is no longer kept (a better default later reaches it).
        await again.SetOpenAsync("library-folders", true);
        var doc = await docs.LoadAsync<Dictionary<string, object>>(InterfaceMemory.Key);
        Assert.DoesNotContain("library-folders", doc!["Sections"].ToString());
        Assert.Contains("story-panel", doc["Sections"].ToString());
    }

    [Fact]
    public async Task Section_ids_are_short_plain_names()
    {
        var memory = new InterfaceMemory(new MemoryDocumentStore());
        await Assert.ThrowsAsync<ArgumentException>(() => memory.SetOpenAsync("Library Folders", false));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.SetOpenAsync(new string('a', InterfaceMemory.MaxIdLength + 1), false));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.SetOpenAsync("", false));
        Assert.True(InterfaceMemory.IsId("fold-2"));
    }

    [Fact]
    public async Task A_damaged_document_is_cleaned_and_capped_when_read()
    {
        var docs = new MemoryDocumentStore();
        var sections = Enumerable.Range(0, InterfaceMemory.MaxSections + 50).ToDictionary(i => $"s{i}", _ => false);
        sections["<script>"] = false;
        await docs.SaveAsync(InterfaceMemory.Key, new { Sections = sections, SettingsCategory = "pictures" });
        var memory = new InterfaceMemory(docs);
        await memory.LoadAsync();
        Assert.True(memory.IsOpen("<script>"));
        Assert.False(memory.IsOpen("s0"));
        Assert.True(memory.IsOpen($"s{InterfaceMemory.MaxSections + 10}"));
        Assert.Equal("pictures", memory.SettingsCategory);

        // Full: a new section is not remembered, but one already kept can still change.
        await memory.SetOpenAsync("new-one", false);
        Assert.True(memory.IsOpen("new-one"));
        await memory.SetOpenAsync("s0", true);
        Assert.True(memory.IsOpen("s0"));
    }

    [Fact]
    public async Task The_last_settings_category_is_remembered_and_an_unknown_one_opens_appearance()
    {
        var docs = new MemoryDocumentStore();
        var memory = new InterfaceMemory(docs);
        await memory.LoadAsync();
        Assert.Null(memory.SettingsCategory);
        Assert.Equal(SettingsCatalog.Appearance, SettingsCatalog.Category(memory.SettingsCategory));

        await memory.SetSettingsCategoryAsync("voice");
        var again = new InterfaceMemory(docs);
        await again.LoadAsync();
        Assert.Equal("voice", again.SettingsCategory);

        Assert.Equal(SettingsCatalog.Appearance, SettingsCatalog.Category("no-such-category"));
        Assert.Equal(SettingsCatalog.Privacy, SettingsCatalog.Category("privacy"));
    }

    [Fact]
    public void Setting_and_clearing_a_chat_default()
    {
        var settings = new AppSettings();
        var set = ModelDefaults.Set(settings, "general", @"D:\m\a.gguf");
        Assert.True(set.Changed);
        Assert.False(set.SearchChanged);
        Assert.Equal(@"D:\m\a.gguf", ModelDefaults.For(set.Settings, "general"));

        Assert.False(ModelDefaults.Set(set.Settings, "general", @"d:\M\A.gguf").Changed);

        var story = ModelDefaults.Set(set.Settings, "story", @"D:\m\a.gguf").Settings;
        Assert.Equal(["general", "story"], ModelDefaults.JobsOf(story, @"D:\M\a.gguf"));

        var cleared = ModelDefaults.Set(story, "general", null);
        Assert.True(cleared.Changed);
        Assert.Null(ModelDefaults.For(cleared.Settings, "general"));
        Assert.Equal(["story"], ModelDefaults.JobsOf(cleared.Settings, @"D:\m\a.gguf"));
        Assert.False(ModelDefaults.Set(cleared.Settings, "general", "").Changed);
        Assert.Empty(settings.DefaultModels);

        Assert.Throws<ArgumentException>(() => ModelDefaults.Set(settings, "pictures", @"D:\m\a.gguf"));
    }

    [Fact]
    public void Changing_the_document_search_model_says_so_so_documents_are_indexed_again()
    {
        var chosen = ModelDefaults.Set(new AppSettings(), ModelDefaults.DocumentSearch, @"D:\m\embed.gguf");
        Assert.True(chosen.SearchChanged);
        var other = ModelDefaults.Set(chosen.Settings, ModelDefaults.DocumentSearch, @"D:\m\embed2.gguf");
        Assert.True(other.SearchChanged);
        var automatic = ModelDefaults.Set(other.Settings, ModelDefaults.DocumentSearch, null);
        Assert.True(automatic.SearchChanged);
        Assert.Empty(automatic.Settings.DefaultModels);
        Assert.False(ModelDefaults.Set(automatic.Settings, ModelDefaults.DocumentSearch, null).SearchChanged);
        Assert.Equal("embedding", ModelDefaults.DocumentSearch);
    }

    [Fact]
    public void A_deleted_model_is_nobodys_default()
    {
        var settings = new AppSettings
        {
            DefaultModels = new Dictionary<string, string>
            {
                ["general"] = @"D:\m\a.gguf", ["story"] = @"D:\m\b.gguf", [ModelDefaults.DocumentSearch] = @"D:\m\a.gguf",
            },
        };
        var gone = ModelDefaults.Without(settings, @"d:\m\A.gguf");
        Assert.True(gone.Changed);
        Assert.True(gone.SearchChanged);
        Assert.Equal(new Dictionary<string, string> { ["story"] = @"D:\m\b.gguf" }, gone.Settings.DefaultModels);
        Assert.False(ModelDefaults.Without(gone.Settings, @"D:\m\c.gguf").Changed);
    }

    [Theory]
    [InlineData("dark", "theme-label")]
    [InlineData("MICRO", "set-microphone")]
    [InlineData("tray", "set-tray")]
    [InlineData("ctrl+alt", "set-dictate-anywhere")]
    [InlineData("password", "set-lock")]
    [InlineData("rating", "rating-label")]
    [InlineData("restore", "set-backup")]
    [InlineData("data folder", "set-about")]
    [InlineData("fireside", "look-label")]
    public void Search_finds_a_setting_by_name_or_other_words(string query, string anchor) =>
        Assert.Contains(SettingsCatalog.Search(query), e => e.Anchor == anchor);

    [Fact]
    public void Search_puts_name_matches_first_and_needs_every_word()
    {
        // Three settings have a word starting "speak" in their name; "Default voice" only among its other words.
        var hits = SettingsCatalog.Search("speak").Select(e => e.Anchor).ToList();
        Assert.Equal(["set-voice-speed", "set-speakers", "set-speech-language"], hits.Take(3));
        Assert.Contains("set-voice", hits.Skip(3));

        Assert.Empty(SettingsCatalog.Search("tray password"));
        Assert.Empty(SettingsCatalog.Search("   "));
        Assert.Empty(SettingsCatalog.Search(null));
        Assert.Empty(SettingsCatalog.Search("zzzz"));
        // A category's name finds what is in it.
        Assert.All(SettingsCatalog.Search("windows tray"), e => Assert.Equal(SettingsCatalog.Windows, e.Category));
    }

    [Fact]
    public void Every_entry_is_in_a_known_category_and_every_category_has_an_entry()
    {
        var ids = SettingsCatalog.Categories.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(InterfaceMemory.IsId(id)));
        Assert.All(SettingsCatalog.Entries, e => Assert.Contains(e.Category, ids));
        Assert.All(ids, id => Assert.Contains(SettingsCatalog.Entries, e => e.Category == id));
        Assert.Equal(SettingsCatalog.Entries.Count, SettingsCatalog.Entries.Select(e => e.Anchor).Distinct().Count());
    }

    /// <summary>The search scrolls to an element id; a setting that moves must take its id along.</summary>
    [Fact]
    public void Every_search_anchor_is_an_id_on_the_settings_page()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var ui = Path.Combine(root!.FullName, "src", "DogAI.UI");
        var files = new[]
        {
            "Pages/Settings.razor", "Components/Voice/VoiceSettings.razor", "Components/Images/PictureSettings.razor",
            "Components/Library/PrivacySettings.razor", "Components/Library/WindowsSettings.razor", "Components/Library/BackupSettings.razor",
        };
        var text = string.Concat(files.Select(f => File.ReadAllText(Path.Combine(ui, f))));
        foreach (var entry in SettingsCatalog.Entries)
            Assert.True(Regex.IsMatch(text, $"id=\"{Regex.Escape(entry.Anchor)}\""), $"No element has id \"{entry.Anchor}\".");
    }
}
