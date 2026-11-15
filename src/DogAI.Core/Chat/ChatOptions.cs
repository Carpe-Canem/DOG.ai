using DogAI.Core.Settings;

namespace DogAI.Core.Chat;

/// <summary>
/// Whether a model that "thinks" may do so before it answers in a chat (owner's decision, 2026-11-15): on in General,
/// where thinking helps questions, and off in character and group chats, where a thinking model could use up the whole
/// context and answer nothing. Stories have their own switch (<see cref="Stories.StoryOptions"/>). Loaded at start.
/// </summary>
public sealed class ChatOptions(ISettingsStore? store = null)
{
    /// <summary>General chats. On unless the owner turned it off.</summary>
    public bool GeneralThinking { get; private set; } = true;

    /// <summary>Character chats and group chats. Off unless the owner turned it on.</summary>
    public bool RoleplayThinking { get; private set; }

    /// <summary>The switches' names and element ids on the Settings page (Chats and stories), for notes that point at them.</summary>
    public const string GeneralSwitch = "Let the model think in General",
        RoleplaySwitch = "Let the model think in character and group chats",
        StorySwitch = "Let the model think in stories",
        GeneralAnchor = "set-general-thinking", RoleplayAnchor = "set-roleplay-thinking", StoryAnchor = "set-story-thinking";

    /// <summary>The name of the switch that decides thinking in a chat of <paramref name="mode"/>.</summary>
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
        GeneralThinking = settings.GeneralThinking;
        RoleplayThinking = settings.RoleplayThinking;
        Changed?.Invoke();
    }

    public async Task SetGeneralThinkingAsync(bool on, CancellationToken ct = default)
    {
        if (on == GeneralThinking) return;
        GeneralThinking = on;
        if (store is not null) await store.SaveAsync(await store.LoadAsync(ct).ConfigureAwait(false) with { GeneralThinking = on }, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task SetRoleplayThinkingAsync(bool on, CancellationToken ct = default)
    {
        if (on == RoleplayThinking) return;
        RoleplayThinking = on;
        if (store is not null) await store.SaveAsync(await store.LoadAsync(ct).ConfigureAwait(false) with { RoleplayThinking = on }, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Whether a chat of this mode may think. Stories are decided by their own switch, not here.</summary>
    public bool ThinksIn(string mode) => mode == "general" ? GeneralThinking : RoleplayThinking;

    /// <summary><paramref name="preset"/> as a request in a chat of <paramref name="mode"/> uses it: told not to think, unless allowed.</summary>
    public SamplerPreset Apply(SamplerPreset preset, string mode) => ThinksIn(mode) ? preset : preset with { Thinking = false };
}
