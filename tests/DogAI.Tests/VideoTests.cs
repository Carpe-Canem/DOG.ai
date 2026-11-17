using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Video;
using DogAI.Engine;
using DogAI.Engine.Images;
using DogAI.Engine.Video;

namespace DogAI.Tests;

/// <summary>
/// Writes WebM files that are whole as far as their structure goes (all DOG.ai reads): the parts the video engine
/// writes, in its order, with pretend frames. Each option adds or changes one thing a clip must not have.
/// </summary>
internal static class TestWebm
{
    /// <summary>Sits in the middle of the first frame, so a search of a data folder's bytes finds a kept clip.</summary>
    public static readonly byte[] Marker = Encoding.ASCII.GetBytes("\u0001KEPT-CLIP-BYTES-QUOKKA\u0002");

    public const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966, Tracks = 0x1654AE6B, TrackEntry = 0xAE,
        Cluster = 0x1F43B675, Tags = 0x1254C367, Attachments = 0x1941A469, Chapters = 0x1043A770;

    public static byte[] El(uint id, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. Id(id), .. Size(body.Length), .. body];
    }

    /// <summary>A part that does not say its size, as a file written to a stream has them.</summary>
    public static byte[] Open(uint id, params byte[][] parts) => [.. Id(id), 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. parts.SelectMany(p => p)];

    public static byte[] Num(uint id, long value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, (byte)value);
            value >>= 8;
        } while (value > 0);
        return El(id, [.. bytes]);
    }

    public static byte[] Text(uint id, string text) => El(id, Encoding.UTF8.GetBytes(text));

    public static byte[] Float(uint id, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        return El(id, bytes);
    }

    private static byte[] Id(uint id) =>
        id > 0xFFFFFF ? [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id > 0xFFFF ? [(byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id > 0xFF ? [(byte)(id >> 8), (byte)id]
        : [(byte)id];

    private static byte[] Size(int length) =>
        length < 127 ? [(byte)(0x80 | length)] : [(byte)(0x10 | length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length];

    /// <param name="inSegment">Parts added at the end of the segment (tags, an attached file).</param>
    /// <param name="inInfo">Parts added to the file's facts (a title, a date).</param>
    /// <param name="inTrack">Parts added to the track (a name).</param>
    /// <param name="moreTracks">Whole tracks added after the video track.</param>
    /// <param name="after">Bytes after the end of the file.</param>
    /// <param name="open">The frames' parts do not say their size.</param>
    public static byte[] Make(int width = 960, int height = 640, int frames = 121, int seed = 1, int filler = 300, byte[]? marker = null,
        string docType = "webm", string codec = "V_VP8", int trackType = 1, string writer = "stable-diffusion.cpp",
        byte[]? inSegment = null, byte[]? inInfo = null, byte[]? inTrack = null, byte[]? moreTracks = null, byte[]? after = null,
        bool open = false, byte[]? padding = null, int blockTrack = 1, byte blockFlags = 0x80)
    {
        var random = new Random(seed);
        var uid = new byte[7];
        random.NextBytes(uid);
        var header = El(Ebml, Num(0x4286, 1), Num(0x42F7, 1), Num(0x42F2, 4), Num(0x42F3, 8), Text(0x4282, docType), Num(0x4287, 4), Num(0x4285, 2));
        var body = new List<byte>();
        body.AddRange(El(0x114D9B74, El(0x4DBB, El(0x53AB, [0x15, 0x49, 0xA9, 0x66]), Num(0x53AC, 110))));
        body.AddRange(El(0xEC, padding ?? new byte[45]));
        body.AddRange(El(Info, Num(0x2AD7B1, 1_000_000), Float(0x4489, frames * 1000f / 24), Text(0x4D80, writer), Text(0x5741, writer), inInfo ?? []));
        body.AddRange(El(Tracks,
            El(TrackEntry, Num(0xD7, 1), El(0x73C5, uid), Num(0x83, trackType), Text(0x86, codec),
                El(0xE0, Num(0xB0, width), Num(0xBA, height), Num(0x54B0, width), Num(0x54BA, height), Float(0x2383E3, 24)), inTrack ?? []),
            moreTracks ?? []));
        for (var i = 0; i < frames; i++)
        {
            var frame = new byte[filler];
            random.NextBytes(frame);
            if (i == 0 && filler >= 2 * (marker ?? Marker).Length) (marker ?? Marker).CopyTo(frame.AsSpan(filler / 2));
            byte[] block = [(byte)(0x80 | blockTrack), 0, 0, blockFlags, .. frame];
            body.AddRange(open ? Open(Cluster, Num(0xE7, i * 42), El(0xA3, block)) : El(Cluster, Num(0xE7, i * 42), El(0xA3, block)));
        }
        body.AddRange(El(0x1C53BB6B, El(0xBB, Num(0xB3, 0), El(0xB7, Num(0xF7, 1), Num(0xF1, 233)))));
        if (inSegment is not null) body.AddRange(inSegment);
        return [.. header, .. El(Segment, [.. body]), .. after ?? []];
    }

    /// <summary>What an engine started without its metadata switch writes: the prompt, as a tag.</summary>
    public static byte[] TagsWith(string words) =>
        El(Tags, El(0x7373, El(0x63C0, Num(0x68CA, 50)), El(0x67C8, Text(0x45A3, "COMMENT"), Text(0x4487, words))));
}

public class WebmInfoTests
{
    [Fact]
    public void A_clip_as_the_engine_writes_it_says_its_size_frames_length_and_writer()
    {
        var info = WebmInfo.Read(TestWebm.Make(960, 640), out var problem);
        Assert.Null(problem);
        Assert.Equal(new WebmInfo(960, 640, 121, 121 / 24.0, "stable-diffusion.cpp"), info!.Value with { Seconds = 121 / 24.0 });
        Assert.InRange(info.Value.Seconds, 5.04, 5.05);

        Assert.Equal((832, 1216, 33), WebmInfo.Read(TestWebm.Make(832, 1216, 33)) is { } tall ? (tall.Width, tall.Height, tall.Frames) : default);
        // Frames that do not say their size (a file written to a stream) are read the same.
        Assert.Equal(9, WebmInfo.Read(TestWebm.Make(frames: 9, open: true))!.Value.Frames);
    }

    [Fact]
    public void Tags_an_attached_file_and_anything_else_that_could_carry_words_are_refused()
    {
        static string? Why(byte[] webm)
        {
            Assert.Null(WebmInfo.Read(webm, out var problem));
            Assert.Null(WebmInfo.Read(webm));
            return problem;
        }

        // What the engine writes without --disable-image-metadata.
        Assert.Equal("it carries tags", Why(TestWebm.Make(inSegment: TestWebm.TagsWith("a red fox in the snow"))));
        Assert.Equal("it carries tags", Why(TestWebm.Make(open: true, inSegment: TestWebm.TagsWith("a red fox"))));
        Assert.Equal("it carries an attached file", Why(TestWebm.Make(inSegment: TestWebm.El(TestWebm.Attachments, TestWebm.El(0x61A7, TestWebm.Text(0x466E, "prompt.txt"))))));
        Assert.Equal("it carries chapters", Why(TestWebm.Make(inSegment: TestWebm.El(TestWebm.Chapters))));
        Assert.Equal("it carries a title", Why(TestWebm.Make(inInfo: TestWebm.Text(0x7BA9, "a red fox"))));
        Assert.Equal("it carries a date", Why(TestWebm.Make(inInfo: TestWebm.Num(0x4461, 123456789))));
        Assert.Equal("it carries a file name", Why(TestWebm.Make(inInfo: TestWebm.Text(0x7384, "fox.webm"))));
        Assert.Equal("its track carries a name", Why(TestWebm.Make(inTrack: TestWebm.Text(0x536E, "a red fox"))));
        Assert.Equal("its track carries private data", Why(TestWebm.Make(inTrack: TestWebm.El(0x63A2, [1, 2, 3]))));
        // The writing program's name is the one place with words: a short name without spaces, never a sentence.
        Assert.Equal("it carries words where the writing program’s name belongs", Why(TestWebm.Make(writer: "a red fox in the snow")));
        Assert.Equal("it carries words where the writing program’s name belongs", Why(TestWebm.Make(writer: new string('x', WebmInfo.MaxNameLength + 1))));
        Assert.NotNull(WebmInfo.Read(TestWebm.Make(writer: new string('x', WebmInfo.MaxNameLength))));
        // Padding is the one part that could hold anything, so it must be empty.
        Assert.Equal("its padding is not empty", Why(TestWebm.Make(padding: Encoding.ASCII.GetBytes("a red fox in the snow"))));
        // A part this reader does not know (here a made-up one, and a segment UID) is refused, not skipped.
        Assert.Equal("it has a part DOG.ai does not know", Why(TestWebm.Make(inSegment: TestWebm.El(0x1F697654, [1]))));
        Assert.Equal("it has a part DOG.ai does not know", Why(TestWebm.Make(inInfo: TestWebm.El(0x73A4, new byte[16]))));
        // A number is a number: a "track number" of forty bytes is words.
        Assert.Equal("it has a part DOG.ai does not know", Why(TestWebm.Make(inTrack: TestWebm.El(0x55AA, Encoding.ASCII.GetBytes("a red fox in the snow at night")))));
        Assert.Equal("it has bytes after its end", Why(TestWebm.Make(after: Encoding.ASCII.GetBytes("a red fox"))));
    }

    [Fact]
    public void Only_one_vp8_video_track_with_its_frames_is_a_clip()
    {
        static string? Why(byte[] webm)
        {
            Assert.Null(WebmInfo.Read(webm, out var problem));
            return problem;
        }

        var sound = TestWebm.El(TestWebm.TrackEntry, TestWebm.Num(0xD7, 2), TestWebm.Num(0x83, 2), TestWebm.Text(0x86, "A_OPUS"));
        Assert.Equal("it has more than one track", Why(TestWebm.Make(moreTracks: sound)));
        Assert.Equal("it has sound", Why(TestWebm.Make(inTrack: TestWebm.El(0xE1, TestWebm.Num(0x9F, 2)))));
        Assert.Equal("its track is not video", Why(TestWebm.Make(trackType: 2)));
        Assert.Equal("its video is not VP8", Why(TestWebm.Make(codec: "V_VP9")));
        Assert.Equal("its video is not VP8", Why(TestWebm.Make(codec: "V_MPEG4/ISO/AVC")));
        Assert.Equal("it does not say it is a WebM", Why(TestWebm.Make(docType: "matroska")));
        Assert.Equal("it has no frames", Why(TestWebm.Make(frames: 0)));
        Assert.Equal("its frames belong to another track", Why(TestWebm.Make(blockTrack: 2)));
        // Several frames packed into one block would make the count wrong.
        Assert.Equal("it has frames DOG.ai cannot count", Why(TestWebm.Make(blockFlags: 0x82)));
        Assert.Equal("it does not start as a WebM", Why(KeptPictureTests.Webp(960, 640)));
        Assert.Equal("it does not start as a WebM", Why([]));
        Assert.Equal("it does not start as a WebM", Why(TestJpegs.Make(960, 640)));
    }

    [Fact]
    public void A_file_cut_short_or_damaged_is_refused_and_never_throws()
    {
        var whole = TestWebm.Make(frames: 12);
        for (var length = 0; length < whole.Length; length += 7)
            Assert.Null(WebmInfo.Read(whole.AsSpan(0, length)));

        // Any byte changed: the reader may still call it a clip (a changed frame is one), but it never throws.
        var random = new Random(7);
        for (var i = 0; i < 3000; i++)
        {
            var damaged = (byte[])whole.Clone();
            for (var n = random.Next(1, 4); n > 0; n--) damaged[random.Next(i % 2 == 0 ? 400 : damaged.Length)] = (byte)random.Next(256);
            _ = WebmInfo.Read(damaged, out _);
        }
    }

    [Fact]
    public void A_clip_must_be_the_size_and_length_asked_for()
    {
        var request = new VideoRequest("a fox", 960, 640);
        Assert.Null(VideoClips.Problem(TestWebm.Make(960, 640), request));
        Assert.Equal("The video is 640 × 960, not the 960 × 640 asked for.", VideoClips.Problem(TestWebm.Make(640, 960), request));
        // 120 frames asked for gave 117 in the trial.
        Assert.Equal("The video has 117 frames, not the 121 asked for.", VideoClips.Problem(TestWebm.Make(960, 640, 117), request));
        Assert.Equal("The video is not a plain WebM file: it carries tags", VideoClips.Problem(TestWebm.Make(inSegment: TestWebm.TagsWith("a fox")), request));
        Assert.Equal("The video is empty.", VideoClips.Problem([], request));
        Assert.Equal("The video is empty.", VideoClips.Problem(null));
        // Without a size, only the structure is asked about.
        Assert.Null(VideoClips.Problem(TestWebm.Make(320, 256, 5)));
    }
}

public class VideoRequestTests
{
    private static byte[] WebpWithExif(int width, int height)
    {
        var plain = KeptPictureTests.Webp(width, height);
        var canvas = new byte[10];
        canvas[0] = 0x08;
        canvas[4] = (byte)(width - 1); canvas[5] = (byte)(width - 1 >> 8);
        canvas[7] = (byte)(height - 1); canvas[8] = (byte)(height - 1 >> 8);
        byte[] exif = [1, 2, 3, 4];
        byte[] file = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, .. canvas, .. plain.AsSpan(12).ToArray(), .. "EXIF"u8, 4, 0, 0, 0, .. exif];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        return file;
    }

    [Theory]
    [InlineData(VideoQuality.Quick, PictureShape.Wide, 960, 640, VideoDecoder.Fast)]
    [InlineData(VideoQuality.Quick, PictureShape.Tall, 640, 960, VideoDecoder.Fast)]
    [InlineData(VideoQuality.Quick, PictureShape.Square, 768, 768, VideoDecoder.Fast)]
    [InlineData(VideoQuality.Better, PictureShape.Wide, 1216, 832, VideoDecoder.Own)]
    [InlineData(VideoQuality.Better, PictureShape.Tall, 832, 1216, VideoDecoder.Own)]
    [InlineData(VideoQuality.Better, PictureShape.Square, 1024, 1024, VideoDecoder.Own)]
    public void A_clip_is_dog_ais_own_picture_size_with_the_trials_numbers(VideoQuality quality, PictureShape shape, int width, int height, VideoDecoder decoder)
    {
        var request = VideoRequest.For("  A fox runs through the snow.  ", quality, shape, seed: 77);

        Assert.Empty(request.Problems());
        Assert.Equal("A fox runs through the snow.", request.Prompt);
        Assert.Equal((width, height, decoder), (request.Width, request.Height, request.Decoder));
        // The owner's decisions of 2026-11-16: five seconds at 24 a second, 20 steps, Euler, guidance 5, flow shift 5, 70.
        Assert.Equal((121, 24, 20, "euler", 5.0, 5.0, 70), (request.Frames, request.Fps, request.Steps, request.Sampler, request.Guidance, request.FlowShift, request.Compression));
        Assert.Equal(77, request.Seed);
        Assert.Equal(VideoRequest.FixedNegative, request.NegativePrompt);
        Assert.Null(request.StartFrame);
        Assert.Equal((quality, shape), request.Level);
        Assert.InRange(request.Seconds, 5.0, 5.1);
        // The engine rounds a size that is no multiple of 32 up; these are never rounded.
        Assert.Equal(0, width % 32 + height % 32);
        // Size and decoder are carried apart: Better's size with the fast decoder is a request like any other.
        Assert.Empty((request with { Decoder = VideoDecoder.Fast }).Problems());
    }

    [Fact]
    public void The_fixed_negative_prompt_is_english_and_says_nothing_about_style()
    {
        Assert.All(VideoRequest.FixedNegative, c => Assert.True(c < 0x7F));
        foreach (var word in new[] { "style", "painting", "artwork", "drawing", "anime", "cartoon", "photo" })
            Assert.DoesNotContain(word, VideoRequest.FixedNegative, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("low quality", VideoRequest.FixedNegative);
    }

    [Fact]
    public void Bad_requests_are_explained()
    {
        static string Said(VideoRequest request) => string.Join(" ", request.Problems());
        var good = new VideoRequest("a fox");
        Assert.Empty(good.Problems());

        Assert.Contains("Say in a sentence", Said(good with { Prompt = "  " }));
        Assert.Contains("multiple of 32", Said(good with { Width = 1000 }));
        Assert.Contains("between 256 and 1280", Said(good with { Height = 2048 }));
        // The engine makes 117 frames of 120.
        Assert.Contains("one more than a multiple of four", Said(good with { Frames = 120 }));
        Assert.Contains("between 5 and 121 frames", Said(good with { Frames = 193 }));
        Assert.Contains("Steps", Said(good with { Steps = 0 }));
        Assert.Contains("Steps", Said(good with { Steps = 51 }));
        Assert.Contains("Guidance", Said(good with { Guidance = double.NaN }));
        Assert.Contains("Flow shift", Said(good with { FlowShift = 0 }));
        Assert.Contains("file-size", Said(good with { Compression = 101 }));
        Assert.Contains("sampler", Said(good with { Sampler = "" }));
        Assert.Contains("Frames a second", Said(good with { Fps = 0 }));
        // The trial's short clip for tests is a request like any other.
        Assert.Empty(new VideoRequest("a fox", 832, 480, Frames: 33, Steps: 8).Problems());
    }

    [Fact]
    public void The_first_frame_is_a_plain_webp_or_jpeg_of_the_clips_own_shape()
    {
        // A picture DOG.ai drew at the clip's own size, and the JPEG the page made.
        Assert.Null(VideoStartFrame.Problem(KeptPictureTests.Webp(960, 640), 960, 640));
        Assert.Null(VideoStartFrame.Problem(TestJpegs.Make(960, 640), 960, 640));
        // Every DOG.ai size of one shape goes with every clip of that shape (the engine takes a sliver off two edges).
        foreach (var (w, h) in new[] { (640, 960), (832, 1216), (960, 1472), (1280, 1856), (800, 1200) })
        {
            Assert.Null(VideoStartFrame.Problem(KeptPictureTests.Webp(w, h), 640, 960));
            Assert.Null(VideoStartFrame.Problem(KeptPictureTests.Webp(w, h), 832, 1216));
        }
        Assert.Null(VideoStartFrame.Problem(KeptPictureTests.Webp(1536, 1536), 768, 768));

        // Another shape is refused: the engine would cut the middle out (a tall portrait in a wide clip lost its head).
        const string Shape = "The first frame is not the shape of the video. Cut it to the video’s shape first.";
        Assert.Equal(Shape, VideoStartFrame.Problem(KeptPictureTests.Webp(640, 960), 960, 640));
        Assert.Equal(Shape, VideoStartFrame.Problem(KeptPictureTests.Webp(768, 768), 960, 640));
        Assert.Equal(Shape, VideoStartFrame.Problem(TestJpegs.Make(1024, 576), 960, 640));
        Assert.Equal(Shape, VideoStartFrame.Problem(KeptPictureTests.Webp(832, 1216), 768, 768));

        // Nothing but the picture: a description of any kind is refused, never cleaned quietly.
        const string Extra = "The first frame carries extra information besides the picture.";
        Assert.Equal(Extra, VideoStartFrame.Problem(WebpWithExif(960, 640), 960, 640));
        Assert.Equal(Extra, VideoStartFrame.Problem(TestJpegs.Make(960, 640, extra: TestJpegs.Exif()), 960, 640));
        Assert.Equal(Extra, VideoStartFrame.Problem(TestJpegs.Make(960, 640, extra: TestJpegs.Comment("a red fox")), 960, 640));
        Assert.Equal(Extra, VideoStartFrame.Problem(TestJpegs.Make(960, 640, extra: TestJpegs.Profile()), 960, 640));

        // Not a picture of the two kinds: a PNG, a WebM, text, a path.
        const string Kind = "The first frame must be a picture DOG.ai drew or one the page made from a file.";
        Assert.Equal(Kind, VideoStartFrame.Problem([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0], 960, 640));
        Assert.Equal(Kind, VideoStartFrame.Problem(TestWebm.Make(960, 640, 5), 960, 640));
        Assert.Equal(Kind, VideoStartFrame.Problem(Encoding.UTF8.GetBytes(@"C:\Users\me\Pictures\fox.jpg"), 960, 640));
        Assert.Equal("The first frame is empty.", VideoStartFrame.Problem([], 960, 640));
        Assert.Contains("between 64 and 4096", VideoStartFrame.Problem(KeptPictureTests.Webp(48, 32), 960, 640));

        // The request checks its own first frame.
        var request = VideoRequest.For("a fox turns its head", VideoQuality.Quick, PictureShape.Wide, startFrame: KeptPictureTests.Webp(960, 640));
        Assert.Empty(request.Problems());
        Assert.Equal([Shape], (request with { StartFrame = KeptPictureTests.Webp(640, 960) }).Problems());
    }

    [Fact]
    public void The_estimate_is_the_trials_table()
    {
        static double Total(VideoQuality quality, VideoDecoder decoder, bool warm = true) =>
            VideoEstimate.For(VideoRequest.For("a fox", quality, PictureShape.Wide) with { Decoder = decoder }, warm).Total;

        // Measured for 20 steps (docs/PLAN.md): 114 s, 150 s, 217 s and 408 s, give or take a tenth.
        Assert.InRange(Total(VideoQuality.Quick, VideoDecoder.Fast), 103, 125);
        Assert.InRange(Total(VideoQuality.Quick, VideoDecoder.Own), 135, 165);
        Assert.InRange(Total(VideoQuality.Better, VideoDecoder.Fast), 195, 239);
        Assert.InRange(Total(VideoQuality.Better, VideoDecoder.Own), 367, 449);
        // After something else large was read the model's files come from the disk again: about half a minute more.
        Assert.Equal(Total(VideoQuality.Quick, VideoDecoder.Fast) + VideoEstimate.ColdStart - VideoEstimate.WarmStart, Total(VideoQuality.Quick, VideoDecoder.Fast, warm: false), 3);

        // Tall takes what wide takes; the sizes the trial did not make are between their neighbours.
        var tall = VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Tall), warm: true);
        Assert.Equal(Total(VideoQuality.Quick, VideoDecoder.Fast), tall.Total, 3);
        Assert.InRange(VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Square), warm: true).Total, 95, 114);
        Assert.InRange(VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Better, PictureShape.Square), warm: true).Total, 400, 470);

        // The parts: only drawing counts steps; the page runs its bar on the other two.
        var better = VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Better, PictureShape.Wide), warm: true);
        Assert.InRange(better.Drawing, 190, 205);
        Assert.InRange(better.Decoding, 185, 200);
        Assert.InRange(better.Encoding, 11, 14);
        Assert.Equal(better.Total, better.Start + better.Drawing + better.Decoding + better.Encoding, 6);
        // 12 steps took 71 s, 30 steps 160 s (Quick, fast decoder).
        Assert.InRange(VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Wide) with { Steps = 12 }, warm: true).Total, 64, 80);
        Assert.InRange(VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Wide) with { Steps = 30 }, warm: true).Total, 144, 176);
        // The short clip of the real-engine test: 33 frames, 832 × 480, 8 steps took 13 to 17 s.
        Assert.InRange(VideoEstimate.For(new VideoRequest("a fox", 832, 480, Frames: 33, Steps: 8), warm: true).Total, 10, 18);
        // A first frame adds half a second.
        Assert.Equal(tall.Start + 0.5, VideoEstimate.For(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Tall, startFrame: KeptPictureTests.Webp(640, 960)), warm: true).Start, 3);
    }

    [Theory]
    [InlineData(20, "about 20 seconds")]
    [InlineData(44, "about 40 seconds")]
    [InlineData(70, "about a minute")]
    [InlineData(114, "about 2 minutes")]
    [InlineData(150, "about 2½ minutes")]
    [InlineData(217, "about 3½ minutes")]
    [InlineData(408, "about 7 minutes")]
    public void The_estimate_is_said_in_round_words(double seconds, string words) =>
        Assert.Equal(words, new VideoEstimate(0, seconds, 0, 0).Words());

    [Fact]
    public void A_clip_needs_fifteen_gigabytes_at_quicks_size_and_sixteen_at_betters()
    {
        const long Gb = 1L << 30;
        foreach (var shape in Enum.GetValues<PictureShape>())
        {
            Assert.Equal(15 * Gb, VideoMemory.Need(VideoRequest.For("a fox", VideoQuality.Quick, shape)));
            Assert.Equal(16 * Gb, VideoMemory.Need(VideoRequest.For("a fox", VideoQuality.Better, shape)));
        }
        // The decoder does not change it: the engine uses what is there and gives way by itself.
        Assert.Equal(15 * Gb, VideoMemory.Need(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Wide) with { Decoder = VideoDecoder.Own }));
        Assert.Equal(15 * Gb, VideoMemory.Need(new VideoRequest("a fox", 832, 480, Frames: 33)));
    }

    [Fact]
    public void A_models_files_are_found_in_the_engines_own_folders_and_named_without_a_full_path()
    {
        var model = VideoFolders.ModelFor(@"D:\models\video", VideoFiles.Wan22);
        Assert.Equal(@"D:\models\video\diffusion_models\wan2.2_ti2v_5B_fp16.safetensors", model.DiffusionPath);
        Assert.Equal(@"D:\models\video\vae\wan2.2_vae.safetensors", model.VaePath);
        Assert.Equal(@"D:\models\video\text_encoders\umt5-xxl-encoder-Q8_0.gguf", model.TextEncoderPath);
        Assert.Equal(@"D:\models\video\vae\taew2_2.safetensors", model.FastDecoderPath);
        Assert.Equal(@"E:\m\video", new DogAI.Core.Settings.AppSettings { ModelsDirectory = @"E:\m" }.VideoFolder());
        Assert.Null(new DogAI.Core.Settings.AppSettings().VideoFolder());

        Assert.Empty(VideoFiles.Wan22.Problems());
        Assert.Contains("path inside its own folder", string.Join(" ", (VideoFiles.Wan22 with { Model = @"D:\models\video\diffusion_models\wan.safetensors" }).Problems()));
        Assert.Contains("path inside its own folder", string.Join(" ", (VideoFiles.Wan22 with { Vae = @"..\..\vae.safetensors" }).Problems()));
        Assert.Contains(".safetensors or .gguf", string.Join(" ", (VideoFiles.Wan22 with { TextEncoder = "t5.pth" }).Problems()));
        Assert.Equal("sub/wan.safetensors", (VideoFiles.Wan22 with { Model = @"sub\wan.safetensors" }).Normalised().Model);
    }
}

