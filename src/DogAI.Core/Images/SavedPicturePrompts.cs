using DogAI.Core.Models;

namespace DogAI.Core.Images;

/// <summary>A prompt and negative prompt the owner saved under a name, so common ones are not typed again.</summary>
public sealed record SavedPicturePrompt(string Id, string Name, string Prompt, string Negative);

/// <summary>
/// The Images section's saved prompts (like automatic1111's styles). One JSON document in the settings store (key
/// "picture-prompts"): inside the database, under the lock, in backups. A prompt is here only because the owner pressed
/// Save; nothing typed in the boxes gets here by itself. One of them can be marked "Start with this", and the boxes
/// then start filled with it; with none marked they start with the chosen checkpoint's own quality words and negative
/// prompt (<see cref="StartingBoxes"/>).
/// Call <see cref="LoadAsync"/> once at startup; after that every lookup is synchronous.
/// </summary>
public sealed class SavedPicturePrompts(IDocumentStore? documents = null)
{
    public const string Key = "picture-prompts";

    public const int MaxNameLength = 80, MaxTextLength = 20_000, MaxCount = 500;

    private List<SavedPicturePrompt> _prompts = [];
    private string? _start;

    public event Action? Changed;

    /// <summary>The saved prompts in alphabetical order.</summary>
    public IReadOnlyList<SavedPicturePrompt> All => _prompts;

    /// <summary>The one marked "Start with this", or null.</summary>
    public SavedPicturePrompt? Start => _start is null ? null : _prompts.FirstOrDefault(p => p.Id == _start);

