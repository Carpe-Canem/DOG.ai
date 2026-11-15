using System.IO.Compression;
using System.Xml.Linq;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Stories;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>M9 step 8: story illustrations on their own (who may be in a picture, the rating on Try again, exports).</summary>
public sealed class IllustrationTests
{
    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private static ChatMessage Passage(long id, long? parent, string text, long? picture = null) =>
        new() { Id = id, ParentId = parent, Role = ChatRole.Assistant, Content = text, ExtraJson = new StoryTurn { Picture = picture }.ToJson() };

    private static ChatMessage Action(long id, long? parent, string text) => new() { Id = id, ParentId = parent, Role = ChatRole.User, Content = text };

    private static Story Adventure() => new() { Kind = StoryKind.Interactive, Bible = new StoryBible { Premise = "A courier crosses a flooded city." } };

    // ---- The passage's picture ----

    [Fact]
    public void A_passage_with_only_a_picture_still_keeps_it()
    {
        // An authored passage has no choices, state, roll or chapter: the picture alone must still be written.
        var json = new StoryTurn { Picture = 42 }.ToJson();
        Assert.NotNull(json);
        Assert.Equal(42, StoryTurn.Of(new ChatMessage { Role = ChatRole.Assistant, ExtraJson = json }).Picture);
        Assert.Null(new StoryTurn().ToJson());

        var both = StoryTurn.Of(new ChatMessage { ExtraJson = new StoryTurn { Choices = [new StoryChoice { Text = "Run" }], Picture = 7 }.ToJson() });
        Assert.Equal(7, both.Picture);
        Assert.Equal("Run", both.Choices![0].Text);

        // Only passages show pictures.
        Assert.Null(Illustrations.Shown(new ChatMessage { Role = ChatRole.User, ExtraJson = json }));
        Assert.Equal(42, Illustrations.Shown(new ChatMessage { Role = ChatRole.Assistant, ExtraJson = json }));
    }

    // ---- Who may be in it ----

    [Fact]
    public void The_people_are_the_cards_with_their_looks_then_the_cast_box_names()
    {
        var mara = new Character { Id = 1, Name = "Mara", Description = "A scout in a green cloak." };
        new Looks("1girl, grey hair, braid", "Maid_marian.safetensors", 0.8).SaveTo(mara);
        var wolf = new Character { Id = 2, Name = "Garrick", Description = "A wolf mercenary." };
        var story = Adventure();
        story.Bible.Cast = "- Elara Kaine: a cartographer with ink-stained hands\nSilas (the harbour master)\nthe crowd, noisy";
        story.Bible.World = "Canals everywhere.";

        var people = Illustrations.People(story, [new StoryCastMember(mara, true), new StoryCastMember(wolf, false)]);

        Assert.Equal(["Mara", "Garrick", "Elara Kaine", "Silas"], people.Select(p => p.Name));
        Assert.Equal("1girl, grey hair, braid", people[0].Looks.Tags);
        Assert.Equal("Maid_marian.safetensors", people[0].Looks.Lora);
        Assert.True(people[0].HasLooks);
        Assert.False(people[1].HasLooks);
        Assert.Equal("A scout in a green cloak.", people[0].About);
        Assert.Equal("a cartographer with ink-stained hands", people[2].About);
        Assert.Equal("the harbour master", people[3].About);
        Assert.Equal(Looks.None, people[2].Looks);

        var cast = Illustrations.CastFor(people);
        Assert.Equal([true, false, false, false], cast.Select(c => c.HasLooks));
        Assert.Equal("Premise: A courier crosses a flooded city.\nWorld: Canals everywhere.", Illustrations.Background(story));
    }

