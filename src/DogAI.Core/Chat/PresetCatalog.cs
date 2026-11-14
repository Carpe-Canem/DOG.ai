using DogAI.Core.Models;

namespace DogAI.Core.Chat;

/// <summary>
/// The built-in styles plus the ones the user made. User styles are saved as one JSON document.
/// Call <see cref="LoadAsync"/> once at startup; after that every lookup is synchronous.
/// </summary>
public sealed class PresetCatalog(IDocumentStore? documents = null)
{
    private const string Key = "sampler-presets";
    private const string UserPrefix = "user-";

    private List<SamplerPreset> _user = [];

    /// <summary>Raised after a style is added, changed or removed.</summary>
    public event Action? Changed;

    public IReadOnlyList<SamplerPreset> All => [.. SamplerPresets.All, .. _user];

    public IReadOnlyList<SamplerPreset> UserPresets => _user;

    public static bool IsBuiltIn(string id) => SamplerPresets.All.Any(p => p.Id == id);

    /// <summary>The style with this id, or the default one if it no longer exists.</summary>
    public SamplerPreset Get(string? id) =>
        _user.FirstOrDefault(p => p.Id == id) ?? SamplerPresets.Get(id);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        var saved = await documents.LoadAsync<List<SamplerPreset>>(Key, ct);
        _user = saved?.Where(p => p.Id.StartsWith(UserPrefix, StringComparison.Ordinal)).Select(Clamp).ToList() ?? [];
    }

    /// <summary>Adds a new style or replaces an existing user style. Built-in styles cannot be changed.</summary>
    public async Task<SamplerPreset> SaveAsync(SamplerPreset preset, CancellationToken ct = default)
    {
        if (IsBuiltIn(preset.Id)) throw new InvalidOperationException("Built-in styles cannot be changed. Make a copy instead.");
        var name = preset.Name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the style a name.");

        var id = preset.Id.StartsWith(UserPrefix, StringComparison.Ordinal) ? preset.Id : UserPrefix + Guid.NewGuid().ToString("N")[..8];
        var saved = Clamp(preset with { Id = id, Name = name, Description = preset.Description.Trim() });
        var index = _user.FindIndex(p => p.Id == id);
        if (index >= 0) _user[index] = saved;
        else _user.Add(saved);
        await PersistAsync(ct);
        return saved;
    }

    /// <summary>Removes a user style. Chats that used it fall back to the default style.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (_user.RemoveAll(p => p.Id == id) > 0) await PersistAsync(ct);
    }

    /// <summary>A copy of any style, ready to be edited and saved as a new one.</summary>
    public static SamplerPreset CopyOf(SamplerPreset source) =>
        source with { Id = "", Name = source.Name + " (copy)" };

    /// <summary>Keeps values inside the ranges the engine accepts.</summary>
    public static SamplerPreset Clamp(SamplerPreset p) => p with
    {
        Temperature = Math.Clamp(p.Temperature, 0, 5),
        TopP = Math.Clamp(p.TopP, 0, 1),
        TopK = Math.Clamp(p.TopK, 0, 1000),
        MinP = Math.Clamp(p.MinP, 0, 1),
        RepeatPenalty = Math.Clamp(p.RepeatPenalty, 0.5, 2),
        DryMultiplier = Math.Clamp(p.DryMultiplier, 0, 5),
        XtcProbability = Math.Clamp(p.XtcProbability, 0, 1),
        XtcThreshold = Math.Clamp(p.XtcThreshold, 0, 0.5),
    };

    private async Task PersistAsync(CancellationToken ct)
    {
        if (documents is not null) await documents.SaveAsync(Key, _user, ct);
        Changed?.Invoke();
    }
}
