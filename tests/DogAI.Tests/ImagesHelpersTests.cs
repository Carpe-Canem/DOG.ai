using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DogAI.Core.Chat;
using DogAI.Core.Images;

namespace DogAI.Tests;

/// <summary>
/// The Images section's helpers (M16 step 4): Check my tags (offers only, every other character of the box kept as
/// typed), the LoRA trigger chips (file words cleaned, inserted only on a click, never twice) and Write it for me… (a
/// DOG.ai model job: schema, no thinking, a token limit, the model's tags checked). Left-out tags are made up here.
/// </summary>
public sealed class ImagesHelpersTests
{
    private static readonly CheckpointSettings Card = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private static readonly TagList Tags = TagList.Of("1girl", "solo", "red dress", "blue eyes", "smile", "long hair", "silver hair", "forest", "night", "cat", "pleated skirt",
        "hatsune miku (cosplay)");

    private static readonly ISet<string> NoneFine = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void Pieces_keep_their_place_and_lose_brackets_and_weights_only_in_the_core()
    {
        const string text = " (red dress:1.2), [blue eyes] ,<lora:style:0.8>, BREAK\n((masterpiece)), hatsune miku \\(cosplay\\), a (b) c";
        var pieces = PromptPieces.Of(text);
        Assert.Equal(["red dress", "blue eyes", "<lora:style:0.8>", "BREAK", "masterpiece", "hatsune miku \\(cosplay\\)", "a (b) c"], pieces.Select(p => p.Core));
        Assert.Equal([true, true, false, false, true, true, false], pieces.Select(p => p.Checkable));
        Assert.All(pieces, p => Assert.Equal(p.Core, text.Substring(p.CoreStart, p.CoreLength)));
        Assert.Equal("(red dress:1.2)", text[pieces[0].Start..pieces[0].End]);
        // A group shared by two pieces: neither owns its brackets.
        var shared = PromptPieces.Of("(red dress, blue eyes:1.2)");
        Assert.Equal(["red dress", "blue eyes"], shared.Select(p => p.Core));
        Assert.All(shared, p => Assert.False(p.OwnBrackets));
    }

    [Fact]
    public void The_boxes_a_checkpoint_starts_with_get_no_offers_against_the_real_tag_list()
    {
        foreach (var family in new[] { ImageFamily.Illustrious, ImageFamily.NoobAI, ImageFamily.Pony })
        {
            var card = CheckpointSettings.ForFamily(family);
            var (prompt, negative) = SavedPicturePrompts.FromCard(card);
            var fine = TagCheck.Fine(card);
            Assert.Empty(TagCheck.Offers(prompt, PromptBox.Prompt, TagList.Bundled, fine));
            Assert.Empty(TagCheck.Offers(negative, PromptBox.Negative, TagList.Bundled, fine));
            // The rating words of every level are left alone too.
            foreach (var level in Enum.GetValues<PictureRating>())
                Assert.Empty(TagCheck.Offers(card.RatingsOrDefault().For(level), PromptBox.Prompt, TagList.Bundled, fine));
        }
        // Another checkpoint's quality words are known even when this card does not hold them.
        Assert.Empty(TagCheck.Offers("score_9, newest, masterpiece, best quality", PromptBox.Prompt, TagList.Bundled, TagCheck.Fine(null)));
        // Real tags are not offered either.
        Assert.Empty(TagCheck.Offers("1girl, solo, smile, long hair, red dress", PromptBox.Prompt, TagList.Bundled, TagCheck.Fine(Card)));
    }

