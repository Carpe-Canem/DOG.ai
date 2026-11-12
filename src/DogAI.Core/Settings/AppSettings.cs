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

    /// <summary>More folders whose .gguf files appear in the library, besides <see cref="ModelsDirectory"/>.</summary>
    public IReadOnlyList<string> ExtraModelFolders { get; init; } = [];

    /// <summary>The model each mode starts with, by mode id ("general", "characters", "story").</summary>
    public IReadOnlyDictionary<string, string> DefaultModels { get; init; } = new Dictionary<string, string>();

    /// <summary>Unload a model nobody has used for this many minutes, freeing graphics memory. 0 keeps it loaded.</summary>
    public int IdleUnloadMinutes { get; init; } = 30;

    /// <summary>Hugging Face access token for gated models, encrypted for this Windows user (DPAPI). Null when none.</summary>
    public string? HfTokenProtected { get; init; }

    /// <summary>Compares lists and dictionaries by content, so two loads of the same settings are equal.</summary>
    public bool Equals(AppSettings? other) =>
        other is not null &&
        ModelsDirectory == other.ModelsDirectory && Theme == other.Theme && Skin == other.Skin &&
        ContextLength == other.ContextLength && LastModelPath == other.LastModelPath &&
        IdleUnloadMinutes == other.IdleUnloadMinutes && HfTokenProtected == other.HfTokenProtected &&
        ExtraModelFolders.SequenceEqual(other.ExtraModelFolders) &&
        DefaultModels.Count == other.DefaultModels.Count &&
        DefaultModels.All(kv => other.DefaultModels.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode() => HashCode.Combine(ModelsDirectory, Theme, Skin, ContextLength, LastModelPath, IdleUnloadMinutes);
}

/// <summary>The few things the UI needs from Windows itself. Implemented by the WPF host.</summary>
public interface IDesktopShell
{
    /// <summary>Shows the Windows "open file" dialog for a model. Returns null if cancelled.</summary>
    Task<string?> PickModelFileAsync(string? startFolder);

    /// <summary>Shows the Windows folder picker. Returns null if cancelled.</summary>
    Task<string?> PickFolderAsync(string? startFolder);

    void CopyText(string text);

    /// <summary>Shows the "open file" dialog for a character card (PNG or JSON). Returns null if cancelled.</summary>
    Task<string?> PickCardFileAsync();

    /// <summary>Shows the "open file" dialog for a picture. Returns null if cancelled.</summary>
    Task<string?> PickImageFileAsync();

    /// <summary>Shows the "save file" dialog and writes the bytes. Returns the path written, or null if cancelled.</summary>
    /// <param name="filter">A Windows dialog filter such as "PNG picture (*.png)|*.png".</param>
    Task<string?> SaveFileAsync(string suggestedName, string filter, byte[] content);

    /// <summary>
    /// Turns any picture Windows can read into (full PNG, small square avatar PNG). The avatar is cropped
    /// around the top centre, where faces usually are on character art. Throws if the picture cannot be read.
    /// </summary>
    Task<(byte[] Png, byte[] Avatar)> PreparePictureAsync(byte[] picture);

    /// <summary>Cuts the given square out of a picture and returns it as a small avatar PNG.</summary>
    Task<byte[]> CropAvatarAsync(byte[] picture, Core.Characters.AvatarCrop crop);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