    public SavedPicturePrompt? Find(string? id) => _prompts.FirstOrDefault(p => p.Id == id);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        var doc = await documents.LoadAsync<Document>(Key, ct);
        _prompts = Sorted((doc?.Prompts ?? [])
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && Problem(p.Name, p.Prompt, p.Negative) is null)
            .Select(p => p with { Name = p.Name.Trim(), Prompt = p.Prompt ?? "", Negative = p.Negative ?? "" })
            .DistinctBy(p => p.Id)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCount));
        _start = _prompts.Any(p => p.Id == doc?.Start) ? doc!.Start : null;
        Changed?.Invoke();
    }

    /// <summary>What stops these from being saved, in plain words, or null.</summary>
    public static string? Problem(string? name, string? prompt, string? negative)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the saved prompt a name.";
        if (name.Trim().Length > MaxNameLength) return $"A name can be at most {MaxNameLength} characters.";
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(negative)) return "There is nothing to save: both boxes are empty.";
        if ((prompt?.Length ?? 0) > MaxTextLength || (negative?.Length ?? 0) > MaxTextLength) return "That is too long to save.";
        return null;
    }

    /// <summary>
    /// "Save these as…": stores what is in the boxes under a name, exactly as typed. A name already in use (capitals
    /// aside) is replaced and keeps its "Start with this" mark. Throws <see cref="InvalidOperationException"/> with
    /// <see cref="Problem"/> when it cannot be saved.
    /// </summary>
    public async Task<SavedPicturePrompt> SaveAsAsync(string name, string prompt, string negative, CancellationToken ct = default)
    {
        if (Problem(name, prompt, negative) is { } problem) throw new InvalidOperationException(problem);
        name = name.Trim();
        var index = _prompts.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && _prompts.Count >= MaxCount)
            throw new InvalidOperationException($"There are already {MaxCount} saved prompts. Delete one first.");
        var saved = new SavedPicturePrompt(index >= 0 ? _prompts[index].Id : NewId(), name, prompt ?? "", negative ?? "");
        if (index >= 0) _prompts[index] = saved;
        else _prompts.Add(saved);
        await PersistAsync(ct);
        return saved;
    }

    /// <summary>
    /// Changes a saved prompt in place (its name too). Throws <see cref="InvalidOperationException"/> when it is gone,
    /// cannot be saved, or would take another saved prompt's name.
    /// </summary>
    public async Task<SavedPicturePrompt> UpdateAsync(SavedPicturePrompt changed, CancellationToken ct = default)
    {
        if (Problem(changed.Name, changed.Prompt, changed.Negative) is { } problem) throw new InvalidOperationException(problem);
        var index = _prompts.FindIndex(p => p.Id == changed.Id);
        if (index < 0) throw new InvalidOperationException("That saved prompt is gone.");
        var name = changed.Name.Trim();
        if (_prompts.Any(p => p.Id != changed.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Another saved prompt is already called \"{name}\".");
        var saved = changed with { Name = name };
        _prompts[index] = saved;
        await PersistAsync(ct);
        return saved;
    }

    /// <summary>Deletes a saved prompt. One that was marked "Start with this" takes the mark with it.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (_prompts.RemoveAll(p => p.Id == id) == 0) return;
        if (_start == id) _start = null;
        await PersistAsync(ct);
    }

    /// <summary>Marks one saved prompt "Start with this", or none (null). Only one is marked at a time.</summary>
    public async Task SetStartAsync(string? id, CancellationToken ct = default)
    {
        if (id is not null && Find(id) is null) throw new InvalidOperationException("That saved prompt is gone.");
        if (_start == id) return;
        _start = id;
        await PersistAsync(ct);
    }

    /// <summary>
    /// What the two boxes start with when the Images section opens after a start of DOG.ai: the saved prompt marked
    /// "Start with this"; with none marked, the checkpoint's own quality words and negative prompt from its settings
    /// card, as text the owner sees and can change; with no checkpoint either, nothing.
    /// </summary>
    public (string Prompt, string Negative) StartingBoxes(CheckpointSettings? card) =>
        Start is { } start ? (start.Prompt, start.Negative) : FromCard(card);

    /// <summary>A checkpoint's quality words (before, then after) and its negative prompt, as text for the boxes.</summary>
    public static (string Prompt, string Negative) FromCard(CheckpointSettings? card) => card is null ? ("", "") :
        (string.Join(", ", new[] { card.QualityBefore, card.QualityAfter }.Select(Piece).Where(p => p.Length > 0)), card.Negative.Trim());

    /// <summary>Replace: the saved prompt takes the place of what is typed.</summary>
    public static (string Prompt, string Negative) Replace(SavedPicturePrompt saved) => (saved.Prompt, saved.Negative);

    /// <summary>
    /// Add: the saved prompt goes after what is typed, separated by a comma. What was typed stays as typed, and a box
    /// that already ends with exactly this saved text is not given it twice.
    /// </summary>
    public static (string Prompt, string Negative) Add(string prompt, string negative, SavedPicturePrompt saved) =>
        (Join(prompt, saved.Prompt), Join(negative, saved.Negative));

    /// <summary>
    /// Text put after what is typed, as Add does: a comma between, what was typed left as typed, and nothing added when
    /// the box already ends with exactly this text. Also used by the prompt helper's "Add after what is typed".
    /// </summary>
    public static string AddTo(string? typed, string added) => Join(typed, added);

    private static string Join(string? typed, string added)
    {
        typed ??= "";
        if (string.IsNullOrWhiteSpace(added)) return typed;
        if (string.IsNullOrWhiteSpace(typed)) return added;
        var head = typed.TrimEnd().TrimEnd(',').TrimEnd();
        if (EndsWithPiece(head, Piece(added))) return typed;
        return head.Length == 0 ? added : head + ", " + added.TrimStart().TrimStart(',').TrimStart();
    }

    /// <summary>
    /// True when the box ends with exactly this text as its own piece: the whole box, or after a comma or a line break.
    /// Ending with the same letters is not enough ("ultra detailed" does not already hold "detailed").
    /// </summary>
    private static bool EndsWithPiece(string head, string piece)
    {
        if (piece.Length == 0 || !head.EndsWith(piece, StringComparison.Ordinal)) return false;
        var before = head[..^piece.Length].TrimEnd(' ', '\t');
        return before.Length == 0 || before[^1] is ',' or '\n' or '\r';
    }

    private static string Piece(string text) => text.Trim().Trim(',').Trim();

    private static List<SavedPicturePrompt> Sorted(IEnumerable<SavedPicturePrompt> prompts) =>
        [.. prompts.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    private async Task PersistAsync(CancellationToken ct)
    {
        _prompts = Sorted(_prompts);
        if (documents is not null) await documents.SaveAsync(Key, new Document { Prompts = _prompts, Start = _start }, ct);
        Changed?.Invoke();
    }

    /// <summary>What is saved.</summary>
    public sealed class Document
    {
        public List<SavedPicturePrompt> Prompts { get; set; } = [];
        public string? Start { get; set; }
    }
}