    [Fact]
    public void Offers_change_nothing_and_an_accepted_one_changes_only_that_word()
    {
        const string text = "  1girl,\n(pleted skirt:1.2), blue eyes  ,frobnicate\nsmile ";
        var offers = TagCheck.Offers(text, PromptBox.Prompt, Tags, NoneFine);
        Assert.Equal(2, offers.Count);
        var fix = offers[0];
        Assert.Equal(("pleted skirt", "pleated skirt", true), (fix.Written, fix.Use, fix.IsCorrection));
        var unknown = offers[1];
        Assert.Equal(("frobnicate", (string?)null), (unknown.Written, unknown.Use));

        var changed = TagCheck.Accept(text, fix, change: true, Tags, NoneFine);
        Assert.Equal("  1girl,\n(pleated skirt:1.2), blue eyes  ,frobnicate\nsmile ", changed);
        var removed = TagCheck.Accept(changed, unknown, change: false, Tags, NoneFine);
        Assert.Equal("  1girl,\n(pleated skirt:1.2), blue eyes\nsmile ", removed);
        Assert.Empty(TagCheck.Offers(removed, PromptBox.Prompt, Tags, NoneFine));

        // "Change" on an offer to take a word out does nothing; the word gone from the box leaves it as it is.
        Assert.Equal(text, TagCheck.Accept(text, unknown, change: true, Tags, NoneFine));
        Assert.Equal("smile", TagCheck.Accept("smile", fix, change: true, Tags, NoneFine));
    }

    [Fact]
    public void Taking_a_word_out_of_a_shared_group_keeps_the_group_and_its_weight()
    {
        var first = TagCheck.Offers("(frobnicate, red dress:1.2), smile", PromptBox.Prompt, Tags, NoneFine).Single();
        Assert.Equal("(red dress:1.2), smile", TagCheck.Accept("(frobnicate, red dress:1.2), smile", first, false, Tags, NoneFine));
        var last = TagCheck.Offers("smile, (red dress, frobnicate:1.2)", PromptBox.Prompt, Tags, NoneFine).Single();
        Assert.Equal("smile, (red dress:1.2)", TagCheck.Accept("smile, (red dress, frobnicate:1.2)", last, false, Tags, NoneFine));
        var own = TagCheck.Offers("smile, ((frobnicate:1.3)), night", PromptBox.Prompt, Tags, NoneFine).Single();
        Assert.Equal("smile, night", TagCheck.Accept("smile, ((frobnicate:1.3)), night", own, false, Tags, NoneFine));
    }

    [Fact]
    public void A_word_written_twice_is_one_offer_and_is_changed_everywhere_in_the_owners_spelling()
    {
        const string text = "pleted_skirt, smile, pleted_skirt";
        var offer = TagCheck.Offers(text, PromptBox.Negative, Tags, NoneFine).Single();
        Assert.Equal((PromptBox.Negative, 2, "pleted skirt"), (offer.Box, offer.Count, offer.Key));
        Assert.Equal("pleated_skirt, smile, pleated_skirt", TagCheck.Accept(text, offer, true, Tags, NoneFine));
        Assert.Equal("pleted_skirt → pleated_skirt (written 2 times)", TagCheck.Describe(offer));

        // Two tags packed into one become both; brackets in a real tag are escaped as the engine needs.
        var packed = TagCheck.Offers("long silver hair", PromptBox.Prompt, Tags, NoneFine).Single();
        Assert.Equal("long hair, silver hair", TagCheck.Accept("long silver hair", packed, true, Tags, NoneFine));
        Assert.Empty(TagCheck.Offers("hatsune miku \\(cosplay\\)", PromptBox.Prompt, Tags, NoneFine));
    }

    [Fact]
    public void A_piece_whose_describing_word_is_unknown_is_not_offered_as_its_last_word()
    {
        var tags = TagList.Of("hair", "silver hair", "smile");
        var offer = TagCheck.Offers("smile, (silvr hair:1.2)", PromptBox.Prompt, tags, NoneFine).Single();
        Assert.Equal(((string?)null, "hair"), (offer.Use, offer.Only));
        Assert.Equal("silvr hair: not in the tag list (only “hair” is)", TagCheck.Describe(offer));
        Assert.Equal("smile, (silvr hair:1.2)", TagCheck.Accept("smile, (silvr hair:1.2)", offer, true, tags, NoneFine));
        Assert.Equal("smile", TagCheck.Accept("smile, (silvr hair:1.2)", offer, false, tags, NoneFine));
        // The chat model's tags still keep the thing itself, as before.
        Assert.Equal(["hair"], tags.Resolve("silvr hair"));
    }

