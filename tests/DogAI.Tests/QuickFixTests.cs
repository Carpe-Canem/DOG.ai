using DogAI.Core.Characters;
using DogAI.Core.Images;
using DogAI.Core.Search;
using DogAI.Core.Settings;
using DogAI.Core.Stories;

namespace DogAI.Tests;

/// <summary>M14: the seed row in Edit and redraw, the Settings and Search buttons going back, and unsaved editor typing held meanwhile.</summary>
public sealed class QuickFixTests
{
    // ---- The seed row ----

    [Theory]
    [InlineData("", SeedMode.New, -1)]
    [InlineData("   ", SeedMode.New, -1)]
    [InlineData("42", SeedMode.Keep, 42)] // the picture's own seed is Keep
    [InlineData(" 42 ", SeedMode.Keep, 42)]
    [InlineData("7", SeedMode.Typed, 7)]
    [InlineData("0", SeedMode.Typed, 0)]
    [InlineData("1,234,567", SeedMode.Typed, 1234567)]
    [InlineData("4294967295", SeedMode.Typed, 4294967295)]
    public void A_typed_seed_is_read_as_a_whole_number(string typed, SeedMode mode, long seed)
    {
        var choice = SeedChoice.Typed(typed, 42);
        Assert.Equal(mode, choice.Mode);
        Assert.Null(choice.Problem);
        Assert.Equal(seed, choice.For(42));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("12.5")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("4294967296")]
    [InlineData("99999999999999999999999")]
    [InlineData("٣")] // a digit, but not one the engine reads
    public void A_seed_that_cannot_be_drawn_says_so_and_is_never_sent(string typed)
    {
        var choice = SeedChoice.Typed(typed, 42);
        Assert.Equal(SeedMode.Typed, choice.Mode);
        Assert.Equal(SeedChoice.ProblemText, choice.Problem);
        Assert.Contains("4,294,967,295", choice.Problem);
        Assert.Throws<InvalidOperationException>(() => choice.For(42));
    }

    [Fact]
    public void The_box_shows_what_each_choice_means()
    {
        Assert.Equal("42", SeedChoice.Keep.Shown(42));
        Assert.Equal("", SeedChoice.Keep.Shown(-1));
        Assert.Equal("", SeedChoice.New.Shown(42));
        Assert.Equal("7", SeedChoice.Typed("7", 42).Shown(42));
        Assert.Equal(42, SeedChoice.Keep.For(42));
        Assert.Equal(-1, SeedChoice.New.For(42));
    }

