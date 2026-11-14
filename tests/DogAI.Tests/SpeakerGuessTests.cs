using DogAI.Core.Stories;
using DogAI.Core.Settings;
using DogAI.Core.Text;
using DogAI.Core.Voice;
using static DogAI.Tests.VoiceFlowTests;

namespace DogAI.Tests;

/// <summary>Who says each line in a story: the colour and the voice follow the names written next to the line.</summary>
public class SpeakerGuessTests
{
    private static readonly string[] Cast = ["Mara Vell", "Ben"];

    private static IReadOnlyList<int?> Guess(string paragraph, params string[] names) =>
        new SpeakerGuess(names.Length > 0 ? names : Cast).Assign(Quotations.Split(paragraph));

    [Theory]
    [InlineData("“You're late,” said Mara.", 0)]
    [InlineData("“You're late,” Mara said.", 0)]
    [InlineData("“You're late!” Ben shouted, slamming the door.", 1)]
    [InlineData("Mara Vell looked up from the map. “You're late.”", 0)]
    [InlineData("Ben's hands shook. \"I know.\"", 1)]
    public void A_tag_or_an_action_beside_the_line_names_its_speaker(string paragraph, int speaker) =>
        Assert.Equal([speaker], Guess(paragraph));

    [Theory]
    [InlineData("“You're late,” she said to Mara.")]          // a pronoun speaks; Mara is spoken to
    [InlineData("Mara looked at Ben. “Hi,” he said.")]        // the tag wins over the action before it
    [InlineData("“Get down!” came a voice from the door.")]   // a tag with nobody named
    [InlineData("“Get down!” The shot rang out past Mara.")]   // a name later in the sentence is not the speaker
    [InlineData("The rain had not stopped. “Get down!”")]      // nobody named at all
    public void A_line_nobody_can_be_pinned_to_has_no_speaker(string paragraph) =>
        Assert.Equal([null], Guess(paragraph));

    [Fact]
    public void The_speaker_carries_on_until_someone_else_is_named()
    {
        Assert.Equal([0, 0], Guess("“Hi,” said Mara, “how are you?”"));
        Assert.Equal([0, 0], Guess("“Hi,” said Mara. The wind rose. “Cold tonight.”"));
        Assert.Equal([0, 1], Guess("“Hi,” said Mara. Ben shrugged. “Cold tonight.”"));
        Assert.Equal([0, null], Guess("“Hi,” said Mara. He shrugged. “Cold tonight.”"));
    }

    [Fact]
    public void Names_are_matched_as_whole_words_and_with_their_case()
    {
        // "Will" the character, not "will" the word; "Benedict" is not Ben.
        Assert.Equal([null], Guess("Benedict sighed. “No.”"));
        Assert.Equal([null], Guess("“I will,” she said.", "Will", "Grace"));
        Assert.Equal([0], Guess("“I will,” Will said.", "Will", "Grace"));
        Assert.Equal([null, null], Guess("“Grace,” he said, “say grace.”", "Will", "Grace"));
    }

    [Fact]
    public void A_first_name_two_characters_share_names_neither_and_short_or_empty_names_are_ignored()
    {
        Assert.Equal([null], Guess("“Hi,” said Anna.", "Anna Bell", "Anna Ross"));
        Assert.Equal([1], Guess("“Hi,” said Anna Ross.", "Anna Bell", "Anna Ross"));
        Assert.True(new SpeakerGuess(["", " ", "X"]).IsEmpty);
        // Card names are untrusted: pattern characters are plain text and match nothing else.
        Assert.Equal([null], Guess("“Hi,” said Ben2.", "(.*)", "Ben"));
    }

    [Fact]
    public void Someone_giving_their_own_name_is_the_speaker()
    {
        string[] cast = ["Suckbot", "Jimmy 'Jive Turkey' Malone"];
        // From a real story: the tag names nobody ("the object"), the line says who it is, and the next line carries on.
        Assert.Equal([0, 0], Guess("\"I am Suckbot,\" the object said. \"And I am tired.\"", cast));
        Assert.Equal([1], Guess("“I'm Jimmy, pal,” came a voice.", cast));
        Assert.Equal([1], Guess("“My name is Jimmy.”", cast));
        Assert.Equal([0], Guess("“Call me Suckbot,” she said.", cast));
        // A tag that names someone wins; a possessive or two different names pin nobody.
        Assert.Equal([0], Guess("“I am Jimmy,” Suckbot mocked.", cast));
        Assert.Equal([null], Guess("“I am Jimmy's friend,” the object said.", cast));
        Assert.Equal([null], Guess("“I'm Jimmy and I'm Suckbot.”", cast));
    }

