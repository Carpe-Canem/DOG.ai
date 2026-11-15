using DogAI.Core.Settings;

namespace DogAI.Core.Chat;

/// <summary>
/// How long a model that "thinks" may do so before it answers in a chat (M18; owner's decision, 2026-11-15): no limit
/// in General, where thinking helps questions, and Short in character and group chats, where a long think made replies
/// slow and could use up the whole context and answer nothing. Stories have their own level
/// (<see cref="Stories.StoryOptions"/>). Loaded at start.
/// </summary>
public sealed class ChatOptions(ISettingsStore? store = null)
{
    /// <summary>General chats.</summary>
    public ThinkingLevel General { get; private set; } = ThinkingLevels.GeneralStart;

    /// <summary>Character chats and group chats.</summary>
    public ThinkingLevel Roleplay { get; private set; } = ThinkingLevels.RoleplayStart;

    /// <summary>The levels' names and element ids on the Settings page (Chats and stories), for notes that point at them.</summary>
    public const string GeneralSwitch = "Thinking in General",
        RoleplaySwitch = "Thinking in character and group chats",
        StorySwitch = "Thinking in stories",
        GeneralAnchor = "set-general-thinking", RoleplayAnchor = "set-roleplay-thinking", StoryAnchor = "set-story-thinking";

    /// <summary>The name of the setting that decides thinking in a chat of <paramref name="mode"/>.</summary>
    public static string SwitchFor(string mode) => mode switch
    {
        "general" => GeneralSwitch,
        "story" => StorySwitch,
        _ => RoleplaySwitch,
    };

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (store is null) return;
        var settings = await store.LoadAsync(ct).ConfigureAwait(false);
        General = ThinkingLevels.General(settings);
        Roleplay = ThinkingLevels.Roleplay(settings);
        Changed?.Invoke();
    }

    public async Task SetGeneralAsync(ThinkingLevel level, CancellationToken ct = default)
    {
        if (level == General) return;
        General = level;
        if (store is not null) await store.SaveAsync(await store.LoadAsync(ct).ConfigureAwait(false) with { GeneralThinkingLevel = level }, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task SetRoleplayAsync(ThinkingLevel level, CancellationToken ct = default)
    {
        if (level == Roleplay) return;
        Roleplay = level;
        if (store is not null) await store.SaveAsync(await store.LoadAsync(ct).ConfigureAwait(false) with { RoleplayThinkingLevel = level }, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>The level for a chat of this mode. Stories are decided by their own level, not here.</summary>
    public ThinkingLevel LevelIn(string mode) => mode == "general" ? General : Roleplay;

    /// <summary><paramref name="preset"/> as a request in a chat of <paramref name="mode"/> uses it: told not to think, or how long it may.</summary>
    public SamplerPreset Apply(SamplerPreset preset, string mode) => ThinkingLevels.Apply(preset, LevelIn(mode));
}
