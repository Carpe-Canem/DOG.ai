using DogAI.Core.Settings;

namespace DogAI.Core.Models;

/// <summary>
/// The default model per job (<see cref="AppSettings.DefaultModels"/>): the chat model each mode loads when it opens,
/// and the document search model. Pictures are not here: their checkpoint is <c>ImageModelCatalog.Chosen</c>.
/// </summary>
public static class ModelDefaults
{
    /// <summary>The key of the document search model (the same as <c>EmbeddingServer.UseCase</c>).</summary>
    public const string DocumentSearch = "embedding";

    /// <summary>The chat modes that have a default model, in the order they are shown.</summary>
    public static IReadOnlyList<string> ChatModes { get; } = ["general", "characters", "story"];

    public static bool IsKnown(string mode) => mode == DocumentSearch || ChatModes.Contains(mode);

    /// <summary>What setting or clearing a default did.</summary>
    /// <param name="Settings">The settings to save.</param>
    /// <param name="Changed">False when the default was already this (nothing to save).</param>
    /// <param name="SearchChanged">The document search model changed: documents are indexed again with the new one.</param>
    public sealed record Outcome(AppSettings Settings, bool Changed, bool SearchChanged);

    /// <summary>Makes <paramref name="path"/> the default for <paramref name="mode"/>, or clears it when the path is null or empty.</summary>
    public static Outcome Set(AppSettings settings, string mode, string? path)
    {
        if (!IsKnown(mode)) throw new ArgumentException($"\"{mode}\" is not a job with a default model.", nameof(mode));
        var current = For(settings, mode);
        if (string.IsNullOrEmpty(path))
        {
            if (current is null) return new(settings, false, false);
            var cleared = settings.DefaultModels.Where(kv => kv.Key != mode).ToDictionary();
            return new(settings with { DefaultModels = cleared }, true, mode == DocumentSearch);
        }
        if (Same(current, path)) return new(settings, false, false);
        var defaults = new Dictionary<string, string>(settings.DefaultModels) { [mode] = path };
        return new(settings with { DefaultModels = defaults }, true, mode == DocumentSearch);
    }

    /// <summary>The default for a mode, or null.</summary>
    public static string? For(AppSettings settings, string mode) =>
        settings.DefaultModels.TryGetValue(mode, out var path) && !string.IsNullOrEmpty(path) ? path : null;

    /// <summary>The jobs a model file is the default for, in the order they are shown.</summary>
    public static IReadOnlyList<string> JobsOf(AppSettings settings, string path) =>
        ChatModes.Append(DocumentSearch).Where(m => Same(For(settings, m), path))
            .Concat(settings.DefaultModels.Keys.Where(k => !IsKnown(k) && Same(settings.DefaultModels[k], path)))
            .ToList();

    /// <summary>A deleted model is no longer anyone's default. <c>SearchChanged</c> when it was the document search model.</summary>
    public static Outcome Without(AppSettings settings, string path)
    {
        if (!settings.DefaultModels.Values.Any(v => Same(v, path))) return new(settings, false, false);
        var search = Same(For(settings, DocumentSearch), path);
        var kept = settings.DefaultModels.Where(kv => !Same(kv.Value, path)).ToDictionary();
        return new(settings with { DefaultModels = kept }, true, search);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
