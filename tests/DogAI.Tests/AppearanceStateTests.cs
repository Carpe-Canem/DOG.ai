using DogAI.Core.Appearance;
using DogAI.Core.Settings;

namespace DogAI.Tests;

public class AppearanceStateTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Value { get; set; } = new();
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class FakeSystemTheme : ISystemTheme
    {
        public bool IsDark { get; set; } = true;
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
    }

    [Fact]
    public async Task Defaults_are_workbench_and_follow_windows()
    {
        var system = new FakeSystemTheme();
        var state = new AppearanceState(new MemoryStore(), system);
        await state.LoadAsync();

        Assert.Equal(Skins.Workbench, state.Skin);
        Assert.Equal(Themes.System, state.Theme);
        Assert.Equal(Themes.Dark, state.EffectiveTheme);

        system.IsDark = false;
        Assert.Equal(Themes.Light, state.EffectiveTheme);
    }

    [Fact]
    public async Task Choices_are_saved_and_announced()
    {
        var store = new MemoryStore { Value = new AppSettings { ModelsDirectory = @"D:\m" } };
        var state = new AppearanceState(store, new FakeSystemTheme());
        var changes = 0;
        state.Changed += () => changes++;

        await state.SetSkinAsync(Skins.Playhouse);
        await state.SetThemeAsync(Themes.Light);
        await state.SetThemeAsync(Themes.Light);

        Assert.Equal(2, changes);
        Assert.Equal(new AppSettings { ModelsDirectory = @"D:\m", Skin = Skins.Playhouse, Theme = Themes.Light }, store.Value);
        Assert.Equal(Themes.Light, state.EffectiveTheme);
    }

    [Fact]
    public async Task Unknown_saved_values_fall_back_to_defaults()
    {
        var store = new MemoryStore { Value = new AppSettings { Skin = "console", Theme = "neon" } };
        var state = new AppearanceState(store, new FakeSystemTheme());
        await state.LoadAsync();

        Assert.Equal(Skins.Workbench, state.Skin);
        Assert.Equal(Themes.System, state.Theme);
    }

    [Fact]
    public async Task Windows_theme_change_only_matters_when_following_windows()
    {
        var system = new FakeSystemTheme();
        var state = new AppearanceState(new MemoryStore(), system);
        var changes = 0;
        state.Changed += () => changes++;

        system.Raise();
        Assert.Equal(1, changes);

        await state.SetThemeAsync(Themes.Dark);
        system.Raise();
        Assert.Equal(2, changes);
    }
}
