namespace DogAI.Core.Voice;

/// <summary>What a recommended voice download is for.</summary>
public enum VoiceModelUse
{
    /// <summary>Speech recognition: talking to DOG.ai (Whisper).</summary>
    Listening,

    /// <summary>A voice for reading replies aloud (Kokoro, Piper).</summary>
    Speaking,
}

/// <summary>
/// A recommended voice download. <paramref name="File"/> set: one file from the repository goes straight into the
/// voice folder (Whisper). Null: every file of the repository goes into a subfolder named <paramref name="Folder"/>.
/// </summary>
public sealed record VoiceDownload(VoiceModelUse Use, string Repo, string Title, string Blurb, long ApproxBytes, string Licence,
    string? File = null, string? Folder = null, bool Recommended = false)
{
    /// <summary>Where it ends up, relative to the voice folder: the file name or the subfolder name.</summary>
    public string Target => File ?? Folder ?? Repo.Split('/')[^1];
}

/// <summary>
/// The voice models DOG.ai recommends (researched 2026-11-12; sizes and licences checked on Hugging Face). Piper
/// voices whose recordings are licensed for non-commercial use only, or whose licence is unclear, are left out.
/// </summary>
public static class VoiceCatalog
{
    public const string WhisperRepo = "ggerganov/whisper.cpp";

    public static IReadOnlyList<VoiceDownload> All { get; } =
    [
        new(VoiceModelUse.Listening, WhisperRepo, "Whisper large-v3 turbo",
            "Understands 99 languages with very few mistakes. Fast on a graphics card (a third of a second for a sentence here).",
            574_041_195, "MIT", File: "ggml-large-v3-turbo-q5_0.bin", Recommended: true),
        new(VoiceModelUse.Listening, WhisperRepo, "Whisper small",
            "A lighter model for many languages; good with clear speech and quick enough without a graphics card.",
            190_085_487, "MIT", File: "ggml-small-q5_1.bin"),
        new(VoiceModelUse.Listening, WhisperRepo, "Whisper base (English only)",
            "The smallest useful model. English only.",
            59_721_011, "MIT", File: "ggml-base.en-q5_1.bin"),

        new(VoiceModelUse.Speaking, "csukuangfj/kokoro-multi-lang-v1_0", "Kokoro v1.0",
            "54 natural voices in one pack: American and British English, plus Spanish, French, Hindi, Italian, Japanese, Portuguese and Chinese.",
            401_000_000, "Apache 2.0", Folder: "kokoro-multi-lang-v1_0", Recommended: true),
        new(VoiceModelUse.Speaking, "csukuangfj/vits-piper-en_US-joe-medium", "Piper: Joe (US English, male)",
            "A small, very quick voice.", 81_000_000, "CC0", Folder: "vits-piper-en_US-joe-medium"),
        new(VoiceModelUse.Speaking, "csukuangfj/vits-piper-en_GB-alba-medium", "Piper: Alba (Scottish English, female)",
            "A small, very quick voice.", 81_000_000, "CC BY 4.0", Folder: "vits-piper-en_GB-alba-medium"),
        new(VoiceModelUse.Speaking, "csukuangfj/vits-piper-en_GB-cori-high", "Piper: Cori (British English, female)",
            "Higher quality, read from public-domain audiobooks.", 135_000_000, "Public domain", Folder: "vits-piper-en_GB-cori-high"),
        new(VoiceModelUse.Speaking, "csukuangfj/vits-piper-en_GB-northern_english_male-medium", "Piper: Northern English male",
            "A small, very quick voice.", 81_000_000, "CC BY-SA 4.0", Folder: "vits-piper-en_GB-northern_english_male-medium"),
    ];

    /// <summary>Sort key for Whisper files: the recommended one first, then the others in the order listed.</summary>
    public static int SpeechPreference(string fileName)
    {
        var listed = All.Where(d => d.File is not null).Select(d => d.File!).ToList();
        var i = listed.FindIndex(f => f.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? listed.Count : i;
    }

    /// <summary>True for Whisper models that only understand English ("ggml-base.en.bin").</summary>
    public static bool IsEnglishOnly(string fileName) => fileName.Contains(".en.", StringComparison.OrdinalIgnoreCase) ||
                                                        fileName.Contains(".en-", StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable name for a voice pack folder or speech model file.</summary>
    public static string Title(string target)
    {
        if (All.FirstOrDefault(d => d.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) is { } known) return known.Title;
        // "vits-piper-en_US-amy-medium" → "Piper: Amy (en_US, medium)"
        var parts = target.Split('-');
        if (parts.Length >= 5 && parts[0] == "vits" && parts[1] == "piper")
        {
            var name = string.Join(' ', parts[3..^1]).Replace('_', ' ');
            return $"Piper: {char.ToUpperInvariant(name[0])}{name[1..]} ({parts[2]}, {parts[^1]})";
        }
        return target;
    }

    /// <summary>"vits-piper-en_GB-alba-medium" → "en_GB". Null when the folder name does not say.</summary>
    public static string? PiperLanguage(string folder)
    {
        var parts = folder.Split('-');
        return parts.Length >= 3 && parts[2].Length == 5 && parts[2][2] == '_' ? parts[2] : null;
    }
}
