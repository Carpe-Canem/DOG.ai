using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DogAI.Core.Video;

/// <summary>What a WebM file's own structure says about it.</summary>
/// <param name="Frames">How many frames the file holds (its blocks, counted; nothing is decoded).</param>
/// <param name="Seconds">How long the file says it plays, or 0 when it does not say.</param>
/// <param name="Writer">The name of the program that wrote it, as the file gives it ("stable-diffusion.cpp"), or "".</param>
public readonly record struct WebmInfo(int Width, int Height, int Frames, double Seconds, string Writer)
{
    /// <summary>Reads a WebM's structure, or returns null when the bytes are not a clip DOG.ai shows or keeps.</summary>
    public static WebmInfo? Read(ReadOnlySpan<byte> data) => Read(data, out _);

    /// <summary>
    /// Reads a WebM's structure in the way <c>WebpInfo</c> reads a picture's: every part of the file is walked and
    /// named, nothing is decoded. Only what a clip from the video engine has is let through: the file header saying
    /// "webm", then one segment holding its index, its facts (numbers and the writing program's name), <b>one VP8
    /// video track</b> and the frames. Anything else gives null and <paramref name="problem"/> says what, in a few
    /// words: <b>tags</b> (where the engine writes the prompt when it is started without its metadata switch), an
    /// <b>attached file</b>, chapters, a title, a date, a file name, a track's name, sound, a second track, another
    /// codec, bytes after the end, and any part this reader does not know.
    /// </summary>
    public static WebmInfo? Read(ReadOnlySpan<byte> data, out string? problem)
    {
        var found = new Found();
        problem = File(data, found) ?? found.Verdict();
        if (problem is not null) return null;
        return new WebmInfo(found.Width, found.Height, found.Frames,
            found.Duration > 0 ? found.Duration * found.Scale / 1_000_000_000.0 : 0, found.Writer ?? found.Muxer ?? "");
    }

    // ---- The parts of a file (EBML element ids, as Matroska names them) ----

    private const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, SeekHead = 0x114D9B74, Info = 0x1549A966, Tracks = 0x1654AE6B,
        Cues = 0x1C53BB6B, Cluster = 0x1F43B675, TrackEntry = 0xAE, Video = 0xE0, Seek = 0x4DBB, CuePoint = 0xBB,
        CueTrackPositions = 0xB7, BlockGroup = 0xA0, Padding = 0xEC, Checksum = 0xBF;

    private enum Kind
    {
        Master, Number, Float, DocType, Codec, App, Muxer, Language, TrackType, TrackNumber, Width, Height, Scale, Duration, Block,
    }

    /// <summary>Every part that is let through, by the part it sits in. A part that is not here is refused.</summary>
    private static readonly Dictionary<(uint Parent, uint Id), Kind> Known = new()
    {
        // The file header.
        [(Ebml, 0x4286)] = Kind.Number, [(Ebml, 0x42F7)] = Kind.Number, [(Ebml, 0x42F2)] = Kind.Number, [(Ebml, 0x42F3)] = Kind.Number,
        [(Ebml, 0x4282)] = Kind.DocType, [(Ebml, 0x4287)] = Kind.Number, [(Ebml, 0x4285)] = Kind.Number,
        // The segment.
        [(Segment, SeekHead)] = Kind.Master, [(Segment, Info)] = Kind.Master, [(Segment, Tracks)] = Kind.Master,
        [(Segment, Cues)] = Kind.Master, [(Segment, Cluster)] = Kind.Master,
        // The index of where the parts are.
        [(SeekHead, Seek)] = Kind.Master, [(Seek, 0x53AB)] = Kind.Number, [(Seek, 0x53AC)] = Kind.Number,
        // The facts: the time unit, the length, and the names of the library and the program that wrote the file.
        [(Info, 0x2AD7B1)] = Kind.Scale, [(Info, 0x4489)] = Kind.Duration, [(Info, 0x4D80)] = Kind.Muxer, [(Info, 0x5741)] = Kind.App,
        // The one track. Numbers and switches only, besides the codec's name and a language code.
        [(Tracks, TrackEntry)] = Kind.Master,
        [(TrackEntry, 0xD7)] = Kind.TrackNumber, [(TrackEntry, 0x73C5)] = Kind.Number, [(TrackEntry, 0x83)] = Kind.TrackType,
        [(TrackEntry, 0xB9)] = Kind.Number, [(TrackEntry, 0x88)] = Kind.Number, [(TrackEntry, 0x55AA)] = Kind.Number,
        [(TrackEntry, 0x9C)] = Kind.Number, [(TrackEntry, 0x6DE7)] = Kind.Number, [(TrackEntry, 0x6DF8)] = Kind.Number,
        [(TrackEntry, 0x23E383)] = Kind.Number, [(TrackEntry, 0x23314F)] = Kind.Float, [(TrackEntry, 0x55EE)] = Kind.Number,
        [(TrackEntry, 0xAA)] = Kind.Number, [(TrackEntry, 0x56AA)] = Kind.Number, [(TrackEntry, 0x56BB)] = Kind.Number,
        [(TrackEntry, 0x86)] = Kind.Codec, [(TrackEntry, 0x22B59C)] = Kind.Language, [(TrackEntry, Video)] = Kind.Master,
        [(Video, 0xB0)] = Kind.Width, [(Video, 0xBA)] = Kind.Height, [(Video, 0x54B0)] = Kind.Number, [(Video, 0x54BA)] = Kind.Number,
        [(Video, 0x54B2)] = Kind.Number, [(Video, 0x54B3)] = Kind.Number, [(Video, 0x9A)] = Kind.Number, [(Video, 0x9D)] = Kind.Number,
        [(Video, 0x53B8)] = Kind.Number, [(Video, 0x53C0)] = Kind.Number, [(Video, 0x54AA)] = Kind.Number, [(Video, 0x54BB)] = Kind.Number,
        [(Video, 0x54CC)] = Kind.Number, [(Video, 0x54DD)] = Kind.Number, [(Video, 0x2383E3)] = Kind.Float,
        // Where to jump to when seeking.
        [(Cues, CuePoint)] = Kind.Master, [(CuePoint, 0xB3)] = Kind.Number, [(CuePoint, CueTrackPositions)] = Kind.Master,
        [(CueTrackPositions, 0xF7)] = Kind.Number, [(CueTrackPositions, 0xF1)] = Kind.Number, [(CueTrackPositions, 0xF0)] = Kind.Number,
        [(CueTrackPositions, 0xB2)] = Kind.Number, [(CueTrackPositions, 0x5378)] = Kind.Number,
        // The frames.
        [(Cluster, 0xE7)] = Kind.Number, [(Cluster, 0xA7)] = Kind.Number, [(Cluster, 0xAB)] = Kind.Number, [(Cluster, 0xA3)] = Kind.Block,
        [(Cluster, BlockGroup)] = Kind.Master,
        [(BlockGroup, 0xA1)] = Kind.Block, [(BlockGroup, 0x9B)] = Kind.Number, [(BlockGroup, 0xFB)] = Kind.Number, [(BlockGroup, 0x75A2)] = Kind.Number,
    };

    /// <summary>Parts that are refused by name, so the reason can be said. Everything else unknown is refused as unknown.</summary>
    private static readonly Dictionary<uint, string> Refused = new()
    {
        [0x1254C367] = "it carries tags",
        [0x1941A469] = "it carries an attached file",
        [0x1043A770] = "it carries chapters",
        [0x7BA9] = "it carries a title",
        [0x4461] = "it carries a date",
        [0x7384] = "it carries a file name",
        [0x536E] = "its track carries a name",
        [0x258688] = "its track carries a codec description",
        [0x63A2] = "its track carries private data",
        [0xE1] = "it has sound",
        [0x75A1] = "its frames carry additions",
    };

    private sealed class Found
    {
        public string? DocType, Codec, Writer, Muxer;
        public int Segments, TrackEntries, Width, Height, Frames;
        public long TrackType = -1, TrackNumber = -1, BlockTrack = -1, Scale = 1_000_000;
        public double Duration;
        public bool MixedBlocks;

        /// <summary>What the file as a whole lacks, after every part was let through.</summary>
        public string? Verdict() =>
            DocType != "webm" ? "it does not say it is a WebM"
            : Segments != 1 ? "it has no single segment"
            : TrackEntries == 0 ? "it has no video track"
            : TrackEntries > 1 ? "it has more than one track"
            : TrackType != 1 ? "its track is not video"
            : Codec != "V_VP8" ? "its video is not VP8"
            : Width <= 0 || Height <= 0 ? "it does not say its size"
            : Frames == 0 ? "it has no frames"
            : MixedBlocks || BlockTrack != TrackNumber ? "its frames belong to another track"
            : null;
    }

    private static string? File(ReadOnlySpan<byte> data, Found found)
    {
        if (!Head(data, 0, data.Length, out var id, out var size, out var body) || id != Ebml || size < 0) return "it does not start as a WebM";
        if (Children(data, body, body + (int)size, Ebml, false, found, out _) is { } header) return header;
        var at = body + (int)size;
        if (!Head(data, at, data.Length, out id, out size, out body) || id != Segment) return "it has no segment";
        found.Segments++;
        // A segment written to a stream may not know its size; it then runs to the end of the file.
        var end = size < 0 ? data.Length : body + (int)size;
        if (end != data.Length) return "it has bytes after its end";
        return Children(data, body, end, Segment, false, found, out _);
    }

    /// <summary>
    /// Walks the parts inside <paramref name="parent"/> from <paramref name="at"/> to <paramref name="end"/>. With
    /// <paramref name="open"/> the parent did not say its size (a cluster written to a stream): it ends where a part
    /// begins that belongs to the segment, and <paramref name="stopped"/> says where.
    /// </summary>
    private static string? Children(ReadOnlySpan<byte> data, int at, int end, uint parent, bool open, Found found, out int stopped)
    {
        stopped = end;
        while (at < end)
        {
            if (!Head(data, at, end, out var id, out var size, out var body)) return "it is cut short";
            if (open && (Known.ContainsKey((Segment, id)) || IsTopLevel(id)))
            {
                stopped = at;
                return null;
            }
            if (id is Padding or Checksum)
            {
                if (size < 0) return "it is cut short";
                // Padding is room left for the index. It must be empty: it is the one part that could hold anything.
                if (id == Padding && data.Slice(body, (int)size).IndexOfAnyExcept((byte)0) >= 0) return "its padding is not empty";
                if (id == Checksum && size != 4) return "it has a part DOG.ai does not know";
                at = body + (int)size;
                continue;
            }
            if (!Known.TryGetValue((parent, id), out var kind))
                return Refused.TryGetValue(id, out var why) ? why : "it has a part DOG.ai does not know";

            if (kind == Kind.Master)
            {
                if (id == TrackEntry) found.TrackEntries++;
                // Only the frames may leave their size open.
                if (size < 0 && id != Cluster) return "it is cut short";
                var inner = size < 0 ? end : body + (int)size;
                if (Children(data, body, inner, id, size < 0, found, out var next) is { } problem) return problem;
                at = size < 0 ? next : inner;
                continue;
            }

            if (size < 0) return "it is cut short";
            var value = data.Slice(body, (int)size);
            if (Value(kind, value, found) is { } wrong) return wrong;
            at = body + (int)size;
        }
        return null;
    }

    private static bool IsTopLevel(uint id) => id is 0x1254C367 or 0x1941A469 or 0x1043A770;

    private static string? Value(Kind kind, ReadOnlySpan<byte> value, Found found)
    {
        switch (kind)
        {
            case Kind.Number or Kind.TrackType or Kind.TrackNumber or Kind.Width or Kind.Height or Kind.Scale:
                if (value.Length > 8) return "it has a part DOG.ai does not know";
                long number = 0;
                foreach (var b in value) number = number << 8 | b;
                if (kind == Kind.TrackType) found.TrackType = number;
                else if (kind == Kind.TrackNumber) found.TrackNumber = number;
                else if (kind == Kind.Width) found.Width = (int)Math.Clamp(number, 0, int.MaxValue);
                else if (kind == Kind.Height) found.Height = (int)Math.Clamp(number, 0, int.MaxValue);
                else if (kind == Kind.Scale) found.Scale = number;
                return null;
            case Kind.Float or Kind.Duration:
                if (value.Length is not (0 or 4 or 8)) return "it has a part DOG.ai does not know";
                if (kind == Kind.Duration)
                    found.Duration = value.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(value) : value.Length == 8 ? BinaryPrimitives.ReadDoubleBigEndian(value) : 0;
                return null;
            case Kind.DocType:
                found.DocType = Name(value, 16);
                return found.DocType is null ? "it does not say it is a WebM" : null;
            case Kind.Codec:
                found.Codec = Name(value, 32);
                return found.Codec is null ? "its video is not VP8" : null;
            case Kind.Language:
                return Name(value, 8) is null ? "its track carries a description" : null;
            case Kind.App or Kind.Muxer:
                // The one place the file holds words. A program's name has no spaces and is short; a sentence is neither.
                if (Name(value, MaxNameLength) is not { } name) return "it carries words where the writing program’s name belongs";
                if (kind == Kind.App) found.Writer = name;
                else found.Muxer = name;
                return null;
            case Kind.Block:
                // The track's number (one byte for tracks 1 to 126), two bytes of time, one of switches, then the frame.
                if (value.Length < 4 || (value[0] & 0x80) == 0) return "it has frames DOG.ai cannot count";
                // Several frames packed into one block would make the count wrong; the engine writes one a block.
                if ((value[3] & 0x06) != 0) return "it has frames DOG.ai cannot count";
                var track = value[0] & 0x7F;
                if (found.BlockTrack < 0) found.BlockTrack = track;
                else if (found.BlockTrack != track) found.MixedBlocks = true;
                found.Frames++;
                return null;
            default:
                return "it has a part DOG.ai does not know";
        }
    }

    /// <summary>The longest name a writing program may give itself.</summary>
    public const int MaxNameLength = 40;

    /// <summary>A short name in plain letters, digits and marks, with no space in it; null for anything else. Zeros at its end are padding.</summary>
    private static string? Name(ReadOnlySpan<byte> value, int max)
    {
        value = value.TrimEnd((byte)0);
        if (value.Length > max) return null;
        foreach (var b in value)
            if (b is <= 0x20 or >= 0x7F) return null;
        return Encoding.ASCII.GetString(value);
    }

    /// <summary>
    /// Reads one part's id and size at <paramref name="at"/>. <paramref name="size"/> is -1 when the part does not say
    /// its size; otherwise the part's content is checked to lie before <paramref name="end"/>.
    /// </summary>
    private static bool Head(ReadOnlySpan<byte> data, int at, int end, out uint id, out long size, out int body)
    {
        id = 0;
        size = 0;
        body = at;
        if (at >= end) return false;
        var first = data[at];
        var idLength = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
        if (idLength == 0 || at + idLength >= end) return false;
        for (var i = 0; i < idLength; i++) id = id << 8 | data[at + i];
        at += idLength;

        var lead = data[at];
        if (lead == 0) return false;
        var sizeLength = BitOperations.LeadingZeroCount((uint)lead) - 23;
        if (at + sizeLength > end) return false;
        var mask = 0xFF >> sizeLength;
        long value = lead & mask;
        var unknown = value == mask;
        for (var i = 1; i < sizeLength; i++)
        {
            var b = data[at + i];
            value = value << 8 | b;
            unknown &= b == 0xFF;
        }
        body = at + sizeLength;
        if (unknown)
        {
            size = -1;
            return true;
        }
        if (value > end - body) return false;
        size = value;
        return true;
    }
}
