using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Search;

namespace DogAI.Tests;

/// <summary>
/// The Images section's Make tab (M16 step 2): the in-memory session (boxes, tries, a Draw of several pictures one after
/// another, Cancel), keeping a try from what it recorded when drawn, the side panel's folds and wording, and its place in
/// the navigation and Ctrl+K.
/// </summary>
public sealed class ImagesMakeTests
{
    private const string Checkpoint = "illustrious/nova.safetensors";
    private static readonly CheckpointSettings Card = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private static ImageModel ModelOf(ImagesControls controls) => ImagesDraw.Model(@"C:\models\image", Checkpoint, Card, controls);

    private static Task<int> Draw(ImagesSession session, IImageEngine engine, string prompt, string negative, ImagesControls controls, CancellationToken ct = default) =>
        session.DrawAsync(engine, ModelOf(controls), Checkpoint, controls.Sampling.Vae, Card.Prediction, ImagesDraw.Request(prompt, negative, controls), ct);

    [Fact]
    public async Task A_draw_of_three_with_a_set_seed_draws_three_single_pictures_from_that_seed_up_exactly_as_typed()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var controls = ImagesControls.StartFor(Card) with { Seed = 4_294_967_294, Count = 3 };
        const string prompt = "  1girl,\n  castle on a cliff  ";

        Assert.Equal(3, await Draw(session, engine, prompt, "lowres\n", controls));

