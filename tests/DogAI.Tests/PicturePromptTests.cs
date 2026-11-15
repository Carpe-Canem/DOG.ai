using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>M9 step 6: quality levels and shapes, Looks on cards and personas, the tag check, the planned scene, prompt assembly and the model job.</summary>
public sealed class PicturePromptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dogai-prompts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    /// <summary>A small list in the bundled list's own format: tag as Danbooru writes it, posts, other spellings.</summary>
    private static readonly TagList Tags = TagList.Read(new StringReader(string.Join('\n',
        "# a comment",
        "1girl\t6000000", "1boy\t1500000", "solo\t5000000",
        "silver_hair\t100000", "long_hair\t500000", "blonde_hair\t1600000\tblond_hair yellow_hair", "black_hair\t900000", "blue_hair\t800000",
        "green_eyes\t900000", "beard\t80000", "ponytail\t300000", "gloves\t200000", "thighhighs\t800000",
        "red_dress\t90000", "cloak\t50000", "green_cloak\t9000", "armor\t200000", "apron\t100000",
        "shoes\t100000", "red_shoes\t20000", "scar_on_cheek\t8000", "smile\t3000000", "frown\t60000",
        "sitting\t900000", "standing\t900000", "holding_cup\t30000", "tavern\t5000", "forest\t90000", "lake\t20000",
        "candlelight\t5000", "night\t300000", "sunset\t80000", "^_^\t70000", "cross_(crucifix)\t100", "glowing_fur\t3000", "ear_focus\t135\tear")));

    // ---- Quality levels and shapes ----

    [Fact]
    public void Shapes_are_sdxl_native_sizes()
    {
        Assert.Equal((832, 1216), PictureLevels.Size(PictureShape.Tall, PictureSize.Normal));
        Assert.Equal((1024, 1024), PictureLevels.Size(PictureShape.Square, PictureSize.Normal));
        Assert.Equal((1216, 832), PictureLevels.Size(PictureShape.Wide, PictureSize.Normal));
    }

    [Theory]
    [InlineData(28, 20)]
    [InlineData(30, 21)]
    [InlineData(20, 16)] // never under the floor…
    [InlineData(12, 12)] // …and never more than the checkpoint's own count
    public void Draft_draws_fewer_steps(int steps, int draft) => Assert.Equal(draft, PictureLevels.DraftSteps(steps));

    [Fact]
    public void A_level_changes_only_what_it_is_about()
    {
        var request = ImagePrompt.Compose(Illustrious, "1girl, forest", [("ink.safetensors", new LoraSettings("ink", 0.6))], seed: 77);

        var draft = request.At(PictureQuality.Draft, PictureShape.Wide, PictureSize.Normal);
        Assert.Equal((1216, 832, 20), (draft.Width, draft.Height, draft.Steps));
        Assert.Null(draft.Hires);

        var standard = request.At(PictureQuality.Standard, PictureShape.Tall, PictureSize.Normal);
        Assert.Equal((832, 1216, 28), (standard.Width, standard.Height, standard.Steps));
        Assert.Null(standard.Hires);

        var fine = request.At(PictureQuality.Fine, PictureShape.Tall, PictureSize.Normal);
        Assert.Equal(28, fine.Steps);
        Assert.Equal(new HiresFix(1.5, 14, 0.3, "Lanczos"), fine.Hires);

        // Going back from Fine takes the second pass off again; nothing else moved, so the same seed redraws the same picture.
        Assert.Equal(standard, fine.At(PictureQuality.Standard, PictureShape.Tall, PictureSize.Normal));
        foreach (var r in new[] { draft, standard, fine })
        {
            Assert.Equal((request.Prompt, request.NegativePrompt, 77L, request.Sampler, request.Guidance), (r.Prompt, r.NegativePrompt, r.Seed, r.Sampler, r.Guidance));
            Assert.Equal(request.Loras, r.Loras);
            Assert.Empty(r.Problems());
        }
    }

    [Fact]
    public void Illustrious_defaults_are_the_tuned_ones()
    {
        Assert.Equal(("euler", 28, 5.5, 2), (Illustrious.Sampler, Illustrious.Steps, Illustrious.Guidance, Illustrious.ClipSkip));
        Assert.Equal(PromptStyle.Tags, Illustrious.Style);
    }

    // ---- Looks ----

    [Fact]
    public void Looks_are_made_safe_on_every_read()
    {
        var looks = new Looks("  silver hair,\r\n green eyes,\t scar on cheek, ", "chars\\mara.safetensors", 9).Clean();
        Assert.Equal("silver hair, green eyes, scar on cheek", looks.Tags);
        Assert.Equal("chars/mara.safetensors", looks.Lora);
        Assert.Equal(Looks.MaxStrength, looks.LoraStrength);
        Assert.Equal(["silver hair", "green eyes", "scar on cheek"], looks.Pieces());

        Assert.Equal(Looks.MaxTagsLength, new Looks(new string('a', 5000)).Clean().Tags.Length);
        Assert.Null(new Looks("x", "..\\..\\secret.safetensors").Clean().Lora);
        Assert.Null(new Looks("x", "C:\\loras\\mara.safetensors").Clean().Lora);
        Assert.Null(new Looks("x", "mara.ckpt").Clean().Lora);
        Assert.Null(new Looks("x", null, 0.5).Clean().LoraStrength); // a strength without a LoRA means nothing
        Assert.Null(new Looks("x", "a.safetensors", double.NaN).Clean().LoraStrength);
        Assert.True(new Looks("  ").Clean().IsEmpty);
    }

    [Fact]
    public void A_cards_looks_survive_export_and_import_in_every_format()
    {
        var looks = new Looks("1girl, silver hair, green eyes", "mara.safetensors", 0.8);
        var card = new Character { Name = "Mara", Description = "A scout.", ExtraJson = """{"extensions":{"depth_prompt":{"depth":4},"dogai":{"other":1}},"character_book":{"entries":[]}}""" };
        looks.SaveTo(card);
        Assert.Equal(looks, Looks.Of(card));

        Assert.Equal(looks, Looks.Of(CharacterCard.FromJson(CharacterCard.ToV2Json(card))));
        Assert.Equal(looks, Looks.Of(CharacterCard.FromJson(CharacterCard.ToV3Json(card))));
        var fromPng = CharacterCard.Read(CharacterCard.ToPng(card));
        Assert.Equal(looks, Looks.Of(fromPng));

        // What other apps put in the card is still there, and so is anything else under DOG.ai's own key.
        var extensions = JsonNode.Parse(fromPng.ExtraJson)!["extensions"]!;
        Assert.Equal(4, (int)extensions["depth_prompt"]!["depth"]!);
        Assert.Equal(1, (int)extensions["dogai"]!["other"]!);
        Assert.NotNull(JsonNode.Parse(fromPng.ExtraJson)!["character_book"]);
    }

    [Fact]
    public void Emptying_the_looks_takes_them_out_of_the_card_and_leaves_the_rest()
    {
        var card = new Character { Name = "Mara", ExtraJson = """{"extensions":{"depth_prompt":{"depth":4}}}""" };
        new Looks("silver hair").SaveTo(card);
        Looks.None.SaveTo(card);
        Assert.Equal("""{"extensions":{"depth_prompt":{"depth":4}}}""", card.ExtraJson);

        var plain = new Character { Name = "Ben" };
        new Looks("black hair").SaveTo(plain);
        Looks.None.SaveTo(plain);
        Assert.Equal("", plain.ExtraJson);
        Assert.True(Looks.Of(plain).IsEmpty);
    }

    [Fact]
    public void Looks_on_an_imported_card_are_untrusted()
    {
        var card = CharacterCard.FromJson("""
            {"spec":"chara_card_v2","data":{"name":"Trap","extensions":{"dogai":{"looks":{"tags":"red hair\nextra line","lora":"..\\..\\Windows\\x.safetensors","lora_strength":50}}}}}
            """);
        var looks = Looks.Of(card);
        Assert.Equal("red hair extra line", looks.Tags);
        Assert.Null(looks.Lora);
        Assert.Null(looks.LoraStrength);

        Assert.True(Looks.Of(new Character { Name = "x", ExtraJson = """{"extensions":{"dogai":"not an object"}}""" }).IsEmpty);
        Assert.True(Looks.Of(new Character { Name = "x", ExtraJson = "not json" }).IsEmpty);
        Assert.True(Looks.Of(new Persona { LooksJson = "{broken" }).IsEmpty);
    }

    [Fact]
    public async Task A_personas_looks_are_saved_and_a_database_from_before_them_upgrades()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        await using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261114101933_SearchWipe");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Personas (Name, Description, IsDefault, CreatedAt) VALUES ('Old me', 'From before pictures.', 1, '2026-11-13 10:00:00')");
        }
        await services.MigrateDogDatabaseAsync();

        var store = services.GetRequiredService<ICharacterStore>();
        var old = Assert.Single(await store.ListPersonasAsync());
        Assert.Equal("From before pictures.", old.Description);
        Assert.True(Looks.Of(old).IsEmpty);

        var looks = new Looks("1boy, black hair, beard", "me.safetensors", 0.7);
        looks.SaveTo(old);
        await store.SavePersonaAsync(old);
        Assert.Equal(looks, Looks.Of((await store.GetPersonaAsync(old.Id))!));

        // The saved text names the LoRA by its place in the loras folder, never by a full path.
        Assert.DoesNotContain(":", JsonNode.Parse((await store.GetPersonaAsync(old.Id))!.LooksJson!)!["lora"]!.GetValue<string>());
    }

    // ---- The tag list ----

    [Theory]
    [InlineData("Silver_Hair", "silver hair")]
    [InlineData("  \"long hair\". ", "long hair")]
    [InlineData("(red dress:1.2)", "red dress")]
    [InlineData("(red dress)", "red dress")]
    [InlineData("cross \\(crucifix\\)", "cross (crucifix)")]
    [InlineData("^_^", "^_^")]
    [InlineData("holding   cup", "holding cup")]
    public void Tags_are_compared_in_one_form(string written, string normal) => Assert.Equal(normal, TagList.Normalise(written));

    [Theory]
    [InlineData("silver hair", "silver hair")]            // a real tag
    [InlineData("Silver_Hair", "silver hair")]
    [InlineData("blond hair", "blonde hair")]             // another spelling Danbooru lists
    [InlineData("pony tail", "ponytail")]                 // joined
    [InlineData("thigh-highs", "thighhighs")]
    [InlineData("glove", "gloves")]                       // singular and plural
    [InlineData("cloaks", "cloak")]
    [InlineData("wearing a red dress", "red dress")]      // words that are not part of the tag
    [InlineData("candelight", "candlelight")]             // one slip
    [InlineData("smille", "smile")]
    [InlineData("tattered cloak", "cloak")]               // unknown describing word: the thing itself
    [InlineData("grey cloak", "cloak")]                   // never the nearby "green cloak"
    [InlineData("rad shoes", "shoes")]                    // a slip must not bring in a colour that was not written
    [InlineData("^_^", "^_^")]
    public void A_written_tag_becomes_a_real_one(string written, string real) => Assert.Equal([real], Tags.Resolve(written));

    [Fact]
    public void Two_tags_packed_into_one_are_taken_apart() =>
        Assert.Equal(["long hair", "silver hair"], Tags.Resolve("long silver hair"));

    [Theory]
    [InlineData("mysterious aura")]
    [InlineData("no shoes")]          // "shoes" would say the opposite
    [InlineData("without gloves")]
    [InlineData("notch in ear")]      // "ear" is only another spelling of "ear focus": too weak to keep
    [InlineData("she remembers the war")]
    [InlineData("glowing shoes on fire")]
    [InlineData("glowing aura")]      // not "glowing fur": a whole short word differs
    [InlineData("xq")]
    [InlineData("")]
    public void What_is_not_a_tag_is_dropped(string written) => Assert.Empty(Tags.Resolve(written));

    [Fact]
    public void A_slot_is_checked_as_a_whole()
    {
        var (kept, notes) = Tags.Check(["smile, Smile", "pony tail", "nonsense word", "sitting"], max: 2);
        Assert.Equal(["smile", "ponytail"], kept);
        Assert.Equal([("pony tail", "ponytail")], notes.Corrected);
        Assert.Equal(["nonsense word"], notes.Dropped);

        var (rest, _) = Tags.Check(["smile", "sitting"], already: new HashSet<string> { "smile" });
        Assert.Equal(["sitting"], rest);
    }

    [Fact]
    public void Without_a_list_tags_are_used_as_written()
    {
        Assert.True(TagList.Empty.IsEmpty);
        Assert.Equal(["mysterious aura"], TagList.Empty.Resolve("Mysterious_Aura"));
    }

    [Fact]
    public void Brackets_are_escaped_in_a_prompt() => Assert.Equal("cross \\(crucifix\\)", TagList.ForPrompt("cross (crucifix)"));

    [Fact]
    public void The_bundled_list_loads_and_knows_everyday_tags()
    {
        var list = TagList.Bundled;
        Assert.True(list.Count > 5000, $"only {list.Count} tags are bundled");
        foreach (var tag in new[] { "1girl", "solo", "grey hair", "green eyes", "smile", "sitting", "forest", "night", "red dress", "looking at viewer", "anthro", "wolf" })
            Assert.True(list.Contains(tag), tag);
        // Danbooru files silver hair under grey hair; the list knows such other spellings.
        Assert.Equal(["grey hair"], list.Resolve("silver hair"));
        Assert.Equal(["pointy ears"], list.Resolve("pointed ears"));
        Assert.Equal(["leather armor"], list.Resolve("worn leather armor"));
        // Made-up tags and sentences are dropped, not bent into something nearby.
        foreach (var junk in new[] { "inner strength", "notch in ear", "she remembers the war", "determined expression", "gazing ahead" })
            Assert.Empty(list.Resolve(junk));
        Assert.Equal(["aura"], list.Resolve("glowing aura")); // never "glowing fur"
        Assert.Empty(list.Resolve("lean"));                   // a build, not the pose "leaning" the source data files it under
        Assert.Empty(list.Resolve("center"));                 // another word, not the rare tag "censer" (seen in M16 step 4)
        Assert.Empty(list.Resolve("orange"));                 // the colour, not the fruit the source data files it under (M16 step 4)
        Assert.Equal(["orange (fruit)"], list.Resolve("orange (fruit)"));
        Assert.Empty(list.Resolve("canopy"));                 // of a tree, not of an aircraft (seen in M16 step 6)
        Assert.Equal(["canopy (aircraft)"], list.Resolve("canopy (aircraft)"));
        Assert.Equal(["canopy bed"], list.Resolve("canopy bed"));
        Assert.Equal(["jacket"], list.Resolve("jackett"));    // a slip into a common tag is still put right
        // No artists, characters or series: a name the model slips in is dropped.
        foreach (var name in new[] { "hatsune miku", "touhou", "twilight sparkle (mlp)" })
            Assert.False(list.Contains(name), name);
        // Every framing the model may choose is a real tag.
        Assert.All(PictureScene.Framings, f => Assert.True(list.Contains(f), f));
        Assert.Empty(list.Resolve("mysterious aura of destiny"));
    }

    // ---- The planned scene ----

    [Fact]
    public void The_schema_names_the_people_and_puts_the_moment_first()
    {
        var schema = JsonNode.Parse(PictureScene.Schema(["Mara", "Ben"], 2))!;
        Assert.Equal("moment", schema["properties"]!.AsObject().First().Key);
        var people = schema["properties"]!["people"]!;
        Assert.Equal(2, (int)people["maxItems"]!);
        Assert.Equal(["Mara", "Ben", PictureScene.SomeoneElse], people["items"]!["properties"]!["name"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        // Framing is one size and one view, each from a closed list.
        Assert.Equal(PictureScene.Sizes, schema["properties"]!["framing"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([PictureScene.StraightOn, .. PictureScene.Views], schema["properties"]!["view"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));

        var portrait = JsonNode.Parse(PictureScene.Schema(["Mara"], 1, onePersonExactly: true))!["properties"]!["people"]!;
        Assert.Equal((1, 1), ((int)portrait["minItems"]!, (int)portrait["maxItems"]!));
        Assert.Equal(["Mara"], portrait["items"]!["properties"]!["name"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.Equal(PictureScene.MaxPeople, (int)JsonNode.Parse(PictureScene.Schema([], 9))!["properties"]!["people"]!["maxItems"]!);
    }

    [Fact]
    public void A_scene_is_read_leniently()
    {
        var scene = PictureScene.Parse("""
            Here it is:
            ```json
            {"moment":"Mara sits by the fire.","people":[
              {"name":"Mara","kind":"Girl","looks":[],"clothing":"red dress, cloak","expression":["smile"],"pose":["sitting"]},
              {"name":"Ben","kind":"wizard"},{"name":"c","kind":"boy"},{"name":"d","kind":"boy"}],
             "place":["tavern"],"light":"candlelight, night","framing":"upper body","view":"from side"}
            ```
            """);
        Assert.Equal(new[] { "upper body", "from side" }, scene.Framing);
        // One size and one view reach the prompt, whatever else was written there.
        Assert.Equal(["full body", "from above"], (scene with { Framing = ["Dramatic", "from_above", "full body", "close-up", "from behind", PictureScene.StraightOn] }).FramingForPrompt());
        Assert.Empty((scene with { Framing = [PictureScene.StraightOn] }).FramingForPrompt());
        Assert.Equal("Mara sits by the fire.", scene.Moment);
        Assert.Equal(PictureScene.MaxPeople, scene.People.Count);
        Assert.Equal(new[] { "red dress", "cloak" }, scene.People[0].Clothing);
        Assert.Equal("girl", scene.People[0].Kind);
        Assert.Equal("other", scene.People[1].Kind);
        Assert.Empty(scene.People[1].Clothing);
        Assert.Equal(new[] { "candlelight", "night" }, scene.Light);
        Assert.True(scene.HasContent);
    }

    [Theory]
    [InlineData("I would rather not.")]
    [InlineData("{\"moment\": \"cut off")]
    [InlineData("[1, 2]")]
    public void An_answer_that_is_not_a_scene_is_refused(string answer) =>
        Assert.Throws<InvalidOperationException>(() => PictureScene.Parse(answer));

    // ---- Assembly ----

    private static readonly PictureScene Tavern = new("Mara sits by the fire.",
        [new ScenePerson("Mara", "girl", ["blue hair"], ["red dress", "Red_Dress", "glowing aura"], ["smile"], ["sitting", "holding a cup"])],
        ["tavern"], ["candle light", "night"], ["upper body", "dramatic angle"]);

    [Fact]
    public void A_prompt_is_built_by_code_in_the_order_tag_checkpoints_expect()
    {
        var order = new PictureOrder(Illustrious,
            [new PersonLooks("Mara", new Looks("1girl, silver hair, (green eyes:1.1)", "chars/mara.safetensors", 0.7), new LoraSettings("mara_trigger"))],
            [("style/ink.safetensors", new LoraSettings("ink style", 0.6))],
            PictureShape.Tall, PictureQuality.Draft, Seed: 5);

        var picture = PicturePrompt.Assemble(Tavern, order, Tags);

        // What she is, her own looks exactly as typed (the model's "blue hair" is ignored), then the scene.
        Assert.Equal("1girl, solo, silver hair, (green eyes:1.1), red dress, smile, sitting, holding cup, tavern, candlelight, night, upper body", picture.Subject);
        Assert.Equal("masterpiece, best quality, amazing quality, very aesthetic, absurdres, ink style, mara_trigger, " + picture.Subject, picture.Request.Prompt);
        Assert.Equal(Illustrious.Negative, picture.Request.NegativePrompt);
        Assert.Equal([new LoraUse("style/ink.safetensors", 0.6), new LoraUse("chars/mara.safetensors", 0.7)], picture.Request.Loras);
        // Planned pictures start Small (M18).
        Assert.Equal((640, 960, 20, 5L, "euler"), (picture.Request.Width, picture.Request.Height, picture.Request.Steps, picture.Request.Seed, picture.Request.Sampler));
        Assert.Empty(picture.Request.Problems());

        Assert.Contains(("holding a cup", "holding cup"), picture.Notes.Corrected);
        Assert.Contains(("candle light", "candlelight"), picture.Notes.Corrected);
        Assert.Equal(["glowing aura"], picture.Notes.Dropped);
        // The moment is for the owner to read; it never reaches the picture engine.
        Assert.DoesNotContain("fire", picture.Request.Prompt);
    }

    [Fact]
    public void Someone_without_looks_gets_the_models_and_people_are_counted()
    {
        var scene = Tavern with
        {
            People =
            [
                Tavern.People[0],
                new ScenePerson(PictureScene.SomeoneElse, "boy", ["black hair", "beard", "1boy"], ["armor"], ["frown"], ["standing"]),
            ],
        };
        var order = new PictureOrder(Illustrious, [new PersonLooks("Mara", new Looks("silver hair"))]);
        var subject = PicturePrompt.Assemble(scene, order, Tags).Subject;
        Assert.Equal("1girl, 1boy, silver hair, red dress, smile, sitting, holding cup, black hair, beard, armor, frown, standing, tavern, candlelight, night, upper body", subject);

        // Two of a kind are counted together, and nobody is "solo".
        var twins = scene with { People = [scene.People[0], scene.People[0] with { Name = "Ada" }] };
        Assert.StartsWith("2girls, silver hair, ", PicturePrompt.Assemble(twins, order, Tags).Subject);

        // A character LoRA whose file is not installed is left out, with its trigger words.
        var missing = new PictureOrder(Illustrious, [new PersonLooks("Mara", new Looks("silver hair", "gone.safetensors"), Lora: null)]);
        Assert.Empty(PicturePrompt.Assemble(Tavern, missing, Tags).Request.Loras!);
    }

    [Fact]
    public void A_place_without_people_is_scenery()
    {
        var scene = new PictureScene("The lake at dusk.", [], ["lake"], ["sunset"], ["wide shot"]);
        var picture = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious, Shape: PictureShape.Wide, Quality: PictureQuality.Fine), Tags);
        Assert.Equal("no humans, scenery, lake, sunset, wide shot", picture.Subject);
        Assert.Equal((960, 640), (picture.Request.Width, picture.Request.Height));
        Assert.Equal(PictureLevels.FineUpscaler, picture.Request.Hires?.Upscaler);
    }

    [Fact]
    public void A_lora_named_twice_is_used_once_at_the_persons_strength()
    {
        var order = new PictureOrder(Illustrious,
            [new PersonLooks("Mara", new Looks("silver hair", "mara.safetensors"), new LoraSettings("mara_trigger", 0.9))],
            [("MARA.safetensors", new LoraSettings("mara_trigger", 0.5))]);
        var request = PicturePrompt.Assemble(Tavern, order, Tags).Request;
        Assert.Equal([new LoraUse("mara.safetensors", 0.9)], request.Loras);
    }

    [Fact]
    public void Three_people_get_fewer_tags_each_so_the_prompt_stays_short()
    {
        var busy = new ScenePerson("x", "girl", [], ["red dress", "cloak", "gloves", "apron", "armor"], ["smile", "frown"], ["sitting", "standing", "holding cup"]);
        var scene = new PictureScene("", [busy, busy with { Name = "y" }, busy with { Name = "z" }], [], [], []);
        var subject = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious), Tags).Subject;
        // Tags already in the prompt are not repeated for the next person, so each gets the next ones in line.
        Assert.StartsWith("3girls, red dress, cloak, gloves, smile, sitting, standing, apron, armor, frown, holding cup", subject);
        Assert.DoesNotContain("solo", subject);
    }

    [Fact]
    public void Sentence_checkpoints_get_phrases_and_no_tag_check()
    {
        var sdxl = CheckpointSettings.ForFamily(ImageFamily.Sdxl);
        var scene = new PictureScene("Mara by the fire.",
            [new ScenePerson("Mara", "girl", ["short blue hair"], ["a long red dress"], ["smiling warmly"], ["sitting by the fire."])],
            ["a candle-lit tavern"], ["at night"], ["upper body"]);

        var mine = PicturePrompt.Assemble(scene, new PictureOrder(sdxl, [new PersonLooks("Mara", new Looks("a tall woman with silver hair"))]), Tags);
        Assert.Equal("upper body, a tall woman with silver hair, a long red dress, smiling warmly, sitting by the fire, a candle-lit tavern, at night", mine.Subject);
        Assert.Equal(mine.Subject + ", high quality, detailed", mine.Request.Prompt);
        Assert.Same(TagNotes.None, mine.Notes);

        var models = PicturePrompt.Assemble(scene, new PictureOrder(sdxl), Tags);
        Assert.StartsWith("upper body, short blue hair, a long red dress", models.Subject);
        var nobody = PicturePrompt.Assemble(scene with { People = [scene.People[0] with { Looks = [] }] }, new PictureOrder(sdxl), Tags);
        Assert.StartsWith("upper body, a woman, a long red dress", nobody.Subject);
    }

    // ---- Style LoRAs of a checkpoint ----

    [Fact]
    public async Task Style_loras_are_kept_per_checkpoint_without_freezing_its_card()
    {
        var store = new MemoryDocumentStore();
        var catalog = new ImageModelCatalog(store);
        await catalog.LoadAsync();
        Assert.Empty(catalog.StyleLorasFor("il.safetensors"));

        await catalog.SetStyleLorasAsync("sub\\il.safetensors", ["style\\ink.safetensors", "STYLE/ink.safetensors", "detail.safetensors"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SetStyleLorasAsync("il.safetensors", ["..\\out.safetensors"]));

        var again = new ImageModelCatalog(store);
        await again.LoadAsync();
        Assert.Equal(["style/ink.safetensors", "detail.safetensors"], again.StyleLorasFor("SUB/il.safetensors"));
        // The card still follows its family's defaults: a style LoRA is not a change to the card.
        Assert.False(again.IsChanged("sub/il.safetensors"));

        await again.SetStyleLorasAsync("sub/il.safetensors", []);
        Assert.Empty(again.StyleLorasFor("sub/il.safetensors"));
    }

    // ---- The model job ----

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

    private const string SceneAnswer = """
        {"moment":"Mara pours ale for a stranger.","people":[
          {"name":"mara","kind":"girl","looks":["blue hair"],"clothing":["apron"],"expression":["smile"],"pose":["standing"]},
          {"name":"A Stranger","kind":"boy","looks":["black hair","beard"],"clothing":["cloak"],"expression":[],"pose":["sitting"]}],
         "place":["tavern"],"light":["candlelight"],"framing":["cowboy shot"]}
        """;

    [Fact]
    public async Task A_passage_is_planned_with_structured_output_and_no_thinking()
    {
        var engine = new ScriptedEngine(SceneAnswer[..60], SceneAnswer[60..]);
        var scene = await new PictureWriter(engine).SceneAsync("Mara poured the ale while the stranger watched the door.",
            [new CastEntry("Mara", HasLooks: true, "A scout\nin leather armor."), new CastEntry("Ben", HasLooks: false)], PromptStyle.Tags, background: "A border town in winter.");

        // Names come back as the cast spells them; anyone else is "someone else".
        Assert.Equal(["Mara", PictureScene.SomeoneElse], scene.People.Select(p => p.Name));
        Assert.Equal("Mara pours ale for a stranger.", scene.Moment);

        var sampler = engine.LastSampler!;
        Assert.False(sampler.Thinking);
        Assert.Equal(PictureWriter.MaxTokens, sampler.MaxTokens);
        Assert.NotNull(JsonNode.Parse(sampler.JsonSchema!));
        var system = engine.LastPrompt![0].Content;
        Assert.Contains("ONE moment", system);
        Assert.Contains("Leave it empty for Mara", system);
        Assert.Contains("Use these names exactly: Mara, Ben", system);
        Assert.Equal(["system", "user"], engine.LastPrompt.Select(m => m.Role));
        Assert.Contains("A border town in winter.", engine.LastPrompt[1].Content);
        Assert.Contains("Mara poured the ale", engine.LastPrompt[1].Content);
        Assert.Contains("- Mara: A scout in leather armor.", engine.LastPrompt[1].Content);

        // And the whole way to a prompt: Mara's looks from her card, the stranger's from the model.
        var picture = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious, [new PersonLooks("Mara", new Looks("1girl, silver hair"))]), Tags);
        Assert.Equal("1girl, 1boy, silver hair, apron, smile, standing, black hair, beard, cloak, sitting, tavern, candlelight, cowboy shot", picture.Subject);
    }

    [Fact]
    public async Task A_portrait_always_shows_its_person()
    {
        var engine = new ScriptedEngine("""{"moment":"A scout at the forest's edge.","people":[{"name":"Someone","kind":"girl","looks":[],"clothing":["cloak"],"expression":["smile"],"pose":["standing"]}],"place":["forest"],"light":[],"framing":["upper body"]}""");
        var scene = await new PictureWriter(engine).PortraitAsync(" Mara ", "A scout who knows the forest.", hasLooks: true, PromptStyle.Tags);
        Assert.Equal("Mara", Assert.Single(scene.People).Name);
        Assert.Contains("a portrait of Mara", engine.LastPrompt![0].Content);
        Assert.Contains("Leave it empty: Mara's looks are already known", engine.LastPrompt[0].Content);

        var nobody = await new PictureWriter(new ScriptedEngine("""{"moment":"","people":[],"place":[],"light":[],"framing":[]}""")).PortraitAsync("Mara", "", false, PromptStyle.Sentences);
        Assert.Equal("Mara", Assert.Single(nobody.People).Name);
    }

    [Fact]
    public async Task Looks_are_drafted_as_checked_tags()
    {
        var engine = new ScriptedEngine("""{"kind":"girl","looks":["Silver_Hair","long silver hair","pony tail","mysterious aura","1girl","green eyes"]}""");
        var draft = await new PictureWriter(engine).DraftLooksAsync("Mara", "A scout with silver hair.", PromptStyle.Tags, Tags);
        Assert.Equal("1girl, silver hair, long hair, ponytail, green eyes", draft.Tags);
        Assert.Equal(["mysterious aura"], draft.Notes.Dropped);
        Assert.Contains(("pony tail", "ponytail"), draft.Notes.Corrected);
        Assert.False(engine.LastSampler!.Thinking);

        var phrase = await new PictureWriter(new ScriptedEngine("""{"looks":"a tall woman with silver hair."}""")).DraftLooksAsync("Mara", "A scout.", PromptStyle.Sentences, Tags);
        Assert.Equal("a tall woman with silver hair", phrase.Tags);
    }

    [Fact]
    public async Task The_model_job_refuses_what_it_cannot_use()
    {
        var off = new PictureWriter(new ScriptedEngine { IsReady = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.SceneAsync("A passage.", [], PromptStyle.Tags));
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.DraftLooksAsync("Mara", "A scout.", PromptStyle.Tags, Tags));

        var writer = new PictureWriter(new ScriptedEngine("""{"moment":"Nothing.","people":[],"place":[],"light":[],"framing":[]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SceneAsync("A passage.", [], PromptStyle.Tags)); // nothing to draw
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SceneAsync("   ", [], PromptStyle.Tags));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.DraftLooksAsync("Mara", " ", PromptStyle.Tags, Tags));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PortraitAsync(" ", "x", false, PromptStyle.Tags));

        var junk = new PictureWriter(new ScriptedEngine("""{"kind":"girl","looks":["mysterious aura","inner strength","a past"]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => junk.DraftLooksAsync("Mara", "A scout.", PromptStyle.Tags, Tags));
    }

    [Fact]
    public void A_cards_text_for_the_model_has_its_names_filled_in()
    {
        var about = PictureWriter.About(new Character { Name = "Mara", Description = "{{char}} guides {{user}} through the forest.", Personality = "wry", Scenario = "" });
        Assert.Equal("Mara guides the user through the forest.\n\nPersonality: wry", about);
    }
}
