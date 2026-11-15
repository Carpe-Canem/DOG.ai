using DogAI.Core.Chat;
using DogAI.Core.Settings;

namespace DogAI.Core.Stories;

/// <summary>
/// Story settings that apply to every story: for now, how long a model that "thinks" may do so when it writes for a
/// story (M18). Short by default (owner's decision, 2026-11-15): a passage starts within seconds, and the thinking
/// cannot use up the room meant for the story. Loaded from settings at start.
/// </summary>
public sealed class StoryOptions(ISettingsStore? store = null)
{
    public ThinkingLevel Level { get; private set; } = ThinkingLevels.StoryStart;

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (store is null) return;
        Level = ThinkingLevels.Story(await store.LoadAsync(ct).ConfigureAwait(false));
        Changed?.Invoke();
    }

    public async Task SetLevelAsync(ThinkingLevel level, CancellationToken ct = default)
    {
        if (level == Level) return;
        Level = level;
        if (store is not null) await store.SaveAsync(await store.LoadAsync(ct).ConfigureAwait(false) with { StoryThinkingLevel = level }, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary><paramref name="preset"/> as a story request uses it: told not to think, or how long it may.</summary>
    public SamplerPreset Apply(SamplerPreset preset) => ThinkingLevels.Apply(preset, Level);
}
