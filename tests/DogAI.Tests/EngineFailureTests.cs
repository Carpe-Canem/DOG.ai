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

    [Theory]
    [InlineData(0xC0000135, "could not find a file")] // a library is missing
    [InlineData(0xC0000139, "does not fit the others")] // a library of the wrong version
    [InlineData(0xC000007B, "does not fit the others")] // a damaged or 32-bit library
    public void A_program_Windows_could_not_start_is_explained_from_its_exit_code(uint code, string expected)
    {
        Assert.Contains(expected, EngineFailure.StartProblem(unchecked((int)code)));
        var message = DogAI.Engine.Images.ImageServer.StartFailure(unchecked((int)code));
        Assert.StartsWith("The picture engine could not start: ", message);
        Assert.Contains("Install it again in Models › Images", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(unchecked((int)0xC0000005))] // a crash: the program's own lines say more
    public void Other_exit_codes_are_left_to_the_programs_own_lines(int code)
    {
        Assert.Null(EngineFailure.StartProblem(code));
        Assert.Null(DogAI.Engine.Images.ImageServer.StartFailure(code));
    }
}
