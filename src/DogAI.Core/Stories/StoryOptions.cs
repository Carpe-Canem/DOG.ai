using DogAI.Core.Chat;
using DogAI.Core.Settings;

namespace DogAI.Core.Stories;

/// <summary>
/// Story settings that apply to every story: for now, whether a model that "thinks" may do so when it writes for a
/// story. Off by default: a thinking model then answers straight away, so passages start at once and the thinking
/// cannot use up the room meant for the story. Loaded from settings at start.
/// </summary>
public sealed class StoryOptions(ISettingsStore? store = null)
{
    public bool Thinking { get; private set; }

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (store is null) return;
        Thinking = (await store.LoadAsync(ct).ConfigureAwait(false)).StoryThinking;
        Changed?.Invoke();
    }

    public async Task SetThinkingAsync(bool on, CancellationToken ct = default)
    {
        if (on == Thinking) return;
        Thinking = on;
        if (store is not null) await store.SaveAsync(await store.LoadAsync(ct).ConfigureAwait(false) with { StoryThinking = on }, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary><paramref name="preset"/> as a story request uses it: told not to think, unless thinking is allowed.</summary>
    public SamplerPreset Apply(SamplerPreset preset) => Thinking ? preset : preset with { Thinking = false };
}
