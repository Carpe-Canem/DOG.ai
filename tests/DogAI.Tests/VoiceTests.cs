using DogAI.Core.Settings;
using DogAI.Core.Voice;
using DogAI.Engine.Voice;

namespace DogAI.Tests;

/// <summary>Voice logic without sound devices or models: names, speakable text, sentence cutting, speech detection.</summary>
public sealed class VoiceTextTests
{
    [Theory]
    [InlineData("windows:Microsoft Zira Desktop", VoiceKind.Windows, "Microsoft Zira Desktop", null)]
    [InlineData("kokoro:kokoro-multi-lang-v1_0/af_heart", VoiceKind.Kokoro, "kokoro-multi-lang-v1_0", "af_heart")]
    [InlineData("piper:vits-piper-en_GB-alba-medium", VoiceKind.Piper, "vits-piper-en_GB-alba-medium", null)]
    public void Voice_names_read_back_what_was_saved(string text, VoiceKind kind, string model, string? speaker)
    {
        var id = VoiceId.Parse(text);
        Assert.Equal(new VoiceId(kind, model, speaker), id);
        Assert.Equal(text, id!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("kokoro:no-speaker")]
    [InlineData("piper:../escape")]
    [InlineData("kokoro:a/b/af_heart")]
    [InlineData("radio:station")]
    public void Unreadable_voice_names_mean_the_default(string? text) => Assert.Null(VoiceId.Parse(text));

    [Fact]
    public void Kokoro_voice_names_are_described_in_words()
    {
        Assert.Equal("Heart (American English, female)", KokoroVoices.Describe("af_heart"));
        Assert.Equal("George (British English, male)", KokoroVoices.Describe("bm_george"));
        Assert.Equal("en", KokoroVoices.Language("bf_emma"));
        Assert.Equal("cmn", KokoroVoices.Language("zf_xiaobei"));
        Assert.Equal("odd", KokoroVoices.Describe("odd"));
    }

    [Fact]
    public void Speech_text_keeps_the_words_and_drops_markup_code_links_and_citations()
    {
        var text = SpeechText.ForSpeech("""
            # Getting started

            Use **bold** ideas and *gentle* words [1]. See [the guide](https://example.com/guide) or https://example.com.

            - First step
            - Second step: run `npm i`

            ```python
            print("never read")
            ```

            > A quote ends here
            """);
        Assert.Equal("Getting started. Use bold ideas and gentle words. See the guide or. First step. Second step: run npm i. A quote ends here.", text);
    }

    [Fact]
    public void Role_play_actions_are_read_without_their_asterisks()
    {
        Assert.Equal("smiles Hello there.", SpeechText.ForSpeech("*smiles* Hello there."));
        Assert.Equal("", SpeechText.ForSpeech("```\ncode only\n```"));
    }

    [Fact]
    public void Sentences_are_cut_at_their_ends_but_not_at_abbreviations_decimals_or_initials()
    {
        var text = "Dr. Smith paid 3.50 dollars. J. R. R. Tolkien wrote books, e.g. this one. Really? Yes!";
        var pieces = Split(text, oneCharacterAtATime: true);
        Assert.Equal(text, string.Concat(pieces));
        Assert.Equal("Dr. Smith paid 3.50 dollars.", pieces[0]);
        Assert.DoesNotContain(pieces, p => p.TrimEnd().EndsWith("Dr.") || p.TrimEnd().EndsWith("J.") || p.TrimEnd().EndsWith("e.g."));
    }

    [Fact]
    public void The_first_sentence_comes_at_once_and_later_short_ones_are_joined()
    {
        var pieces = Split("Hi. Ok. Yes. No. Fine. Sure. Good. Great. Lovely. Right. Then. Now we go on.", oneCharacterAtATime: true);
        Assert.Equal("Hi.", pieces[0]);
        Assert.True(pieces.Count < 6, string.Join("|", pieces));
    }

    [Fact]
    public void Code_blocks_are_held_back_until_they_close()
    {
        var splitter = new SentenceSplitter();
        Assert.Equal(["Look."], splitter.Feed("Look.\n"));
        Assert.Empty(splitter.Feed("Look.\n```js\nlet a = 1. b = 2.\n"));
        var rest = splitter.Finish("Look.\n```js\nlet a = 1. b = 2.\n```\nDone.");
        Assert.Equal("```js\nlet a = 1. b = 2.\n```\nDone.", string.Concat(rest));
        Assert.Equal("Done.", SpeechText.ForSpeech(string.Concat(rest)));
    }

    [Fact]
    public void List_items_and_paragraphs_end_pieces()
    {
        var pieces = Split("Here is the list of what to buy before the weekend starts for us\n- eggs\n- milk\n\nThat is all.", oneCharacterAtATime: false);
        Assert.Contains("Here is the list of what to buy before the weekend starts for us\n", pieces);
    }

    private static List<string> Split(string text, bool oneCharacterAtATime)
    {
        var splitter = new SentenceSplitter();
        var pieces = new List<string>();
        if (oneCharacterAtATime)
            for (var i = 1; i <= text.Length; i++) pieces.AddRange(splitter.Feed(text[..i]));
        pieces.AddRange(splitter.Finish(text));
        return pieces;
    }

    [Fact]
    public void Noise_markers_and_invented_stock_phrases_are_dropped()
    {
        Assert.Equal("Hello there.", Transcript.Clean([" [BLANK_AUDIO] ", "Hello there.", "(wind blowing)"]));
        Assert.True(Transcript.IsLikelyInvented("Thank you.", TimeSpan.FromSeconds(1.5), 0.1));
        Assert.True(Transcript.IsLikelyInvented("A real sentence.", TimeSpan.FromSeconds(4), 0.9));
        Assert.True(Transcript.IsLikelyInvented("...", TimeSpan.FromSeconds(4), null));
        Assert.False(Transcript.IsLikelyInvented("Thank you.", TimeSpan.FromSeconds(5), 0.01));
        Assert.False(Transcript.IsLikelyInvented("What is the capital of France?", TimeSpan.FromSeconds(2), 0.05));
    }

    [Fact]
    public void Speech_is_found_between_silences_with_a_little_lead_in()
    {
        var vad = new VoiceActivity(endSilence: TimeSpan.FromMilliseconds(600));
        var audio = Silence(1.0).Concat(Tone(1.2, 0.2)).Concat(Silence(1.5)).ToArray();
        var found = new List<float[]>();
        var starts = 0;
        for (var at = 0; at < audio.Length; at += 800)
        {
            found.AddRange(vad.Feed(audio.AsSpan(at, Math.Min(800, audio.Length - at)), out var started));
            if (started) starts++;
        }
        Assert.Equal(1, starts);
        var utterance = Assert.Single(found);
        var seconds = utterance.Length / 16000.0;
        Assert.InRange(seconds, 1.5, 2.4); // the speech, the lead-in and the pause that ended it
        Assert.False(vad.Speaking);
    }

    [Fact]
    public void A_click_is_not_speech_and_quiet_noise_is_learned()
    {
        var vad = new VoiceActivity();
        var noise = Tone(2.0, 0.003);
        Assert.Empty(vad.Feed(noise, out _));
        var click = Silence(0.5).Concat(Tone(0.15, 0.5)).Concat(Silence(1.5)).ToArray();
        Assert.Empty(vad.Feed(click, out _));
    }

    private static float[] Silence(double seconds) => new float[(int)(seconds * 16000)];

    private static float[] Tone(double seconds, double amplitude) =>
        Enumerable.Range(0, (int)(seconds * 16000)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / 16000))).ToArray();

    [Fact]
    public void Wav_files_round_trip_and_resample()
    {
        var clip = new AudioClip(Tone(0.5, 0.5), 16000);
        var back = Wav.Read(Wav.Write(clip));
        Assert.Equal(16000, back.SampleRate);
        Assert.Equal(clip.Samples.Length, back.Samples.Length);
        Assert.InRange(back.Samples.Zip(clip.Samples, (a, b) => Math.Abs(a - b)).Max(), 0, 0.001);
        Assert.Equal(4000, Wav.Resample(clip.Samples, 16000, 8000).Length);
        Assert.Equal(12000, Wav.Resample(clip.Samples, 16000, 24000).Length);
        Assert.Throws<InvalidDataException>(() => Wav.Read([1, 2, 3]));
    }

    [Fact]
    public void Piper_folder_names_become_readable_titles()
    {
        Assert.Equal("Piper: Amy (en_US, medium)", VoiceCatalog.Title("vits-piper-en_US-amy-medium"));
        Assert.Equal("Kokoro v1.0", VoiceCatalog.Title("kokoro-multi-lang-v1_0"));
        Assert.Equal("en_GB", VoiceCatalog.PiperLanguage("vits-piper-en_GB-alba-medium"));
        Assert.True(VoiceCatalog.IsEnglishOnly("ggml-base.en-q5_1.bin"));
        Assert.False(VoiceCatalog.IsEnglishOnly("ggml-large-v3-turbo-q5_0.bin"));
        Assert.True(VoiceCatalog.SpeechPreference("ggml-large-v3-turbo-q5_0.bin") < VoiceCatalog.SpeechPreference("ggml-small-q5_1.bin"));
    }

    [Fact]
    public void Voice_packs_are_found_and_kokoro_names_come_from_the_model_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var kokoro = Directory.CreateDirectory(Path.Combine(root, "kokoro-test")).FullName;
            File.WriteAllBytes(Path.Combine(kokoro, "model.onnx"), FakeOnnx(("speaker_names", "af_heart,bm_george"), ("sample_rate", "24000")));
            File.WriteAllText(Path.Combine(kokoro, "voices.bin"), "x");
            File.WriteAllText(Path.Combine(kokoro, "tokens.txt"), "x");
            var piper = Directory.CreateDirectory(Path.Combine(root, "vits-piper-en_US-joe-medium")).FullName;
            File.WriteAllBytes(Path.Combine(piper, "en_US-joe-medium.onnx"), FakeOnnx());
            File.WriteAllText(Path.Combine(piper, "tokens.txt"), "x");
            Directory.CreateDirectory(Path.Combine(piper, "espeak-ng-data"));
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            File.WriteAllBytes(Path.Combine(root, "ggml-small-q5_1.bin"), new byte[2 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(root, "ggml-large-v3-turbo-q5_0.bin"), new byte[2 * 1024 * 1024]);

            var packs = VoiceLibrary.Packs(root);
            Assert.Equal(2, packs.Count);
            Assert.Equal(["af_heart", "bm_george"], packs.Single(p => p.Kind == VoiceKind.Kokoro).Speakers);
            Assert.Equal("vits-piper-en_US-joe-medium", packs.Single(p => p.Kind == VoiceKind.Piper).Name);
            Assert.Equal("ggml-large-v3-turbo-q5_0.bin", VoiceLibrary.SpeechModels(root)[0].Name);

            var options = packs.SelectMany(VoiceLibrary.Options).ToList();
            Assert.Contains(options, o => o.Id == new VoiceId(VoiceKind.Kokoro, "kokoro-test", "bm_george") && o.Label == "George (British English, male)");
            Assert.Contains(options, o => o.Id == new VoiceId(VoiceKind.Piper, "vits-piper-en_US-joe-medium"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The smallest protocol-buffer file shaped like an ONNX model: a version, a large field to skip, and metadata.</summary>
    private static byte[] FakeOnnx(params (string Key, string Value)[] metadata)
    {
        var bytes = new List<byte> { 0x08, 0x07 }; // ir_version = 7
        bytes.AddRange(Field(7, new byte[300])); // a "graph" to skip
        foreach (var (key, value) in metadata)
            bytes.AddRange(Field(14, Field(1, System.Text.Encoding.UTF8.GetBytes(key)).Concat(Field(2, System.Text.Encoding.UTF8.GetBytes(value))).ToArray()));
        return bytes.ToArray();
    }

    private static IEnumerable<byte> Field(int number, byte[] body)
    {
        var result = new List<byte>();
        result.AddRange(Varint((ulong)(number << 3 | 2)));
        result.AddRange(Varint((ulong)body.Length));
        result.AddRange(body);
        return result;
    }

    private static IEnumerable<byte> Varint(ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            yield return value == 0 ? b : (byte)(b | 0x80);
        } while (value != 0);
    }
}

/// <summary>Reading aloud and dictation with stand-ins for the voice, speakers, microphone and speech model.</summary>
public sealed class VoiceFlowTests
{
    internal sealed class MemorySettings(AppSettings? start = null) : ISettingsStore
    {
        public AppSettings Current { get; set; } = start ?? new AppSettings();
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeVoice : ISpeechSynthesizer
    {
        public List<(string Text, VoiceId Voice)> Said { get; } = [];
        public Exception? Fail { get; set; }
        public static readonly VoiceId Default = new(VoiceKind.Windows, "Default");

        public Task<IReadOnlyList<VoiceOption>> VoicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VoiceOption>>([new VoiceOption(Default, "Default", "Windows voices")]);

        public Task<VoiceId?> ResolveAsync(VoiceId? wanted, CancellationToken ct = default) => Task.FromResult(wanted ?? Default);

        public void Refresh() { }

        public async Task<AudioClip> SynthesizeAsync(string text, VoiceId voice, float speed, CancellationToken ct = default)
        {
            await Task.Yield();
            if (Fail is not null) throw Fail;
            lock (Said) Said.Add((text, voice));
            return new AudioClip(new float[10], 1000);
        }
    }

    internal sealed class FakeSpeakers : IAudioOutput
    {
        public List<int> Played { get; } = [];
        public TaskCompletionSource? Hold { get; set; }

        public async Task PlayAsync(AudioClip clip, CancellationToken ct = default)
        {
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            lock (Played) Played.Add(clip.Samples.Length);
        }
    }

    [Fact]
    public async Task A_whole_reply_is_read_in_pieces_and_in_order()
    {
        var voice = new FakeVoice();
        var speakers = new FakeSpeakers();
        using var reader = new ReadAloud(voice, speakers, new MemorySettings());

        reader.Speak("msg:1", "First sentence here. **Second** sentence [1].\n\n```\ncode\n```\nThird.", null);
        Assert.Equal("msg:1", reader.Key);
        await reader.IdleAsync();

        Assert.Equal(["First sentence here.", "Second sentence.", "Third."], voice.Said.Select(s => s.Text));
        Assert.All(voice.Said, s => Assert.Equal(FakeVoice.Default, s.Voice));
        Assert.Equal(3, speakers.Played.Count);
        Assert.Null(reader.Key);
    }

    [Fact]
    public async Task Stop_ends_reading_at_once_and_a_new_reading_replaces_the_old()
    {
        var voice = new FakeVoice();
        var speakers = new FakeSpeakers { Hold = new TaskCompletionSource() };
        using var reader = new ReadAloud(voice, speakers, new MemorySettings());

        reader.Speak("msg:1", "One. Two. Three.", null);
        await WaitUntil(() => voice.Said.Count > 0);
        reader.Speak("msg:2", "Other.", new VoiceId(VoiceKind.Windows, "B"));
        Assert.Equal("msg:2", reader.Key);
        speakers.Hold.SetResult();
        await reader.IdleAsync();
        Assert.Contains(voice.Said, s => s.Text == "Other." && s.Voice.Model == "B");

        speakers.Hold = new TaskCompletionSource();
        reader.Speak("msg:3", "Never finishes.", null);
        reader.Stop();
        await reader.IdleAsync();
        Assert.Null(reader.Key);
    }

    [Fact]
    public async Task Streaming_text_is_read_as_it_arrives_and_queued_readings_wait_their_turn()
    {
        var voice = new FakeVoice();
        var speakers = new FakeSpeakers();
        using var reader = new ReadAloud(voice, speakers, new MemorySettings());

        reader.Begin("live:a", Task.FromResult<VoiceId?>(new VoiceId(VoiceKind.Windows, "A")));
        reader.Feed("live:a", "Hello from Ann. And");
        await WaitUntil(() => voice.Said.Count == 1);
        reader.Begin("live:b", Task.FromResult<VoiceId?>(new VoiceId(VoiceKind.Windows, "B")), queue: true);
        reader.End("live:a", "Hello from Ann. And more.");
        reader.End("live:b", "Bo here.");
        await reader.IdleAsync();

        Assert.Equal(["Hello from Ann.", "And more.", "Bo here."], voice.Said.Select(s => s.Text));
        Assert.Equal(["A", "A", "B"], voice.Said.Select(s => s.Voice.Model));
    }

    [Fact]
    public async Task A_voice_problem_is_reported_in_words()
    {
        var voice = new FakeVoice { Fail = new VoiceUnavailableException("The voice is gone.") };
        using var reader = new ReadAloud(voice, new FakeSpeakers(), new MemorySettings());
        reader.Speak("msg:1", "Hello.", null);
        await reader.IdleAsync();
        Assert.Equal("The voice is gone.", reader.Problem);
    }

    internal sealed class FakeMic : IMicrophone
    {
        public event Action<float[]>? Samples;
        public event Action<string>? Failed;
        public bool IsRecording { get; private set; }
        public Task StartAsync(CancellationToken ct = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }
        public void Stop() => IsRecording = false;
        public void Send(float[] samples) => Samples?.Invoke(samples);
        public void Break() => Failed?.Invoke("unplugged");
    }

    internal sealed class FakeEars(string text, double noSpeech = 0.01) : ISpeechRecognizer
    {
        public string? Model { get; set; } = "whisper.bin";
        public List<int> Heard { get; } = [];
        public string? RunsOn => "processor";
        public Task<string?> ModelPathAsync(CancellationToken ct = default) => Task.FromResult(Model);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<Recognized> RecognizeAsync(float[] samples, string language, CancellationToken ct = default)
        {
            lock (Heard) Heard.Add(samples.Length);
            return Task.FromResult(new Recognized(" " + text, noSpeech, "en"));
        }
    }

    private static float[] Tone(double seconds, double amplitude) =>
        Enumerable.Range(0, (int)(seconds * 16000)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / 16000))).ToArray();

    [Fact]
    public async Task Push_to_talk_writes_down_what_was_said_without_sending()
    {
        var mic = new FakeMic();
        using var dictation = new Dictation(mic, new FakeEars("What time is it?"), new MemorySettings());
        var heard = new List<(string, bool)>();
        dictation.Heard += (t, s) => heard.Add((t, s));

        await dictation.StartAsync();
        Assert.True(mic.IsRecording);
        Assert.Equal(DictationState.Listening, dictation.State);
        mic.Send(Tone(1.5, 0.2));
        await dictation.FinishAsync();

        Assert.Equal([("What time is it?", false)], heard);
        Assert.False(mic.IsRecording);
        Assert.Equal(DictationState.Off, dictation.State);
    }

    [Fact]
    public async Task Hands_free_sends_each_thing_said_but_not_while_paused()
    {
        var mic = new FakeMic();
        var ears = new FakeEars("Tell me a joke.");
        using var dictation = new Dictation(mic, ears, new MemorySettings());
        var heard = new List<(string, bool)>();
        dictation.Heard += (t, s) => { lock (heard) heard.Add((t, s)); };
        var paused = true;
        dictation.Paused = () => paused;

        await dictation.StartHandsFreeAsync();
        mic.Send(new float[16000]);
        mic.Send(Tone(1.0, 0.2));
        mic.Send(new float[32000]);
        Assert.Empty(ears.Heard);

        paused = false;
        mic.Send(new float[16000]);
        mic.Send(Tone(1.0, 0.2));
        mic.Send(new float[32000]);
        await WaitUntil(() => { lock (heard) return heard.Count == 1; });
        Assert.Equal(("Tell me a joke.", true), heard[0]);
        Assert.True(dictation.HandsFree);

        dictation.Stop();
        Assert.False(mic.IsRecording);
        Assert.False(dictation.IsOn);
    }

    [Fact]
    public async Task Made_up_text_is_dropped_and_missing_models_or_microphones_are_explained()
    {
        var mic = new FakeMic();
        var ears = new FakeEars("Thank you.", noSpeech: 0.5);
        using var dictation = new Dictation(mic, ears, new MemorySettings());
        var heard = new List<string>();
        dictation.Heard += (t, _) => heard.Add(t);

        await dictation.StartAsync();
        mic.Send(Tone(1.0, 0.2));
        await dictation.FinishAsync();
        Assert.Empty(heard);

        ears.Model = null;
        await dictation.StartAsync();
        Assert.False(dictation.IsOn);
        Assert.Contains("Models → Voice", dictation.Problem);

        ears.Model = "whisper.bin";
        await dictation.StartHandsFreeAsync();
        mic.Break();
        Assert.False(dictation.IsOn);
        Assert.Equal("unplugged", dictation.Problem);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
