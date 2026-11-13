using DogAI.Core.Models;
using DogAI.Core.Net;

namespace DogAI.Tests;

public class NvidiaSmiTests
{
    [Fact]
    public void Reads_cards_in_mebibytes()
    {
        var gpus = NvidiaSmi.ParseGpus("NVIDIA GeForce RTX 4090, 24564, 892, 23247\r\n");
        var gpu = Assert.Single(gpus);
        Assert.Equal("NVIDIA GeForce RTX 4090", gpu.Name);
        Assert.Equal(24564L * 1024 * 1024, gpu.TotalBytes);
        Assert.Equal(23247L * 1024 * 1024, gpu.FreeBytes);
    }

    [Fact]
    public void Skips_lines_it_cannot_read() =>
        Assert.Empty(NvidiaSmi.ParseGpus("No devices were found\n[N/A], [N/A]\n"));

    [Fact]
    public void Sums_memory_per_process()
    {
        var apps = NvidiaSmi.ParseApps("1234, 8000\n1234, 1000\n99, 50\n");
        Assert.Equal(9000L * 1024 * 1024, apps[1234]);
        Assert.Equal(50L * 1024 * 1024, apps[99]);
    }

    [Theory]
    [InlineData("pid_2320_luid_0x00000000_0x01093ec1_phys_0", 2320)]
    [InlineData("pid_7_luid_0x00000000_0x0000d1a2_phys_1", 7)]
    [InlineData("_Total", null)]
    [InlineData("luid_0x00000000_0x01093ec1_phys_0", null)]
    public void Reads_the_process_id_from_a_gpu_counter_name(string instance, int? expected) =>
        Assert.Equal(expected, NvidiaSmi.CounterPid(instance));

    [Theory]
    [InlineData("| NVIDIA-SMI 621.14    KMD Version: 621.14     CUDA UMD Version: 13.5     |", "13.5")]
    [InlineData("| NVIDIA-SMI 560.94     Driver Version: 560.94     CUDA Version: 12.6     |", "12.6")]
    public void Finds_the_driver_cuda_version(string banner, string expected) =>
        Assert.Equal(Version.Parse(expected), NvidiaSmi.ParseCudaVersion(banner));
}

public class FitEstimatorTests
{
    private static readonly FitBudget Rtx4090 = new(23 * Sizes.GiB, 24 * Sizes.GiB, 1008, 60);

    private static ModelShape Model(double gib, int layers = 40, long kvPerToken = 160 * 1024) => new()
    {
        WeightBytes = (long)(gib * Sizes.GiB),
        Layers = layers,
        KvBytesPerToken = kvPerToken,
        TrainedContext = 131072,
    };

    [Fact]
    public void A_12B_model_at_Q4_fits_fully_and_is_fast()
    {
        var fit = FitEstimator.Estimate(Model(6.6), 16384, Rtx4090);
        Assert.Equal(FitKind.Full, fit.Kind);
        Assert.Equal(40, fit.GpuLayers);
        Assert.InRange(fit.TokensPerSecond, 60, 120);
    }

    [Fact]
    public void A_bigger_context_can_push_a_model_off_the_card()
    {
        Assert.Equal(FitKind.Full, FitEstimator.Estimate(Model(19), 8192, Rtx4090).Kind);
        Assert.Equal(FitKind.Partial, FitEstimator.Estimate(Model(19), 65536, Rtx4090).Kind);
    }

    [Fact]
    public void A_70B_model_runs_partly_on_the_card_with_a_layer_count()
    {
        var fit = FitEstimator.Estimate(Model(40, layers: 80, kvPerToken: 320 * 1024), 8192, Rtx4090);
        Assert.Equal(FitKind.Partial, fit.Kind);
        Assert.InRange(fit.GpuLayers, 30, 50);
        Assert.InRange(fit.TokensPerSecond, 1, 10);
    }

    [Fact]
    public void A_model_bigger_than_card_and_memory_together_is_too_large() =>
        Assert.Equal(FitKind.TooLarge, FitEstimator.Estimate(Model(71, layers: 96), 8192, Rtx4090).Kind);

    [Fact]
    public void Without_a_graphics_card_everything_runs_from_memory()
    {
        var fit = FitEstimator.Estimate(Model(4), 4096, new FitBudget(0, 24 * Sizes.GiB, 0, 60));
        Assert.Equal(FitKind.Partial, fit.Kind);
        Assert.Equal(0, fit.GpuLayers);
        Assert.True(double.IsFinite(fit.TokensPerSecond) && fit.TokensPerSecond > 0);
    }

    [Fact]
    public void Context_is_capped_at_what_the_model_was_trained_for()
    {
        var shape = Model(6) with { TrainedContext = 4096 };
        Assert.Equal(FitEstimator.Estimate(shape, 4096, Rtx4090).NeededBytes, FitEstimator.Estimate(shape, 131072, Rtx4090).NeededBytes);
    }

