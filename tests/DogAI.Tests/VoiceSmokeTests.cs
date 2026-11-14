using System.Runtime.Versioning;
using DogAI.Core.Settings;
using DogAI.Core.Voice;
using DogAI.Engine.Voice;

namespace DogAI.Tests;

/// <summary>
/// The real voices and speech model, run only when DOGAI_TEST_VOICE_DIR points at a models folder whose "voice"
/// folder holds a Whisper model and voices: every kind of voice says a sentence, and Whisper must hear it back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VoiceSmokeTests
{
    private static string? ModelsDir => Environment.GetEnvironmentVariable("DOGAI_TEST_VOICE_DIR");

    private const string Sentence = "The quick brown fox jumps over the lazy dog near the river bank.";

    [Fact]
    public async Task Each_voice_kind_speaks_and_whisper_hears_the_words()
    {
        if (ModelsDir is not { Length: > 0 } dir || !Directory.Exists(Path.Combine(dir, "voice"))) return;
        var settings = new VoiceFlowTests.MemorySettings(new AppSettings { ModelsDirectory = dir, SpeechLanguage = "en" });
        using var voices = new VoiceBox(settings);
        using var ears = new WhisperRecognizer(settings);
        Assert.NotNull(await ears.ModelPathAsync());

        var options = await voices.VoicesAsync();
        var picks = new[]
        {
            options.FirstOrDefault(o => o.Id is { Kind: VoiceKind.Kokoro, Speaker: "af_heart" }),
            options.FirstOrDefault(o => o.Id.Kind == VoiceKind.Piper),
            options.FirstOrDefault(o => o.Id.Kind == VoiceKind.Windows),
        }.OfType<VoiceOption>().ToList();
        Assert.NotEmpty(picks);

        foreach (var pick in picks)
        {
            var clip = await voices.SynthesizeAsync(Sentence, pick.Id, 1f);
            Assert.InRange(clip.Duration.TotalSeconds, 2, 10);
            var heard = await ears.RecognizeAsync(Wav.Resample(clip.Samples, clip.SampleRate, VoiceActivity.SampleRate), "en");
            var text = Transcript.Clean([heard.Text]).ToLowerInvariant();
            Assert.True(text.Contains("fox") && text.Contains("lazy dog"), $"{pick.Label}: heard \"{heard.Text}\"");
        }
    }
}