    [Fact]
    public async Task The_planner_gets_the_passage_two_people_at_most_and_the_background()
    {
        var engine = new Answer("""{"moment":"Mara looks out over the canal.","people":[{"name":"Mara","kind":"girl","looks":[],"clothing":["green cloak"],"expression":["smile"],"pose":["standing"]},{"name":"Garrick","kind":"boy","looks":[],"clothing":[],"expression":[],"pose":[]},{"name":"Elara Kaine","kind":"girl","looks":[],"clothing":[],"expression":[],"pose":[]}],"place":["canal"],"light":["night"],"framing":"cowboy shot","view":"straight on"}""");
        var story = Adventure();
        story.Bible.Cast = "Elara Kaine: a cartographer";
        var mara = new Character { Id = 1, Name = "Mara", Description = "A scout." };
        new Looks("1girl, grey hair").SaveTo(mara);
        var people = Illustrations.People(story, [new StoryCastMember(mara, true), new StoryCastMember(new Character { Id = 2, Name = "Garrick" }, false)]);

        var scene = await Illustrations.PlanAsync(new PictureWriter(engine), story, people, Passage(5, null, "Mara stood by the canal at night."), PromptStyle.Tags, PictureRating.General);

        Assert.Equal(Illustrations.MaxPeople, scene.People.Count);
        Assert.Contains("Mara stood by the canal at night.", engine.User);
        Assert.Contains("Premise: A courier crosses a flooded city.", engine.User);
        Assert.Contains("Elara Kaine: a cartographer", engine.User);
        Assert.Contains("at most 2", engine.System);
        Assert.Contains("Leave it empty for Mara", engine.System);
    }

    // ---- Try again under the rating chosen now ----

    /// <summary>Requests compared field by field (their LoRA lists are compared by content).</summary>
    private static void Same(ImageRequest expected, ImageRequest actual)
    {
        Assert.Equal(expected with { Loras = null }, actual with { Loras = null });
        Assert.Equal(expected.Loras ?? [], actual.Loras ?? []);
    }

    [Fact]
    public void Try_again_takes_the_rating_chosen_now()
    {
        var highest = ImagePrompt.Compose(Illustrious, "1girl, forest", rating: PictureRating.Explicit);
        Assert.Contains(Illustrious.RatingsOrDefault().Explicit, highest.Prompt);

        // Lowered to General: the highest level's words leave the prompt and go into the negative prompt.
        var general = PictureRatings.Reapply(highest, Illustrious, PictureRating.General);
        Same(ImagePrompt.Compose(Illustrious, "1girl, forest", rating: PictureRating.General), general);
        Assert.Contains(PictureRatings.Pieces(Illustrious.RatingsOrDefault().Explicit)[0], general.NegativePrompt);
        Assert.DoesNotContain(PictureRatings.Pieces(Illustrious.RatingsOrDefault().Explicit)[0], general.Prompt);

        // Raised again: back to the same request, with the card's own negative prompt.
        var back = PictureRatings.Reapply(general, Illustrious, PictureRating.Explicit);
        Assert.Equal(highest, back);
        Assert.Equal(Illustrious.Negative, back.NegativePrompt);

        // The same rating changes nothing, and the subject is never touched (even a tag that is also a rating word).
        Assert.Equal(general, PictureRatings.Reapply(general, Illustrious, PictureRating.General));
        var tricky = ImagePrompt.Compose(Illustrious, "1girl, general, sensitive", rating: PictureRating.Sensitive);
        var redone = PictureRatings.Reapply(tricky, Illustrious, PictureRating.General);
        Assert.Equal(Join(Illustrious.QualityBefore, "general", "1girl, general, sensitive", Illustrious.QualityAfter), redone.Prompt);
    }

    private static string Join(params string[] parts) => string.Join(", ", parts.Where(p => p.Length > 0));

