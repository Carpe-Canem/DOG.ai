using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Search;
using DogAI.Core.Video;

namespace DogAI.Tests;

/// <summary>The Video section's Kept videos tab and the Animate… buttons (M20 step 5).</summary>
public class VideoKeptTests
{
    private static Clip Kept(VideoQuality quality, PictureShape shape, byte[]? frame = null, long seed = 4321, long id = 7)
    {
        var request = VideoRequest.For("A wolf turns his head to the fire.", quality, shape, seed, frame);
        var webm = TestWebm.Make(request.Width, request.Height, request.Frames, seed: (int)(seed % 1000));
        var clip = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, request,
            new VideoResult(webm, request.Width, request.Height, request.Frames, request.Fps, seed));
        clip.Id = id;
        clip.CreatedAt = new DateTime(2026, 11, 17, 13, 3, 0, DateTimeKind.Utc);
        return clip;
    }

    /// <summary>A first frame of the shape as the page writes it (Better's size), or a DOG.ai WebP at Small's.</summary>
    private static byte[] FrameFor(PictureShape shape, bool webp)
    {
        var (w, h) = VideoRequest.SizeOf(webp ? VideoQuality.Quick : VideoQuality.Better, shape);
        return webp ? KeptPictureTests.Webp(w, h) : TestJpegs.Make(w, h);
    }

    // ── Make one like this ──

    [Theory]
    [InlineData(VideoQuality.Quick, PictureShape.Wide, 0)]
    [InlineData(VideoQuality.Quick, PictureShape.Tall, 1)]
    [InlineData(VideoQuality.Quick, PictureShape.Square, 2)]
    [InlineData(VideoQuality.Better, PictureShape.Wide, 2)]
    [InlineData(VideoQuality.Better, PictureShape.Tall, 0)]
    [InlineData(VideoQuality.Better, PictureShape.Square, 1)]
    public void Make_one_like_this_puts_back_what_makes_the_same_clip(VideoQuality quality, PictureShape shape, int frameKind)
    {
        // 0: from the sentence alone; 1: from a frame the page cut (JPEG); 2: from a WebP DOG.ai drew.
        var frame = frameKind == 0 ? null : FrameFor(shape, webp: frameKind == 2);
        var clip = Kept(quality, shape, frame);

        var (like, problem) = VideoLike.Of(clip);
        Assert.Null(problem);
        Assert.Equal(new VideoControls(quality, shape, 4321), like!.Controls);
        Assert.Equal(clip.Prompt, like.Sentence);

        // What the Make tab builds from it is the request the clip was made with.
        var again = VideoRequest.For(like.Sentence, like.Controls.Quality, like.Controls.Shape, like.Controls.Seed!.Value, like.Frame?.Bytes);
        var was = clip.ToRequest();
        Assert.Equal(was with { StartFrame = null }, again with { StartFrame = null });
        if (frame is null)
        {
            // Nothing to hold: any frame held before is let go.
            Assert.Null(like.Frame);
        }
        else
        {
            Assert.Equal(frame, like.Frame!.Bytes);
            Assert.Equal(shape, like.Frame.Shape);
            Assert.Equal(VideoPanels.FromKeptVideo, like.Frame.From);
        }
    }

    [Fact]
    public void A_clip_read_from_a_list_has_no_first_frame_to_put_back_and_says_so()
    {
        var clip = Kept(VideoQuality.Quick, PictureShape.Wide, FrameFor(PictureShape.Wide, webp: false));
        clip.StartFrame = null; // as PageAsync gives it: the size is known, the bytes are not read
        Assert.True(clip.HasStartFrame);
        var (like, problem) = VideoLike.Of(clip);
        Assert.Null(like);
        Assert.Equal("Its first frame could not be read.", problem);
    }

    [Fact]
    public void Putting_a_better_clip_back_on_a_card_that_cannot_make_better_says_it_will_be_quick()
    {
        var (like, _) = VideoLike.Of(Kept(VideoQuality.Better, PictureShape.Wide));
        Assert.Contains("Draw makes the same clip", VideoPanels.PutBack(like!, betterOffered: true));
        var said = VideoPanels.PutBack(like!, betterOffered: false);
        Assert.Contains("which this card cannot make, so Draw makes it at Quick", said);
        Assert.Contains("(4321)", said);
        Assert.Contains("first frame", VideoPanels.PutBack(VideoLike.Of(Kept(VideoQuality.Quick, PictureShape.Tall, FrameFor(PictureShape.Tall, true))).Like!, true));
    }

    // ── Animate… ──

    [Theory]
    // DOG.ai's own sizes go as they are: Small, Normal, Fine and Small made larger (×1.5, and ×1.25's fallback).
    [InlineData(640, 960)] [InlineData(960, 640)] [InlineData(768, 768)]
    [InlineData(832, 1216)] [InlineData(1216, 832)] [InlineData(1024, 1024)]
    [InlineData(1280, 1856)] [InlineData(1856, 1280)] [InlineData(1536, 1536)]
    [InlineData(960, 1472)] [InlineData(1472, 960)] [InlineData(1152, 1152)] [InlineData(960, 960)]
    public void A_picture_DOG_ai_drew_starts_a_clip_as_it_is(int width, int height)
    {
        var webp = KeptPictureTests.Webp(width, height);
        var (frame, problem) = VideoFrame.Of(webp, VideoPanels.FromKeptPicture);
        Assert.Null(problem);
        Assert.Same(webp, frame!.Bytes);
        Assert.Equal(VideoFrames.Nearest(width, height), frame.Shape);
        Assert.StartsWith("data:image/webp;base64,", frame.Src);
    }

    [Fact]
    public void Anything_else_is_refused_as_it_is_so_the_page_cuts_it()
    {
        // A picture enlarged ×4 (over the engine's 4,096), a size of no shape, and a PNG (portraits in the editors are PNG).
        Assert.NotNull(VideoFrame.Of(KeptPictureTests.Webp(3328, 4864), VideoPanels.FromKeptPicture).Problem);
        Assert.NotNull(VideoFrame.Of(KeptPictureTests.Webp(1000, 600), VideoPanels.FromTry).Problem);
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 2, 0, 0, 0, 2, 0, 8, 2, 0, 0, 0];
        Assert.NotNull(VideoFrame.Of(png, VideoPanels.FromPortrait).Problem);

        // What the page then writes passes: the cut of each at Better's size of its nearest shape.
        foreach (var (w, h) in new[] { (3328, 4864), (1000, 600), (512, 512) })
        {
            var cut = VideoFrames.Cut(w, h);
            Assert.Null(VideoFrame.Of(TestJpegs.Make(cut.OutWidth, cut.OutHeight), VideoPanels.CutFrom(VideoPanels.FromPortrait, cut.Shape)).Problem);
        }
        Assert.Equal("the portrait, cut to wide", VideoPanels.CutFrom(VideoPanels.FromPortrait, PictureShape.Wide));
    }

    [Fact]
    public void A_picture_handed_over_is_shown_large_until_a_clip_is_picked_or_made()
    {
        var session = new VideoSession();
        var (frame, _) = VideoFrame.Of(FrameFor(PictureShape.Tall, webp: true), VideoPanels.FromStory);
        Assert.False(session.FrameShown);

        session.Hold(frame);
        Assert.True(session.FrameShown);
        Assert.Null(session.Shown);
        Assert.Equal("The clip will start from a story picture. Say below what moves, then press Draw.", VideoPanels.Arrived(frame!));

        session.Hold(null);
        Assert.False(session.FrameShown);
    }

    [Fact]
    public async Task Picking_a_try_or_a_new_clip_ends_showing_the_frame()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-videokept-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = VideoFolders.ModelFor(root, VideoFiles.Wan22);
            foreach (var (path, _) in model.Files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }
            var session = new VideoSession();
            var engine = new FakeVideoEngine();
            var request = VideoRequest.For("A fox runs.", VideoQuality.Quick, PictureShape.Wide);
            var first = await session.MakeAsync(engine, model, request, VideoEstimate.For(request));
            Assert.Same(first, session.Shown);

            var (frame, _) = VideoFrame.Of(FrameFor(PictureShape.Wide, webp: false), VideoPanels.FromChat);
            session.Hold(frame);
            Assert.Null(session.Shown);
            Assert.Same(first, session.Selected); // still the try Keep and the seed row speak of
            session.Select(first);
            Assert.Same(first, session.Shown);

            session.Hold(frame);
            var second = await session.MakeAsync(engine, model, request with { StartFrame = frame!.Bytes }, VideoEstimate.For(request));
            Assert.Same(second, session.Shown);
            Assert.Same(frame, session.Frame); // the frame stays held for the next clip
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Every_picture_place_has_an_animate_button_that_hands_its_picture_on()
    {
        var ui = Path.Combine(SolutionRoot(), "src", "DogAI.UI");
        foreach (var (file, id) in new[]
        {
            ("Pages/ImagesMake.razor", "images-animate"),
            ("Components/Images/KeptPictures.razor", "kept-animate"),
            ("Pages/CharacterEditor.razor", "portrait-animate"),
            ("Pages/Personas.razor", "portrait-animate"),
            ("Components/Images/MessagePictures.razor", "picture-animate"),
        })
        {
            var text = File.ReadAllText(Path.Combine(ui, file));
            Assert.Contains($"<AnimateButton Id=\"{id}\"", text);
        }
        // The one way a picture is handed over: the button holds it and opens the Make tab.
        var button = File.ReadAllText(Path.Combine(ui, "Components", "Video", "AnimateButton.razor"));
        Assert.Contains("Session.Hold(frame)", button);
        Assert.Contains("VideoFrames.Cut", button);
        Assert.Contains("NavigateTo(VideoPanels.Address)", button);
    }

    // ── The tab, its words ──

    [Fact]
    public void Kept_videos_is_a_second_address_of_the_video_page_and_in_ctrl_k()
    {
        var screen = Assert.Single(QuickJump.Screens, s => s.Route == VideoPanels.KeptAddress);
        Assert.False(screen.Main);
        Assert.Equal("Kept videos", screen.Title);
        Assert.Contains(QuickJump.Find([], "kept videos"), t => t.Route == VideoPanels.KeptAddress);
        Assert.Equal("Kept videos", ReturnPlace.NameOf(VideoPanels.KeptAddress));
        Assert.Equal([(VideoPanels.Address, "Make"), (VideoPanels.KeptAddress, "Kept videos")], VideoPanels.Tabs);

        var page = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "DogAI.UI", "Pages", "VideoMake.razor"));
        Assert.Contains("@page \"/video\"", page);
        Assert.Contains("@page \"/video/kept\"", page);
    }

    [Fact]
    public void The_kept_words_count_and_describe_a_clip()
    {
        Assert.Equal("No kept videos yet. Clips you keep on the Make tab show here.", VideoPanels.KeptCount(0, 0));
        Assert.Equal("1 kept video", VideoPanels.KeptCount(1, 1));
        Assert.Equal("30 kept videos · showing 24", VideoPanels.KeptCount(30, 24));
        Assert.Equal("1,200 kept videos", VideoPanels.KeptCount(1200, 1200));

        var clip = Kept(VideoQuality.Quick, PictureShape.Wide, FrameFor(PictureShape.Wide, false));
        var meta = VideoPanels.KeptMeta(clip, new DateTime(2026, 11, 17, 14, 3, 0));
        Assert.StartsWith("960 × 640 · 5 s · Quick · seed 4321 · from a picture · ", meta);
        Assert.EndsWith(" MB · kept 17 Nov 2026, 14:03", meta);
        Assert.DoesNotContain("from a picture", VideoPanels.KeptMeta(Kept(VideoQuality.Better, PictureShape.Tall), DateTime.Now));
        Assert.Contains("Better", VideoPanels.KeptMeta(Kept(VideoQuality.Better, PictureShape.Tall), DateTime.Now));
    }

    private static string SolutionRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
