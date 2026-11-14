using DogAI.Core.Images;

namespace DogAI.Tests;

/// <summary>
/// The picture engine's output lines reach DOG.ai's log only through <see cref="PictureLogFilter"/>. These tests feed it
/// lines in the shapes the real engine prints (copied from a run) and lines an engine could print if it ever echoed a
/// prompt, with marker words in the prompts.
/// </summary>
public class PictureLogFilterTests
{
    private const string Marker = "zqxmarkerword";
    private const string NegativeMarker = "vexnegativeword";

    private static readonly ImageRequest Request = new(
        $"masterpiece, best quality, 1girl, {Marker}, silver hair, looking at viewer",
        $"worst quality, lowres, {NegativeMarker}, text");

    /// <summary>What the engine prints for one picture at its normal level (build master-1094).</summary>
    private static readonly string[] StatusLines =
    [
        "[I] generate_image 832x1216 --- image.cpp:1320",
        "[I] conditioning cache hit --- conditioning_cache.h:88",
        "[I] get_learned_condition completed, taking 0.05s --- image.cpp:1402",
        "[I] sampling using Euler A method --- request.cpp:612",
        "[I] get_sigmas with discrete scheduler --- denoiser.hpp:402",
        "[I] generating image: 1/1 - seed 1234 --- image.cpp:1466",
        "[I] sampling completed, taking 5.21s --- image.cpp:1502",
        "[I] generating 1 latent images completed, taking 5.30s --- image.cpp:1510",
        "[I] decoding 1 latents --- image.cpp:1530",
        "[I] latent 1 decoded, taking 0.41s --- image.cpp:1544",
        "[I] decode_first_stage completed, taking 0.41s --- image.cpp:1550",
        "[I] generate_image completed in 5.92s --- image.cpp:1560",
        "[W] align hires target up 1248x1824 to 1280x1856 (multiple=64) --- request.cpp:540",
        "[I] (1680 / 1680) LoRA tensors have been applied, lora_file_path = D:\\Models\\image\\loras\\Incase.safetensors --- lora.hpp:812",
        "[E] generate_image failed --- image.cpp:1570",
        "ggml_backend_cuda_buffer_type_alloc_buffer: allocating 2048.00 MiB on device 0: cudaMalloc failed: out of memory",
        "CUDA error: out of memory",
    ];

    [Fact]
    public void Before_any_prompt_is_sent_every_line_passes()
    {
        var filter = new PictureLogFilter();
        Assert.True(filter.Allows("ggml_cuda_init: found 1 CUDA devices (Total VRAM: 24563 MiB):"));
        Assert.True(filter.Allows("  Device 0: NVIDIA GeForce RTX 4090, compute capability 8.9, VMM: yes, VRAM: 24563 MiB"));
        Assert.True(filter.Allows("[I] loading model from 'D:\\Models\\image\\checkpoints\\nova.safetensors' --- diffusion_engine.cpp:686"));
        Assert.True(filter.Allows("  |=====>                    | 3/20 - 5.01it/s"));
        Assert.True(filter.Allows("anything at all, even " + Marker));
        Assert.False(filter.Allows(""));
        Assert.False(filter.Allows(null));
        Assert.Equal(0, filter.TakeHeldBack());
    }

    [Fact]
    public void The_engines_status_lines_pass_after_a_prompt_was_sent()
    {
        var filter = new PictureLogFilter();
        filter.Sending(Request);
        Assert.All(StatusLines, line => Assert.True(filter.Allows(line), line));
        // A restart of the engine later in the run prints its start-up lines again.
        Assert.True(filter.Allows("ggml_cuda_init: found 1 CUDA devices (Total VRAM: 24563 MiB):"));
        Assert.True(filter.Allows("  Device 0: NVIDIA GeForce RTX 4090, compute capability 8.9, VMM: yes, VRAM: 24563 MiB"));
        Assert.True(filter.Allows("[I] loading model from 'D:\\Models\\image\\checkpoints\\nova.safetensors' --- diffusion_engine.cpp:686"));
        Assert.True(filter.Allows("[INFO ] stable-diffusion.cpp:2204 - sampling completed, taking 5.21s"));
        Assert.True(filter.Allows("[WARN ] stable-diffusion.cpp:100 - something"));
        Assert.True(filter.Allows("[ERROR] stable-diffusion.cpp:100 - something broke"));
        Assert.Equal(0, filter.TakeHeldBack());
    }

    [Theory]
    // The prompt as it was sent, or part of it, in a status line.
    [InlineData("[I] prompt: masterpiece, best quality, 1girl, zqxmarkerword, silver hair --- image.cpp:1300")]
    [InlineData("[I] txt2img zqxmarkerword --- image.cpp:1300")]
    [InlineData("[W] unknown word 'ZQXMARKERWORD' --- tokenizer.cpp:12")]
    [InlineData("[E] could not parse (zqxmarkerword:1.2) --- prompt.cpp:40")]
    // Inside a longer word, or cut short.
    [InlineData("[I] cache key=promptzqxmarkerwordend --- conditioning_cache.h:88")]
    [InlineData("[I] token zqxmark... --- tokenizer.cpp:12")]
    // The negative prompt counts as much as the prompt.
    [InlineData("[I] negative: vexnegativeword --- image.cpp:1301")]
    // Ordinary words of the prompt give a line away too, whole or inside another word.
    [InlineData("[I] silver --- image.cpp:1")]
    [InlineData("[I] encoding text_encoders --- image.cpp:1")]
    [InlineData("[I] viewers: 3 --- image.cpp:1")]
    // Short words (two or three letters) only as whole words.
    [InlineData("[I] job at step 3 --- image.cpp:1")]
    public void A_status_line_that_shares_a_word_with_a_prompt_is_held_back(string line)
    {
        var filter = new PictureLogFilter();
        filter.Sending(Request);
        Assert.False(filter.Allows(line), line);
        Assert.Equal(1, filter.TakeHeldBack());
        Assert.Equal(0, filter.TakeHeldBack());
    }