    private const string Passage =
        "Mara looked up from the map. “You're late.”\n\n" +
        "“The bridge was out,” Ben said. “We went round.” He dropped his pack. \"Is there food?\"\n\n" +
        "*She points at the pot.* “Help yourself,” said Mara Vell.\n\n" +
        "- “A list line,” Ben said.\n\n" +
        "Nobody spoke. “Who's there?” came a voice. 5\" of snow lay on the sill.";

    [Fact]
    public void Colours_follow_the_speaker_and_unsure_lines_keep_the_plain_colour()
    {
        var html = string.Concat(MarkdownRenderer.Render(Passage, speech: true, speakers: [new Speaker("Mara Vell", 2), new Speaker("Ben", 5)]).Select(s => s.Html));
        Assert.Contains("<span class=\"say tone-2\">“You're late.”</span>", html);
        Assert.Contains("<span class=\"say tone-5\">“The bridge was out,”</span>", html);
        Assert.Contains("<span class=\"say tone-5\">“We went round.”</span>", html);
        Assert.Contains("<span class=\"say\">&quot;Is there food?&quot;</span>", html); // "He": not sure
        Assert.Contains("<em class=\"act\">She points at the pot.</em>", html);
        Assert.Contains("<span class=\"say tone-2\">“Help yourself,”</span>", html);
        Assert.Contains("<span class=\"say tone-5\">“A list line,”</span>", html);
        Assert.Contains("<span class=\"say\">“Who's there?”</span>", html);
        Assert.False(html.Contains('\u0001'), "A speaker placeholder was left in the markup.");
    }

    [Fact]
    public void Without_speakers_the_markup_is_as_before()
    {
        var plain = string.Concat(MarkdownRenderer.Render(Passage, speech: true).Select(s => s.Html));
        Assert.DoesNotContain("tone-", plain);
        Assert.Contains("<span class=\"say\">“You're late.”</span>", plain);
    }

    [Theory]
    [InlineData(Passage)]
    [InlineData("\"Hi,\" said Mara. \"Hello,\" Ben said, \"and goodbye.\"\n\nA \"word\" in narration, then «Bonjour», said Ben.")]
    [InlineData("“An open quote that never closes, said Mara.\n\nBen said: “Fine.”")]
    [InlineData("> “Quoted block,” said Mara.\n\n1. “One,” Ben said.\n2. Mara: “Two.”")]
    [InlineData("\"I'm Ben,\" the stranger said. \"And I am tired.\"\n\n\"My name is Mara Vell,\" came the reply.")]
    public void Colour_and_voice_find_the_same_lines_with_the_same_speakers(string markdown)
    {
        var tones = new[] { 3, 4 };
        var html = string.Concat(MarkdownRenderer.Render(markdown, speech: true, speakers: [new Speaker(Cast[0], tones[0]), new Speaker(Cast[1], tones[1])]).Select(s => s.Html));
        var fromColour = System.Text.RegularExpressions.Regex.Matches(html, "<span class=\"say( tone-(\\d))?\">[“«]|<span class=\"say( tone-(\\d))?\">&quot;")
            .Select(m => m.Groups[2].Success ? Array.IndexOf(tones, int.Parse(m.Groups[2].Value)) : m.Groups[4].Success ? Array.IndexOf(tones, int.Parse(m.Groups[4].Value)) : (int?)null)
            .Select(i => i < 0 ? null : i)
            .ToList();

        var guess = new SpeakerGuess(Cast);
        var fromVoice = SpeechText.Paragraphs(markdown).SelectMany(p => guess.Assign(Quotations.Split(p))).ToList();

        Assert.NotEmpty(fromVoice);
        Assert.Equal(fromVoice, fromColour);
    }

    [Fact]
    public void Tones_follow_the_characters_ids_not_their_order()
    {
        Assert.Equal(1, SpeakerTones.Of([30, 10, 20], 10));
        Assert.Equal(3, SpeakerTones.Of([30, 10, 20], 30));
        Assert.Null(SpeakerTones.Of([30, 10], 99));
        Assert.Equal(1, SpeakerTones.Of(Enumerable.Range(1, 7).Select(i => (long)i), 7));
    }

    private static readonly VoiceId MaraVoice = new(VoiceKind.Kokoro, "kokoro", "af_mara");
    private static readonly VoiceId Narrator = new(VoiceKind.Windows, "Narrator");

    [Fact]
    public void A_passage_is_cut_into_narration_and_lines_for_each_voice()
    {
        var cast = new VoiceCast(Cast, [MaraVoice, null]);
        var pieces = VoicedPieces.Split("Mara looked up. “You're late.” She **smiled**.\n\n“The bridge was out,” Ben said.", cast);
        Assert.Equal(
            [("Mara looked up.", null), ("“You're late.”", 0), ("She smiled.", null), ("“The bridge was out,” Ben said.", null)],
            pieces.Select(p => (p.Text, p.Speaker)));
        Assert.All(pieces, p => Assert.True(p.Plain));
    }