    [Fact]
    public void Mixture_of_experts_models_are_estimated_faster_than_dense_ones()
    {
        var dense = FitEstimator.Estimate(Model(18), 8192, Rtx4090);
        var moe = FitEstimator.Estimate(Model(18) with { ExpertCount = 128, ExpertsUsed = 8 }, 8192, Rtx4090);
        Assert.True(moe.TokensPerSecond > dense.TokensPerSecond * 2);
    }

    [Fact]
    public void Cache_size_follows_the_standard_formula() =>
        // 32 layers × 8 key/value heads × (128 + 128) × 2 bytes = 128 KiB per token.
        Assert.Equal(128 * 1024, ModelShape.KvPerToken(32, 8, 128, 128));

    [Fact]
    public void Budget_counts_memory_held_by_the_loaded_model_as_available()
    {
        var hw = new HardwareProfile
        {
            Gpus = [new GpuInfo("NVIDIA GeForce RTX 4090", 24 * Sizes.GiB, 20 * Sizes.GiB, 4 * Sizes.GiB)],
            RamFreeBytes = 20 * Sizes.GiB,
        };
        var budget = FitBudget.From(hw, engineVramBytes: 18 * Sizes.GiB);
        Assert.Equal(22 * Sizes.GiB - FitBudget.VramMargin, budget.VramBytes);
        Assert.Equal(1008, budget.GpuGBps);
    }
}

public class QuantizationTests
{
    [Theory]
    [InlineData("gemma-4-12b-it-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("gemma-4-12b-it-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("gemma-4-12b-it-BF16.gguf", "BF16")]
    [InlineData("gemma-4-12b-it-IQ4_XS.gguf", "IQ4_XS")]
    [InlineData("IQ2_XS/Qwen3.8-Flash-Next-GSQ-RCO-IQ2_XS-00001-of-00002.gguf", "IQ2_XS")]
    [InlineData("Qwen3-8B.Q8_0.gguf", "Q8_0")]
    [InlineData("model.gguf", null)]
    public void Reads_the_quantization_from_a_file_name(string path, string? expected) =>
        Assert.Equal(expected, Quantization.Parse(path));

    [Fact]
    public void Groups_split_parts_and_leaves_out_add_ons()
    {
        var variants = Quantization.Group(
        [
            new("README.md", 100, null),
            new("Q8_0/big-Q8_0-00002-of-00002.gguf", 5, "b"),
            new("Q8_0/big-Q8_0-00001-of-00002.gguf", 10, "a"),
            new("big-Q4_K_M.gguf", 8, "c"),
            new("mmproj-F16.gguf", 1, "d"),
            new("mtp-big.gguf", 1, "e"),
        ]);

        Assert.Equal(2, variants.Count);
        var split = variants.Single(v => v.Quant == "Q8_0");
        Assert.Equal(15, split.Size);
        Assert.EndsWith("00001-of-00002.gguf", split.Main.Path);
        Assert.Equal("big-Q8_0", split.Name);
    }

    private static (ModelVariant, FitResult) Option(string quant, long size, FitKind kind) =>
        (new ModelVariant(quant, quant, [new RemoteFile($"m-{quant}.gguf", size, null)]), new FitResult(kind, 0, 40, size, 10));

    [Fact]
    public void Picks_the_best_quality_that_fits_fully_up_to_8_bit()
    {
        var best = Quantization.PickBest(
        [
            Option("Q4_K_M", 7, FitKind.Full), Option("Q6_K", 10, FitKind.Full), Option("Q8_0", 13, FitKind.Full),
            Option("BF16", 24, FitKind.Partial),
        ]);
        Assert.Equal("Q8_0", best?.Quant);
    }

    [Fact]
    public void Picks_a_4_to_5_bit_file_when_nothing_fits_fully()
    {
        var best = Quantization.PickBest(
        [
            Option("Q3_K_M", 30, FitKind.Partial), Option("Q4_K_M", 40, FitKind.Partial), Option("Q8_0", 70, FitKind.TooLarge),
        ]);
        Assert.Equal("Q4_K_M", best?.Quant);
    }

    [Fact]
    public void Picks_nothing_when_nothing_can_run() =>
        Assert.Null(Quantization.PickBest([Option("Q4_K_M", 400, FitKind.TooLarge)]));
}

public class OnlineSessionTests
{
    [Fact]
    public void Going_online_only_works_inside_the_models_section()
    {
        var gate = new OnlineGate();
        var session = new OnlineSession(gate);

        session.SetWanted(true);
        Assert.False(gate.IsOnline);

        session.EnterModels();
        session.SetWanted(true);
        Assert.True(gate.IsOnline);

        session.LeaveModels();
        Assert.False(gate.IsOnline);
        Assert.False(session.Wanted);
    }

