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

    /// <summary>Colour spoken lines and actions in character chats, group chats and stories.</summary>
    public bool SpeechColours { get; init; } = true;

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

    /// <summary>Speech recognition model: a file name in the voice folder. Null uses the first one found.</summary>
    public string? SpeechModel { get; init; }

    /// <summary>Language spoken to DOG.ai, as a code ("en", "de"…), or "auto" to let the model tell.</summary>
    public string SpeechLanguage { get; init; } = "auto";

    /// <summary>Microphone by its Windows name. Null uses the Windows default.</summary>
    public string? Microphone { get; init; }

    /// <summary>Speakers or headphones by their Windows name. Null uses the Windows default.</summary>
    public string? Speakers { get; init; }

    /// <summary>The voice used when a character or story has none of its own (a <see cref="DogAI.Core.Voice.VoiceId"/> as text). Null picks one.</summary>
    public string? Voice { get; init; }

    /// <summary>How fast voices speak: 1 is normal, 0.5 to 2.</summary>
    public double VoiceSpeed { get; init; } = 1.0;

    /// <summary>Read every new reply aloud as it is written.</summary>
    public bool ReadRepliesAloud { get; init; }

    /// <summary>Hands-free listening: 0 needs clear, loud speech; 1 normal; 2 picks up quiet voices.</summary>
    public int HandsFreeSensitivity { get; init; } = 1;

    /// <summary>Show DOG.ai's icon in the Windows notification area (tray), with Open and Quit.</summary>
    public bool TrayIcon { get; init; }

    /// <summary>Closing the window leaves DOG.ai running in the tray instead of quitting. Needs <see cref="TrayIcon"/>.</summary>
    public bool CloseToTray { get; init; }

    /// <summary>Hold Ctrl+Alt+Space in any program to talk, and what you say is typed there when you let go.</summary>
    public bool DictateAnywhere { get; init; }

    /// <summary>Let a model that "thinks" do so before story passages, choices, rewrites and story bible drafts. Off by
    /// default: thinking made stories slow and could use up all the room before any story was written.</summary>
    public bool StoryThinking { get; init; }

    /// <summary>Models lists also show models too large for this PC. Off by default.</summary>
    public bool ModelsShowAll { get; init; }

    /// <summary>Which models to list: "usable", "top" or "uncensored" (see <see cref="DogAI.Core.Models.ModelFilters"/>).</summary>
    public string ModelsFilter { get; init; } = "usable";

    /// <summary>Compares lists and dictionaries by content, so two loads of the same settings are equal.</summary>
    public bool Equals(AppSettings? other) =>
        other is not null &&
        ModelsDirectory == other.ModelsDirectory && Theme == other.Theme && Skin == other.Skin &&
        ContextLength == other.ContextLength && LastModelPath == other.LastModelPath &&
        IdleUnloadMinutes == other.IdleUnloadMinutes && HfTokenProtected == other.HfTokenProtected &&
        SpeechModel == other.SpeechModel && SpeechLanguage == other.SpeechLanguage && Microphone == other.Microphone &&
        Speakers == other.Speakers && Voice == other.Voice && VoiceSpeed.Equals(other.VoiceSpeed) &&
        ReadRepliesAloud == other.ReadRepliesAloud && HandsFreeSensitivity == other.HandsFreeSensitivity &&
        TrayIcon == other.TrayIcon && CloseToTray == other.CloseToTray && DictateAnywhere == other.DictateAnywhere &&
        ModelsShowAll == other.ModelsShowAll && ModelsFilter == other.ModelsFilter && StoryThinking == other.StoryThinking &&
        ExtraModelFolders.SequenceEqual(other.ExtraModelFolders) &&
        DefaultModels.Count == other.DefaultModels.Count &&
        DefaultModels.All(kv => other.DefaultModels.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(ModelsDirectory, Theme, Skin, ContextLength, LastModelPath, IdleUnloadMinutes),
        HashCode.Combine(SpeechModel, SpeechLanguage, Microphone, Speakers, Voice, VoiceSpeed, ReadRepliesAloud, HandsFreeSensitivity),
        HashCode.Combine(TrayIcon, CloseToTray, DictateAnywhere, ModelsShowAll, ModelsFilter, StoryThinking));

    /// <summary>Where voice models are kept: a "voice" folder inside the models folder.</summary>
    public string? VoiceFolder() => ModelsDirectory is null ? null : Path.Combine(ModelsDirectory, "voice");
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

    /// <summary>Shows the "open file" dialog for documents (several may be picked). Empty if cancelled.</summary>
    Task<IReadOnlyList<string>> PickDocumentFilesAsync();

    /// <summary>Opens a file on this PC in the program Windows uses for it. False if it is not there any more.</summary>
    bool OpenFile(string path);

    /// <summary>Shows the "save file" dialog and returns the chosen path without writing anything (for files too big to hold in memory). Null if cancelled.</summary>
    Task<string?> PickSavePathAsync(string suggestedName, string filter);

    /// <summary>Shows the "open file" dialog for one file of the kind the filter describes. Null if cancelled.</summary>
    Task<string?> PickOpenPathAsync(string title, string filter);

    /// <summary>Opens a web address in the user's own browser. Only http and https; false for anything else.</summary>
    bool OpenUrl(string url);

    /// <summary>Closes DOG.ai and starts it again (used to finish a restore).</summary>
    void Restart();
}

/// <summary>The parts of DOG.ai that live in Windows itself: the tray icon, closing to the tray, and the key that works anywhere.</summary>
public interface IWindowsIntegration
{
    /// <summary>Reads the saved settings and turns each part on or off. Call after changing them.</summary>
    Task ApplyAsync();

    /// <summary>Empty when everything asked for is working; otherwise what could not be turned on.</summary>
    string? Problem { get; }
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
