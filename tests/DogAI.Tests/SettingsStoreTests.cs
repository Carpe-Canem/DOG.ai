using DogAI.Core;
using DogAI.Core.Settings;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public SettingsStoreTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
    }

    [Fact]
    public async Task Migrate_creates_the_database_and_settings_round_trip()
    {
        await _services.MigrateDogDatabaseAsync();
        var store = _services.GetRequiredService<ISettingsStore>();

        Assert.Equal(new AppSettings(), await store.LoadAsync());

        var saved = new AppSettings { ModelsDirectory = @"D:\DOG-AI\Models", Theme = "dark" };
        await store.SaveAsync(saved);
        await store.SaveAsync(saved with { Theme = "light" });

        Assert.Equal(saved with { Theme = "light" }, await store.LoadAsync());
    }

    [Fact]
    public async Task Migrate_is_safe_to_run_twice()
    {
        await _services.MigrateDogDatabaseAsync();
        await _services.MigrateDogDatabaseAsync();
    }

    [Fact]
    public void Paths_honour_an_explicit_root()
    {
        var paths = new AppPaths(_root);
        Assert.StartsWith(_root, paths.DatabaseFile);
        Assert.StartsWith(_root, paths.LogsDirectory);
        Assert.EndsWith(Path.Combine("DOG.ai", "Models"), paths.SuggestModelsDirectory());
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
