using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DogAI.Core.Characters;
using DogAI.Core.Chat;

namespace DogAI.Tests;

public sealed class CardBuilderTests
{
    private sealed class ScriptedEngine(params string[] parts) : IChatEngine
    {
        public bool IsReady { get; set; } = true;
        public string? ModelName => "fake";
        public int ContextLength => 8192;
        public SamplerPreset? LastSampler { get; private set; }
        public IReadOnlyList<PromptMessage>? LastPrompt { get; private set; }

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Task.FromResult(text.Length / 4);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => Task.FromResult("");

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            LastPrompt = messages;
            LastSampler = sampler;
            return Stream(ct);
        }

        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) => Stream(ct);

        private async IAsyncEnumerable<ChatDelta> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ChatDelta(part, null, null);
            }
        }
    }

    private const string Card = """
        {"name":"Ada","description":"{{char}} keeps a lighthouse.","personality":"calm, wry","scenario":"A storm night.",
         "first_mes":"*Ada opens the door.* Come in.","alternate_greetings":["Hello."," "],"mes_example":"<START>\n{{user}}: Hi\n{{char}}: Hm.",
         "tags":["Sea","cozy","sea"]}
        """;

    [Fact]
    public async Task Drafts_a_card_with_structured_output()
    {
        var engine = new ScriptedEngine(Card[..40], Card[40..]);
        var lengths = new List<int>();

        var c = await new CardBuilder(engine).DraftAsync("a lighthouse keeper", t => lengths.Add(t.Length));

        Assert.Equal("Ada", c.Name);
        Assert.Equal("{{char}} keeps a lighthouse.", c.Description);
        Assert.Equal(["Hello."], c.AlternateGreetings);
        Assert.Equal(["sea", "cozy"], c.Tags);
        Assert.Equal(0, c.Id);
        Assert.NotNull(engine.LastSampler?.JsonSchema);
        Assert.NotNull(JsonNode.Parse(engine.LastSampler!.JsonSchema!)); // the schema itself is valid JSON
        Assert.Contains("a lighthouse keeper", engine.LastPrompt![^1].Content);
        Assert.Equal(2, lengths.Count);
    }

    [Fact]
    public void Reads_a_card_wrapped_in_a_code_fence() =>
        Assert.Equal("Ada", CardBuilder.Parse("Here you go:\n```json\n" + Card + "\n```").Name);

    [Theory]
    [InlineData("I cannot do that.")]
    [InlineData("{\"name\": \"Ada\", \"description\": \"cut off")]
    [InlineData("{\"description\": \"no name\"}")]
    public void Refuses_answers_that_are_not_a_card(string answer) =>
        Assert.Throws<InvalidOperationException>(() => CardBuilder.Parse(answer));

    [Fact]
    public async Task Needs_a_model_and_an_idea()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CardBuilder(new ScriptedEngine { IsReady = false }).DraftAsync("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CardBuilder(new ScriptedEngine()).DraftAsync("   "));
    }
}