    [Fact]
    public void While_a_passage_streams_each_paragraph_waits_until_it_is_finished()
    {
        var splitter = new VoicedPieces(new VoiceCast(Cast, [MaraVoice, null]));
        Assert.Empty(splitter.Feed("“You're late,” said"));
        var first = splitter.Feed("“You're late,” said Mara.\n\nBen ");
        Assert.Equal([("“You're late,”", (int?)0), ("said Mara.", null)], first.Select(p => (p.Text, p.Speaker)));
        var rest = splitter.Finish("“You're late,” said Mara.\n\nBen nodded.");
        Assert.Equal(["Ben nodded."], rest.Select(p => p.Text));
    }

    /// <summary>A synthesizer that has only some voices, as when a card's voice was deleted.</summary>
    private sealed class SomeVoices(params VoiceId[] have) : ISpeechSynthesizer
    {
        public List<(string Text, VoiceId Voice)> Said { get; } = [];
        public Task<IReadOnlyList<VoiceOption>> VoicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VoiceOption>>(have.Select(v => new VoiceOption(v, v.Model, "")).ToList());
        public Task<VoiceId?> ResolveAsync(VoiceId? wanted, CancellationToken ct = default) =>
            Task.FromResult<VoiceId?>(wanted is not null && have.Contains(wanted) ? wanted : have[0]);
        public void Refresh() { }
        public Task<AudioClip> SynthesizeAsync(string text, VoiceId voice, float speed, CancellationToken ct = default)
        {
            lock (Said) Said.Add((text, voice));
            return Task.FromResult(new AudioClip(new float[10], 1000));
        }
    }

    [Fact]
    public async Task Lines_are_read_in_the_characters_voices_and_a_missing_voice_falls_back_to_the_narrator()
    {
        var gone = new VoiceId(VoiceKind.Piper, "deleted-voice");
        var voices = new SomeVoices(Narrator, MaraVoice);
        using var reader = new ReadAloud(voices, new FakeSpeakers(), new MemorySettings());

        reader.Speak("msg:1", "“Tea?” said Mara. “No,” Ben said.", Narrator, new VoiceCast(Cast, [MaraVoice, gone]));
        await reader.IdleAsync();

        Assert.Null(reader.Problem);
        Assert.Equal(
            [("“Tea?”", MaraVoice), ("said Mara.", Narrator), ("“No,”", Narrator), ("Ben said.", Narrator)],
            voices.Said.Select(s => (s.Text, s.Voice)));
    }

    [Fact]
    public async Task Streaming_a_story_reads_each_line_in_its_voice()
    {
        var voices = new SomeVoices(Narrator, MaraVoice);
        using var reader = new ReadAloud(voices, new FakeSpeakers(), new MemorySettings());
        reader.Begin("live", Task.FromResult<VoiceId?>(Narrator), cast: Task.FromResult<VoiceCast?>(new VoiceCast(Cast, [MaraVoice, null])));
        reader.Feed("live", "Mara Vell sighed.");
        reader.Feed("live", "Mara Vell sighed. “Again?”\n\nThe");
        reader.End("live", "Mara Vell sighed. “Again?”\n\nThe fire cracked.");
        await reader.IdleAsync();

        Assert.Equal(
            [("Mara Vell sighed.", Narrator), ("“Again?”", MaraVoice), ("The fire cracked.", Narrator)],
            voices.Said.Select(s => (s.Text, s.Voice)));
    }
}

/// <summary>"She said" and "he said": a pronoun is pinned to the one person nearby it can mean.</summary>
public class PronounGuessTests
{
    private static readonly Person[] Cast = [new("Mara Vell", Pronoun.She), new("Ben", Pronoun.He)];

    private static IReadOnlyList<int?> Guess(params string[] paragraphs) => Guess(Cast, paragraphs);

    private static IReadOnlyList<int?> Guess(Person[] people, params string[] paragraphs)
    {
        var guess = new SpeakerGuess(people);
        return paragraphs.SelectMany(p => guess.Assign(Quotations.Split(p))).ToList();
    }

    [Fact]
    public void A_cards_own_text_tells_its_pronoun_when_it_is_clear()
    {
        Assert.Equal(Pronoun.She, Pronouns.Guess("{{char}} is a pilot. She keeps a ledger; her boat is old and she loves it.", "Loyal to {{user}}, his friend."));
        Assert.Equal(Pronoun.He, Pronouns.Guess("He grew up here. He hums when he works."));
        Assert.Equal(Pronoun.Unknown, Pronouns.Guess("{{char}} is a pilot.", "She is kind to him and his brother."));
        Assert.Equal(Pronoun.Unknown, Pronouns.Guess("They keep a ledger.", null));
    }