/// <summary>
/// A pretend sd-server answering the video route from memory: a clip's job moves through the stages the real one
/// reports, one per poll, and ends with the file and a <c>generation_info</c> that repeats the prompt.
/// </summary>
internal sealed class FakeVideoSd : HttpMessageHandler
{
    public static readonly byte[] PreviewBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 7];

    public List<JsonObject> Started { get; } = [];
    public List<string> Requests { get; } = [];
    public int Steps { get; set; } = 4;

    /// <summary>The file the job ends with; by default a clip of the size and length asked for.</summary>
    public Func<JsonObject, byte[]>? Result { get; set; }
    public string? FailWith { get; set; }
    public (HttpStatusCode Status, string Body)? Refuse { get; set; }
    public bool Endless { get; set; }

    /// <summary>Like the model's own decoder: asked to cancel, the job goes on saying "generating".</summary>
    public bool Uncancellable { get; set; }
    public bool WithFrameStage { get; set; }
    public bool CancelRequested { get; private set; }

    /// <summary>Runs when the request has arrived, before it is answered.</summary>
    public Action<JsonObject>? OnStart { get; set; }
    private int _poll;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(request.Method + " " + path);
        if (request.Method == HttpMethod.Post && path == "/sdcpp/v1/vid_gen")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Started.Add(body);
            OnStart?.Invoke(body);
            if (Refuse is { } refuse) return Json(refuse.Status, refuse.Body);
            return Json(HttpStatusCode.Accepted, """{"id":"job-v","kind":"vid_gen","status":"queued","created":1,"poll_url":"/sdcpp/v1/jobs/job-v"}""");
        }
        if (request.Method == HttpMethod.Post && path == "/sdcpp/v1/jobs/job-v/cancel")
        {
            CancelRequested = true;
            return Json(HttpStatusCode.OK, """{"id":"job-v","status":"generating","cancel_requested":true}""");
        }
        if (request.Method == HttpMethod.Get && path == "/sdcpp/v1/jobs/job-v") return Json(HttpStatusCode.OK, NextState());
        if (request.Method == HttpMethod.Get && path == "/sdcpp/v1/models/status") return Json(HttpStatusCode.OK, """{"state":"empty"}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Stage(string stage, string? detail = null) =>
        $$"""{"id":"job-v","status":"generating","progress":{"stage":"{{stage}}","detail":{{(detail is null ? "null" : $"\"{detail}\"")}},"completed":null,"total":null,"unit":null},"preview":null}""";

    private string NextState()
    {
        var n = _poll++;
        if (CancelRequested && !Uncancellable)
            return """{"id":"job-v","status":"cancelled","progress":null,"preview":null,"result":null,"error":{"code":"cancelled","message":"cancelled"}}""";
        var stages = new List<string> { """{"id":"job-v","status":"queued","progress":null,"preview":null,"result":null,"error":null}""", Stage("preparing") };
        if (WithFrameStage) stages.Add(Stage("vae_encode"));
        stages.Add(Stage("conditioning", "positive_prompt"));
        if (n < stages.Count) return stages[n];
        var step = n - stages.Count;
        if (Endless || step <= Steps)
        {
            var shown = Endless ? Math.Min(step, 50) : step;
            var previewStep = shown - shown % 2;
            var preview = previewStep == 0 ? "null" : $$"""{"step":{{previewStep}},"b64_json":"{{Convert.ToBase64String(PreviewBytes)}}"}""";
            // The engine repeats the newest preview on every poll.
            return $$"""{"id":"job-v","status":"generating","progress":{"stage":"sampling","completed":{{shown}},"total":{{(Endless ? 50 : Steps)}},"unit":"steps"},"preview":{{preview}} }""";
        }
        if (step == Steps + 1) return Stage("vae_decode");
        if (step == Steps + 2) return Stage("encoding", "video");
        if (FailWith is { } message)
            return $$"""{"id":"job-v","status":"failed","error":{"code":"generation_failed","message":"{{message}}"} }""";

        var asked = Started[^1];
        var file = Result?.Invoke(asked) ?? TestWebm.Make((int)asked["width"]!, (int)asked["height"]!, (int)asked["video_frames"]!);
        // What the real engine sends beside the file: the prompt again, the negative prompt, the model's file names.
        var info = new JsonObject
        {
            ["prompt"] = asked["prompt"]!.GetValue<string>(),
            ["negative_prompt"] = asked["negative_prompt"]!.GetValue<string>(),
            ["diffusion_model"] = @"D:\models\video\diffusion_models\wan2.2_ti2v_5B_fp16.safetensors",
            ["seed"] = (long)asked["seed"]!,
        };
        return new JsonObject
        {
            ["id"] = "job-v",
            ["status"] = "completed",
            ["progress"] = new JsonObject { ["stage"] = "done" },
            ["result"] = new JsonObject
            {
                ["b64_json"] = Convert.ToBase64String(file),
                ["mime_type"] = "video/webm",
                ["output_format"] = "webm",
                ["fps"] = (int)asked["fps"]!,
                ["frame_count"] = (int)asked["video_frames"]!,
                ["generation_info"] = info,
            },
            ["error"] = null,
        }.ToJsonString();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public class VideoApiTests
{
    private const string Word = "zqxsentenceword";

    internal static SdApi Api(FakeVideoSd server, int cancelWaitMs = 150) => new(new HttpClient(server), 5555)
    {
        PollInterval = TimeSpan.Zero,
        VideoPollInterval = TimeSpan.Zero,
        VideoCancelWait = TimeSpan.FromMilliseconds(cancelWaitMs),
    };

    [Fact]
    public void The_request_body_has_every_setting_and_nothing_a_picture_has()
    {
        var frame = KeptPictureTests.Webp(960, 640);
        var request = VideoRequest.For("A fox turns its head.", VideoQuality.Quick, PictureShape.Wide, startFrame: frame) with { Preview = true, PreviewEvery = 3 };
        var body = SdApi.VideoBody(request, 4_000_000_123);

        Assert.Equal("A fox turns its head.", (string?)body["prompt"]);
        Assert.Equal(VideoRequest.FixedNegative, (string?)body["negative_prompt"]);
        Assert.Equal((960, 640), ((int)body["width"]!, (int)body["height"]!));
        Assert.Equal(4_000_000_123, (long)body["seed"]!);
        Assert.Equal((121, 24), ((int)body["video_frames"]!, (int)body["fps"]!));
        var sample = body["sample_params"]!.AsObject();
        Assert.Equal("euler", (string?)sample["sample_method"]);
        Assert.Equal(20, (int)sample["sample_steps"]!);
        Assert.Equal(5.0, (double)sample["guidance"]!["txt_cfg"]!);
        Assert.Equal(5.0, (double)sample["flow_shift"]!);
        Assert.Equal("webm", (string?)body["output_format"]);
        Assert.Equal(70, (int)body["output_compression"]!);
        Assert.Equal("tae", (string?)body["preview"]);
        Assert.Equal(3, (int)body["preview_interval"]!);
        // The first frame is the very bytes, as base64: no path, no address, no "data:" in front.
        Assert.Equal(Convert.ToBase64String(frame), (string?)body["init_image"]);

        // The video route has none of these, and DOG.ai sends no file name or folder of any kind.
        foreach (var key in new[] { "batch_count", "embed_image_metadata", "lora", "hires", "clip_skip", "end_image", "output_path", "output" })
            Assert.False(body.ContainsKey(key), key);
        Assert.DoesNotContain(@":\", body.ToJsonString());

        // From a sentence alone there is no first frame at all; without previews none are asked for.
        var plain = SdApi.VideoBody(VideoRequest.For("A lake.", VideoQuality.Better, PictureShape.Tall), 1);
        Assert.False(plain.ContainsKey("init_image"));
        Assert.Equal("none", (string?)plain["preview"]);
        Assert.Equal((832, 1216), ((int)plain["width"]!, (int)plain["height"]!));
    }

    [Fact]
    public async Task Makes_a_clip_reports_each_stage_and_step_once_and_never_takes_the_generation_info()
    {
        var server = new FakeVideoSd { Steps = 4, WithFrameStage = true };
        var progress = new Collect<VideoProgress>();
        var request = new VideoRequest($"A fox runs, {Word}", 832, 480, Frames: 33, Steps: 4, Preview: true);

        var made = await Api(server).MakeVideoAsync(request, 99, progress, CancellationToken.None);

        Assert.Equal(99, (long)server.Started.Single()["seed"]!);
        Assert.Equal((832, 480, 33), WebmInfo.Read(made.File) is { } info ? (info.Width, info.Height, info.Frames) : default);
        Assert.Equal((33, 24, "webm"), (made.Frames, made.Fps, made.Format));
        Assert.Equal(["preparing", "vae_encode", "conditioning", "sampling", "vae_decode", "encoding", "done"], progress.Items.Select(p => p.Stage).Distinct());
        var sampling = progress.Items.Where(p => p.Stage == VideoStages.Drawing).ToList();
        Assert.Equal([0, 1, 2, 3, 4], sampling.Select(p => p.Step));
        Assert.All(sampling, p => Assert.Equal(4, p.TotalSteps));
        // Decoding and encoding report no count.
        Assert.All(progress.Items.Where(p => p.Stage is VideoStages.Decoding or VideoStages.Encoding), p => Assert.Null(p.Step));
        // Previews at steps 2 and 4, each passed on once although the engine repeats them.
        Assert.Equal(2, progress.Items.Count(p => p.Preview is not null));

        // The engine's reply repeats the prompt in generation_info; nothing DOG.ai made of the reply holds it.
        var job = await Api(new FakeVideoSd { Steps = 0 }.Also(s => s.Started.Add(SdApi.VideoBody(request, 1)))).VideoJobAsync("job-v");
        Assert.DoesNotContain(Word, System.Text.Json.JsonSerializer.Serialize(new { made.Frames, made.Fps, made.Format, job.Status, job.Error, job.PreviewStep }));
        Assert.Equal(["File", "Frames", "Fps", "Format"], typeof(SdVideo).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));
        Assert.Equal(["Status", "Progress", "PreviewStep", "Preview", "Video", "Error", "IsFinished"], typeof(SdVideoJob).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));
    }

    [Fact]
    public void A_finished_job_is_read_field_by_field()
    {
        var file = TestWebm.Make(640, 960, 5);
        var job = SdApi.ParseVideoJob(JsonNode.Parse($$"""
            {"id":"j","status":"completed","result":{"b64_json":"{{Convert.ToBase64String(file)}}","mime_type":"video/webm","output_format":"webm","fps":24,"frame_count":5,
              "generation_info":{"prompt":"{{Word}} a fox","negative_prompt":"blurry {{Word}}","model":"D:\\models\\video\\wan.safetensors"} },"error":null}
            """)!.AsObject());
        Assert.True(job.IsFinished);
        Assert.Equal(file, job.Video!.File);
        Assert.Equal((5, 24, "webm"), (job.Video.Frames, job.Video.Fps, job.Video.Format));
        Assert.Null(job.Error);

        var cancelled = SdApi.ParseVideoJob(JsonNode.Parse("""{"status":"cancelled","error":{"code":"cancelled"}}""")!.AsObject());
        Assert.True(cancelled.IsFinished);
        Assert.Null(cancelled.Video);
        Assert.Equal("cancelled", cancelled.Error);
        // A result that is not base64 is no clip.
        Assert.Null(SdApi.ParseVideoJob(JsonNode.Parse("""{"status":"completed","result":{"b64_json":"***"}}""")!.AsObject()).Video);
    }

    [Fact]
    public async Task A_failed_or_refused_clip_is_said_plainly()
    {
        var request = new VideoRequest("a fox");
        var e = await Assert.ThrowsAsync<VideoEngineException>(() => Api(new FakeVideoSd { FailWith = "out of memory" }).MakeVideoAsync(request, 1, null, default));
        Assert.Equal("The video could not be made. The engine said: out of memory", e.Message);

        var server = new FakeVideoSd { Refuse = (HttpStatusCode.BadRequest, """{"error":{"code":"invalid_argument","message":"video_frames must be positive"}}""") };
        e = await Assert.ThrowsAsync<VideoEngineException>(() => Api(server).MakeVideoAsync(request, 1, null, default));
        Assert.Equal("The video engine refused the request (HTTP 400): video_frames must be positive", e.Message);

        server.Refuse = (HttpStatusCode.ServiceUnavailable, """{"code":"model_busy","error":"busy"}""");
        e = await Assert.ThrowsAsync<VideoEngineException>(() => Api(server).MakeVideoAsync(request, 1, null, default));
        Assert.Equal("The video engine is busy with another video.", e.Message);

        e = await Assert.ThrowsAsync<VideoEngineException>(() => Api(new FakeVideoSd { Result = _ => [] }).MakeVideoAsync(request, 1, null, default));
        Assert.Equal("The video engine finished but sent no video.", e.Message);
        // A picture's refusals are worded as they always were.
        var picture = new FakeSdServer { Refuse = (HttpStatusCode.ServiceUnavailable, """{"code":"model_busy","error":"busy"}""") };
        var p = await Assert.ThrowsAsync<ImageEngineException>(() => new SdApi(new HttpClient(picture), 1).DrawAsync(new ImageRequest("x"), 1, null, default));
        Assert.Equal("The picture engine is busy with another picture.", p.Message);
    }

    [Fact]
    public async Task Cancelling_asks_the_engine_to_stop_and_does_not_wait_for_a_decoder_that_cannot_be_interrupted()
    {
        // While it is drawn the engine stops at once.
        var server = new FakeVideoSd { Endless = true };
        using (var cts = new CancellationTokenSource())
        {
            var progress = new Collect<VideoProgress> { Then = p => { if (p.Step >= 5) cts.Cancel(); } };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(server).MakeVideoAsync(new VideoRequest("a fox"), 1, progress, cts.Token));
            Assert.True(server.CancelRequested);
        }

        // The model's own decoder goes on for minutes: the wait is given up after a few seconds (150 ms here), and the
        // caller ends the program.
        var stubborn = new FakeVideoSd { Endless = true, Uncancellable = true };
        using (var cts = new CancellationTokenSource())
        {
            var progress = new Collect<VideoProgress> { Then = p => { if (p.Step >= 5) cts.Cancel(); } };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(stubborn).MakeVideoAsync(new VideoRequest("a fox"), 1, progress, cts.Token));
            Assert.True(stubborn.CancelRequested);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the cancel waited {clock.Elapsed.TotalSeconds:0.0} s");
        }
        // In the app the wait is three seconds: far under the 38 s to 3 minutes the decoder would take.
        Assert.Equal(TimeSpan.FromSeconds(3), new SdApi(new HttpClient(server), 1).VideoCancelWait);
    }
}

internal static class FakeVideoSdExtensions
{
    public static FakeVideoSd Also(this FakeVideoSd server, Action<FakeVideoSd> then)
    {
        then(server);
        return server;
    }
}

public class VideoServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-videoserver-" + Guid.NewGuid().ToString("N"));
    private readonly VideoModel _model;

    public VideoServerTests()
    {
        _model = VideoFolders.ModelFor(_root, VideoFiles.Wan22);
        foreach (var (path, _) in _model.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private VideoServer Server(out LoopbackHttp http)
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));
        http = new LoopbackHttp();
        return new VideoServer(http, new SdServerLocator(paths), paths);
    }

    /// <summary>
    /// Both programs' start switches, whole. A picture's are byte for byte what they were before M20; a video's are
    /// the trial's: the model's four files, the metadata switch, 3 GB kept free, and none of the switches that put
    /// the model in RAM.
    /// </summary>
    [Fact]
    public void Both_programs_start_switches_are_pinned()
    {
        var picture = ImageServer.Arguments(
            new ImageModel(@"D:\m\image\checkpoints\c.safetensors", @"D:\m\image\loras", @"D:\m\image\vae\v.safetensors", ImagePrediction.V, @"D:\m\image\upscalers"),
            41234, @"D:\e\dogai-blank.html");
        Assert.Equal(
        [
            "--listen-ip", "127.0.0.1", "--listen-port", "41234",
            "-m", @"D:\m\image\checkpoints\c.safetensors",
            "--serve-html-path", @"D:\e\dogai-blank.html",
            "--completed-ttl-seconds", "60",
            "--disable-image-metadata",
            "--lora-model-dir", @"D:\m\image\loras",
            "--vae", @"D:\m\image\vae\v.safetensors",
            "--prediction", "v",
            "--hires-upscalers-dir", @"D:\m\image\upscalers",
        ], picture);
        Assert.Equal(
        [
            "--listen-ip", "127.0.0.1", "--listen-port", "1",
            "-m", @"D:\m\c.safetensors",
            "--serve-html-path", "b.html",
            "--completed-ttl-seconds", "60",
            "--disable-image-metadata",
        ], ImageServer.Arguments(new ImageModel(@"D:\m\c.safetensors"), 1, "b.html"));

        var model = VideoFolders.ModelFor(@"D:\m\video", VideoFiles.Wan22);
        string[] common =
        [
            "--listen-ip", "127.0.0.1", "--listen-port", "41235",
            "--diffusion-model", @"D:\m\video\diffusion_models\wan2.2_ti2v_5B_fp16.safetensors",
            "--vae", @"D:\m\video\vae\wan2.2_vae.safetensors",
            "--t5xxl", @"D:\m\video\text_encoders\umt5-xxl-encoder-Q8_0.gguf",
            "--taesd", @"D:\m\video\vae\taew2_2.safetensors",
        ];
        string[] rest =
        [
            "--serve-html-path", @"D:\e\dogai-blank.html",
            "--completed-ttl-seconds", "60",
            "--disable-image-metadata",
            "--max-vram", "-3",
        ];
        // The fast decoder makes the clip; with the model's own, the fast one only makes the previews.
        Assert.Equal([.. common, .. rest], VideoServer.Arguments(model, VideoDecoder.Fast, 41235, @"D:\e\dogai-blank.html"));
        Assert.Equal([.. common, "--taesd-preview-only", .. rest], VideoServer.Arguments(model, VideoDecoder.Own, 41235, @"D:\e\dogai-blank.html"));

        foreach (var decoder in Enum.GetValues<VideoDecoder>())
        {
            var args = VideoServer.Arguments(model, decoder, 1, "b.html");
            // Without this switch the prompt is written inside every clip.
            Assert.Contains("--disable-image-metadata", args);
            Assert.DoesNotContain(args, a => a is "--offload-to-cpu" or "--mmap" or "-m" or "--model" or "--lora-model-dir" or "--hires-upscalers-dir"
                or "-v" or "--verbose" or "--log-level" or "--color" or "-o" or "--output");
            Assert.DoesNotContain(args, a => a.Contains("output", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task The_log_filter_is_told_the_sentence_before_the_request_leaves_and_only_the_sentence()
    {
        using var server = Server(out var http);
        using var _ = http;
        var logged = new List<string>();
        var printed = new List<string>();
        server.Logged += logged.Add;
        server.Output += printed.Add;

        // Before any sentence was sent every line passes: the program cannot print what it was never given.
        server.Remember("[INFO ] loading diffusion model zqxfoxword");
        Assert.Single(logged);

        var fake = new FakeVideoSd
        {
            // The request has arrived at the engine; whatever it prints from here on is already guarded.
            OnStart = _ =>
            {
                server.Remember("[INFO ] generating video for zqxfoxword at dusk");
                server.Remember("[DEBUG] prompt after parsing: a zqxfoxword runs");
                server.Remember("[INFO ] sampling completed, taking 99.50s");
                // The fixed negative prompt is in DOG.ai's own source: its words hold nothing back.
                server.Remember("[INFO ] wrote no watermark and no subtitles");
            },
        };
        var result = await server.MakeWithAsync(VideoApiTests.Api(fake), new VideoRequest("A zqxfoxword runs through the snow", 832, 480, Seed: 5, Frames: 33, Steps: 2), null, default);

        Assert.Equal((832, 480, 33, 24, 5), (result.Width, result.Height, result.Frames, result.Fps, result.Seed));
        Assert.Equal(5, printed.Count);
        Assert.Equal(
        [
            "[INFO ] loading diffusion model zqxfoxword",
            "[INFO ] sampling completed, taking 99.50s",
            "[INFO ] wrote no watermark and no subtitles",
        ], logged);
        // A seed below zero is picked here and reported.
        var picked = await server.MakeWithAsync(VideoApiTests.Api(new FakeVideoSd()), new VideoRequest("a fox", 832, 480, Frames: 33, Steps: 2), null, default);
        Assert.InRange(picked.Seed, 0, uint.MaxValue);
    }

    [Fact]
    public async Task A_clip_with_tags_in_it_or_of_another_size_is_never_handed_on()
    {
        using var server = Server(out var http);
        using var _ = http;
        var request = new VideoRequest("A zqxfoxword runs", 832, 480, Frames: 33, Steps: 2);

        // What an engine started without --disable-image-metadata sends: the prompt inside the file.
        var tagged = new FakeVideoSd { Result = asked => TestWebm.Make(832, 480, 33, inSegment: TestWebm.TagsWith((string)asked["prompt"]!)) };
        var e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeWithAsync(VideoApiTests.Api(tagged), request, null, default));
        Assert.Equal("The video engine made a file DOG.ai does not show or keep. The video is not a plain WebM file: it carries tags", e.Message);
        Assert.DoesNotContain("zqxfoxword", e.Message);

        var rounded = new FakeVideoSd { Result = _ => TestWebm.Make(832, 512, 33) };
        e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeWithAsync(VideoApiTests.Api(rounded), request, null, default));
        Assert.Contains("832 × 512, not the 832 × 480 asked for", e.Message);

        var other = new FakeVideoSd { Result = _ => KeptPictureTests.Webp(832, 480) };
        e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeWithAsync(VideoApiTests.Api(other), request, null, default));
        Assert.Contains("not a plain WebM file", e.Message);
    }

    [Fact]
    public async Task Nothing_is_started_for_a_bad_request_a_refused_format_or_a_missing_file()
    {
        using var server = Server(out var http);
        using var _ = http;
        var changes = 0;
        server.Changed += () => changes++;
        var request = new VideoRequest("a fox");

        var e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeAsync(_model, request with { Frames = 120 }));
        Assert.Contains("multiple of four", e.Message);
        e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeAsync(_model, request with { StartFrame = KeptPictureTests.Webp(640, 960) }));
        Assert.Contains("not the shape of the video", e.Message);

        // Older formats can carry program code and are never opened.
        var ckpt = Path.Combine(_root, "wan.ckpt");
        File.WriteAllText(ckpt, "x");
        e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeAsync(_model with { DiffusionPath = ckpt }, request));
        Assert.Equal("The video model must be a .safetensors or .gguf file. Other formats can carry program code, so DOG.ai does not open them.", e.Message);
        e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeAsync(_model with { VaePath = Path.ChangeExtension(_model.VaePath, ".pt") }, request));
        Assert.Contains("decoder must be a .safetensors or .gguf file", e.Message);

        var gone = _model with { FastDecoderPath = Path.Combine(_root, "vae", "missing.safetensors") };
        e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeAsync(gone, request));
        Assert.Equal("The fast decoder file was not found: " + gone.FastDecoderPath, e.Message);

        Assert.Equal(0, changes);
        Assert.False(server.IsRunning);
        Assert.False(server.IsBusy);
    }

    [Fact]
    public async Task Without_the_picture_engine_installed_it_says_where_to_get_it()
    {
        if (Environment.GetEnvironmentVariable(SdServerLocator.OverrideVariable) is { Length: > 0 }) return;
        using var server = Server(out var http);
        using var _ = http;
        var progress = new Collect<VideoProgress>();

        var e = await Assert.ThrowsAsync<VideoEngineException>(() => server.MakeAsync(_model, new VideoRequest("a fox"), progress));

        Assert.Equal("Videos are made by the picture engine. " + SdServerLocator.MissingMessage, e.Message);
        Assert.Equal(e.Message, server.Error);
        Assert.False(server.IsRunning);
        Assert.False(server.IsBusy);
        Assert.Equal(VideoStages.Starting, Assert.Single(progress.Items).Stage);
    }
}

/// <summary>A stand-in for the video engine: like the real one, its program runs only while a clip is made.</summary>
internal sealed class FakeVideoEngine : IVideoEngine
{
    public bool IsBusy { get; private set; }
    public bool IsRunning { get; private set; }
    public event Action? Changed;
    public List<string> Events { get; init; } = [];
    public List<(VideoModel Model, VideoRequest Request)> Made { get; } = [];
    public string? FailWith { get; set; }

    /// <summary>When set, every clip stops after its first step until this completes.</summary>
    public TaskCompletionSource? Hold { get; set; }
    public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<VideoResult> MakeAsync(VideoModel model, VideoRequest request, IProgress<VideoProgress>? progress = null, CancellationToken ct = default)
    {
        if (request.Problems() is { Count: > 0 } problems) throw new VideoEngineException(string.Join(" ", problems));
        Made.Add((model, request));
        lock (Events) Events.Add("make");
        IsBusy = true;
        IsRunning = true;
        Changed?.Invoke();
        try
        {
            progress?.Report(new VideoProgress(VideoStages.Starting));
            if (FailWith is { } why) throw new VideoEngineException(why);
            for (var step = 1; step <= 3; step++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new VideoProgress(VideoStages.Drawing, step, 3));
                if (step == 1 && Hold is { } hold)
                {
                    Held.TrySetResult();
                    await hold.Task.WaitAsync(ct);
                    Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                await Task.Yield();
            }
            lock (Events) Events.Add("made");
            var seed = request.Seed >= 0 ? request.Seed : 4321;
            return new VideoResult(TestWebm.Make(request.Width, request.Height, request.Frames, (int)seed, filler: 40), request.Width, request.Height, request.Frames, request.Fps, seed);
        }
        finally
        {
            // The program is gone with the clip, whatever became of it.
            IsBusy = false;
            IsRunning = false;
            Changed?.Invoke();
        }
    }

    public Task StopAsync()
    {
        IsRunning = false;
        lock (Events) Events.Add("stop-video");
        return Task.CompletedTask;
    }
}

public class VideoCardTests : IDisposable
{
    private const long Gb = FakeCard.Gb;
    private static readonly ImageModel Checkpoint = new(@"D:\m\c.safetensors", @"D:\m\loras");
    private static readonly ImageRequest Picture = new("1girl", Width: 832, Height: 1216, Steps: 3);
    private static readonly VideoRequest Quick = VideoRequest.For("A fox runs through the snow.", VideoQuality.Quick, PictureShape.Wide, seed: 9);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-videocard-" + Guid.NewGuid().ToString("N"));
    private readonly VideoModel _model;

    public VideoCardTests()
    {
        _model = VideoFolders.ModelFor(_root, VideoFiles.Wan22);
        foreach (var (path, _) in _model.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Rig
    {
        public List<string> Events { get; } = [];
        public FakeChatModel Chat { get; }
        public FakeImageEngine Images { get; }
        public FakeVideoEngine Video { get; }
        public FakeGuest Search { get; }
        public FakeCard Card { get; }
        public CardCoordinator Coordinator { get; }
        public IVideoEngine Engine => Coordinator;
        public List<string> Notes { get; } = [];

        public Rig(bool chatLoaded = true, long chatBytes = 9 * Gb, bool searchLoaded = false)
        {
            Chat = new FakeChatModel { Events = Events };
            if (chatLoaded) Chat.Loaded();
            Images = new FakeImageEngine { Events = Events };
            Video = new FakeVideoEngine { Events = Events };
            Search = new FakeGuest("the document search model", Gb) { Events = Events, HoldsMemory = searchLoaded };
            Card = new FakeCard(Chat, Images) { ChatBytes = chatBytes };
            Coordinator = new CardCoordinator(Images, Chat, Card, _ => (long)(6.5 * Gb), [Search], Video) { PollInterval = TimeSpan.FromMilliseconds(5) };
            Coordinator.Noted += Notes.Add;
        }

        public string Order() { lock (Events) return string.Join(" ", Events); }
    }

    [Fact]
    public async Task A_clip_clears_the_whole_card_under_one_turn_and_the_chat_model_comes_back_when_asked()
    {
        var rig = new Rig(searchLoaded: true);
        await rig.Coordinator.LoadAsync(Checkpoint);
        // A picture first: the 9 GB chat model and the picture model fit side by side, so both are on the card.
        await rig.Coordinator.DrawAsync(Picture);
        Assert.True(rig.Chat.IsLoaded && rig.Images.IsRunning && rig.Search.HoldsMemory);
        Assert.Equal(["the chat model", "the picture model", "the document search model"], rig.Coordinator.InMemory);
        rig.Events.Clear();
        var progress = new Collect<VideoProgress>();

        var clip = await rig.Engine.MakeAsync(_model, Quick, progress);

        // Everything left the card before the clip started, in one go, and nothing came back by itself.
        Assert.Equal("rest release let-go make made", rig.Order());
        Assert.Equal((960, 640, 121, 9L), (clip.Width, clip.Height, clip.Frames, clip.Seed));
        Assert.Null(VideoClips.Problem(clip.Webm, Quick));
        Assert.False(rig.Chat.IsLoaded);
        Assert.True(rig.Chat.IsResting);
        Assert.False(rig.Images.IsRunning);
        Assert.False(rig.Search.HoldsMemory);
        Assert.False(rig.Video.IsRunning);
        Assert.Empty(rig.Coordinator.InMemory);
        Assert.False(rig.Coordinator.IsBusy);
        Assert.Equal(new CardDecision(CardChoice.SetChatAside, 15 * Gb, 23 * Gb), rig.Coordinator.LastDecision);
        Assert.Equal([VideoStages.MakingRoom, VideoStages.Starting, VideoStages.Drawing], progress.Items.Select(p => p.Stage).Distinct());
        Assert.Equal("a video needs the whole card (about 15.0 GB); the chat model (9.0 GB) was set aside, the picture model was let go and the document search model was let go. 23.0 GB is free.", rig.Notes[^1]);
        Assert.DoesNotContain(rig.Notes, n => n.Contains("fox", StringComparison.OrdinalIgnoreCase));
        // The picture's checkpoint stays chosen.
        Assert.Equal(Checkpoint, rig.Coordinator.Model);

        // As after a picture: the chat model loads again when it is next asked for, and answers.
        Assert.True(await rig.Chat.AskAsync());
        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal(1, rig.Chat.Loads);
        // And a picture straight after a clip is drawn as ever.
        await rig.Coordinator.DrawAsync(Picture);
        Assert.Equal("rest release let-go make made load reply draw drawn", rig.Order());
    }

    [Fact]
    public async Task With_nothing_on_the_card_a_clip_is_simply_made_and_better_needs_sixteen_gigabytes()
    {
        var rig = new Rig(chatLoaded: false);
        var progress = new Collect<VideoProgress>();
        var better = VideoRequest.For("A lake at dawn.", VideoQuality.Better, PictureShape.Tall);

        await rig.Engine.MakeAsync(_model, better, progress);

        Assert.Equal("make made", rig.Order());
        Assert.Equal(new CardDecision(CardChoice.NothingLoaded, 16 * Gb, 23 * Gb), rig.Coordinator.LastDecision);
        Assert.DoesNotContain(progress.Items, p => p.Stage == VideoStages.MakingRoom);
        Assert.Equal((_model, better), rig.Video.Made.Single());
        Assert.Equal("a video needs the whole card (about 16.0 GB); nothing else was on the card. 23.0 GB is free.", rig.Notes[^1]);
        // A model the owner never loaded is not loaded here.
        Assert.False(rig.Chat.IsLoaded || rig.Chat.IsResting);
    }

    [Fact]
    public async Task A_reply_being_written_is_never_cut_off_the_clip_waits_for_it()
    {
        var rig = new Rig();
        rig.Chat.IsBusy = true;
        var progress = new Collect<VideoProgress>();

        var making = rig.Engine.MakeAsync(_model, Quick, progress);
        await Task.Delay(60);

        Assert.False(making.IsCompleted);
        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal("", rig.Order());
        Assert.Contains(progress.Items, p => p.Stage == VideoStages.Waiting);
        Assert.True(rig.Coordinator.IsBusy);
        Assert.Equal("A reply is being written. Unload when it has finished.", rig.Coordinator.BusyReason);

        rig.Chat.IsBusy = false;
        await making;
        Assert.Equal("rest make made", rig.Order());
    }

    [Fact]
    public async Task While_a_clip_is_made_everything_else_waits_and_unload_everything_says_why()
    {
        var rig = new Rig();
        await rig.Coordinator.LoadAsync(Checkpoint);
        rig.Video.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var making = rig.Engine.MakeAsync(_model, Quick);
        await rig.Video.Held.Task;

        Assert.True(rig.Coordinator.IsBusy);
        Assert.True(rig.Coordinator.IsMakingVideo);
        Assert.True(rig.Engine.IsBusy);
        Assert.True(rig.Engine.IsRunning);
        Assert.Equal(["the video model"], rig.Coordinator.InMemory);
        Assert.Equal("A video is being made. Unload when it has finished.", rig.Coordinator.BusyReason);
        var cleared = await rig.Coordinator.ClearAsync(() => Task.FromResult(false));
        Assert.Equal("A video is being made. Unload when it has finished.", cleared.Refused);

        // A message sent meanwhile, a picture and a model the owner loads all wait for the clip: none is answered or
        // drawn beside it, and none fails.
        var asked = rig.Chat.AskAsync();
        var drawn = rig.Coordinator.DrawAsync(Picture);
        await Task.Delay(60);
        Assert.False(asked.IsCompleted);
        Assert.False(drawn.IsCompleted);
        Assert.Equal("rest make", rig.Order());

        rig.Video.Hold.SetResult();
        await making;
        Assert.True(await asked);
        await drawn;
        Assert.StartsWith("rest make made ", rig.Order());
        Assert.False(rig.Video.IsRunning);
        Assert.False(rig.Coordinator.IsMakingVideo);
        Assert.Null(rig.Coordinator.BusyReason);
    }

    [Fact]
    public async Task When_the_cleared_card_still_has_too_little_it_is_refused_in_words_and_nothing_is_made()
    {
        // Another program holds 10 GB of the card: with everything of DOG.ai's gone, 14 GB is free and a clip needs 15.
        var rig = new Rig();
        rig.Card.Other = 10 * Gb;

        var e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick));

        Assert.Equal("A video needs about 15.0 GB of graphics memory and only 14.0 GB is free after DOG.ai made room. Close other programs that use the graphics card, then try again.", e.Message);
        Assert.Equal("rest", rig.Order());
        Assert.Empty(rig.Video.Made);
        Assert.False(rig.Coordinator.IsBusy);
        Assert.Contains("so the video was not made", rig.Notes[^1]);
        // The chat model was set aside for it; it comes back with the next message, as after a picture.
        Assert.True(await rig.Chat.AskAsync());

        // The document search model is indexing: it is never let go while it is used, and the refusal names it.
        var busy = new Rig(searchLoaded: true);
        busy.Search.IsBusy = true;
        busy.Card.Other = 10 * Gb;
        e = await Assert.ThrowsAsync<VideoEngineException>(() => busy.Engine.MakeAsync(_model, Quick));
        Assert.Equal("A video needs about 15.0 GB of graphics memory and only 14.0 GB is free after DOG.ai made room. The document search model is being used and stays in graphics memory: try again when it has finished.", e.Message);
        Assert.True(busy.Search.HoldsMemory);

        // With room enough the clip is made beside a guest that is being used.
        var beside = new Rig(searchLoaded: true);
        beside.Search.IsBusy = true;
        await beside.Engine.MakeAsync(_model, Quick);
        Assert.Equal("rest make made", beside.Order());
        Assert.True(beside.Search.HoldsMemory);
    }

    [Fact]
    public async Task What_can_never_be_made_is_refused_before_anything_is_moved()
    {
        var rig = new Rig(searchLoaded: true);
        await rig.Coordinator.LoadAsync(Checkpoint);

        // A card too small for any clip.
        rig.Card.Total = 12 * Gb;
        var e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick));
        Assert.Equal("A video needs about 15.0 GB of graphics memory, and this PC’s graphics card has 12.0 GB.", e.Message);
        rig.Card.Total = 24 * Gb;

        // A request that cannot be made, a first frame of another shape, a missing or refused file.
        e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick with { Prompt = " " }));
        Assert.Contains("Say in a sentence", e.Message);
        e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick with { StartFrame = KeptPictureTests.Webp(640, 960) }));
        Assert.Contains("not the shape of the video", e.Message);
        e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model with { TextEncoderPath = Path.Combine(_root, "gone.gguf") }, Quick));
        Assert.StartsWith("The text encoder file was not found", e.Message);
        e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model with { DiffusionPath = Path.Combine(_root, "wan.ckpt") }, Quick));
        Assert.Contains("must be a .safetensors or .gguf file", e.Message);

        Assert.Equal("", rig.Order());
        Assert.True(rig.Chat.IsLoaded);
        Assert.True(rig.Search.HoldsMemory);
        Assert.False(rig.Coordinator.IsBusy);
        // A DOG.ai without a video engine says so.
        var none = new CardCoordinator(rig.Images, new FakeChatModel(), rig.Card);
        e = await Assert.ThrowsAsync<VideoEngineException>(() => ((IVideoEngine)none).MakeAsync(_model, Quick));
        Assert.Equal("This DOG.ai has no video engine.", e.Message);
    }

    [Fact]
    public async Task A_cancelled_or_failed_clip_gives_the_card_back()
    {
        var rig = new Rig();
        await rig.Coordinator.LoadAsync(Checkpoint);
        rig.Video.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var making = rig.Engine.MakeAsync(_model, Quick, ct: cts.Token);
        await rig.Video.Held.Task;

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => making);

        Assert.False(rig.Video.IsRunning);
        Assert.False(rig.Coordinator.IsBusy);
        Assert.True(rig.Chat.IsResting);
        // The turn was given back: a picture is drawn, and the chat model answers.
        rig.Video.Hold = null;
        await rig.Coordinator.DrawAsync(Picture);
        Assert.True(await rig.Chat.AskAsync());

        rig.Video.FailWith = "The video engine stopped while making the video. Try again; it will be started again.";
        var e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick));
        Assert.Equal(rig.Video.FailWith, e.Message);
        Assert.False(rig.Coordinator.IsBusy);
        rig.Video.FailWith = null;
        await rig.Engine.MakeAsync(_model, Quick);
    }

    [Fact]
    public async Task A_card_that_cannot_be_measured_is_cleared_and_the_clip_is_made()
    {
        var rig = new Rig();
        rig.Card.Unmeasurable = true;

        await rig.Engine.MakeAsync(_model, Quick);

        Assert.Equal("rest make made", rig.Order());
        Assert.Equal(new CardDecision(CardChoice.SetChatAside, 15 * Gb, -1), rig.Coordinator.LastDecision);
        Assert.EndsWith("The card could not be measured.", rig.Notes[^1]);
    }

    [Fact]
    public async Task A_model_loading_only_because_a_page_opened_is_set_aside_not_waited_for()
    {
        var rig = new Rig(chatLoaded: false);
        var finish = new TaskCompletionSource();
        var pageLoad = rig.Chat.SlowLoadAsync(finish.Task, automatic: true);
        for (var i = 0; i < 200 && !rig.Chat.Loading; i++) await Task.Delay(5);

        await rig.Engine.MakeAsync(_model, Quick);
        await pageLoad;

        Assert.Equal("load-set-aside make made", rig.Order());
        Assert.True(rig.Chat.IsResting);
        Assert.Equal(CardChoice.SetChatAside, rig.Coordinator.LastDecision!.Choice);
    }
}
