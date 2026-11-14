using DogAI.Core.Settings;

namespace DogAI.Core.Appearance;

public sealed record SkinInfo(string Id, string Name, string Description);

/// <summary>The interchangeable looks. Each is a layout shell plus a set of colour and type tokens.</summary>
public static class Skins
{
    public const string Workbench = "workbench";
    public const string Fireside = "fireside";
    public const string Playhouse = "playhouse";
    public const string Default = Workbench;

    public static IReadOnlyList<SkinInfo> All { get; } =
    [
        new(Workbench, "Workbench", "Icon rail and dense panes with one amber accent. Built for getting things done."),
        new(Fireside, "Fireside", "Top tabs, warm paper tones and a serif reading column. Calm and bookish."),
        new(Playhouse, "Playhouse", "A home screen, a colour for each mode and big rounded cards. The most playful."),
    ];

    public static string Normalize(string? id) => All.Any(s => s.Id == id) ? id! : Default;
}

public static class Themes
{
    public const string System = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    public static string Normalize(string? id) => id is Dark or Light ? id : System;
}

/// <summary>Reports whether Windows is currently set to dark mode for apps.</summary>
public interface ISystemTheme
{
    bool IsDark { get; }
    event Action? Changed;
}

/// <summary>The current skin and theme. UI subscribes to <see cref="Changed"/> and re-renders.</summary>
public sealed class AppearanceState
{
    private readonly ISettingsStore _store;
    private readonly ISystemTheme _system;

    public AppearanceState(ISettingsStore store, ISystemTheme system)
    {
        _store = store;
        _system = system;
        _system.Changed += () =>
        {
            if (Theme == Themes.System) Changed?.Invoke();
        };
    }

    public string Skin { get; private set; } = Skins.Default;

    /// <summary>What the user chose: system, dark or light.</summary>
    public string Theme { get; private set; } = Themes.System;

    /// <summary>What is actually shown: dark or light.</summary>
    public string EffectiveTheme => Theme == Themes.System ? (_system.IsDark ? Themes.Dark : Themes.Light) : Theme;

    /// <summary>Whether spoken lines and actions are coloured in character chats, group chats and stories.</summary>
    public bool SpeechColours { get; private set; } = true;

    public event Action? Changed;

    public async Task SetSpeechColoursAsync(bool on, CancellationToken ct = default)
    {
        if (on == SpeechColours) return;
        SpeechColours = on;
        Changed?.Invoke();
        await _store.SaveAsync(await _store.LoadAsync(ct) with { SpeechColours = on }, ct);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await _store.LoadAsync(ct);
        SpeechColours = settings.SpeechColours;
        Skin = Skins.Normalize(settings.Skin);
        Theme = Themes.Normalize(settings.Theme);
        Changed?.Invoke();
    }

    public async Task SetSkinAsync(string skin, CancellationToken ct = default)
    {
        skin = Skins.Normalize(skin);
        if (skin == Skin) return;
        Skin = skin;
        Changed?.Invoke();
        await _store.SaveAsync(await _store.LoadAsync(ct) with { Skin = skin }, ct);
    }

    public async Task SetThemeAsync(string theme, CancellationToken ct = default)
    {
        theme = Themes.Normalize(theme);
        if (theme == Theme) return;
        Theme = theme;
        Changed?.Invoke();
        await _store.SaveAsync(await _store.LoadAsync(ct) with { Theme = theme }, ct);
    }
}
