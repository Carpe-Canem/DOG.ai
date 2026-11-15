using DogAI.Core.Images;
using DogAI.Core.Settings;
using System.Text.Json.Nodes;

namespace DogAI.Tests;

/// <summary>M18 step 2: pictures start Small and are made larger by hand, and kept pictures still draw again as they were.</summary>
public class PictureSizeTests
{
    private const long Gb = FakeCard.Gb;
    private const string Root = @"C:\models\image";
    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    /// <summary>A request with its LoRA list left out: lists compare by reference in a record (there are no LoRAs here).</summary>
    private static ImageRequest Plain(ImageRequest r) => r with { Loras = null, Preview = false };

    private static ImageRequest Base(long seed = -1) => ImagePrompt.Compose(Illustrious, "1girl, silver hair", seed: seed, rating: PictureRating.General);

    private static async Task<PictureTries> TriesAsync()
    {
        var engine = new FakeImageEngine();
        await engine.LoadAsync(ImageFolders.ModelFor(Root, "il.safetensors", Illustrious));
        return new PictureTries(engine);
    }

    // ---- Sizes ----

    [Fact]
    public void Small_is_the_trials_size_every_side_a_multiple_of_64_and_Normal_is_as_before()
    {
        Assert.Equal((640, 960), PictureLevels.Size(PictureShape.Tall, PictureSize.Small));
        Assert.Equal((960, 640), PictureLevels.Size(PictureShape.Wide, PictureSize.Small));
        Assert.Equal((768, 768), PictureLevels.Size(PictureShape.Square, PictureSize.Small));
        Assert.Equal((832, 1216), PictureLevels.Size(PictureShape.Tall, PictureSize.Normal));
        Assert.Equal((1216, 832), PictureLevels.Size(PictureShape.Wide, PictureSize.Normal));
        Assert.Equal((1024, 1024), PictureLevels.Size(PictureShape.Square, PictureSize.Normal));
        foreach (var shape in Enum.GetValues<PictureShape>())
        {
            var (width, height) = PictureLevels.Size(shape, PictureSize.Small);
            // The picture engine rounds every side up to a multiple of 64; any other number comes out larger than asked.
            Assert.Equal((0, 0), (width % 64, height % 64));
            Assert.Equal((shape, PictureSize.Small), PictureLevels.Of(width, height));
            Assert.Equal((shape, PictureSize.Normal), PictureLevels.Of(PictureLevels.Size(shape, PictureSize.Normal).Width, PictureLevels.Size(shape, PictureSize.Normal).Height));
        }
        Assert.Null(PictureLevels.Of(704, 1088));
        Assert.Equal(PictureSize.Small, default(PictureSize)); // a value nobody set reads as Small
    }

    [Fact]
    public void Make_larger_from_Small_is_a_hires_fix_with_Fines_second_pass()
    {
        var small = Base(9).At(PictureQuality.Fine, PictureShape.Tall, PictureSize.Small, PictureLevels.LargerScale);
        Assert.Equal((640, 960), (small.Width, small.Height));
        Assert.Equal(new HiresFix(1.5, 14, 0.3, "Lanczos"), small.Hires);
        Assert.Equal((960, 1472), small.LargestPass);
        Assert.Equal((1472, 960), Base(9).At(PictureQuality.Fine, PictureShape.Wide, PictureSize.Small).LargestPass);
        Assert.Equal((1152, 1152), Base(9).At(PictureQuality.Fine, PictureShape.Square, PictureSize.Small).LargestPass);
        // ×1.25 is Normal's size for Tall and Wide; Square comes out 960 × 960 (768 × 1.25 is already a multiple of 64).
        Assert.Equal((832, 1216), Base(9).At(PictureQuality.Fine, PictureShape.Tall, PictureSize.Small, PictureLevels.LargerFallbackScale).LargestPass);
        Assert.Equal((1216, 832), Base(9).At(PictureQuality.Fine, PictureShape.Wide, PictureSize.Small, PictureLevels.LargerFallbackScale).LargestPass);
        Assert.Equal((960, 960), Base(9).At(PictureQuality.Fine, PictureShape.Square, PictureSize.Small, PictureLevels.LargerFallbackScale).LargestPass);
        // Well under the line where pictures are decoded in tiles, which stays where it was.
        Assert.False(small.DecodesInTiles);
        Assert.Equal(2_400_000, ImageRequest.TiledAbovePixels);
    }

