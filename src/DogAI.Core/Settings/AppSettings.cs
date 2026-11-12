namespace DogAI.Core.Settings;

/// <summary>User-level settings. Add properties with defaults; older saved settings keep loading.</summary>
public sealed record AppSettings
{
    /// <summary>Folder that holds downloaded models. Null until first run picks a default.</summary>
    public string? ModelsDirectory { get; init; }

    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; init; } = "system";
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
