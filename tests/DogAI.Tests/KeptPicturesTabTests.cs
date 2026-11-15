using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// The Images section's Kept pictures tab (M16 step 3): a picture kept from the Make tab, read back a page at a time and
/// opened, gives "Make one like this" the very request it was drawn with; Remove marks the try not kept again; the
/// tab's wording.
/// </summary>
public sealed class KeptPicturesTabTests : IDisposable
{
    private const string Checkpoint = "illustrious/nova.safetensors";
    private static readonly CheckpointSettings Card = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-kept-tab-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServiceProvider Open()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        return new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
    }

    private static Task<int> Draw(ImagesSession session, IImageEngine engine, string prompt, string negative, ImagesControls controls) =>
        session.DrawAsync(engine, ImagesDraw.Model(@"C:\models\image", Checkpoint, Card, controls), Checkpoint, controls.Sampling.Vae,
            Card.Prediction, ImagesDraw.Request(prompt, negative, controls));

    [Fact]
    public async Task Make_one_like_this_from_a_kept_picture_gives_the_request_it_was_drawn_with()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var drawn = ImagesControls.StartFor(Card) with
        {
            Sampling = new ImagesSampling("dpm++2m", "karras", 22, 6.5, 2, "sdxl/vae.safetensors"),
            Width = 1024, Height = 768,
            Loras = [new LoraUse("style/ink.safetensors", 0.65)],
            Seed = 777,
            HiresOn = true,
            Hires = new HiresFix(1.5, 12, 0.35, "Latent"),
            Count = 2,
        };
        await Draw(session, engine, "  a lighthouse,\n at dusk ", "blurry, ", drawn);
        var second = session.Tries[1];

        await using var services = Open();
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var picture = second.ToKeep();
        picture.Preview = KeptPictureTests.Webp(384, 288, filler: 500);
        var row = await store.AddAsync(picture);
        // A portrait is not an Images picture: the tab never lists it.
        await store.AddAsync(Picture.Of(PictureOwners.Character, 5, Checkpoint, Card, ImagesDraw.Request("portrait", "", drawn with { Count = 1 }),
            new ImageResult([KeptPictureTests.Webp(1024, 768)], ImageFormat.Webp, 1)));

        var page = await store.PageAsync(PictureQuery.Images());
        var listed = Assert.Single(page.Pictures);
        Assert.Equal(row.Id, listed.Id);
        Assert.NotNull(listed.Preview);
        Assert.Null(listed.Image);
        Assert.Equal(["illustrious/nova.safetensors"], await store.CheckpointsAsync(PictureOwners.Images, PictureOwners.ImagesOwner));

        // Opened large, then "Make one like this" with whatever the side panel holds now (another checkpoint's numbers, 4 per Draw).
        var opened = (await store.GetAsync(listed.Id))!;
        var now = ImagesControls.StartFor(Card) with { Count = 4 };
        var like = ImagesDraw.Like(opened, now);
        Assert.Equal(Checkpoint, like.Checkpoint);
        Assert.Equal(("  a lighthouse,\n at dusk ", "blurry, "), (like.Prompt, like.NegativePrompt));
        Assert.Equal(778, like.Controls.Seed); // the second picture of a Draw from 777
        Assert.Equal(4, like.Controls.Count);
        var again = ImagesDraw.Singles(ImagesDraw.Request(like.Prompt, like.NegativePrompt, like.Controls))[0];
        Assert.Equal(second.Request with { Loras = [] }, again with { Loras = [] });
        Assert.Equal(second.Request.Loras, again.Loras);
        Assert.Equal("sdxl/vae.safetensors", like.Controls.Sampling.Vae);
    }

    [Fact]
    public async Task Removing_a_kept_picture_offers_keep_again_for_its_try_and_no_other()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        await Draw(session, engine, "a fox", "", ImagesControls.StartFor(Card) with { Count = 3 });
        session.Select(session.Tries[1]);
        session.Kept(session.Tries[0], 4);
        session.Kept(session.Tries[1], 5);
        var changed = 0;
        session.Changed += () => changed++;

        Assert.True(session.Unkept(5));
        Assert.Equal([4L, null, null], session.Tries.Select(t => t.KeptId));
        Assert.Null(session.Selected!.KeptId);
        Assert.Same(session.Tries[1], session.Selected);
        Assert.Equal(1, changed);

        Assert.False(session.Unkept(99));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void The_tab_says_how_many_and_what_a_picture_was_drawn_with()
    {
        Assert.Equal("No kept pictures yet. Pictures you keep on the Make tab show here.", ImagesPanels.KeptCount(0, 0, false));
        Assert.Equal("No kept picture matches.", ImagesPanels.KeptCount(0, 0, true));
        Assert.Equal("1 kept picture", ImagesPanels.KeptCount(1, 1, false));
        Assert.Equal("18 kept pictures", ImagesPanels.KeptCount(18, 18, false));
        Assert.Equal("1,312 kept pictures · showing 40", ImagesPanels.KeptCount(1312, 40, false));
        Assert.Equal("3 found", ImagesPanels.KeptCount(3, 3, true));

        Assert.Equal("nova", ImagesPanels.CheckpointName("illustrious/nova.safetensors"));
        Assert.Equal("nova", ImagesPanels.CheckpointName(@"illustrious\nova.safetensors"));
        Assert.Equal("Unknown checkpoint", ImagesPanels.CheckpointName(""));

        var p = new Picture
        {
            ImageWidth = 1248, ImageHeight = 1824, Seed = 2817463950, Sampler = "euler", Steps = 28, Guidance = 5.5,
            Hires = new HiresFix(1.5, 14, 0.3, "Lanczos"), Loras = [new LoraUse("a.safetensors", 1)], Checkpoint = Checkpoint,
        };
        Assert.Equal("1248 × 1824 · seed 2817463950 · Euler, 28 steps · guidance 5.5 · hires ×1.5 · 1 LoRA · nova · kept 15 Nov 2026, 14:03",
            ImagesPanels.KeptMeta(p, new DateTime(2026, 11, 15, 14, 3, 0)));
    }
}