    [Theory]
    // Detail lines are where the engine prints prompts and tokens: dropped whatever they say.
    [InlineData("[D] prompt after extract and remove lora: \"something else entirely\" --- image.cpp:1290")]
    [InlineData("[DEBUG] stable-diffusion.cpp:1500 - parse 'x' to [['x', 1]]")]
    [InlineData("[T] trace --- x.cpp:1")]
    // Lines of no known kind: progress bars and bare text.
    [InlineData("  |=====>                                            |  3/20 -   5.01it/s")]
    [InlineData("something the engine never used to print")]
    [InlineData("zqxmarkerword")]
    [InlineData("\"prompt\": \"zqxmarkerword\"")]
    public void Detail_lines_and_unrecognised_lines_are_dropped_once_a_prompt_was_sent(string line)
    {
        var filter = new PictureLogFilter();
        filter.Sending(Request);
        Assert.False(filter.Allows(line), line);
        // Not counted: the count is for status lines that would otherwise have been logged.
        Assert.Equal(0, filter.TakeHeldBack());
    }

    [Fact]
    public void Short_words_do_not_hold_back_lines_that_only_contain_them_inside_other_words()
    {
        var filter = new PictureLogFilter();
        filter.Sending("at, a, 1, on", "");
        // "at" is inside "latent" and "taking"; "a" and "1" are single characters and never matched.
        Assert.True(filter.Allows("[I] latent 1 decoded, taking 0.41s --- image.cpp:1544"));
        Assert.True(filter.Allows("[I] sampling using Euler A method --- request.cpp:612"));
        Assert.False(filter.Allows("[I] sampling at 5 it/s --- image.cpp:1"));
    }

    [Fact]
    public void Words_in_languages_written_without_spaces_are_found_inside_longer_text()
    {
        var filter = new PictureLogFilter();
        filter.Sending("猫, 銀髪の少女", "");
        Assert.False(filter.Allows("[I] prompt 白い猫が走る --- image.cpp:1"));
        Assert.False(filter.Allows("[I] 銀髪の少女 --- image.cpp:1"));
        Assert.True(filter.Allows("[I] sampling completed, taking 5.21s --- image.cpp:1502"));
    }

    [Fact]
    public void Every_prompt_sent_counts_not_only_the_newest()
    {
        var filter = new PictureLogFilter();
        filter.Sending(Request);
        filter.Sending("a completely different picture of a heron", "");
        Assert.False(filter.Allows($"[I] still working on {Marker} --- image.cpp:1"));
        Assert.False(filter.Allows("[I] heron --- image.cpp:1"));
        Assert.Equal(2, filter.TakeHeldBack());
    }

    [Fact]
    public void A_prompts_words_are_forgotten_ten_minutes_after_it_was_last_sent()
    {
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var filter = new PictureLogFilter(() => now);
        var line = $"[I] {Marker} --- image.cpp:1";
        filter.Sending(Request);

        now += TimeSpan.FromMinutes(9);
        Assert.False(filter.Allows(line));
        // Sent again (a redraw): the ten minutes start over.
        filter.Sending(Request);
        now += TimeSpan.FromMinutes(9);
        Assert.False(filter.Allows(line));

        // The engine drops a finished picture after one minute, so by now it has nothing of this prompt left to print.
        now += TimeSpan.FromMinutes(2);
        Assert.True(filter.Allows(line));
        // Lines of no known kind stay out for the rest of the run all the same.
        Assert.False(filter.Allows("bare text"));
    }

    [Fact]
    public void A_whole_session_of_lines_with_echoed_prompts_lets_no_marker_through()
    {
        var filter = new PictureLogFilter();
        var passed = new List<string>();
        void Feed(string line) { if (filter.Allows(line)) passed.Add(line); }

        Feed("[I] loading model from 'D:\\Models\\image\\checkpoints\\nova.safetensors' --- diffusion_engine.cpp:686");
        filter.Sending(Request);
        foreach (var line in StatusLines) Feed(line);
        // Every way a careless engine might print what it was sent.
        foreach (var text in new[] { Request.Prompt, Request.NegativePrompt, Marker, NegativeMarker, Marker.ToUpperInvariant(), $"({Marker}:1.3)", $"<{Marker}>" })
        {
            foreach (var shape in new[] { "[I] {0} --- image.cpp:1", "[W] {0}", "[E] bad prompt: {0}", "[D] {0}", "[INFO ] sd.cpp:1 - {0}", "{0}", "  {0}", "prompt={0}", "ggml_debug: {0}", "CUDA {0}" })
                Feed(string.Format(shape, text));
        }
        foreach (var line in StatusLines) Feed(line);

        Assert.DoesNotContain(passed, line => line.Contains(Marker, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(passed, line => line.Contains(NegativeMarker, StringComparison.OrdinalIgnoreCase));
        // And the ordinary lines did get through, before and after.
        Assert.Equal(1 + 2 * StatusLines.Length, passed.Count);
    }
}