    [Fact]
    public void Try_again_handles_families_without_some_rating_words()
    {
        var pony = CheckpointSettings.ForFamily(ImageFamily.Pony);
        var safe = ImagePrompt.Compose(pony, "1girl", rating: PictureRating.General);
        // Pony has no Sensitive words: the safe word goes and nothing takes its place.
        Same(ImagePrompt.Compose(pony, "1girl", rating: PictureRating.Sensitive), PictureRatings.Reapply(safe, pony, PictureRating.Sensitive));
        Same(safe, PictureRatings.Reapply(PictureRatings.Reapply(safe, pony, PictureRating.Sensitive), pony, PictureRating.General));

        // A sentence checkpoint has no rating words: the prompt stays exactly as written.
        var sdxl = CheckpointSettings.ForFamily(ImageFamily.Sdxl);
        var sentence = ImagePrompt.Compose(sdxl, "A woman, smiling, by a canal", rating: PictureRating.General);
        Assert.Equal(sentence.Prompt, PictureRatings.Reapply(sentence, sdxl, PictureRating.Explicit).Prompt);

        // A prompt the owner rewrote without the quality words: rating words at its start are still swapped.
        var bare = Illustrious with { QualityBefore = "" };
        var edited = ImagePrompt.Compose(bare, "1girl", rating: PictureRating.Explicit);
        Assert.Equal(Join("general", "1girl", bare.QualityAfter), PictureRatings.Reapply(edited, bare, PictureRating.General).Prompt);
    }

    // ---- Exports ----

    private static readonly byte[] Webp = KeptPictureTests.Webp(1216, 832, 300, 3);
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    [Fact]
    public void Html_carries_the_pictures_inside_the_page_and_markdown_none()
    {
        var path = new[] { Passage(1, null, "You wake.", picture: 10), Action(2, 1, "Jump"), Passage(3, 2, "You soar.", picture: 99), Passage(4, 3, "Nothing.") };
        var pictures = new Dictionary<long, StoryExport.ExportPicture> { [10] = new(Webp, "image/webp") };

        var html = StoryExport.ToHtml("Run", Adventure(), path, pictures);

        // Above its passage, as a data: address; a picture that is not there (99) is left out.
        var figure = html.IndexOf("<figure class=\"picture\"><img src=\"data:image/webp;base64," + Convert.ToBase64String(Webp), StringComparison.Ordinal);
        Assert.True(figure >= 0);
        Assert.True(figure < html.IndexOf("You wake.", StringComparison.Ordinal));
        Assert.Equal(1, html.Split("<figure").Length - 1);
        Assert.DoesNotContain("http", html);
        Assert.Contains("img{max-width:100%", html);

        Assert.DoesNotContain("data:", StoryExport.ToMarkdown("Run", Adventure(), path));
        Assert.DoesNotContain("<figure", StoryExport.ToHtml("Run", Adventure(), path));
    }

