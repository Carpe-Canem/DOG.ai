namespace DogAI.Core.Settings;

/// <summary>User-level settings. Add properties with defaults; older saved settings keep loading.</summary>
public sealed record AppSettings
{
    /// <summary>Folder that holds downloaded models. Null until first run picks a default.</summary>
    public string? ModelsDirectory { get; init; }

    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; init; } = "system";

    /// <summary>"workbench", "fireside" or "playhouse".</summary>
    public string Skin { get; init; } = "workbench";

    /// <summary>Context window to give a model when loading it, in tokens.</summary>
    public int ContextLength { get; init; } = 8192;

    /// <summary>The model file loaded most recently, offered again next time.</summary>
    public string? LastModelPath { get; init; }
}

/// <summary>The few things the UI needs from Windows itself. Implemented by the WPF host.</summary>
public interface IDesktopShell
{
    /// <summary>Shows the Windows "open file" dialog for a model. Returns null if cancelled.</summary>
    Task<string?> PickModelFileAsync(string? startFolder);

    void CopyText(string text);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