        Assert.Equal(3, engine.Requests.Count);
        Assert.All(engine.Requests, r =>
        {
            Assert.Equal(1, r.Count);
            Assert.Equal(prompt, r.Prompt);
            Assert.Equal("lowres\n", r.NegativePrompt);
            Assert.Equal(ImageFormat.Webp, r.Format);
            Assert.True(r.Preview);
        });
        // The seed counts up and wraps after the highest.
        Assert.Equal([4_294_967_294L, 4_294_967_295L, 0L], engine.Requests.Select(r => r.Seed));
        Assert.Equal([4_294_967_294L, 4_294_967_295L, 0L], session.Tries.Select(t => t.Request.Seed));
        Assert.Equal([1, 2, 3], session.Tries.Select(t => t.Number));
        Assert.Same(session.Tries[^1], session.Selected);
        Assert.All(session.Tries, t => Assert.False(t.Request.Preview));
        Assert.All(session.Tries, t => Assert.Equal((832, 1216), (t.ImageWidth, t.ImageHeight)));
        Assert.False(session.IsDrawing);
        Assert.Equal(0, session.Drawing);
    }

    [Fact]
    public async Task A_new_seed_each_time_records_the_seed_the_engine_used()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        await Draw(session, engine, "a cat", "", ImagesControls.StartFor(Card) with { Count = 2 });

        Assert.All(engine.Requests, r => Assert.Equal(-1, r.Seed));
        Assert.Equal([100L, 101L], session.Tries.Select(t => t.Request.Seed));
        Assert.Equal(101, session.Tries[^1].ToKeep().Seed);
    }

    [Fact]
    public async Task Keep_and_use_these_settings_take_what_the_try_was_drawn_with_not_the_controls_now()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var drawn = ImagesControls.StartFor(Card) with
        {
            Sampling = new ImagesSampling("dpm++2m", "karras", 20, 7, 1, "sdxl/vae.safetensors"),
            Width = 1024, Height = 768,
            Loras = [new LoraUse("style/ink.safetensors", 0.7)],
            Seed = 42,
            HiresOn = true,
            Hires = new HiresFix(1.25, 10, 0.4, "Latent"),
        };
        await Draw(session, engine, "a lighthouse at dusk", "blurry", drawn);
        Assert.Equal(@"C:\models\image\vae\sdxl\vae.safetensors", engine.Model!.VaePath?.Replace('/', '\\'));
        var shown = session.Selected!;

        // The owner changes everything afterwards; Keep must not notice.
        var now = ImagesControls.StartFor(Card) with { Count = 4 };
        var kept = shown.ToKeep();
        Assert.Equal(PictureOwners.Images, kept.OwnerKind);
        Assert.Equal(PictureOwners.ImagesOwner, kept.OwnerId);
        Assert.Equal(Checkpoint, kept.Checkpoint);
        Assert.Equal("sdxl/vae.safetensors", kept.Vae);
        Assert.Equal(("a lighthouse at dusk", "blurry"), (kept.Prompt, kept.NegativePrompt));
        Assert.Equal((1024, 768, 42L, 20, "dpm++2m", "karras", 7.0, 1), (kept.Width, kept.Height, kept.Seed, kept.Steps, kept.Sampler, kept.Scheduler, kept.Guidance, kept.ClipSkip));
        Assert.Equal([new LoraUse("style/ink.safetensors", 0.7)], kept.Loras);
        Assert.Equal(new HiresFix(1.25, 10, 0.4, "Latent"), kept.Hires);
        Assert.Empty(kept.Problems());

        var like = ImagesDraw.Like(kept, now);
        Assert.Equal(("a lighthouse at dusk", "blurry", Checkpoint), (like.Prompt, like.NegativePrompt, like.Checkpoint));
        Assert.Equal(drawn with { Count = 4 }, like.Controls with { Loras = drawn.Loras });
        Assert.Equal(drawn.Loras, like.Controls.Loras);

        var marked = session.Kept(shown, 9);
        Assert.Equal(9, marked.KeptId);
        Assert.Same(marked, session.Selected);
        Assert.EndsWith("· kept", ImagesPanels.Meta(marked));
    }

    [Fact]
    public async Task Cancel_keeps_the_pictures_already_drawn()
    {
        var engine = new StopsAtSecond();
        var session = new ImagesSession();
        var draw = Draw(session, engine, "a forest", "", ImagesControls.StartFor(Card) with { Count = 4 });
        await engine.Second.Task;
        Assert.True(session.IsDrawing);
        Assert.Equal((2, 4), (session.Drawing, session.Of));
        Assert.Equal("Picture 2 of 4 · sampling", ImagesPanels.Doing(session.Drawing, session.Of, session.Progress?.Stage));

        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draw);
        Assert.Single(session.Tries);
        Assert.False(session.IsDrawing);
        Assert.Equal(0, session.Drawing);
        Assert.Null(session.Preview);

        // A second Draw works after it.
        engine.Stop = false;
        Assert.Equal(1, await Draw(session, engine, "a forest", "", ImagesControls.StartFor(Card)));
        Assert.Equal(2, session.Tries.Count);
    }

    [Fact]
    public async Task A_draw_with_a_problem_sends_nothing_and_two_draws_never_run_at_once()
    {
        var engine = new StopsAtSecond();
        var session = new ImagesSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Draw(session, engine, "  ", "", ImagesControls.StartFor(Card)));
        Assert.Empty(engine.Requests);

        var first = Draw(session, engine, "a forest", "", ImagesControls.StartFor(Card) with { Count = 2 });
        await engine.Second.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Draw(session, engine, "a field", "", ImagesControls.StartFor(Card)));
        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task At_most_thirty_two_tries_are_held_and_the_oldest_go_first()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        await Draw(session, engine, "first", "", ImagesControls.StartFor(Card));
        var first = session.Tries[0];
        for (var i = 0; i < 4; i++) await Draw(session, engine, "more", "", ImagesControls.StartFor(Card) with { Count = 8 });

        Assert.Equal(ImagesSession.Max, session.Tries.Count);
        Assert.True(session.Dropped);
        Assert.DoesNotContain(first, session.Tries);
        Assert.Equal([.. Enumerable.Range(2, 32)], session.Tries.Select(t => t.Number));
        Assert.Same(session.Tries[^1], session.Selected);
    }

    [Fact]
    public void The_boxes_are_filled_once_per_run_and_keep_what_is_typed()
    {
        var session = new ImagesSession();
        var calls = 0;
        session.Start(() => { calls++; return ("masterpiece", "lowres"); });
        Assert.Equal(("masterpiece", "lowres"), (session.Prompt, session.Negative));

        session.Type("masterpiece, a fox", "");
        session.Start(() => { calls++; return ("other", "other"); });
        Assert.Equal(1, calls);
        Assert.Equal(("masterpiece, a fox", ""), (session.Prompt, session.Negative));

        // Typing before the first fill counts as started too: the boxes are never overwritten.
        var typedFirst = new ImagesSession();
        typedFirst.Type("mine", "");
        typedFirst.Start(() => ("theirs", "theirs"));
        Assert.Equal("mine", typedFirst.Prompt);
    }

    [Fact]
    public void The_session_holds_nothing_that_could_be_saved()
    {
        // In memory only: no store is given to it, and no constructor takes one.
        Assert.All(typeof(ImagesSession).GetConstructors(), c => Assert.Empty(c.GetParameters()));
    }

    [Fact]
    public void The_side_panel_folds_have_unique_ids_that_clash_with_no_chat_panel_and_say_what_is_set()
    {
        var ids = ImagesPanels.Folds.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
        var chat = ChatPanels.Layout.SelectMany(p => p.Value.Select(x => ChatPanels.Id(p.Key, x.Panel))).ToList();
        Assert.Empty(ids.Intersect(chat));

        Assert.Equal("None", ImagesPanels.LorasSummary(0));
        Assert.Equal("2 on", ImagesPanels.LorasSummary(2));
        Assert.Equal("832 × 1216", ImagesPanels.SizeSummary(832, 1216));
        Assert.Equal("Euler · 28 steps", ImagesPanels.SamplingSummary(ImagesSampling.Of(Card)));
        Assert.Equal("New each time", ImagesPanels.SeedSummary(null));
        Assert.Equal("2817463950", ImagesPanels.SeedSummary(2817463950));
        Assert.Equal("Off", ImagesPanels.HiresSummary(false, PictureLevels.FineHires(28)));
        Assert.Equal("×1.5 · 14 steps", ImagesPanels.HiresSummary(true, PictureLevels.FineHires(28)));
        Assert.Equal("Picture · Starting…", ImagesPanels.Doing(1, 1, null));
    }

    [Fact]
    public void Images_is_a_main_screen_in_ctrl_k_and_settings_go_back_to_it()
    {
        var images = Assert.Single(QuickJump.Screens, s => s.Route == ImagesPanels.Address);
        Assert.True(images.Main);
        Assert.Equal("Images", images.Title);
        Assert.Contains(QuickJump.Find([], "draw"), t => t.Route == ImagesPanels.Address);
        Assert.Equal("Images", ReturnPlace.NameOf(ImagesPanels.Address));
        // The Kept pictures tab (step 3): its own entry, found by "kept", and a back target by name.
        var kept = Assert.Single(QuickJump.Screens, s => s.Route == ImagesPanels.KeptAddress);
        Assert.False(kept.Main);
        Assert.Equal("Kept pictures", QuickJump.Find([], "kept").First(t => t.Kind == JumpKind.Screen).Title);
        Assert.Equal("Kept pictures", ReturnPlace.NameOf(ImagesPanels.KeptAddress));
        Assert.Equal([ImagesPanels.Address, ImagesPanels.KeptAddress], ImagesPanels.Tabs.Select(t => t.Id));
    }

    [Fact]
    public void A_preview_from_the_page_keeps_only_its_picture_part()
    {
        var plain = KeptPictureTests.Webp(263, 384, 500);
        var vp8 = plain.AsSpan(12).ToArray();
        var vp8x = new byte[10];
        vp8x[0] = 0x20; // a colour profile follows
        vp8x[4] = 262 & 0xFF; vp8x[5] = 262 >> 8; vp8x[7] = 383 & 0xFF; vp8x[8] = 383 >> 8;
        var icc = new byte[455]; // odd: padded
        new Random(3).NextBytes(icc);
        var extended = Riff(Chunk("VP8X", vp8x), Chunk("ICCP", icc), vp8);

        Assert.NotNull(WebpInfo.Read(extended));
        var stripped = PicturePreview.Plain(extended);
        Assert.Equal(plain, stripped);
        Assert.Null(PicturePreview.Problem(stripped, 832, 1216));

        // Already plain, or holding something besides a profile and the picture: unchanged.
        Assert.Same(plain, PicturePreview.Plain(plain));
        var withExif = Riff(Chunk("VP8X", vp8x), Chunk("EXIF", [1, 2, 3, 4]), vp8);
        Assert.Same(withExif, PicturePreview.Plain(withExif));
        Assert.NotNull(PicturePreview.Problem(withExif, 832, 1216));
        var withAlpha = Riff(Chunk("VP8X", vp8x), Chunk("ALPH", [0, 0]), vp8);
        Assert.Same(withAlpha, PicturePreview.Plain(withAlpha));
    }

    private static byte[] Chunk(string name, byte[] body)
    {
        var chunk = new byte[8 + body.Length + (body.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(chunk, 0);
        BitConverter.GetBytes((uint)body.Length).CopyTo(chunk, 4);
        body.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        var body = chunks.SelectMany(c => c).ToArray();
        return [.. "RIFF"u8.ToArray(), .. BitConverter.GetBytes((uint)(body.Length + 4)), .. "WEBP"u8.ToArray(), .. body];
    }

    /// <summary>Draws the first picture, then waits at the second until it is cancelled (while <see cref="Stop"/>).</summary>
    private sealed class StopsAtSecond : IImageEngine
    {
        public ImageModel? Model { get; private set; }
        public bool IsBusy => false;
        public bool IsRunning => Model is not null;
        public string? Error => null;
        public event Action? Changed { add { } remove { } }
        public List<ImageRequest> Requests { get; } = [];
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stop { get; set; } = true;

        public Task LoadAsync(ImageModel model, CancellationToken ct = default) { Model = model; return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken ct = default) { Model = null; return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<ImageResult> DrawAsync(ImageRequest request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
        {
            Requests.Add(request);
            progress?.Report(new ImageProgress("sampling", 1, request.Steps));
            if (Stop && Requests.Count == 2)
            {
                Second.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new ImageResult([KeptPictureTests.Webp(request.Width, request.Height, 200, Requests.Count)], request.Format, 500 + Requests.Count);
        }
    }
}
