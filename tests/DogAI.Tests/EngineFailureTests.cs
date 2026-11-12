using DogAI.Engine;
using Xunit;

namespace DogAI.Tests;

public class EngineFailureTests
{
    private const string Lead = "The model engine stopped while loading the model.";

    [Fact]
    public void Model_newer_than_the_engine_says_to_update_it()
    {
        // What llama-server b7224 printed for a Qwen3.8 file.
        string[] lines =
        [
            "llama_model_loader: - kv   0:                       general.architecture str              = qwen35",
            "llama_model_load: error loading model: error loading model architecture: unknown model architecture: 'qwen35'",
            "llama_model_load_from_file_impl: failed to load model",
            "srv    load_model: failed to load model, 'D:\\Models\\Qwen3.8-27B-UD-Q4_K_M.gguf'",
            "main: exiting due to model loading error",
        ];
        var message = EngineFailure.Explain(Lead, lines);
        Assert.StartsWith(Lead, message);
        Assert.Contains("\"qwen35\"", message);
        Assert.Contains("Models → Engine", message);
    }

    [Fact]
    public void Unknown_tokenizer_also_says_to_update()
    {
        string[] lines = ["llama_model_load: error loading model: error loading model vocabulary: unknown pre-tokenizer type: 'newtok'"];
        Assert.Contains("Models → Engine", EngineFailure.Explain(Lead, lines));
    }

    [Fact]
    public void Other_failures_quote_the_first_error_not_the_last()
    {
        string[] lines =
        [
            "llama_model_load: error loading model: tensor 'blk.0.attn_q.weight' data is not within the file bounds",
            "main: exiting due to model loading error",
        ];
        var message = EngineFailure.Explain(Lead, lines);
        Assert.Equal($"{Lead} It said: error loading model: tensor 'blk.0.attn_q.weight' data is not within the file bounds", message);
    }

    [Fact]
    public void No_error_lines_gives_the_lead_alone()
    {
        Assert.Equal(Lead, EngineFailure.Explain(Lead, ["build: 7224", "system info: n_threads = 8"]));
    }
}
