using DogAI.Core.Models;

/// <summary>Model cards copied from Hugging Face (first 140 lines) in ModelCards/.</summary>
public class ModelCardTests
{
    private static string Card(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ModelCards", name + ".md"));

    private static ModelAbout? Summary(string name) => ModelCard.Summarize(name, ModelCard.Split(Card(name)).Body);

    [Theory]
    [InlineData("mradermacher_Huihui-Qwen3.5-9B-abliterated-GGUF")]
    [InlineData("bartowski_DeepSeek-R1-Distill-Qwen-32B-abliterated-GGUF")]
    [InlineData("llmfan46_gemma-4-31B-it-uncensored-heretic-GGUF")]
    public void Cards_that_only_describe_files_or_ask_for_money_give_nothing(string name) =>
        Assert.Null(Summary(name));

    [Theory]
    [InlineData("Qwen_Qwen3.8-27B", "we are pleased to introduce Qwen3.8", "Agent Execution")]
    [InlineData("unsloth_Qwen3.8-27B-GGUF", "we are pleased to introduce Qwen3.8", "Agent Execution")]
    [InlineData("google_gemma-4-12B-it", "Gemma is a family of open models", "Reasoning")]
    [InlineData("unsloth_gemma-4-12b-it-GGUF", "Gemma is a family of open models", "Reasoning")]
    [InlineData("Qwen_Qwen3-Embedding-0.6B-GGUF", "designed for text embedding", "Multilingual Capability")]
    public void Original_cards_and_copies_of_them_give_the_description_and_strengths(string name, string intro, string highlight)
    {
        var about = Summary(name);
        Assert.NotNull(about);
        Assert.Contains(intro, about!.Intro);
        Assert.Contains(about.Highlights, h => h.StartsWith(highlight, StringComparison.Ordinal));
        // Plain text only: no markdown links, emphasis or HTML survive.
        Assert.All(about.Highlights.Append(about.Intro!), t => Assert.DoesNotMatch(@"\]\(|\*\*|<[a-z]|https?://", t));
    }

    [Fact]
    public void An_uncensored_edit_says_what_it_was_made_from()
    {
        var about = Summary("huihui-ai_Huihui-Qwen3.5-9B-abliterated");
        Assert.StartsWith("This is an uncensored version of Qwen/Qwen3.5-9B", about!.Intro);
        Assert.Empty(about.Highlights);
    }

    [Theory]
    [InlineData("---\nbase_model: org/Model\n---\n", "org/Model")]
    [InlineData("---\nbase_model:\n- org/Model\nlicense: mit\n---\n", "org/Model")]
    [InlineData("---\nbase_model: [\"org/Model\"]\n---\n", "org/Model")]
    [InlineData("---\nbase_model:\n- org/A\n- org/B\n---\n", null)]
    [InlineData("---\nlicense: mit\n---\n", null)]
    [InlineData("---\nbase_model: not a repo\n---\n", null)]
    public void The_base_model_is_read_from_the_metadata_block(string card, string? expected) =>
        Assert.Equal(expected, ModelCard.BaseModel(ModelCard.Split(card).FrontMatter));

    [Fact]
    public void The_base_model_is_read_from_search_tags()
    {
        Assert.Equal("Qwen/Qwen3.8-27B", ModelCard.BaseModelFromTags(["gguf", "base_model:Qwen/Qwen3.8-27B", "base_model:quantized:Qwen/Qwen3.8-27B"]));
        Assert.Null(ModelCard.BaseModelFromTags(["base_model:merge:org/A", "base_model:merge:org/B"]));
        Assert.Null(ModelCard.BaseModelFromTags(["gguf"]));
    }

    [Fact]
    public async Task Describing_follows_the_chain_back_to_the_original()
    {
        var cards = new Dictionary<string, string>
        {
            ["mradermacher/Huihui-Qwen3.5-9B-abliterated-GGUF"] = Card("mradermacher_Huihui-Qwen3.5-9B-abliterated-GGUF"),
            ["huihui-ai/Huihui-Qwen3.5-9B-abliterated"] = Card("huihui-ai_Huihui-Qwen3.5-9B-abliterated"),
            ["Qwen/Qwen3.5-9B"] = Card("Qwen_Qwen3.8-27B"),
        };
        var asked = new List<string>();
        var about = await ModelCard.DescribeAsync("mradermacher/Huihui-Qwen3.5-9B-abliterated-GGUF", (repo, _) =>
        {
            asked.Add(repo);
            return Task.FromResult(cards[repo]);
        });

        Assert.Equal(["huihui-ai/Huihui-Qwen3.5-9B-abliterated", "Qwen/Qwen3.5-9B"], about.Select(a => a.Repo));
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task A_missing_base_card_ends_the_walk_but_a_missing_first_card_is_an_error()
    {
        var about = await ModelCard.DescribeAsync("a/one", (repo, _) => repo == "a/one"
            ? Task.FromResult("---\nbase_model: b/two\n---\nThis model writes long, careful answers about history and science.")
            : throw new HttpRequestException("gone"));
        Assert.Single(about);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ModelCard.DescribeAsync("a/one", (_, _) => throw new HttpRequestException("offline")));
    }

    [Fact]
    public async Task A_card_pointing_at_itself_is_read_once()
    {
        var count = 0;
        await ModelCard.DescribeAsync("a/one", (_, _) => { count++; return Task.FromResult("---\nbase_model: a/one\n---\nfiles"); });
        Assert.Equal(1, count);
    }
}
