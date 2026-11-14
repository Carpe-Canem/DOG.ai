using DogAI.Core.Models;

namespace DogAI.Core.Settings;

/// <summary>
/// What the screens remember about their own layout: which collapsible sections are open or closed, and the Settings
/// category shown last. Kept as one JSON document in the settings store (key "interface"), apart from
/// <see cref="AppSettings"/>, so saving it never races a screen that saves settings from an older copy. It goes into
/// backups and the lock covers it, like the other documents.
/// </summary>
public sealed class InterfaceMemory(IDocumentStore? documents = null)
{
    public const string Key = "interface";

    /// <summary>Longest section id kept, and how many are kept at most (ids come from code, but a damaged document must not grow without end).</summary>
    public const int MaxIdLength = 64, MaxSections = 200;

    private InterfaceDocument _doc = new();

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        var doc = await documents.LoadAsync<InterfaceDocument>(Key, ct);
        _doc = new InterfaceDocument
        {
            Sections = (doc?.Sections ?? []).Where(kv => IsId(kv.Key)).Take(MaxSections)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            SettingsCategory = doc?.SettingsCategory,
        };
        Changed?.Invoke();
    }

    /// <summary>Whether a collapsible section is open: as the owner left it, else <paramref name="openByDefault"/>.</summary>
    public bool IsOpen(string id, bool openByDefault = true) =>
        _doc.Sections.TryGetValue(id, out var open) ? open : openByDefault;

    /// <summary>Remembers a section opened or closed. Back at its default, it is forgotten, so a better default in a later version reaches it.</summary>
    public async Task SetOpenAsync(string id, bool open, bool openByDefault = true, CancellationToken ct = default)
    {
        if (!IsId(id)) throw new ArgumentException("A section id is a short name made of letters, digits and dashes.", nameof(id));
        if (open == openByDefault) _doc.Sections.Remove(id);
        else
        {
            if (!_doc.Sections.ContainsKey(id) && _doc.Sections.Count >= MaxSections) return;
            _doc.Sections[id] = open;
        }
        await PersistAsync(ct);
    }

    /// <summary>The Settings category shown last, as saved (unchecked; <see cref="SettingsCatalog.Category"/> makes it a known one).</summary>
    public string? SettingsCategory => _doc.SettingsCategory;

    public async Task SetSettingsCategoryAsync(string category, CancellationToken ct = default)
    {
        if (!IsId(category) || _doc.SettingsCategory == category) return;
        _doc.SettingsCategory = category;
        await PersistAsync(ct);
    }

    /// <summary>Ids are short names in lower case letters, digits and dashes.</summary>
    public static bool IsId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private async Task PersistAsync(CancellationToken ct)
    {
        if (documents is not null) await documents.SaveAsync(Key, _doc, ct);
        Changed?.Invoke();
    }

    private sealed class InterfaceDocument
    {
        public Dictionary<string, bool> Sections { get; set; } = new(StringComparer.Ordinal);
        public string? SettingsCategory { get; set; }
    }
}