    [Fact]
    public void The_epub_holds_each_picture_once_as_a_file_of_the_book()
    {
        var story = new Story { Kind = StoryKind.Authored, Outline = [new StoryChapter { Title = "One" }, new StoryChapter { Title = "Two" }] };
        var path = new[]
        {
            new ChatMessage { Id = 1, Role = ChatRole.User, ExtraJson = new StoryTurn { Chapter = 0 }.ToJson() },
            Passage(2, 1, "The letter came.\n\nline one  \nline two", picture: 10),
            new ChatMessage { Id = 3, ParentId = 2, Role = ChatRole.User, ExtraJson = new StoryTurn { Chapter = 1 }.ToJson() },
            Passage(4, 3, "They sailed.", picture: 11),
        };
        var pictures = new Dictionary<long, StoryExport.ExportPicture> { [10] = new(Jpeg, "image/jpeg"), [11] = new(Webp, "image/webp") };

        var bytes = StoryExport.ToEpub("The Light", story, path, new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc), pictures);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }
        byte[] Bytes(string name)
        {
            using var stream = zip.GetEntry(name)!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        var opf = XDocument.Parse(Read("OEBPS/content.opf"));
        XNamespace ns = "http://www.idpf.org/2007/opf";
        var items = opf.Descendants(ns + "item").ToDictionary(i => (string)i.Attribute("href")!, i => (string)i.Attribute("media-type")!);
        Assert.Equal("image/jpeg", items["images/picture-1.jpg"]);
        Assert.Equal("image/webp", items["images/picture-2.webp"]);
        Assert.Equal(Jpeg, Bytes("OEBPS/images/picture-1.jpg"));
        Assert.Equal(Webp, Bytes("OEBPS/images/picture-2.webp"));
        // Pictures are not pages: the reading order is unchanged.
        Assert.Equal(3, opf.Descendants(ns + "itemref").Count());

        var first = Read("OEBPS/chapter-1.xhtml");
        XDocument.Parse(first.Replace("<!DOCTYPE html>", ""));
        Assert.Contains("<figure class=\"picture\"><img src=\"images/picture-1.jpg\" alt=\"Illustration\" /></figure>", first);
        Assert.True(first.IndexOf("<figure", StringComparison.Ordinal) < first.IndexOf("The letter came.", StringComparison.Ordinal));
        Assert.Contains("images/picture-2.webp", Read("OEBPS/chapter-2.xhtml"));

        // Without pictures the book is as before.
        using var plain = new ZipArchive(new MemoryStream(StoryExport.ToEpub("The Light", story, path)));
        Assert.DoesNotContain(plain.Entries, e => e.FullName.Contains("images/"));
    }

    /// <summary>A chat model that answers one fixed text and remembers what it was asked.</summary>
    private sealed class Answer(string answer) : IChatEngine
    {
        public bool IsReady => true;
        public string? ModelName => "fake";
        public int ContextLength => 8192;
        public string System { get; private set; } = "";
        public string User { get; private set; } = "";

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Task.FromResult(text.Length / 4);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => Task.FromResult("");

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            System = messages[0].Content;
            User = messages[^1].Content;
            return One();
        }

        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) => One();

        private async IAsyncEnumerable<ChatDelta> One()
        {
            await Task.Yield();
            yield return new ChatDelta(answer, null, null);
        }
    }
}

/// <summary>Story illustrations through the real chat service and SQLite stores.</summary>
public sealed partial class ChatServiceTests
{
    private IPictureStore PictureStore => _services.GetRequiredService<IPictureStore>();

    private static PictureTry DrawTry(PictureTries tries) =>
        tries.DrawAsync(Path.Combine(Path.GetTempPath(), "dogai-image-root"), "il.safetensors", CheckpointSettings.ForFamily(ImageFamily.Illustrious),
            ImagePrompt.Compose(CheckpointSettings.ForFamily(ImageFamily.Illustrious), "1girl, canal", width: 1216, height: 832, rating: PictureRating.General),
            PictureQuality.Draft, PictureShape.Wide, PictureSize.Normal, "She looks out.", TagNotes.None).GetAwaiter().GetResult();

    private async Task<(long Story, long Passage)> IllustratedStoryAsync()
    {
        var story = await _chat.StartStoryAsync("Canals", new Story { Kind = StoryKind.Interactive }, "You wake by the canal.");
        return (story.Id, story.ActiveLeafId!.Value);
    }

    private async Task<long?> ShownPictureAsync(long story, long passage) =>
        StoryTurn.Of((await _store.GetMessagesAsync(story)).Single(m => m.Id == passage)).Picture;

