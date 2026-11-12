using System.Text;
using DogAI.Core.Voice;

namespace DogAI.Engine.Voice;

/// <summary>A downloaded voice: one Kokoro pack (many speakers) or one Piper voice.</summary>
/// <param name="Folder">The pack's folder; its name is what <see cref="VoiceId.Model"/> holds.</param>
/// <param name="ModelFile">The .onnx file.</param>
public sealed record VoicePack(string Folder, VoiceKind Kind, string ModelFile, IReadOnlyList<string> Speakers, long SizeBytes)
{
    public string Name => Path.GetFileName(Folder);
}

/// <summary>A downloaded speech recognition (Whisper) model file.</summary>
public sealed record SpeechModelFile(string Path, long SizeBytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Finds voice models in the voice folder ("voice" inside the models folder).</summary>
public static class VoiceLibrary
{
    /// <summary>Whisper model files ("ggml-*.bin") directly in the folder, the preferred one first.</summary>
    public static IReadOnlyList<SpeechModelFile> SpeechModels(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, "ggml-*.bin")
                .Select(f => new SpeechModelFile(f, new FileInfo(f).Length))
                .Where(f => f.SizeBytes > 1024 * 1024)
                .OrderBy(f => VoiceCatalog.SpeechPreference(f.Name))
                .ThenByDescending(f => f.SizeBytes)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Voice packs in subfolders: Kokoro (model, voices.bin, tokens.txt) or Piper (one .onnx with tokens.txt).</summary>
    public static IReadOnlyList<VoicePack> Packs(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return [];
        var packs = new List<VoicePack>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
                if (ReadPack(dir) is { } pack) packs.Add(pack);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return packs;
    }

    public static VoicePack? ReadPack(string dir)
    {
        try
        {
            if (!File.Exists(Path.Combine(dir, "tokens.txt"))) return null;
            var models = Directory.EnumerateFiles(dir, "*.onnx").ToList();
            if (models.Count == 0) return null;
            var size = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            if (File.Exists(Path.Combine(dir, "voices.bin")))
            {
                var model = models.FirstOrDefault(m => Path.GetFileName(m).Equals("model.onnx", StringComparison.OrdinalIgnoreCase)) ?? models[0];
                var names = OnnxMetadata.Read(model).GetValueOrDefault("speaker_names");
                var speakers = names?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
                return speakers.Count == 0 ? null : new VoicePack(dir, VoiceKind.Kokoro, model, speakers, size);
            }
            if (!Directory.Exists(Path.Combine(dir, "espeak-ng-data"))) return null;
            return new VoicePack(dir, VoiceKind.Piper, models[0], ["default"], size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The voices a pack offers, as picker entries.</summary>
    public static IEnumerable<VoiceOption> Options(VoicePack pack)
    {
        var title = VoiceCatalog.Title(pack.Name);
        if (pack.Kind == VoiceKind.Piper)
        {
            yield return new VoiceOption(new VoiceId(VoiceKind.Piper, pack.Name), title, "Piper voices", VoiceCatalog.PiperLanguage(pack.Name));
            yield break;
        }
        // English voices first, then the rest in the pack's own order.
        foreach (var speaker in pack.Speakers.OrderBy(s => s.StartsWith('a') ? 0 : s.StartsWith('b') ? 1 : 2))
            yield return new VoiceOption(new VoiceId(VoiceKind.Kokoro, pack.Name, speaker), KokoroVoices.Describe(speaker), title, KokoroVoices.LanguageLabel(speaker));
    }
}

/// <summary>
/// Reads the metadata block of an ONNX model file (names of Kokoro's voices, sample rate) without loading the model:
/// the file is a protocol buffer, so the large parts are skipped by their length.
/// </summary>
public static class OnnxMetadata
{
    private const int MetadataField = 14;

    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        while (stream.Position < stream.Length)
        {
            var tag = Varint(stream);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: Varint(stream); continue;
                case 1: stream.Seek(8, SeekOrigin.Current); continue;
                case 5: stream.Seek(4, SeekOrigin.Current); continue;
                case 2: break;
                default: throw new InvalidDataException("Not an ONNX model file.");
            }
            var length = (long)Varint(stream);
            if (length < 0 || stream.Position + length > stream.Length) throw new InvalidDataException("Not an ONNX model file.");
            if (field != MetadataField || length > 1 << 20)
            {
                stream.Seek(length, SeekOrigin.Current);
                continue;
            }
            var entry = new byte[length];
            stream.ReadExactly(entry);
            var (key, value) = ReadEntry(entry);
            if (key is not null) result[key] = value ?? "";
        }
        return result;
    }

    private static (string? Key, string? Value) ReadEntry(byte[] entry)
    {
        using var m = new MemoryStream(entry);
        string? key = null, value = null;
        while (m.Position < m.Length)
        {
            var tag = Varint(m);
            if ((tag & 7) != 2) return (null, null);
            var length = (int)Varint(m);
            if (length < 0 || m.Position + length > m.Length) return (null, null);
            var bytes = new byte[length];
            m.ReadExactly(bytes);
            if (tag >> 3 == 1) key = Encoding.UTF8.GetString(bytes);
            else if (tag >> 3 == 2) value = Encoding.UTF8.GetString(bytes);
        }
        return (key, value);
    }

    private static ulong Varint(Stream s)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = s.ReadByte();
            if (b < 0) throw new InvalidDataException("Not an ONNX model file.");
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Not an ONNX model file.");
    }
}