    private const string Root = @"C:\models\image";
    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    [Fact]
    public async Task Redraw_keeps_the_seed_by_default_and_takes_a_new_or_typed_one_when_asked()
    {
        var engine = new PortraitTests.WebpEngine();
        var tries = new PictureTries(engine);
        var first = await tries.DrawAsync(Root, "il.safetensors", Illustrious, ImagePrompt.Compose(Illustrious, "1girl"), PictureQuality.Draft,
            PictureShape.Tall, "Mara.", TagNotes.None);
        var firstImage = first.Image;

        // Keep, as before M14: the same seed, and with nothing else changed the same picture.
        var kept = await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall, seed: SeedChoice.Keep);
        Assert.Equal(first.Base.Seed, engine.Requests[^1].Seed);
        Assert.Equal(first.Image, kept.Image);
        await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall);
        Assert.Equal(first.Base.Seed, engine.Requests[^1].Seed);

        // New seed: the engine picks one and the new try remembers it.
        var fresh = await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall, seed: SeedChoice.New);
        Assert.Equal(-1, engine.Requests[^1].Seed);
        Assert.NotEqual(first.Base.Seed, fresh.Base.Seed);
        Assert.Equal(engine.LastSeed, fresh.Base.Seed);
        Assert.Equal("Mara.", fresh.Moment); // the prompt is the plan's: its moment still describes it

        // A typed number is drawn exactly.
        var typed = await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall, seed: SeedChoice.Typed("4294967295", first.Base.Seed));
        Assert.Equal(4294967295, engine.Requests[^1].Seed);
        Assert.Equal(4294967295, typed.Base.Seed);

        // A number that cannot be drawn is refused before anything is sent.
        var sent = engine.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall, seed: SeedChoice.Typed("-5", first.Base.Seed)));
        Assert.Equal(sent, engine.Requests.Count);

        // Every redraw is a new try; the one it came from is untouched.
        Assert.Equal(5, tries.All.Count);
        Assert.Same(first, tries.All[0]);
        Assert.Same(firstImage, first.Image);
    }

    [Fact]
    public async Task A_kept_picture_redrawn_with_a_new_seed_is_a_new_try_and_the_kept_one_stays()
    {
        var engine = new PortraitTests.WebpEngine();
        var tries = new PictureTries(engine);
        var keptPicture = new Picture { Id = 7, Checkpoint = "il.safetensors", Prompt = "1girl", Width = 832, Height = 1216, Seed = 5, Steps = 28, Sampler = "euler", Image = KeptPictureTests.Webp(832, 1216) };
        var kept = tries.Adopt(keptPicture, Illustrious);

        var redrawn = await tries.RedrawAsync(Root, kept, kept.Base.Prompt, kept.Quality, kept.Shape, seed: SeedChoice.New);
        Assert.Null(redrawn.KeptId);
        Assert.Equal(7, tries.All[0].KeptId);
        Assert.Equal(5, tries.All[0].Base.Seed);
        Assert.NotEqual(5, redrawn.Base.Seed);
    }

    // ---- Settings and Search go back ----

    [Theory]
    [InlineData("settings")]
    [InlineData("settings/voice")]
    [InlineData("/settings/pictures")]
    [InlineData("Settings")]
    [InlineData("search")]
    [InlineData("search?q=dragon")]
    [InlineData("settings#set-general-thinking")]
    public void Settings_and_search_are_never_remembered(string address)
    {
        var place = new ReturnPlace();
        place.Visited("general/12");
        place.Visited(address);
        Assert.Equal("general/12", place.Remembered);
    }

    [Fact]
    public void A_button_goes_back_only_from_its_own_place()
    {
        var place = new ReturnPlace();

        // Nothing remembered yet: back goes to the look's start page.
        Assert.Equal("", place.Target("settings", ReturnPlace.Settings));
        Assert.Equal("Back to the start page", place.Title("settings", ReturnPlace.Settings, "Settings"));

        place.Visited("characters/3/edit");
        Assert.Equal("settings", place.Target("characters/3/edit", ReturnPlace.Settings));
        Assert.Equal("Settings", place.Title("characters/3/edit", ReturnPlace.Settings, "Settings"));
        Assert.Equal("characters/3/edit", place.Target("settings/chats", ReturnPlace.Settings));
        Assert.Equal("Back to the character editor", place.Title("settings/chats", ReturnPlace.Settings, "Settings"));
        Assert.Equal("characters/3/edit", place.Target("search", ReturnPlace.Search));

        // From Settings, the Search button leads into Search, and back from there still goes to the editor.
        Assert.Equal("search", place.Target("settings", ReturnPlace.Search));
        place.Visited("search");
        Assert.Equal("characters/3/edit", place.Target("search", ReturnPlace.Search));

        // A page whose name only starts like a place is not inside it.
        Assert.False(ReturnPlace.IsIn("settingsx", ReturnPlace.Settings));
        Assert.False(ReturnPlace.IsIn("general", ReturnPlace.Search));
        Assert.True(ReturnPlace.IsIn("/search?q=x", ReturnPlace.Search));
    }

    [Fact]
    public void Every_screen_is_named_by_its_own_title_and_other_pages_by_their_kind()
    {
        foreach (var screen in QuickJump.Screens.Where(s => !ReturnPlace.IsIn(s.Route, ReturnPlace.Settings) && !ReturnPlace.IsIn(s.Route, ReturnPlace.Search)))
            Assert.Equal(screen.Title, ReturnPlace.NameOf(screen.Route));
        Assert.Equal(DogAI.Core.Models.ModelsPlaces.LibraryName, ReturnPlace.NameOf("models/library"));
        Assert.Equal("New story", ReturnPlace.NameOf("story/new?character=3"));
        Assert.Equal("the chat", ReturnPlace.NameOf("general/12"));
        Assert.Equal("the chat", ReturnPlace.NameOf("characters/chat/4"));
        Assert.Equal("the group chat", ReturnPlace.NameOf("characters/group/5"));
        Assert.Equal("the character", ReturnPlace.NameOf("characters/3"));
        Assert.Equal("the character editor", ReturnPlace.NameOf("characters/3/edit"));
        Assert.Equal("the lore editor", ReturnPlace.NameOf("characters/3/lore"));
        Assert.Equal("the story", ReturnPlace.NameOf("story/9"));
        Assert.Equal("the story setup", ReturnPlace.NameOf("story/9/setup"));
        Assert.Equal("Documents", ReturnPlace.NameOf("general/documents/2"));
        Assert.Equal("Models", ReturnPlace.NameOf("models/somewhere/else"));
        Assert.Equal("Home", ReturnPlace.NameOf(""));
        var playhouse = new ReturnPlace();
        playhouse.Visited("/");
        Assert.Equal("", playhouse.Target("settings", ReturnPlace.Settings));
        Assert.Equal("Back to Home", playhouse.Title("settings", ReturnPlace.Settings, "Settings"));
    }

    [Fact]
    public void Every_shell_uses_the_buttons_that_go_back()
    {
        var layout = Path.Combine(SolutionRoot(), "src", "DogAI.UI", "Layout");
        foreach (var shell in new[] { "WorkbenchShell.razor", "FiresideShell.razor", "PlayhouseShell.razor" })
        {
            var text = File.ReadAllText(Path.Combine(layout, shell));
            Assert.Contains("<PlaceButton Place=\"@ReturnPlace.Settings\"", text);
            Assert.Contains("<PlaceButton Place=\"@ReturnPlace.Search\"", text);
            Assert.DoesNotContain("href=\"settings\"", text);
            Assert.DoesNotContain("href=\"search\"", text);
        }
    }

    // ---- Unsaved typing held while away ----

    [Fact]
    public void A_held_form_comes_back_once_under_its_address()
    {
        var held = new UnsavedForms();
        var form = new List<string> { "typed" };
        held.Hold("/characters/5/edit#x", form);
        Assert.True(held.Holds("characters/5/edit"));
        Assert.Null(held.Take<string>("characters/5/edit")); // another kind of form is not handed over
        Assert.Same(form, held.Take<List<string>>("Characters/5/edit"));
        Assert.Null(held.Take<List<string>>("characters/5/edit")); // taken: the editor holds it again if left unsaved
        Assert.Equal(0, held.Count);

        held.Hold("story/new?character=3", form);
        Assert.False(held.Holds("story/new"));
        held.Forget("story/new?character=3");
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public void A_change_anywhere_in_a_form_changes_its_print_and_pictures_are_hashed()
    {
        var picture = new byte[2_000_000];
        picture[5] = 1;
        var card = new Character { Name = "Mara", Description = "A sailor.", Image = picture, AlternateGreetings = ["Ahoy."] };
        var before = UnsavedForms.Print(card, Looks.None, "sea", false);
        Assert.Equal(before, UnsavedForms.Print(card, Looks.None, "sea", false));
        Assert.True(before.Length < 5_000); // the picture is counted by a hash, not copied

        card.AlternateGreetings[0] = "Ahoy there.";
        var greeting = UnsavedForms.Print(card, Looks.None, "sea", false);
        Assert.NotEqual(before, greeting);

        card.Image = (byte[])picture.Clone();
        Assert.Equal(greeting, UnsavedForms.Print(card, Looks.None, "sea", false)); // the same bytes are the same picture
        card.Image[6] = 1;
        Assert.NotEqual(greeting, UnsavedForms.Print(card, Looks.None, "sea", false));

        Assert.NotEqual(UnsavedForms.Print(card, Looks.None), UnsavedForms.Print(card, new Looks("silver hair")));
        Assert.NotEqual(UnsavedForms.Print(card, true), UnsavedForms.Print(card, false));

        var story = new Story { Bible = new StoryBible { Premise = "A lighthouse." } };
        var storyBefore = UnsavedForms.Print(story, "Title");
        story.Outline.Add(new StoryChapter { Title = "One" });
        Assert.NotEqual(storyBefore, UnsavedForms.Print(story, "Title"));
    }

    private static string SolutionRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