    // ---- Kept pictures draw again as they were ----

    [Fact]
    public void A_kept_Normal_and_a_kept_Fine_picture_draw_the_same_request_as_before_M18()
    {
        // The requests as M16 made them: 832 × 1216, Fine with its hires fix ×1.5, 14 steps, strength 0.3, Lanczos.
        var normal = Base(5) with { Width = 832, Height = 1216 };
        var fine = normal with { Hires = new HiresFix(1.5, 14, 0.3, "Lanczos") };
        foreach (var before in new[] { normal.ForKeeping(), fine.ForKeeping() })
        {
            var kept = Picture.Of(PictureOwners.Character, 1, "il.safetensors", Illustrious, before, new ImageResult([[1]], ImageFormat.Webp, 5));
            Assert.Equal(Plain(before), Plain(kept.ToRequest()));
            var level = PictureTries.BaseOf(kept, Illustrious.Steps);
            Assert.Equal(PictureSize.Normal, level.Size);
            Assert.Equal(Plain(kept.ToRequest()), Plain(PictureTries.Levelled(level.Base, level.Quality, level.Shape, level.Size, level.Scale)));
        }
    }

    public static TheoryData<PictureShape, PictureSize, PictureQuality, double> Levels()
    {
        var data = new TheoryData<PictureShape, PictureSize, PictureQuality, double>();
        foreach (var shape in Enum.GetValues<PictureShape>())
        {
            data.Add(shape, PictureSize.Small, PictureQuality.Draft, 1.5);
            data.Add(shape, PictureSize.Small, PictureQuality.Standard, 1.5);
            data.Add(shape, PictureSize.Small, PictureQuality.Fine, PictureLevels.LargerScale);
            data.Add(shape, PictureSize.Small, PictureQuality.Fine, PictureLevels.LargerFallbackScale);
            data.Add(shape, PictureSize.Normal, PictureQuality.Draft, 1.5);
            data.Add(shape, PictureSize.Normal, PictureQuality.Standard, 1.5);
            data.Add(shape, PictureSize.Normal, PictureQuality.Fine, 1.5);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public async Task Every_level_kept_comes_back_as_itself(PictureShape shape, PictureSize size, PictureQuality quality, double scale)
    {
        var tries = await TriesAsync();
        var drawn = await tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), quality, shape, size, null, TagNotes.None, scale: scale);
        var kept = drawn.ToKeep(PictureOwners.Character, 1);
        kept.Id = 3;

        var level = PictureTries.BaseOf(kept, Illustrious.Steps);
        Assert.Equal((quality, shape, size), (level.Quality, level.Shape, level.Size));
        if (quality == PictureQuality.Fine) Assert.Equal(scale, level.Scale);
        // The byte-for-byte guarantee: drawn again from what it is read as, it is the very request it was drawn with.
        Assert.Equal(Plain(drawn.Request), Plain(PictureTries.Levelled(level.Base, level.Quality, level.Shape, level.Size, level.Scale)));
        Assert.Equal(Plain(kept.ToRequest()), Plain(drawn.Request));
        var adopted = tries.Adopt(kept, Illustrious);
        Assert.Equal(Plain(drawn.Request), Plain(adopted.Request));
        Assert.Equal(drawn.LevelTitle, adopted.LevelTitle);
    }

    [Fact]
    public void A_Small_picture_made_larger_is_not_taken_for_Fine()
    {
        var made = new Picture { Checkpoint = "il.safetensors", Prompt = "1girl", Width = 640, Height = 960, Seed = 5, Steps = 28, Sampler = "euler",
            Hires = PictureLevels.FineHires(28, 1.25) };
        var level = PictureTries.BaseOf(made, 28);
        Assert.Equal((PictureSize.Small, PictureShape.Tall, 1.25), (level.Size, level.Shape, level.Scale));
        Assert.Equal("Made larger ×1.25", PictureLevels.Title(level.Quality, level.Size, level.Scale));
        Assert.Equal("Fine", PictureLevels.Title(PictureQuality.Fine, PictureSize.Normal, 1.5));
        Assert.Equal("Standard", PictureLevels.Title(PictureQuality.Standard, PictureSize.Small, 1.5));
    }

