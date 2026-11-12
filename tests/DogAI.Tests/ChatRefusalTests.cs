using DogAI.Engine;
using Xunit;

namespace DogAI.Tests;

public class ChatRefusalTests
{
    [Fact]
    public void Search_model_named_embedding_is_refused_for_chat()
    {
        var info = new GgufInfo { Name = "Qwen3 Embedding 0.6b", Architecture = "qwen3" };
        var why = ModelLauncher.ChatRefusal(info, @"D:\Models\qwen3-embedding-0.6b.gguf");
        Assert.NotNull(why);
        Assert.Contains("Qwen3 Embedding 0.6b", why);
        Assert.Contains("document search", why);
    }

    [Fact]
    public void Model_that_pools_is_refused_even_with_a_plain_name()
    {
        var info = new GgufInfo { Name = "mystery", Architecture = "qwen3", PoolingType = 1 };
        Assert.NotNull(ModelLauncher.ChatRefusal(info, @"D:\Models\mystery.gguf"));
    }

    [Fact]
    public void Chat_model_is_not_refused()
    {
        var info = new GgufInfo { Name = "Qwen3 8B Instruct", Architecture = "qwen3" };
        Assert.Null(ModelLauncher.ChatRefusal(info, @"D:\Models\qwen3-8b-instruct.gguf"));
    }

    [Fact]
    public void Unreadable_header_is_left_to_the_engine()
    {
        Assert.Null(ModelLauncher.ChatRefusal(null, @"D:\Models\broken.gguf"));
    }
}
