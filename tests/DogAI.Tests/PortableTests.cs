using DogAI.Core;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Data;
using DogAI.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

public sealed class PortableTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dogai-portable-" + Guid.NewGuid().ToString("N"));

    public PortableTests() => Directory.CreateDirectory(_home);

    private AppPaths Portable()
    {
        File.WriteAllText(Path.Combine(_home, AppPaths.PortableMarker), "portable");
        return AppPaths.Resolve(_home, overrideRoot: null);
    }

    [Fact]
    public void Marker_next_to_the_program_keeps_everything_in_its_folder()
    {
        var paths = Portable();
        Assert.True(paths.IsPortable);
        Assert.Equal(_home, paths.PortableHome);
        Assert.Equal(Path.Combine(_home, "data"), paths.Root);
        foreach (var path in new[] { paths.DatabaseFile, paths.LogsDirectory, paths.WebViewDataDirectory, paths.EngineDirectory, paths.TempDirectory, paths.HomeRecordFile })
            Assert.StartsWith(paths.Root + Path.DirectorySeparatorChar, path);
        Assert.Equal(Path.Combine(_home, "models"), paths.PortableModelsDirectory);
        if (Environment.GetEnvironmentVariable(AppPaths.ModelsOverrideVariable) is null)
            Assert.Equal(paths.PortableModelsDirectory, paths.SuggestModelsDirectory());

        paths.EnsureCreated();
        Assert.True(Directory.Exists(Path.Combine(_home, "models")));
    }

    [Fact]
    public void Program_in_an_app_subfolder_keeps_its_data_in_the_parent()
    {
        File.WriteAllText(Path.Combine(_home, AppPaths.PortableMarker), "portable");
        var program = Path.Combine(_home, AppPaths.AppSubfolder);
        Directory.CreateDirectory(program);
        var paths = AppPaths.Resolve(program, overrideRoot: null);
        Assert.True(paths.IsPortable);
        Assert.Equal(_home, paths.PortableHome);
        Assert.Equal(Path.Combine(_home, "data"), paths.Root);
        Assert.Equal(Path.Combine(_home, "models"), paths.PortableModelsDirectory);
    }

    [Fact]
    public void A_marker_in_the_parent_of_some_other_folder_does_not_count()
    {
        File.WriteAllText(Path.Combine(_home, AppPaths.PortableMarker), "portable");
        var program = Path.Combine(_home, "bin");
        Directory.CreateDirectory(program);
        Assert.False(AppPaths.Resolve(program, overrideRoot: null).IsPortable);
    }

    [Fact]
    public void Without_the_marker_the_usual_folder_is_used()
    {
        var paths = AppPaths.Resolve(_home, overrideRoot: null);
        Assert.False(paths.IsPortable);
        Assert.Null(paths.PortableModelsDirectory);
        Assert.DoesNotContain(_home, paths.Root);
    }

    [Fact]
    public void A_test_data_folder_wins_over_the_marker()
    {
        Portable();
        var other = Path.Combine(_home, "elsewhere");
        var paths = AppPaths.Resolve(_home, other);
        Assert.False(paths.IsPortable);
        Assert.Equal(other, paths.Root);
    }

    [Fact]
    public void Portable_engine_is_only_the_one_in_its_folder()
    {
        if (Environment.GetEnvironmentVariable(LlamaServerLocator.OverrideVariable) is { Length: > 0 }) return;
        var paths = Portable();
        var locator = new LlamaServerLocator(paths);
        // Not winget's copy, not one on PATH, even when this PC has them.
        Assert.Null(locator.Find());
        Assert.Contains("Models › Engine", locator.MissingMessage);

        Directory.CreateDirectory(paths.EngineDirectory);
        var own = Path.Combine(paths.EngineDirectory, "llama-server.exe");
        File.WriteAllText(own, "");
        Assert.Equal(own, locator.Find());
    }

    [Fact]
    public async Task Moving_the_folder_carries_saved_paths_along()
    {
        var oldHome = Path.Combine(_home, "old place");
        var newHome = Path.Combine(_home, "new place");
        var data = Path.Combine(newHome, "data");
        var paths = new AppPaths(data);
        paths.EnsureCreated();
        using (var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider())
        {
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<ISettingsStore>();
            await store.SaveAsync(new AppSettings
            {
                ModelsDirectory = Path.Combine(oldHome, "models"),
                LastModelPath = Path.Combine(oldHome, "models", "qwen", "q.gguf"),
                ExtraModelFolders = [@"E:\other models"],
                DefaultModels = new Dictionary<string, string> { ["embedding"] = Path.Combine(oldHome, "models", "e.gguf") },
            });
            await services.GetRequiredService<IModelRecordStore>().SaveAsync(
                new ModelRecord { Path = Path.Combine(oldHome, "models", "qwen", "q.gguf"), Repo = "x/qwen", Quant = "Q4_K_M", UseCase = "general" });
        }
        SqliteConnection.ClearAllPools();

        // Recorded as the old place, now running from the new one.
        File.WriteAllText(paths.HomeRecordFile, oldHome);
        File.WriteAllText(Path.Combine(newHome, AppPaths.PortableMarker), "portable");
        var moved = AppPaths.Resolve(newHome, overrideRoot: null);
        Assert.NotNull(PortableMove.Apply(moved));
        Assert.Equal(newHome, File.ReadAllText(moved.HomeRecordFile));
        Assert.Null(PortableMove.Apply(moved)); // nothing more to do on the next start

        using (var services = new ServiceCollection().AddSingleton(moved).AddDogData().BuildServiceProvider())
        {
            var settings = await services.GetRequiredService<ISettingsStore>().LoadAsync();
            Assert.Equal(Path.Combine(newHome, "models"), settings.ModelsDirectory);
            Assert.Equal(Path.Combine(newHome, "models", "qwen", "q.gguf"), settings.LastModelPath);
            Assert.Equal(Path.Combine(newHome, "models", "e.gguf"), settings.DefaultModels["embedding"]);
            Assert.Equal([@"E:\other models"], settings.ExtraModelFolders);
            var record = Assert.Single(await services.GetRequiredService<IModelRecordStore>().AllAsync());
            Assert.Equal(Path.Combine(newHome, "models", "qwen", "q.gguf"), record.Path);
        }
    }

    [Fact]
    public void First_portable_start_just_records_where_it_is()
    {
        var paths = Portable();
        paths.EnsureCreated();
        Assert.Null(PortableMove.Apply(paths));
        Assert.Equal(_home, File.ReadAllText(paths.HomeRecordFile));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }
}