    [Fact]
    public void Words_left_alone_are_never_offered()
    {
        var fine = TagCheck.Fine(Card, ["mychar_v2, glowy style"]);
        Assert.Empty(TagCheck.Offers("masterpiece, mychar_v2, glowy style, <lora:mychar:0.8>, BREAK, smile", PromptBox.Prompt, Tags, fine));
        Assert.Empty(TagCheck.Offers("frobnicate", PromptBox.Prompt, TagList.Empty, NoneFine));
    }

    [Fact]
    public void A_left_out_tag_the_owner_typed_is_offered_only_for_taking_out_never_turned_into_a_tag_nearby()
    {
        var tags = TagList.Of("hidden thong", "red dress", "smile").Excluding(TagFingerprints.OfHashes([TagFingerprints.Of("hidden thing")]));
        var offer = TagCheck.Offers("smile, hidden thing", PromptBox.Prompt, tags, NoneFine).Single();
        Assert.Null(offer.Use);
        Assert.Equal("smile", TagCheck.Accept("smile, hidden thing", offer, false, tags, NoneFine));
    }

    [Fact]
    public void Trigger_chips_show_the_owners_words_as_typed_and_clean_the_files()
    {
        var tags = TagList.Of("smile").Excluding(TagFingerprints.OfHashes([TagFingerprints.Of("hidden thing")]));
        var (fromFile, hidden) = LoraChips.For(@"mychar_v2, C:\Users\maker\set, /home/maker/x, see https://example.com, hidden thing, " +
            new string('a', 61) + ", glowy style, MyChar_v2", owners: false, tags);
        Assert.Equal(["mychar_v2", "glowy style"], fromFile);
        Assert.Equal(5, hidden);
        Assert.Equal("5 words from the file are not shown.", LoraChips.HiddenNote(hidden));
        Assert.Equal("One word from the file is not shown.", LoraChips.HiddenNote(1));

        var (own, none) = LoraChips.For("hidden thing, my/word", owners: true, tags);
        Assert.Equal(["hidden thing", "my/word"], own);
        Assert.Equal(0, none);

        var many = LoraChips.For(string.Join(", ", Enumerable.Range(1, 12).Select(i => "word" + i)), owners: false, tags).Chips;
        Assert.Equal(LoraChips.Max, many.Count);
    }

    [Fact]
    public void A_chip_goes_after_what_is_typed_and_never_twice()
    {
        Assert.Equal("mychar_v2", PromptPieces.Insert("", "mychar_v2"));
        Assert.Equal("1girl,\nsmile, mychar_v2", PromptPieces.Insert("1girl,\nsmile, ", "mychar_v2"));
        Assert.Equal("1girl, (MyChar V2:1.1), smile", PromptPieces.Insert("1girl, (MyChar V2:1.1), smile", "mychar_v2"));
        Assert.True(PromptPieces.Has("a, (mychar_v2:0.8)", "MyChar V2"));
        Assert.False(PromptPieces.Has("a, mychar", "mychar_v2"));
    }

    [Fact]
    public async Task Write_it_for_me_asks_without_thinking_with_a_schema_and_checks_the_models_tags()
    {
        var tags = TagList.Of("1girl", "solo", "pleated skirt", "forest", "night", "smile").Excluding(TagFingerprints.OfHashes([TagFingerprints.Of("hidden thing")]));
        const string answer = """{"moment":"A girl in a red dress smiles in a forest at night.","tags":["1girl","solo","pleted skirt","Smile","made up words here","hidden thing","forest","night","1girl"]}""";
        var engine = new ScriptedEngine(answer[..30], answer[30..]);
        var draft = await new PictureWriter(engine).PromptAsync("  A girl in a red dress in a forest at night  ", PromptStyle.Tags, PictureRating.General, tags);

        Assert.Equal("1girl, solo, pleated skirt, smile, forest, night", draft.Prompt);
        Assert.Equal("A girl in a red dress smiles in a forest at night.", draft.Moment);
        Assert.Contains(("pleted skirt", "pleated skirt"), draft.Notes.Corrected);
        Assert.Contains("made up words here", draft.Notes.Dropped);

        var sampler = engine.LastSampler!;
        Assert.False(sampler.Thinking);
        Assert.Equal(PictureWriter.MaxTokens, sampler.MaxTokens);
        Assert.NotNull(JsonNode.Parse(sampler.JsonSchema!));
        Assert.Equal("system", engine.LastPrompt![0].Role);
        Assert.Contains("No quality or rating words", engine.LastPrompt[0].Content);
        Assert.Contains(PictureRatings.ForPlanner(PictureRating.General), engine.LastPrompt[0].Content);
        Assert.EndsWith("A girl in a red dress in a forest at night", engine.LastPrompt[1].Content);
    }