    // ---- Try again and Make larger ----

    [Fact]
    public async Task Try_again_keeps_the_size_and_Make_larger_keeps_the_seed()
    {
        var tries = await TriesAsync();
        var small = await tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), PictureQuality.Standard, PictureShape.Tall, PictureSize.Small, "Mara.", TagNotes.None);
        Assert.Equal((640, 960, null), (small.Request.Width, small.Request.Height, small.Request.Hires));

        var again = await tries.TryAgainAsync(Root, small);
        Assert.Equal(PictureSize.Small, again.Size);

        var larger = await tries.MakeLargerAsync(Root, small, PictureLevels.LargerScale);
        Assert.Equal(Plain(small.Base), Plain(larger.Base)); // same seed, prompt and size
        Assert.Equal((PictureQuality.Fine, PictureSize.Small, 1.5), (larger.Quality, larger.Size, larger.Scale));
        Assert.Equal((960, 1472), larger.Request.LargestPass);
        Assert.Equal("Made larger ×1.5", larger.LevelTitle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tries.MakeLargerAsync(Root, larger, 1.5));

        // Kept and taken back in: it comes back as Small made larger, and Try again stays so.
        var keptLarger = larger.ToKeep(PictureOwners.Character, 1);
        keptLarger.Id = 4;
        var back = tries.Adopt(keptLarger, Illustrious);
        Assert.Equal((PictureQuality.Fine, PictureSize.Small, 1.5), (back.Quality, back.Size, back.Scale));
        var backAgain = await tries.TryAgainAsync(Root, back);
        Assert.Equal((PictureSize.Small, 1.5, (960, 1472)), (backAgain.Size, backAgain.Scale, backAgain.Request.LargestPass));

        // Normal goes to Fine as before.
        var normal = await tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), PictureQuality.Draft, PictureShape.Tall, PictureSize.Normal, null, TagNotes.None);
        var fine = await tries.MakeLargerAsync(Root, normal, await PictureLevels.LargerScaleAsync(PictureSize.Normal, _ => Task.FromResult<bool?>(false)));
        Assert.Equal((1280, 1856, 28), (fine.Request.LargestPass.Width, fine.Request.LargestPass.Height, fine.Request.Steps));
        Assert.Equal("Fine", fine.LevelTitle);
    }

    [Fact]
    public async Task Try_again_on_a_kept_Normal_picture_stays_Normal()
    {
        var tries = await TriesAsync();
        var kept = new Picture { Id = 7, Checkpoint = "il.safetensors", Prompt = "1girl", Width = 832, Height = 1216, Seed = 5, Steps = 28, Sampler = "euler", Image = [1] };
        var back = tries.Adopt(kept, Illustrious);
        var again = await tries.TryAgainAsync(Root, back);
        Assert.Equal((832, 1216), (again.Request.Width, again.Request.Height));
        // Edit and redraw keeps a made-larger scale only while the size stays.
        var edited = await tries.RedrawAsync(Root, back, back.Base.Prompt, PictureQuality.Fine, PictureShape.Tall, PictureSize.Normal);
        Assert.Equal(1.5, edited.Request.Hires!.Scale);
    }

    [Theory]
    [InlineData(PictureSize.Small, null, null, 1.5)]       // no chat model loaded: nothing to keep
    [InlineData(PictureSize.Small, true, null, 1.5)]
    [InlineData(PictureSize.Small, false, true, 1.25)]     // only ×1.25 fits beside the chat model
    [InlineData(PictureSize.Small, false, false, 1.5)]     // neither: drawn ×1.5, the chat model set aside
    [InlineData(PictureSize.Normal, false, true, 1.5)]     // Normal goes to Fine
    public async Task Make_larger_chooses_its_scale(PictureSize size, bool? fits15, bool? fits125, double expected)
    {
        var scale = await PictureLevels.LargerScaleAsync(size, s => Task.FromResult(s == 1.5 ? fits15 : fits125));
        Assert.Equal(expected, scale);
    }

    [Theory]
    [InlineData(PictureSize.Small, false, false, PictureSize.Small)]
    [InlineData(PictureSize.Normal, null, null, PictureSize.Normal)]   // no chat model loaded
    [InlineData(PictureSize.Normal, true, true, PictureSize.Normal)]
    [InlineData(PictureSize.Normal, false, true, PictureSize.Small)]   // Normal would set it aside, Small would not
    [InlineData(PictureSize.Normal, false, false, PictureSize.Normal)] // even Small does not fit: drawn as asked
    public async Task A_new_picture_starts_Small_only_when_that_keeps_the_chat_model(PictureSize wanted, bool? normalFits, bool? smallFits, PictureSize expected)
    {
        var size = await PictureLevels.StartSizeAsync(wanted, z => Task.FromResult(z == PictureSize.Normal ? normalFits : smallFits));
        Assert.Equal(expected, size);
    }

    // ---- Beside the chat model, with the trial's numbers ----

    [Fact]
    public async Task With_Huihui_Q8_loaded_Small_and_Normal_fit_and_Small_made_larger_fits_only_at_x1_25()
    {
        var chat = new FakeChatModel().Loaded();
        var images = new FakeImageEngine();
        // The trial: 24 GB card, Windows 1.6 GB, the chat model 8.6 GB, the idle picture model 7.0 GB, a 6.46 GB checkpoint.
        var card = new FakeCard(chat, images) { Other = (long)(1.6 * Gb), ChatBytes = (long)(8.6 * Gb), PictureBytes = 7 * Gb };
        var coordinator = new CardCoordinator(images, chat, card, _ => (long)(6.46 * Gb));
        var model = ImageFolders.ModelFor(Root, "il.safetensors", Illustrious);
        await coordinator.LoadAsync(model);
        await coordinator.DrawAsync(Base(1).At(PictureQuality.Standard, PictureShape.Tall, PictureSize.Small)); // the picture model is now held

        Task<bool?> Fits(PictureSize size, double? scale = null) => coordinator.FitsBesideChatAsync(model,
            Base(1).At(scale is null ? PictureQuality.Standard : PictureQuality.Fine, PictureShape.Tall, size, scale ?? 1.5));
        Assert.True(await Fits(PictureSize.Small));
        Assert.True(await Fits(PictureSize.Normal));
        Assert.False(await Fits(PictureSize.Small, 1.5));
        Assert.True(await Fits(PictureSize.Small, 1.25));
        Assert.Equal(1.25, await PictureLevels.LargerScaleAsync(PictureSize.Small, s => Fits(PictureSize.Small, s)));

        // A chat model of 10 GB: Normal no longer fits, Small does, so a Normal setting starts Small.
        card.ChatBytes = 10 * Gb;
        Assert.Equal(PictureSize.Small, await PictureLevels.StartSizeAsync(PictureSize.Normal, z => Fits(z)));

        // No chat model in memory: nothing to keep, so nothing shrinks.
        await chat.RestAsync();
        Assert.Null(await Fits(PictureSize.Normal));
        Assert.Equal(PictureSize.Normal, await PictureLevels.StartSizeAsync(PictureSize.Normal, z => Fits(z)));
    }

    // ---- Upscale › Redraw larger ----

    [Fact]
    public async Task Redraw_larger_offers_x1_25_only_for_a_Small_picture_and_starts_on_Make_larger_s_choice()
    {
        var small = Base(5).At(PictureQuality.Standard, PictureShape.Tall, PictureSize.Small).ForKeeping();
        var normal = Base(5).At(PictureQuality.Standard, PictureShape.Tall, PictureSize.Normal).ForKeeping();
        var smallSizes = PictureUpscale.RedrawSizes(small);
        Assert.Equal([1.25, 1.5, 2, 2.5, 3], smallSizes.Select(s => s.Scale));
        Assert.Equal((832, 1216), (smallSizes[0].Width, smallSizes[0].Height));
        Assert.Equal((960, 1472), (smallSizes[1].Width, smallSizes[1].Height));
        Assert.DoesNotContain(PictureUpscale.RedrawSizes(normal), s => s.Scale == 1.25);

        Assert.Equal(1.5, await PictureUpscale.FirstScaleAsync(small, smallSizes, _ => Task.FromResult<bool?>(null)));
        Assert.Equal(1.25, await PictureUpscale.FirstScaleAsync(small, smallSizes, s => Task.FromResult<bool?>(s < 1.3)));
        Assert.Equal(2, await PictureUpscale.FirstScaleAsync(normal, PictureUpscale.RedrawSizes(normal), _ => Task.FromResult<bool?>(false)));
        // A Small picture made larger already starts on ×2 as any other.
        var larger = PictureUpscale.Redraw(small, 1.5);
        Assert.Equal(2, await PictureUpscale.FirstScaleAsync(larger, PictureUpscale.RedrawSizes(larger), _ => Task.FromResult<bool?>(null)));
    }

    // ---- Settings › Pictures and the Images section ----

    [Fact]
    public async Task Settings_start_Small_and_a_document_from_before_reads_as_Small()
    {
        var documents = new MemoryDocumentStore();
        await documents.SaveAsync(ImageModelCatalog.Key, new JsonObject
        {
            ["Preferences"] = new JsonObject { ["Quality"] = 2, ["ShowPromptFirst"] = true, ["Rating"] = 1 },
        });
        var catalog = new ImageModelCatalog(documents);
        await catalog.LoadAsync();
        Assert.Equal((PictureSize.Small, PictureQuality.Fine, true), (catalog.Preferences.Size, catalog.Preferences.Quality, catalog.Preferences.ShowPromptFirst));
        Assert.Equal(PictureSize.Small, PicturePreferences.Default.Size);

        await catalog.SetPreferencesAsync(catalog.Preferences with { Size = PictureSize.Normal });
        var again = new ImageModelCatalog(documents);
        await again.LoadAsync();
        Assert.Equal(PictureSize.Normal, again.Preferences.Size);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SetPreferencesAsync(catalog.Preferences with { Size = (PictureSize)7 }));

        await documents.SaveAsync(ImageModelCatalog.Key, new JsonObject { ["Preferences"] = new JsonObject { ["Size"] = 7 } });
        await again.LoadAsync();
        Assert.Equal(PictureSize.Small, again.Preferences.Size);
    }

    [Fact]
    public async Task The_Images_section_starts_at_the_setting_and_remembers_only_a_size_picked_there()
    {
        var documents = new MemoryDocumentStore();
        const string checkpoint = "il.safetensors";
        // Before M18 every save wrote the size, so 832 × 1216 was there whether picked or not: forgotten once.
        await documents.SaveAsync(ImagesMemory.Key, new JsonObject { ["Width"] = 832, ["Height"] = 1216, ["Count"] = 2 });
        var memory = new ImagesMemory(documents);
        await memory.LoadAsync();
        var start = memory.For(checkpoint, Illustrious, PictureSize.Small);
        Assert.Equal((640, 960, 2), (start.Width, start.Height, start.Count));
        Assert.Equal((PictureShape.Tall, PictureSize.Small), (start.Shape, start.Size));
        Assert.Equal((832, 1216), (memory.For(checkpoint, Illustrious, PictureSize.Normal).Width, memory.For(checkpoint, Illustrious, PictureSize.Normal).Height));

        // Saving another control does not fix the size: it goes on following Settings.
        await memory.SaveAsync(checkpoint, Illustrious, start with { Count = 3 }, PictureSize.Small);
        Assert.Equal((832, 1216), (memory.For(checkpoint, Illustrious, PictureSize.Normal).Width, memory.For(checkpoint, Illustrious, PictureSize.Normal).Height));

        // A size picked there is remembered whatever Settings say, also after a restart.
        var wide = start.At(PictureShape.Wide);
        Assert.Equal((960, 640), (wide.Width, wide.Height)); // a shape keeps the size shown
        Assert.Equal((1216, 832), (wide.At(PictureSize.Normal).Width, wide.At(PictureSize.Normal).Height)); // a size keeps the shape
        await memory.SaveAsync(checkpoint, Illustrious, wide, PictureSize.Small);
        var reread = new ImagesMemory(documents);
        await reread.LoadAsync();
        Assert.Equal((960, 640), (reread.For(checkpoint, Illustrious, PictureSize.Normal).Width, reread.For(checkpoint, Illustrious, PictureSize.Normal).Height));
    }
}
