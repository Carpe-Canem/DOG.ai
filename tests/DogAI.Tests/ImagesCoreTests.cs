using System.Text;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Data;
using DogAI.Engine.Images;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// The Images section's Core and storage (M16 step 1): the raw request, the remembered controls, saved prompts, the
/// Images owner kind, previews in the row, and kept pictures listed and searched a page at a time. The lock, backup,
/// restore and portable checks for the preview column are in <see cref="KeptPictureTests"/>, the removed-data ones in
/// <see cref="RemovedDataTests"/>.
/// </summary>
public sealed class ImagesCoreTests : IDisposable
{
    private const string Checkpoint = "illustrious/nova.safetensors";
    private static readonly CheckpointSettings Card = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-images-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AppPaths Install(string name = "a")
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.EnsureCreated();
        return paths;
    }

    private static ServiceProvider Open(AppPaths paths) => new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();

    private static Picture Kept(string prompt, int width = 832, int height = 1216, string checkpoint = Checkpoint, int seed = 1,
        string owner = PictureOwners.Images, bool preview = true)
    {
        var (w, h) = PicturePreview.SizeFor(width, height);
        return new Picture
        {
            OwnerKind = owner, OwnerId = 0, Image = KeptPictureTests.Webp(width, height, seed: seed), ImageWidth = width, ImageHeight = height,
            Preview = preview ? KeptPictureTests.Webp(w, h, filler: 500, seed: seed + 500) : null,
            Checkpoint = checkpoint, Prompt = prompt, NegativePrompt = "lowres, unicorn",
            Width = width, Height = height, Seed = seed, Steps = 28, Sampler = "euler", Guidance = 5.5,
        };
    }

    // ---- The raw request ----

    [Fact]
    public void What_is_typed_is_sent_exactly_as_typed_with_nothing_added()
    {
        const string prompt = "  1girl, silver hair,\n(forest:1.2), <not a lora>, ";
        const string negative = " blurry ,, ";
        var controls = ImagesControls.StartFor(Card) with { Loras = [new LoraUse("style\\ink.safetensors", 0.8)] };

        var request = ImagesDraw.Request(prompt, negative, controls);

        Assert.Equal(prompt, request.Prompt);
        Assert.Equal(negative, request.NegativePrompt);
        // What the engine is sent holds the same two texts, character for character.
        var body = SdApi.Body(request, 7);
        Assert.Equal(prompt, (string?)body["prompt"]);
        Assert.Equal(negative, (string?)body["negative_prompt"]);
        // None of the checkpoint's quality words, its negative prompt or any rating word came along.
        Assert.DoesNotContain("masterpiece", request.Prompt);
        Assert.DoesNotContain("worst quality", request.NegativePrompt);
        foreach (var word in Enum.GetValues<PictureRating>().Select(Card.RatingsOrDefault().For).SelectMany(w => w.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            Assert.DoesNotContain(word, request.Prompt);
            Assert.DoesNotContain(word, request.NegativePrompt);
        }
        // The LoRA goes as a file with its strength, not as words in the prompt.
        Assert.Equal([new LoraUse("style/ink.safetensors", 0.8)], request.Loras);
        Assert.Empty(request.Problems());
    }

    [Fact]
    public void The_request_carries_every_control_and_is_drawn_the_way_kept_pictures_are()
    {
        var controls = new ImagesControls(new ImagesSampling("dpm++2m", "karras", 33, 6.5, 1, "sdxl-fix.safetensors"),
            1024, 768, [], 123, true, new HiresFix(2, 12, 0.4, "Lanczos"), 3);

        var request = ImagesDraw.Request("a cat", "", controls);

        Assert.Equal((1024, 768, 123L, 33, "dpm++2m", "karras", 6.5, 1), (request.Width, request.Height, request.Seed, request.Steps,
            request.Sampler, request.Scheduler, request.Guidance, request.ClipSkip));
        Assert.Equal(new HiresFix(2, 12, 0.4, "Lanczos"), request.Hires);
        Assert.Equal(3, request.Count);
        Assert.Equal((ImageFormat.Webp, PictureFormat.Quality), (request.Format, request.Quality));

        // Hires fix off: its numbers stay in the controls and are not sent. No seed: the engine picks one.
        var plain = ImagesDraw.Request("a cat", "", controls with { HiresOn = false, Seed = null });
        Assert.Null(plain.Hires);
        Assert.Equal(-1, plain.Seed);
    }

    [Fact]
    public void Several_pictures_per_draw_are_one_request_each_with_seeds_that_go_up_from_a_set_seed()
    {
        var controls = ImagesControls.StartFor(Card) with { Seed = 500, Count = 4 };

        var singles = ImagesDraw.Singles(ImagesDraw.Request("a cat", "", controls));

        Assert.Equal([500L, 501, 502, 503], singles.Select(r => r.Seed));
        Assert.All(singles, r => Assert.Equal((1, "a cat"), (r.Count, r.Prompt)));
        // A new seed: every picture gets its own.
        var fresh = ImagesDraw.Singles(ImagesDraw.Request("a cat", "", controls with { Seed = null }));
        Assert.Equal([-1L, -1, -1, -1], fresh.Select(r => r.Seed));
        // The highest seed wraps round rather than leaving the range the engine takes.
        var last = ImagesDraw.Singles(ImagesDraw.Request("a cat", "", controls with { Seed = SeedChoice.Highest, Count = 2 }));
        Assert.Equal([SeedChoice.Highest, 0L], last.Select(r => r.Seed));
    }

    [Fact]
    public void A_draw_that_cannot_work_says_why_in_words()
    {
        var start = ImagesControls.StartFor(Card);
        Assert.Empty(ImagesDraw.Problems("a cat", "", start));
        Assert.Contains("The prompt is empty.", ImagesDraw.Problems("  ", "", start));
        Assert.Empty(start.Problems());

        Assert.Contains(start.With(width: 830).Problems(), p => p.Contains("multiple of 8"));
        Assert.Contains(start.With(width: 8000).Problems(), p => p.Contains("between 256 and 4096"));
        Assert.Contains((start with { Count = 9 }).Problems(), p => p.Contains("Between 1 and 8"));
        Assert.Contains(SeedChoice.ProblemText, (start with { Seed = SeedChoice.Highest + 1 }).Problems());
        Assert.Contains((start with { Sampling = start.Sampling with { Steps = 0 } }).Problems(), p => p.Contains("Steps"));
        Assert.Contains((start with { Sampling = start.Sampling with { Vae = @"C:\vae\x.safetensors" } }).Problems(), p => p.Contains("vae folder"));
        Assert.Contains((start with { Loras = [new LoraUse("../x.safetensors")] }).Problems(), p => p.Contains("LoRA folder"));
        Assert.Contains((start with { Loras = [new LoraUse("a.safetensors"), new LoraUse("A.safetensors", 0.5)] }).Problems(), p => p.Contains("twice"));
        Assert.Contains((start with { Loras = [.. Enumerable.Range(0, 21).Select(i => new LoraUse($"l{i}.safetensors"))] }).Problems(), p => p.Contains("At most 20"));
        // The hires fix is judged only while it is on.
        var big = start.With(width: 4096) with { Hires = new HiresFix(2, 10, 0.3) };
        Assert.Empty(big.Problems());
        Assert.Contains((big with { HiresOn = true }).Problems(), p => p.Contains("larger than 4096"));
    }

    [Fact]
    public void The_model_is_the_checkpoint_with_the_vae_from_the_controls_and_the_cards_prediction()
    {
        var card = CheckpointSettings.ForFamily(ImageFamily.NoobAI, vPrediction: true) with { Vae = "card-vae.safetensors" };
        var controls = ImagesControls.StartFor(card);
        Assert.Equal("card-vae.safetensors", controls.Sampling.Vae);

        var model = ImagesDraw.Model(@"D:\models\image", @"noob\v.safetensors", card, controls with { Sampling = controls.Sampling with { Vae = null } });

        Assert.Equal(@"D:\models\image\checkpoints\noob\v.safetensors", model.CheckpointPath);
        Assert.Equal(@"D:\models\image\loras", model.LoraFolder);
        Assert.Null(model.VaePath);
        Assert.Equal(ImagePrediction.V, model.Prediction);
    }

    [Fact]
    public void A_drawn_picture_is_kept_for_the_images_section_and_make_one_like_this_draws_it_again()
    {
        var controls = new ImagesControls(new ImagesSampling("dpm++2m", "karras", 33, 6.5, 1, "sdxl-fix.safetensors"),
            832, 1216, [new LoraUse("style/ink.safetensors", 0.8)], null, true, new HiresFix(1.5, 14, 0.3, "Lanczos"), 3);
        var request = ImagesDraw.Request(" 1girl,\nforest ", "blurry", controls);
        var result = new ImageResult([KeptPictureTests.Webp(1248, 1824), KeptPictureTests.Webp(1248, 1824, seed: 2), KeptPictureTests.Webp(1248, 1824, seed: 3)],
            ImageFormat.Webp, 9000);

        var picture = ImagesDraw.Keep(Checkpoint, Card, controls, request, result, index: 2);

        Assert.Equal((PictureOwners.Images, PictureOwners.ImagesOwner), (picture.OwnerKind, picture.OwnerId));
        Assert.Empty(picture.Problems());
        Assert.Equal(9002, picture.Seed);
        Assert.Equal((" 1girl,\nforest ", "blurry"), (picture.Prompt, picture.NegativePrompt));
        Assert.Equal(("sdxl-fix.safetensors", Card.Prediction), (picture.Vae, picture.Prediction));
        Assert.Equal((1248, 1824, 832, 1216), (picture.ImageWidth, picture.ImageHeight, picture.Width, picture.Height));

        // Make one like this: the picture's settings and seed go back into the boxes and controls; what a picture does
        // not record (how many per Draw) stays as it is now.
        var now = ImagesControls.StartFor(Card) with { Count = 5 };
        var like = ImagesDraw.Like(picture, now);
        Assert.Equal((Checkpoint, " 1girl,\nforest ", "blurry"), (like.Checkpoint, like.Prompt, like.NegativePrompt));
        Assert.Equal((9002L, 5, true), (like.Controls.Seed, like.Controls.Count, like.Controls.HiresOn));
        var again = ImagesDraw.Singles(ImagesDraw.Request(like.Prompt, like.NegativePrompt, like.Controls))[0];
        Assert.Equal(SdApi.Body(picture.ToRequest(), picture.Seed).ToJsonString(), SdApi.Body(again, again.Seed).ToJsonString());

        // A picture without a hires fix switches it off and leaves the numbers that were set.
        picture.Hires = null;
        var off = ImagesDraw.Like(picture, now with { HiresOn = true, Hires = new HiresFix(2, 9, 0.5) });
        Assert.Equal((false, new HiresFix(2, 9, 0.5)), (off.Controls.HiresOn, off.Controls.Hires));
    }

    // ---- The remembered controls ----

    [Fact]
    public async Task Controls_start_from_the_checkpoints_own_card_and_are_then_remembered()
    {
        var documents = new MemoryDocumentStore();
        var memory = new ImagesMemory(documents);
        await memory.LoadAsync();

        var start = memory.For(Checkpoint, Card);
        Assert.Equal(ImagesSampling.Of(Card), start.Sampling);
        Assert.Equal(("euler", 28, 5.5, 2), (start.Sampling.Sampler, start.Sampling.Steps, start.Sampling.Guidance, start.Sampling.ClipSkip));
        Assert.Equal((832, 1216, PictureShape.Tall), (start.Width, start.Height, start.Shape));
        Assert.Empty(start.Loras);
        Assert.Null(start.Seed);
        Assert.Equal((false, PictureLevels.FineHires(28), 1), (start.HiresOn, start.Hires, start.Count));
        Assert.False(memory.SamplingChanged(Checkpoint));

        var changed = new ImagesControls(new ImagesSampling("dpm++2m", "karras", 40, 7, null, "sdxl-fix.safetensors"),
            1024, 640, [new LoraUse("style\\ink.safetensors", 0.7)], 4_000_000_000, true, new HiresFix(2, 10, 0.25, "Lanczos"), 6);
        var events = 0;
        memory.Changed += () => events++;
        await memory.SaveAsync(Checkpoint, Card, changed);
        Assert.Equal(1, events);

        // A new start of DOG.ai reads the same back.
        var again = new ImagesMemory(documents);
        await again.LoadAsync();
        var read = again.For(Checkpoint, Card);
        Assert.Equal(changed.Sampling, read.Sampling);
        Assert.Equal((1024, 640, (PictureShape?)null), (read.Width, read.Height, read.Shape));
        Assert.Equal([new LoraUse("style/ink.safetensors", 0.7)], read.Loras);
        Assert.Equal((4_000_000_000L, true, new HiresFix(2, 10, 0.25, "Lanczos"), 6), (read.Seed, read.HiresOn, read.Hires, read.Count));
        Assert.True(again.SamplingChanged(Checkpoint));
    }

    [Fact]
    public async Task Sampling_numbers_are_remembered_per_checkpoint_and_the_rest_is_shared()
    {
        var memory = new ImagesMemory(new MemoryDocumentStore());
        var pony = CheckpointSettings.ForFamily(ImageFamily.Pony);
        await memory.SaveAsync(Checkpoint, Card, memory.For(Checkpoint, Card).At(PictureShape.Wide) with
        {
            Sampling = ImagesSampling.Of(Card) with { Steps = 40 }, Count = 2,
        });

        // Another checkpoint still follows its own card, at the size and count that were set.
        var other = memory.For("pony.safetensors", pony);
        Assert.Equal(ImagesSampling.Of(pony), other.Sampling);
        Assert.Equal(("euler_a", 25, 7.0), (other.Sampling.Sampler, other.Sampling.Steps, other.Sampling.Guidance));
        Assert.Equal((1216, 832, PictureShape.Wide, 2), (other.Width, other.Height, other.Shape, other.Count));
        Assert.False(memory.SamplingChanged("pony.safetensors"));
        // The hires numbers follow each checkpoint's steps until the owner sets them.
        Assert.Equal(PictureLevels.FineHires(25), other.Hires);
        Assert.Equal(PictureLevels.FineHires(40), memory.For(Checkpoint, Card).Hires);

        // Back to the card's numbers: forgotten again, so a card changed later reaches the Images section.
        await memory.SaveAsync(Checkpoint, Card, memory.For(Checkpoint, Card) with { Sampling = ImagesSampling.Of(Card) });
        Assert.False(memory.SamplingChanged(Checkpoint));
        Assert.Equal(35, memory.For(Checkpoint, Card with { Steps = 35 }).Sampling.Steps);

        await memory.SaveAsync(Checkpoint, Card, memory.For(Checkpoint, Card) with { Sampling = ImagesSampling.Of(Card) with { Guidance = 9 } });
        Assert.Equal(9, memory.For(Checkpoint, Card).Sampling.Guidance);
        await memory.ResetSamplingAsync(Checkpoint);
        Assert.Equal(5.5, memory.For(Checkpoint, Card).Sampling.Guidance);
    }

    [Fact]
    public async Task The_images_section_has_a_checkpoint_of_its_own_that_starts_as_the_pictures_default()
    {
        var documents = new MemoryDocumentStore();
        var catalog = new ImageModelCatalog(documents);
        await catalog.ChooseAsync("b.safetensors");
        var memory = new ImagesMemory(documents);
        string[] installed = ["a.safetensors", "b.safetensors", "sub\\c.safetensors"];

        Assert.Null(memory.Checkpoint);
        Assert.Equal("b.safetensors", memory.CheckpointAmong(installed, catalog));

        await memory.ChooseAsync("sub\\c.safetensors");
        Assert.Equal("sub/c.safetensors", memory.Checkpoint);
        Assert.Equal("sub\\c.safetensors", memory.CheckpointAmong(installed, catalog));
        // Choosing here leaves the default for portraits and story pictures alone.
        Assert.Equal("b.safetensors", catalog.Chosen);
        // A chosen checkpoint that is gone: the default again, and with no default the first one.
        Assert.Equal("b.safetensors", memory.CheckpointAmong(["a.safetensors", "b.safetensors"], catalog));
        Assert.Equal("a.safetensors", memory.CheckpointAmong(["a.safetensors"], catalog));
        Assert.Null(memory.CheckpointAmong([], catalog));

        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.ChooseAsync(@"D:\models\x.safetensors"));
        await memory.ChooseAsync(null);
        Assert.Null(memory.Checkpoint);
    }

    [Fact]
    public async Task What_is_remembered_holds_no_prompt_and_controls_with_a_problem_are_not_saved()
    {
        var documents = new MemoryDocumentStore();
        var memory = new ImagesMemory(documents);
        await memory.ChooseAsync(Checkpoint);
        await memory.SaveAsync(Checkpoint, Card, memory.For(Checkpoint, Card) with
        {
            Sampling = ImagesSampling.Of(Card) with { Steps = 30 }, Loras = [new LoraUse("ink.safetensors")], Seed = 5, HiresOn = true,
            Hires = new HiresFix(2, 10, 0.25), Count = 2,
        });

        // The document's fields are the controls and nothing else: there is nowhere a prompt could be.
        var saved = (await documents.LoadAsync<JsonObject>(ImagesMemory.Key))!;
        Assert.Equal(["Checkpoint", "Count", "Height", "Hires", "HiresOn", "Loras", "Sampling", "Seed", "Width"], saved.Select(p => p.Key).Order());
        Assert.DoesNotContain("rompt", saved.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var before = saved.ToJsonString();
        var bad = memory.For(Checkpoint, Card) with { Count = 99 };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => memory.SaveAsync(Checkpoint, Card, bad));
        Assert.Contains("Between 1 and 8", e.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.SaveAsync(@"C:\x.safetensors", Card, memory.For(Checkpoint, Card)));
        Assert.Equal(before, (await documents.LoadAsync<JsonObject>(ImagesMemory.Key))!.ToJsonString());
    }

    [Fact]
    public async Task A_damaged_document_is_read_as_far_as_it_makes_sense()
    {
        var documents = new MemoryDocumentStore();
        await documents.SaveAsync(ImagesMemory.Key, new JsonObject
        {
            ["Checkpoint"] = @"C:\models\x.safetensors",
            ["Sampling"] = new JsonObject
            {
                ["good.safetensors"] = new JsonObject { ["Sampler"] = "euler", ["Steps"] = 30, ["Guidance"] = 6 },
                ["bad.safetensors"] = new JsonObject { ["Sampler"] = "euler", ["Steps"] = 9000, ["Guidance"] = 6 },
                ["none.safetensors"] = new JsonObject { ["Steps"] = 20, ["Guidance"] = 6 },
                ["../out.safetensors"] = new JsonObject { ["Sampler"] = "euler", ["Steps"] = 30, ["Guidance"] = 6 },
            },
            ["Width"] = 1000,
            ["Height"] = 99999,
            ["Loras"] = new JsonArray(
                new JsonObject { ["Path"] = "ok.safetensors", ["Strength"] = 0.5 },
                new JsonObject { ["Path"] = @"\\server\share\x.safetensors", ["Strength"] = 1 },
                new JsonObject { ["Path"] = "strong.safetensors", ["Strength"] = 50 },
                new JsonObject { ["Path"] = "OK.safetensors", ["Strength"] = 1 }),
            ["Seed"] = -5,
            ["HiresOn"] = true,
            ["Hires"] = new JsonObject { ["Scale"] = 40, ["Steps"] = 10, ["Denoise"] = 0.3, ["Upscaler"] = "Lanczos" },
            ["Count"] = 800,
        });
        var memory = new ImagesMemory(documents);

        await memory.LoadAsync();

        Assert.Null(memory.Checkpoint);
        Assert.True(memory.SamplingChanged("good.safetensors"));
        Assert.False(memory.SamplingChanged("bad.safetensors"));
        Assert.False(memory.SamplingChanged("none.safetensors"));
        Assert.False(memory.SamplingChanged("../out.safetensors"));
        var controls = memory.For("good.safetensors", Card);
        Assert.Equal(30, controls.Sampling.Steps);
        Assert.Equal((832, 1216), (controls.Width, controls.Height));
        Assert.Equal([new LoraUse("ok.safetensors", 0.5)], controls.Loras);
        Assert.Null(controls.Seed);
        Assert.Equal((false, PictureLevels.FineHires(30), 1), (controls.HiresOn, controls.Hires, controls.Count));
        Assert.Empty(controls.Problems());
    }

    [Fact]
    public async Task The_controls_live_in_a_document_of_their_own_that_a_settings_save_does_not_undo()
    {
        var paths = Install();
        await using (var services = Open(paths))
        {
            await services.MigrateDogDatabaseAsync();
            var settings = services.GetRequiredService<ISettingsStore>();
            var copy = await settings.LoadAsync();
            var memory = new ImagesMemory(services.GetRequiredService<IDocumentStore>());
            await memory.LoadAsync();
            await memory.SaveAsync(Checkpoint, Card, memory.For(Checkpoint, Card) with { Count = 3 });
            // A screen saves the settings it loaded before the controls changed.
            await settings.SaveAsync(copy with { Skin = "fireside" });
        }
        SqliteConnection.ClearAllPools();

        await using (var services = Open(paths))
        {
            var memory = new ImagesMemory(services.GetRequiredService<IDocumentStore>());
            await memory.LoadAsync();
            Assert.Equal(3, memory.For(Checkpoint, Card).Count);
            Assert.Equal("fireside", (await services.GetRequiredService<ISettingsStore>().LoadAsync()).Skin);
        }
    }

    // ---- Saved prompts ----

    [Fact]
    public async Task A_saved_prompt_is_kept_as_typed_under_its_name_and_the_same_name_replaces_it()
    {
        var documents = new MemoryDocumentStore();
        var prompts = new SavedPicturePrompts(documents);
        await prompts.LoadAsync();
        Assert.Empty(prompts.All);

        var forest = await prompts.SaveAsAsync("  Forest ", " 1girl,\nforest ", "");
        await prompts.SaveAsAsync("castle", "castle, night", "blurry");
        await prompts.SaveAsAsync("Avoid", "", "lowres, text");

        Assert.Equal(["Avoid", "castle", "Forest"], prompts.All.Select(p => p.Name));
        Assert.Equal(("Forest", " 1girl,\nforest ", ""), (forest.Name, forest.Prompt, forest.Negative));
        await prompts.SetStartAsync(forest.Id);

        // The same name, capitals aside: replaced in place, with its id and its mark.
        var replaced = await prompts.SaveAsAsync("FOREST", "1boy, forest", "dark");
        Assert.Equal(forest.Id, replaced.Id);
        Assert.Equal(3, prompts.All.Count);
        Assert.Equal(replaced, prompts.Start);

        var again = new SavedPicturePrompts(documents);
        await again.LoadAsync();
        Assert.Equal(prompts.All, again.All);
        Assert.Equal(("1boy, forest", "dark"), again.StartingBoxes(Card));
    }

    [Fact]
    public async Task A_saved_prompt_needs_a_name_and_something_to_save()
    {
        var prompts = new SavedPicturePrompts(new MemoryDocumentStore());
        Assert.Equal("Give the saved prompt a name.", (await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.SaveAsAsync(" ", "a", ""))).Message);
        Assert.Contains("both boxes are empty", (await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.SaveAsAsync("x", " ", "\n"))).Message);
        Assert.Contains("at most 80", (await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.SaveAsAsync(new string('n', 81), "a", ""))).Message);
        Assert.Contains("too long", (await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.SaveAsAsync("x", new string('a', 20_001), ""))).Message);
        Assert.Empty(prompts.All);

        var a = await prompts.SaveAsAsync("A", "a", "");
        var b = await prompts.SaveAsAsync("B", "b", "");
        // Renaming onto another one's name is refused; any other change is kept.
        Assert.Contains("already called", (await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.UpdateAsync(b with { Name = "a" }))).Message);
        var renamed = await prompts.UpdateAsync(b with { Name = " Beta ", Negative = "blurry" });
        Assert.Equal(("Beta", "b", "blurry"), (renamed.Name, renamed.Prompt, renamed.Negative));
        Assert.Equal(renamed, prompts.Find(b.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.UpdateAsync(new SavedPicturePrompt("nope", "C", "c", "")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => prompts.SetStartAsync("nope"));
        Assert.Equal(a, prompts.Find(a.Id));
    }

    [Fact]
    public async Task The_boxes_start_with_the_marked_prompt_or_else_the_checkpoints_own_quality_words()
    {
        var prompts = new SavedPicturePrompts(new MemoryDocumentStore());
        var a = await prompts.SaveAsAsync("A", "castle", "blurry");
        var b = await prompts.SaveAsAsync("B", "forest", "");

        // None marked: the checkpoint's quality words and negative prompt, as text to see and change.
        Assert.Null(prompts.Start);
        Assert.Equal((Card.QualityBefore, Card.Negative), prompts.StartingBoxes(Card));
        Assert.StartsWith("masterpiece, best quality", prompts.StartingBoxes(Card).Prompt);
        // Words that go after the prompt (plain SDXL) and an empty negative prompt (Pony).
        Assert.Equal(("high quality, detailed", "lowres, blurry, worst quality, low quality, watermark, text, signature"),
            prompts.StartingBoxes(CheckpointSettings.ForFamily(ImageFamily.Sdxl)));
        Assert.Equal("", prompts.StartingBoxes(CheckpointSettings.ForFamily(ImageFamily.Pony)).Negative);
        Assert.Equal(("before, after", "no"), SavedPicturePrompts.FromCard(Card with { QualityBefore = " before, ", QualityAfter = ", after ", Negative = " no " }));
        // No checkpoint at all: empty boxes.
        Assert.Equal(("", ""), prompts.StartingBoxes(null));

        // One is marked at a time, and it wins over the checkpoint's words.
        await prompts.SetStartAsync(a.Id);
        Assert.Equal(("castle", "blurry"), prompts.StartingBoxes(Card));
        await prompts.SetStartAsync(b.Id);
        Assert.Equal(b, prompts.Start);
        Assert.Equal(("forest", ""), prompts.StartingBoxes(Card));

        // Deleting the marked one takes the mark along; unmarking works too.
        await prompts.DeleteAsync(b.Id);
        Assert.Null(prompts.Start);
        Assert.Equal((Card.QualityBefore, Card.Negative), prompts.StartingBoxes(Card));
        await prompts.SetStartAsync(a.Id);
        await prompts.SetStartAsync(null);
        Assert.Null(prompts.Start);
    }

    [Fact]
    public void Replace_takes_the_place_of_what_is_typed_and_add_goes_after_it()
    {
        var saved = new SavedPicturePrompt("1", "Forest", "forest, dappled sunlight", "blurry");

        Assert.Equal(("forest, dappled sunlight", "blurry"), SavedPicturePrompts.Replace(saved));
        Assert.Equal(("1girl, forest, dappled sunlight", "lowres, blurry"), SavedPicturePrompts.Add("1girl", "lowres", saved));
        // A typed box that already ends with a comma gets one comma, and an empty box just the saved text.
        Assert.Equal(("1girl, forest, dappled sunlight", "blurry"), SavedPicturePrompts.Add("1girl,  ", "", saved));
        // An empty half of a saved prompt leaves that box exactly as typed.
        Assert.Equal((" 1girl ", "lowres, blurry"), SavedPicturePrompts.Add(" 1girl ", "lowres", saved with { Prompt = " " }));
        // Pressed twice, it is not added twice.
        var once = SavedPicturePrompts.Add("1girl", "lowres", saved);
        Assert.Equal(once, SavedPicturePrompts.Add(once.Prompt, once.Negative, saved));
        // Nor when it is the whole box, or stands on a line of its own.
        Assert.Equal("blurry", SavedPicturePrompts.AddTo("blurry", "blurry"));
        Assert.Equal("lowres,\nblurry ", SavedPicturePrompts.AddTo("lowres,\nblurry ", "blurry"));
        // A box that only ends with the same letters does not hold it yet: "ultra detailed" is not "detailed".
        Assert.Equal("ultra detailed, detailed", SavedPicturePrompts.AddTo("ultra detailed", "detailed"));
        Assert.Equal("1girl, big hat, hat", PromptPieces.Insert("1girl, big hat", "hat"));
        Assert.Equal(("dark forest, forest, dappled sunlight", "not blurry, blurry"), SavedPicturePrompts.Add("dark forest", "not blurry", saved with { Prompt = "forest, dappled sunlight" }));
    }

    [Fact]
    public async Task Damaged_saved_prompts_are_left_out_when_reading()
    {
        var documents = new MemoryDocumentStore();
        await documents.SaveAsync(SavedPicturePrompts.Key, new JsonObject
        {
            ["Prompts"] = new JsonArray(
                new JsonObject { ["Id"] = "1", ["Name"] = "Good", ["Prompt"] = "forest" },
                new JsonObject { ["Id"] = "2", ["Name"] = "", ["Prompt"] = "x", ["Negative"] = "" },
                new JsonObject { ["Id"] = "", ["Name"] = "No id", ["Prompt"] = "x", ["Negative"] = "" },
                new JsonObject { ["Id"] = "4", ["Name"] = "Empty", ["Prompt"] = "", ["Negative"] = " " },
                new JsonObject { ["Id"] = "5", ["Name"] = "good", ["Prompt"] = "second of that name", ["Negative"] = "" }),
            ["Start"] = "4",
        });
        var prompts = new SavedPicturePrompts(documents);

        await prompts.LoadAsync();

        Assert.Equal(new SavedPicturePrompt("1", "Good", "forest", ""), Assert.Single(prompts.All));
        Assert.Null(prompts.Start);
    }

    // ---- The Images owner kind, pages and search ----

    [Fact]
    public async Task The_images_section_keeps_any_number_of_pictures_and_lists_only_its_own_a_page_at_a_time()
    {
        var paths = Install();
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var ids = new List<long>();
        for (var i = 1; i <= 7; i++) ids.Add((await store.AddAsync(Kept($"1girl, picture{i}", seed: i))).Id);
        // Portraits, passage pictures and test pictures stay where they are.
        await store.AddAsync(Kept("a portrait", owner: PictureOwners.Character));
        await store.AddAsync(Kept("a test", owner: PictureOwners.Test));

        var first = await store.PageAsync(PictureQuery.Images(take: 3));

        Assert.Equal((7, 0, true, 3), (first.Total, first.Skip, first.HasMore, first.Next));
        Assert.Equal(ids.AsEnumerable().Reverse().Take(3), first.Pictures.Select(p => p.Id));
        // Each comes with its preview and everything about it, and without the picture itself.
        Assert.All(first.Pictures, p => Assert.Null(p.Image));
        Assert.Equal(Kept("x", seed: 7).Preview, first.Pictures[0].Preview);
        Assert.Equal(("1girl, picture7", "lowres, unicorn", 7L, Checkpoint, 832, 1216), (first.Pictures[0].Prompt, first.Pictures[0].NegativePrompt,
            first.Pictures[0].Seed, first.Pictures[0].Checkpoint, first.Pictures[0].ImageWidth, first.Pictures[0].ImageHeight));
        Assert.True(first.Pictures[0].ImageBytes > 0);

        var second = await store.PageAsync(PictureQuery.Images(skip: first.Next, take: 3));
        var third = await store.PageAsync(PictureQuery.Images(skip: second.Next, take: 3));
        Assert.Equal(ids.AsEnumerable().Reverse().Skip(3).Take(3), second.Pictures.Select(p => p.Id));
        Assert.Equal((ids[0], false, 7), (Assert.Single(third.Pictures).Id, third.HasMore, third.Next));
        // Past the end: nothing, and the count is still right. A silly page size is brought into range.
        var past = await store.PageAsync(PictureQuery.Images(skip: 50));
        Assert.Equal((0, 7, false), (past.Pictures.Count, past.Total, past.HasMore));
        Assert.Single((await store.PageAsync(PictureQuery.Images(take: -4))).Pictures);
        Assert.Equal(7, (await store.PageAsync(PictureQuery.Images(skip: -3, take: 100_000))).Pictures.Count);

        // Removing one removes just that one; the old list by owner still works and leaves images and previews out.
        await store.DeleteAsync(ids[3]);
        Assert.Equal(6, (await store.PageAsync(PictureQuery.Images())).Total);
        var list = await store.ListAsync(PictureOwners.Images, PictureOwners.ImagesOwner);
        Assert.Equal(6, list.Count);
        Assert.All(list, p => Assert.True(p.Image is null && p.Preview is null));
        Assert.Single(await store.ListAsync(PictureOwners.Character, 0));
    }

    [Fact]
    public async Task Kept_pictures_are_found_by_words_in_the_prompt_and_filtered_by_checkpoint_and_shape()
    {
        var paths = Install();
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var forest = (await store.AddAsync(Kept("1girl, Silver Hair, forest, 100% wool"))).Id;
        var castle = (await store.AddAsync(Kept("1boy, castle, silver_hair, night", 1216, 832, "pony\\v6.safetensors"))).Id;
        var cat = (await store.AddAsync(Kept("a cat on a wall, forest behind", 1024, 1024, "pony/v6.safetensors"))).Id;
        var odd = (await store.AddAsync(Kept("landscape, forestry, 100 wool", 640, 1536))).Id;

        async Task<long[]> Find(string? words = null, string? checkpoint = null, PictureShape? shape = null)
        {
            var page = await store.PageAsync(PictureQuery.Images(words, checkpoint, shape));
            Assert.Equal(page.Pictures.Count, page.Total);
            // The store's answer is the one the plain rule gives.
            Assert.All(page.Pictures, p => Assert.True(PictureQuery.Matches(p.Prompt, PictureQuery.WordsOf(words))));
            return [.. page.Pictures.Select(p => p.Id)];
        }

        Assert.Equal([odd, cat, castle, forest], await Find());
        Assert.Equal([odd, cat, castle, forest], await Find("   "));
        // Every word must be there, in any order and any case, and part of a word counts.
        Assert.Equal([odd, cat, forest], await Find("FOREST"));
        Assert.Equal([forest], await Find("forest, silver"));
        Assert.Equal([forest], await Find("hair  1girl"));
        Assert.Equal([castle, forest], await Find("silver"));
        Assert.Empty(await Find("forest castle"));
        Assert.Empty(await Find("dragon"));
        // Only the prompt is searched, not the negative prompt.
        Assert.Empty(await Find("unicorn"));
        // % and _ are letters here, not wildcards.
        Assert.Equal([forest], await Find("100%"));
        Assert.Equal([castle], await Find("silver_hair"));
        Assert.Empty(await Find("%"+"z"));

        Assert.Equal([cat, castle], await Find(checkpoint: "pony/v6.safetensors"));
        Assert.Equal([cat, castle], await Find(checkpoint: "pony\\v6.safetensors"));
        Assert.Equal([odd, forest], await Find(shape: PictureShape.Tall));
        Assert.Equal([castle], await Find(shape: PictureShape.Wide));
        Assert.Equal([cat], await Find(shape: PictureShape.Square));
        Assert.Equal([cat], await Find("forest", "pony/v6.safetensors", PictureShape.Square));
        Assert.Empty(await Find("forest", "pony/v6.safetensors", PictureShape.Wide));

        // What the checkpoint filter can offer: the checkpoints of this owner's pictures.
        await store.AddAsync(Kept("a portrait", checkpoint: "other.safetensors", owner: PictureOwners.Character));
        Assert.Equal([Checkpoint, "pony/v6.safetensors"], await store.CheckpointsAsync(PictureOwners.Images, PictureOwners.ImagesOwner));
        Assert.Empty(await store.CheckpointsAsync(PictureOwners.Message, 5));
    }

    [Fact]
    public void Typed_words_are_split_at_spaces_and_commas_and_a_size_has_a_shape()
    {
        Assert.Equal(["silver", "hair", "1girl"], PictureQuery.WordsOf(" silver hair,1girl , Silver\n"));
        Assert.Empty(PictureQuery.WordsOf(null));
        Assert.Empty(PictureQuery.WordsOf(" , "));
        Assert.Equal(PictureQuery.MaxWords, PictureQuery.WordsOf(string.Join(' ', Enumerable.Range(0, 30).Select(i => $"w{i}"))).Count);
        Assert.Equal(PictureQuery.MaxWordLength, PictureQuery.WordsOf(new string('a', 500))[0].Length);
        Assert.True(PictureQuery.Matches("1girl, Silver Hair", ["silver", "GIRL"]));
        Assert.False(PictureQuery.Matches("1girl, Silver Hair", ["silver", "boy"]));

        Assert.Equal(PictureShape.Tall, PictureQuery.Shapes(832, 1216));
        Assert.Equal(PictureShape.Wide, PictureQuery.Shapes(1216, 832));
        Assert.Equal(PictureShape.Square, PictureQuery.Shapes(1024, 1024));
        Assert.Equal(PictureShape.Tall, PictureQuery.Shapes(1248, 1824));
    }

    // ---- Previews ----

    [Fact]
    public void A_preview_is_the_shape_of_its_picture_and_small()
    {
        Assert.Equal((263, 384), PicturePreview.SizeFor(832, 1216));
        Assert.Equal((384, 263), PicturePreview.SizeFor(1216, 832));
        Assert.Equal((384, 384), PicturePreview.SizeFor(1024, 1024));
        Assert.Equal((263, 384), PicturePreview.SizeFor(1248, 1824));
        // A small picture is not enlarged.
        Assert.Equal((256, 300), PicturePreview.SizeFor(256, 300));
        Assert.Equal((0, 0), PicturePreview.SizeFor(0, 100));

        Assert.Null(PicturePreview.Problem(KeptPictureTests.Webp(263, 384, 500), 832, 1216));
        Assert.Null(PicturePreview.Problem(KeptPictureTests.Webp(262, 384, 500), 832, 1216));
        Assert.Contains("not a WebP", PicturePreview.Problem(Encoding.ASCII.GetBytes("\u0089PNG and then some more bytes to be long enough"), 832, 1216));
        Assert.Contains("not a WebP", PicturePreview.Problem(null, 832, 1216));
        Assert.Contains("at most 512", PicturePreview.Problem(KeptPictureTests.Webp(832, 1216, 500), 832, 1216));
        Assert.Contains("too large", PicturePreview.Problem(KeptPictureTests.Webp(263, 384, PicturePreview.MaxBytes), 832, 1216));
        Assert.Contains("not the shape", PicturePreview.Problem(KeptPictureTests.Webp(384, 263, 500), 832, 1216));
        Assert.Contains("not the shape", PicturePreview.Problem(KeptPictureTests.Webp(384, 384, 500), 832, 1216));
    }

    [Fact]
    public async Task A_preview_is_kept_in_the_pictures_row_when_it_is_kept_or_later()
    {
        var paths = Install();
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();

        // With the picture.
        var with = await store.AddAsync(Kept("with", seed: 1));
        Assert.Equal(Kept("x", seed: 1).Preview, await store.PreviewAsync(with.Id));
        Assert.Equal(Kept("x", seed: 1).Preview, (await store.GetAsync(with.Id))!.Preview);
        Assert.Equal(Kept("x", seed: 1).Image, await store.ImageAsync(with.Id));

        // Later, for a picture kept without one (every picture kept before M16 is one of these), and again to replace it.
        var without = await store.AddAsync(Kept("without", seed: 2, preview: false));
        Assert.Null(await store.PreviewAsync(without.Id));
        Assert.Null((await store.PageAsync(PictureQuery.Images("without"))).Pictures[0].Preview);
        var preview = KeptPictureTests.Webp(263, 384, 700, seed: 40);
        Assert.True(await store.SetPreviewAsync(without.Id, preview));
        Assert.Equal(preview, await store.PreviewAsync(without.Id));
        var newer = KeptPictureTests.Webp(263, 384, 900, seed: 41);
        Assert.True(await store.SetPreviewAsync(without.Id, newer));
        Assert.Equal(newer, (await store.PageAsync(PictureQuery.Images("without"))).Pictures[0].Preview);
        // The picture itself is untouched by all that.
        Assert.Equal(Kept("x", seed: 2).Image, await store.ImageAsync(without.Id));

        // Bytes that cannot be this picture's preview are refused and change nothing; a picture that is gone says so.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetPreviewAsync(without.Id, [1, 2, 3]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetPreviewAsync(without.Id, KeptPictureTests.Webp(384, 263, 500)));
        Assert.Equal(newer, await store.PreviewAsync(without.Id));
        Assert.False(await store.SetPreviewAsync(9999, preview));
        Assert.Null(await store.PreviewAsync(9999));
        var bad = Kept("bad", seed: 3);
        bad.Preview = KeptPictureTests.Webp(832, 1216, 500);
        Assert.Contains("at most 512", (await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(bad))).Message);
        Assert.Equal(2, (await store.PageAsync(PictureQuery.Images())).Total);

        // Nothing but the database files is in the data folder: no preview file, no cache.
        Assert.All(Directory.GetFiles(paths.Root, "*", SearchOption.AllDirectories),
            file => Assert.StartsWith(AppPaths.DatabaseName, Path.GetFileName(file)));
    }

    // ---- Upgrading ----

    [Fact]
    public async Task A_database_from_before_previews_is_upgraded_with_every_picture_intact_and_the_image_moved_last()
    {
        var paths = Install("old");
        var portrait = KeptPictureTests.Webp(832, 1216, seed: 11);
        var passage = KeptPictureTests.Webp(1216, 832, seed: 12);
        await using (var services = Open(paths))
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261115125214_ModelMemory");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Characters (Name, Description, Personality, Scenario, FirstMessage, AlternateGreetings, ExampleMessages, SystemPrompt, " +
                "PostHistoryInstructions, CreatorNotes, Creator, CharacterVersion, Tags, ExtraJson, CreatedAt, UpdatedAt) " +
                "VALUES ('Ann', 'A baker', '', '', 'Hi', '[]', '', '', '', '', '', '', '[]', '', '2026-11-13 10:00:00', '2026-11-13 10:00:00')");
            const string insert =
                "INSERT INTO Pictures (OwnerKind, OwnerId, Image, ImageWidth, ImageHeight, ImageBytes, Checkpoint, Vae, Prediction, Prompt, NegativePrompt, " +
                "Width, Height, Seed, Steps, Sampler, Scheduler, Guidance, ClipSkip, Loras, Hires, CreatedAt) VALUES ";
            await db.Database.ExecuteSqlRawAsync(insert +
                "('character', 1, {0}, 832, 1216, {1}, 'nova.safetensors', 'fix.safetensors', 1, 'a baker', 'lowres', 832, 1216, 4000000001, 28, 'euler', 'karras', 5.5, 2, " +
                "'[{{\"Path\":\"style/ink.safetensors\",\"Strength\":0.8}}]', '{{\"Scale\":1.5,\"Steps\":14,\"Denoise\":0.3,\"Upscaler\":\"Lanczos\"}}', '2026-11-14 09:00:00')",
                portrait, portrait.Length);
            await db.Database.ExecuteSqlRawAsync(insert +
                "('message', 77, {0}, 1216, 832, {1}, 'nova.safetensors', NULL, 0, 'a street', '', 1216, 832, 5, 20, 'euler_a', NULL, 6, NULL, '[]', NULL, '2026-11-14 10:00:00')",
                passage, passage.Length);
            // A third picture that was kept and removed again: its id must not be handed out a second time.
            await db.Database.ExecuteSqlRawAsync(insert +
                "('test', 0, {0}, 1216, 832, {1}, 'nova.safetensors', NULL, 0, 'removed', '', 1216, 832, 5, 20, 'euler_a', NULL, 6, NULL, '[]', NULL, '2026-11-14 11:00:00')",
                passage, passage.Length);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Pictures WHERE Id = 3");
        }
        SqliteConnection.ClearAllPools();

        await using (var services = Open(paths))
        {
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<IPictureStore>();

            var first = (await store.GetAsync(1))!;
            Assert.Equal(portrait, first.Image);
            Assert.Null(first.Preview);
            Assert.Equal((PictureOwners.Character, 1L, 832, 1216, portrait.Length), (first.OwnerKind, first.OwnerId, first.ImageWidth, first.ImageHeight, first.ImageBytes));
            Assert.Equal(("nova.safetensors", "fix.safetensors", ImagePrediction.V, "a baker", "lowres"), (first.Checkpoint, first.Vae, first.Prediction, first.Prompt, first.NegativePrompt));
            Assert.Equal((832, 1216, 4000000001L, 28, "euler", "karras", 5.5, 2), (first.Width, first.Height, first.Seed, first.Steps, first.Sampler, first.Scheduler, first.Guidance, first.ClipSkip));
            Assert.Equal([new LoraUse("style/ink.safetensors", 0.8)], first.Loras);
            Assert.Equal(new HiresFix(1.5, 14, 0.3, "Lanczos"), first.Hires);
            Assert.Equal(new DateTime(2026, 11, 14, 9, 0, 0), first.CreatedAt);
            var second = (await store.GetAsync(2))!;
            Assert.Equal(passage, second.Image);
            Assert.Equal((PictureOwners.Message, 77L, "a street", (string?)null, (HiresFix?)null), (second.OwnerKind, second.OwnerId, second.Prompt, second.Vae, second.Hires));
            Assert.Null(await store.GetAsync(3));

            // Ids go on from where they were, and the old pictures can be given previews.
            var kept = await store.AddAsync(Kept("new"));
            Assert.Equal(4, kept.Id);
            Assert.True(await store.SetPreviewAsync(1, KeptPictureTests.Webp(263, 384, 500)));
            Assert.Equal(portrait, await store.ImageAsync(1));

            // The image is the last column and the preview the one before it; the index is back.
            await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('Pictures') ORDER BY cid";
            var columns = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            Assert.Equal(["Preview", "Image"], columns.TakeLast(2));
            Assert.Equal(24, columns.Count);
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('IX_Pictures_OwnerKind_OwnerId', 'Pictures_message_ad', 'Pictures_character_ad', 'Pictures_persona_ad', 'Pictures')";
            Assert.Equal(5L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'Pictures_new%'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());

            // The rule that a deleted character takes its pictures along still holds on the rebuilt table.
            await services.GetRequiredService<ICharacterStore>().DeleteAsync(1);
            Assert.Null(await store.GetAsync(1));
            Assert.NotNull(await store.GetAsync(2));
            Assert.NotNull(await store.GetAsync(kept.Id));
        }
    }

    [Fact]
    public async Task Going_back_to_before_previews_drops_only_the_preview_and_keeps_pictures_and_their_rules()
    {
        var paths = Install("back");
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var kept = Kept("1girl, forest", seed: 5);
        kept.Preview = KeptPictureTests.Webp(263, 384, 500);
        var id = (await store.AddAsync(kept)).Id;
        var image = (await store.ImageAsync(id))!;

        await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261115125214_ModelMemory");

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Pictures') ORDER BY cid";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        Assert.DoesNotContain("Preview", columns);
        Assert.Equal("Image", columns[^1]);
        // The three rules that take an owner's pictures along are still there, and so is the picture.
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('IX_Pictures_OwnerKind_OwnerId', 'Pictures_message_ad', 'Pictures_character_ad', 'Pictures_persona_ad', 'Pictures')";
        Assert.Equal(5L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT Image FROM Pictures WHERE Id = " + id;
        Assert.Equal(image, (byte[])(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_new_database_has_the_image_last_too_and_reading_a_page_does_not_read_the_pictures()
    {
        var paths = Install();
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        // Twelve pictures of 2 MB each. With the image in the middle of the row, finding a word read all 24 MB.
        for (var i = 0; i < 12; i++)
        {
            var picture = Kept($"1girl, word{i}", seed: i);
            picture.Image = KeptPictureTests.Webp(832, 1216, filler: 2_000_000, seed: i);
            await store.AddAsync(picture);
        }
        await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // The pages of the database this connection has read so far, before and after a search.
        async Task<long> PagesRead()
        {
            command.CommandText = "SELECT COUNT(*) FROM Pictures WHERE OwnerKind = 'images' AND Prompt LIKE '%word7%' AND ImageHeight > ImageWidth";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            return ((SqliteConnection)connection).Handle is { } handle ? Status(handle) : 0;
        }
        static long Status(SQLitePCL.sqlite3 handle)
        {
            // SQLITE_DBSTATUS_CACHE_MISS (8): pages read from the file since the connection opened.
            SQLitePCL.raw.sqlite3_db_status(handle, 8, out var current, out _, 0);
            return current;
        }

        var read = await PagesRead();

        // 24 MB is some 6,000 pages; the search needs a few dozen.
        Assert.InRange(read, 1, 600);
    }
}

internal static class ImagesControlsTestHelp
{
    public static ImagesControls With(this ImagesControls controls, int width) => controls with { Width = width };
}