    [Fact]
    public async Task Write_it_for_me_with_no_people_mentioned_brings_no_person_in()
    {
        var tags = TagList.Of("1girl", "solo", "no humans", "fox", "fox ears", "snow", "red hair");
        var engine = new ScriptedEngine("""{"moment":"A red fox in the snow.","people":0,"tags":["1girl, solo","fox ears","fox","snow","2girls"]}""");
        var draft = await new PictureWriter(engine).PromptAsync("a red fox in the snow", PromptStyle.Tags, tags: tags);
        Assert.Equal("no humans, fox ears, fox, snow", draft.Prompt);
        Assert.Contains("\"people\"", engine.LastSampler!.JsonSchema);

        // With people mentioned the model's count tags stay as written.
        var two = await new PictureWriter(new ScriptedEngine("""{"moment":"x","people":1,"tags":["1girl","solo","red hair"]}""")).PromptAsync("a girl", PromptStyle.Tags, tags: tags);
        Assert.Equal("1girl, solo, red hair", two.Prompt);
        Assert.True(PictureWriter.IsPersonCount("2girls") && PictureWriter.IsPersonCount("multiple boys") && PictureWriter.IsPersonCount("Solo"));
        Assert.False(PictureWriter.IsPersonCount("fox ears"));
    }

    [Fact]
    public async Task Write_it_for_me_for_a_sentence_checkpoint_gives_one_line_and_refuses_an_empty_answer()
    {
        var engine = new ScriptedEngine("""{"moment":"A lighthouse.","prompt":"a lighthouse on a cliff,\nstormy sea, dusk"}""");
        var draft = await new PictureWriter(engine).PromptAsync("A lighthouse in a storm", PromptStyle.Sentences);
        Assert.Equal("a lighthouse on a cliff, stormy sea, dusk", draft.Prompt);
        Assert.Contains("\"prompt\"", engine.LastSampler!.JsonSchema);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PictureWriter(new ScriptedEngine("""{"moment":"x","tags":["zzz qqq"]}"""))
            .PromptAsync("something", PromptStyle.Tags, tags: Tags));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PictureWriter(new ScriptedEngine("no json")).PromptAsync("something", PromptStyle.Tags, tags: Tags));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PictureWriter(new ScriptedEngine("{}")).PromptAsync("   ", PromptStyle.Tags, tags: Tags));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PictureWriter(new ScriptedEngine("{}") { IsReady = false }).PromptAsync("a cat", PromptStyle.Tags, tags: Tags));
    }

    [Fact]
    public void The_helpers_sentence_is_held_in_memory_only()
    {
        var session = new ImagesSession();
        session.Describe("A knight by a campfire");
        Assert.Equal("A knight by a campfire", session.Sentence);
        // The remembered controls have no place for it (only ImagesSession, which takes no store, holds it).
        Assert.DoesNotContain(typeof(ImagesControls).GetProperties(), p => p.Name.Contains("Sentence", StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(ImagesSession).GetConstructors(), c => Assert.Empty(c.GetParameters()));
    }

    private sealed class ScriptedEngine(params string[] parts) : IChatEngine
    {
        public bool IsReady { get; set; } = true;
        public string? ModelName => "fake";
        public int ContextLength => 8192;
        public SamplerPreset? LastSampler { get; private set; }
        public IReadOnlyList<PromptMessage>? LastPrompt { get; private set; }

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Task.FromResult(text.Length / 4);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => Task.FromResult("");

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            LastPrompt = messages;
            LastSampler = sampler;
            return Stream(ct);
        }

        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) => Stream(ct);

        private async IAsyncEnumerable<ChatDelta> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ChatDelta(part, null, null);
            }
        }
    }
}