    [Fact]
    public void A_running_download_keeps_the_connection_until_it_ends()
    {
        var gate = new OnlineGate();
        var session = new OnlineSession(gate);
        session.EnterModels();
        session.SetWanted(true);

        var work = session.BeginWork();
        session.LeaveModels();
        Assert.True(gate.IsOnline);

        work.Dispose();
        Assert.False(gate.IsOnline);
        Assert.False(session.Wanted);
    }

    [Fact]
    public void Turning_it_off_closes_the_gate_even_during_a_download()
    {
        var gate = new OnlineGate();
        var session = new OnlineSession(gate);
        session.EnterModels();
        session.SetWanted(true);
        using var work = session.BeginWork();

        session.SetWanted(false);
        Assert.False(gate.IsOnline);
    }
}

public class CuratedPicksTests
{
    [Fact]
    public void The_shipped_list_loads_and_covers_every_use_case()
    {
        Assert.NotEmpty(CuratedPicks.All);
        foreach (var useCase in CuratedPicks.UseCases)
            Assert.Contains(CuratedPicks.All, p => p.UseCase == useCase);
        Assert.All(CuratedPicks.All, p => Assert.Contains('/', p.Repo));
        Assert.NotEqual("", CuratedPicks.Updated);
    }

    [Fact]
    public void Offline_estimates_pick_a_sensible_quant_for_a_24_GB_card()
    {
        var pick = CuratedPicks.All.First(p => p.Repo == "unsloth/Qwen3.8-27B-GGUF");
        var budget = new FitBudget(23 * Sizes.GiB, 24 * Sizes.GiB, 1008, 60);
        var best = Quantization.PickBest(pick.EstimatedVariants()
            .Select(v => (v, FitEstimator.Estimate(pick.EstimatedShape(v.Size), 8192, budget))));
        Assert.NotNull(best);
        Assert.InRange(best!.Bits, 4.25, 5.7);
    }
}

public class LoadDecisionTests
{
    private static readonly FitBudget Budget = new(22 * Sizes.GiB, 20 * Sizes.GiB, 1008, 60);

    [Fact]
    public void A_model_that_cannot_fit_is_refused_with_a_reason()
    {
        var plan = new FitResult(FitKind.TooLarge, 0, 96, 75 * Sizes.GiB, 0);
        var decision = LoadDecision.Decide("Colossus", 8192, plan, Budget, null);
        Assert.NotNull(decision.Refusal);
        Assert.Contains("too large", decision.Refusal);
        Assert.Contains("needs about 75", decision.Refusal);
        Assert.Contains("about 42", decision.Refusal);
    }

    [Fact]
    public void Partial_fits_load_with_the_estimated_layers_and_full_fits_with_all()
    {
        Assert.Equal(new LoadDecision(37, null), LoadDecision.Decide("m", 8192, new FitResult(FitKind.Partial, 37, 80, 1, 1), Budget, null));
        Assert.Equal(new LoadDecision(-1, null), LoadDecision.Decide("m", 8192, new FitResult(FitKind.Full, 40, 40, 1, 1), Budget, null));
    }

    [Fact]
    public void Without_an_estimate_the_model_is_loaded_anyway() =>
        Assert.Null(LoadDecision.Decide("m", 8192, null, null, null).Refusal);

    [Fact]
    public void Benchmarked_settings_for_the_same_context_win_over_the_estimate()
    {
        var record = new ModelRecord { Path = "x", TunedGpuLayers = 50, TunedContext = 8192 };
        var tooLarge = new FitResult(FitKind.TooLarge, 0, 80, 1, 0);
        Assert.Equal(new LoadDecision(50, null), LoadDecision.Decide("m", 8192, tooLarge, Budget, record));
        Assert.NotNull(LoadDecision.Decide("m", 32768, tooLarge, Budget, record).Refusal);
    }
}

public class ModelFilterTests
{
    [Theory]
    [InlineData("Orenguteng/Llama-3-8B-Lexi-Uncensored-GGUF", true)]
    [InlineData("mlabonne/gemma-3-12b-it-abliterated-GGUF", true)]
    [InlineData("unsloth/gemma-4-12b-it-GGUF", false)]
    public void Uncensored_is_recognised_from_the_name(string id, bool expected) =>
        Assert.Equal(expected, ModelFilters.IsUncensored(id, null));

    [Fact]
    public void Uncensored_is_recognised_from_tags() =>
        Assert.True(ModelFilters.IsUncensored("someone/plain-name", ["gguf", "Uncensored"]));

    [Theory]
    [InlineData("top", "top")]
    [InlineData("uncensored", "uncensored")]
    [InlineData("usable", "usable")]
    [InlineData("junk", "usable")]
    [InlineData(null, "usable")]
    public void Unknown_filters_fall_back_to_usable(string? saved, string expected) =>
        Assert.Equal(expected, ModelFilters.Normalize(saved));

    [Fact]
    public void Smallest_download_is_a_three_bit_version() =>
        Assert.InRange(ModelFilters.SmallestBytes(10_000_000_000), 4_800_000_000, 4_900_000_000);
}