    [Fact]
    public async Task A_passage_keeps_one_picture_which_can_be_replaced_and_removed()
    {
        var (story, passage) = await IllustratedStoryAsync();
        var tries = new PictureTries(new PortraitTests.WebpEngine());
        var other = await PictureStore.AddAsync(DrawTry(tries).ToKeep(PictureOwners.Message, 999_999));

        var first = await Illustrations.KeepAsync(PictureStore, _chat, story, passage, DrawTry(tries));
        Assert.Equal(first, await ShownPictureAsync(story, passage));
        var row = (await PictureStore.GetAsync(first))!;
        Assert.Equal((PictureOwners.Message, passage), (row.OwnerKind, row.OwnerId));
        Assert.Equal((1216, 832), (row.Width, row.Height));

        // Another picture replaces it; the old row goes.
        var second = await Illustrations.KeepAsync(PictureStore, _chat, story, passage, DrawTry(tries));
        Assert.NotEqual(first, second);
        Assert.Equal(second, await ShownPictureAsync(story, passage));
        Assert.Equal([second], (await PictureStore.ListAsync(PictureOwners.Message, passage)).Select(p => p.Id));

        // The kept picture chosen again (Use on "the picture in use"): nothing new is written.
        var adopted = new PictureTries(new PortraitTests.WebpEngine()).Adopt((await PictureStore.GetAsync(second))!, CheckpointSettings.ForFamily(ImageFamily.Illustrious));
        Assert.Equal(second, await Illustrations.KeepAsync(PictureStore, _chat, story, passage, adopted));
        Assert.Single(await PictureStore.ListAsync(PictureOwners.Message, passage));

        // The rest of the passage's story details are kept, and loading for an export finds it.
        var shown = await ShownAsync(story);
        Assert.Equal([second], (await Illustrations.LoadAsync(PictureStore, shown)).Keys);

        await Illustrations.RemoveAsync(PictureStore, _chat, story, passage);
        Assert.Null(await ShownPictureAsync(story, passage));
        Assert.Empty(await PictureStore.ListAsync(PictureOwners.Message, passage));
        Assert.Empty(await Illustrations.LoadAsync(PictureStore, await ShownAsync(story)));
        Assert.NotNull(await PictureStore.GetAsync(other.Id)); // another owner's picture is never touched
    }

    [Fact]
    public async Task A_picture_for_a_passage_that_is_gone_leaves_nothing()
    {
        var (story, passage) = await IllustratedStoryAsync();
        var tries = new PictureTries(new PortraitTests.WebpEngine());
        var wrong = passage + 1000;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Illustrations.KeepAsync(PictureStore, _chat, story, wrong, DrawTry(tries)));
        Assert.Empty(await PictureStore.ListAsync(PictureOwners.Message, wrong));

        // A passage pointing at a row that is gone shows nothing (and an export leaves it out).
        await _chat.SetPassagePictureAsync(story, passage, 123_456);
        Assert.Empty(await Illustrations.LoadAsync(PictureStore, await ShownAsync(story)));
    }

    [Fact]
    public async Task Deleting_a_story_takes_its_pictures()
    {
        var (story, passage) = await IllustratedStoryAsync();
        var tries = new PictureTries(new PortraitTests.WebpEngine());
        var kept = await Illustrations.KeepAsync(PictureStore, _chat, story, passage, DrawTry(tries));
        var (otherStory, otherPassage) = await IllustratedStoryAsync();
        var otherKept = await Illustrations.KeepAsync(PictureStore, _chat, otherStory, otherPassage, DrawTry(tries));

        await _store.DeleteAsync(story);

        Assert.Null(await PictureStore.GetAsync(kept));
        Assert.NotNull(await PictureStore.GetAsync(otherKept));
    }

    [Fact]
    public async Task Choices_worked_out_while_a_picture_is_kept_do_not_drop_it()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        var passage = (await ShownAsync(id)).Single().Id;
        var tries = new PictureTries(new PortraitTests.WebpEngine());
        long kept = 0;

        // The choices request: while the model "writes", the owner presses Use on a picture for this passage.
        async IAsyncEnumerable<ChatDelta> KeepThenAnswer()
        {
            kept = await Illustrations.KeepAsync(PictureStore, _chat, id, passage, DrawTry(tries));
            await foreach (var delta in FakeEngine.Say(ChoicesJson)) yield return delta;
        }
        _engine.Reply = _ => _engine.Samplers[^1].JsonSchema is null ? FakeEngine.Say("unused") : KeepThenAnswer();

        await _chat.SuggestChoicesAsync(id, passage);

        var turn = StoryTurn.Of((await ShownAsync(id)).Single());
        Assert.Equal(kept, turn.Picture);
        Assert.Equal(["Open the door", "Climb the wall"], turn.Choices!.Select(c => c.Text));
    }
}