    [Fact]
    public void She_and_he_go_to_the_one_person_nearby_who_fits()
    {
        Assert.Equal([1], Guess("Mara looked at Ben. “Hi,” he said."));
        Assert.Equal([0], Guess("Mara looked at Ben. “Hi,” she said."));
        Assert.Equal([0], Guess("Mara Vell taps the map. “We can.” She slides it over. “If you’re willing.”").TakeLast(1));
        // Named in the paragraph before.
        Assert.Equal([1, 0], Guess("Ben ran up the planks, panting.", "“Mara,” he gasped.", "Mara didn't move.", "“You're late,” she said."));
    }

    [Fact]
    public void A_pronoun_stays_unsure_when_nobody_or_several_could_be_meant()
    {
        // Spoken to, not speaking.
        Assert.Equal([null], Guess("“You're late,” she said to Mara."));
        // Nobody named nearby.
        Assert.Equal([null], Guess("The rain fell.", "“Hi,” he said."));
        // Two women nearby.
        var two = new Person[] { new("Mara", Pronoun.She), new("Elsa", Pronoun.She) };
        Assert.Equal([null], Guess(two, "Mara and Elsa waited. “Late again,” she said."));
        // Someone without a known pronoun nearby could be the "she".
        var other = new Person[] { new("Mara", Pronoun.She), new("Elara") };
        Assert.Equal([null], Guess(other, "Elara looked at Mara. “Hi,” she said."));
        Assert.Equal([1], Guess(other, "“Hi,” said Elara."));
        // "They", "I", "you" are never pinned.
        Assert.Equal([null], Guess("Mara waved. “Hi,” they said."));
        // A card whose pronoun is not known is never the "she".
        Assert.Equal([null], Guess([new Person("Mara")], "Mara waved. “Hi,” she said."));
    }

    [Fact]
    public void Names_in_the_Cast_box_are_read_and_listed_after_the_cards()
    {
        Assert.Equal(["Elara Kaine", "Silas Thorne", "Old Nan"], StoryPrompt.CastBoxNames(
            "Elara Kaine: a disgraced cartographer.\n- Silas Thorne (the commissioner)\n\nOld Nan, who keeps the ferry\nthe crowd at the dock\nA tall stranger with a long coat and no name at all"));

        var mara = new DogAI.Core.Characters.Character { Name = "Mara", Description = "She is a pilot. Her boat leaks." };
        var story = new Story { Bible = new StoryBible { Cast = "Mara: the pilot\nElara: a cartographer" } };
        var people = StoryPrompt.People(story, [new StoryCastMember(mara, false)]);
        Assert.Equal([new Person("Mara", Pronoun.She), new Person("Elara")], people);
    }

    [Fact]
    public void Colour_and_voice_agree_across_paragraphs_with_pronouns()
    {
        const string passage = "Ben ran up the planks.\n\n“Mara,” he gasped. “The engine stalled.”\n\nMara didn't move.\n\n“You're late,” she said.\n\n“Who's there?” Elara called.";
        var people = new Person[] { new("Mara", Pronoun.She), new("Ben", Pronoun.He), new("Elara") };
        var html = string.Concat(MarkdownRenderer.Render(passage, speech: true,
            speakers: [new Speaker("Mara", 1, Pronoun.She), new Speaker("Ben", 2, Pronoun.He), new Speaker("Elara", null)]).Select(s => s.Html));
        Assert.Contains("<span class=\"say tone-2\">“Mara,”</span>", html);
        Assert.Contains("<span class=\"say tone-2\">“The engine stalled.”</span>", html);
        Assert.Contains("<span class=\"say tone-1\">“You're late,”</span>", html);
        Assert.Contains("<span class=\"say\">“Who's there?”</span>", html); // Elara: known, but no colour of her own

        var pieces = VoicedPieces.Split(passage, new DogAI.Core.Voice.VoiceCast(people, [null, new DogAI.Core.Voice.VoiceId(DogAI.Core.Voice.VoiceKind.Windows, "Ben")]));
        Assert.Equal(1, pieces.Single(p => p.Text.StartsWith("“Mara")).Speaker);
        Assert.Null(pieces.Single(p => p.Text.StartsWith("“You're")).Speaker); // Mara has no voice of her own: the narrator reads it
        Assert.Null(pieces.Single(p => p.Text.StartsWith("“Who's")).Speaker);
    }
}
