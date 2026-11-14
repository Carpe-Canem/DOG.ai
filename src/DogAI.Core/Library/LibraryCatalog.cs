using DogAI.Core.Models;

namespace DogAI.Core.Library;

/// <summary>
/// The assistants and saved prompts. Each list is one JSON document in the settings store. The first run fills them with a
/// few examples; after that the lists are the user's, and deleting every one leaves them empty.
/// Call <see cref="LoadAsync"/> once at startup; after that every lookup is synchronous.
/// </summary>
public sealed class LibraryCatalog(IDocumentStore? documents = null)
{
    private const string AssistantsKey = "assistants";
    private const string PromptsKey = "prompts";

    private List<Assistant> _assistants = [];
    private List<PromptEntry> _prompts = [];

    public event Action? Changed;

    public IReadOnlyList<Assistant> Assistants => _assistants;
    public IReadOnlyList<PromptEntry> Prompts => _prompts;

    public Assistant? FindAssistant(string? id) => _assistants.FirstOrDefault(a => a.Id == id);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        var assistants = await documents.LoadAsync<List<Assistant>>(AssistantsKey, ct);
        var prompts = await documents.LoadAsync<List<PromptEntry>>(PromptsKey, ct);
        _assistants = assistants?.Where(a => a.Id.Length > 0).ToList() ?? Starters.Assistants();
        _prompts = prompts?.Where(p => p.Id.Length > 0).ToList() ?? Starters.Prompts();
        // Remember that the examples were handed out, so deleting them all is respected.
        if (assistants is null) await documents.SaveAsync(AssistantsKey, _assistants, ct);
        if (prompts is null) await documents.SaveAsync(PromptsKey, _prompts, ct);
        Changed?.Invoke();
    }

    public async Task<Assistant> SaveAsync(Assistant assistant, CancellationToken ct = default)
    {
        var name = assistant.Name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the assistant a name.");
        var saved = assistant with
        {
            Id = assistant.Id.Length > 0 ? assistant.Id : NewId(),
            Name = name,
            Description = assistant.Description.Trim(),
            ModelPath = string.IsNullOrWhiteSpace(assistant.ModelPath) ? null : assistant.ModelPath,
        };
        var index = _assistants.FindIndex(a => a.Id == saved.Id);
        if (index >= 0) _assistants[index] = saved;
        else _assistants.Add(saved);
        await PersistAsync(AssistantsKey, _assistants, ct);
        return saved;
    }

    public async Task DeleteAssistantAsync(string id, CancellationToken ct = default)
    {
        if (_assistants.RemoveAll(a => a.Id == id) > 0) await PersistAsync(AssistantsKey, _assistants, ct);
    }

    public async Task<PromptEntry> SaveAsync(PromptEntry prompt, CancellationToken ct = default)
    {
        var name = prompt.Name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the prompt a name.");
        if (string.IsNullOrWhiteSpace(prompt.Text)) throw new InvalidOperationException("Write the prompt's text.");
        var saved = prompt with { Id = prompt.Id.Length > 0 ? prompt.Id : NewId(), Name = name };
        var index = _prompts.FindIndex(p => p.Id == saved.Id);
        if (index >= 0) _prompts[index] = saved;
        else _prompts.Add(saved);
        await PersistAsync(PromptsKey, _prompts, ct);
        return saved;
    }

    public async Task DeletePromptAsync(string id, CancellationToken ct = default)
    {
        if (_prompts.RemoveAll(p => p.Id == id) > 0) await PersistAsync(PromptsKey, _prompts, ct);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    private async Task PersistAsync<T>(string key, List<T> list, CancellationToken ct)
    {
        if (documents is not null) await documents.SaveAsync(key, list, ct);
        Changed?.Invoke();
    }

    /// <summary>The examples a new install starts with. They are ordinary entries: edit or delete them.</summary>
    internal static class Starters
    {
        public static List<Assistant> Assistants() =>
        [
            new() { Id = "starter-coder", Name = "Coder", Description = "Writes and explains code",
                Instructions = "You are a careful programmer. Give working code in fenced blocks with the language named, keep explanations short, and say so when you are unsure or when something in the question is ambiguous.",
                PresetId = "precise" },
            new() { Id = "starter-editor", Name = "Editor", Description = "Improves writing without changing its voice",
                Instructions = "You are an editor. Improve the user's text for clarity and flow while keeping their voice and meaning. Show the revised text first, then a short list of what you changed and why.",
                PresetId = "balanced" },
            new() { Id = "starter-tutor", Name = "Tutor", Description = "Explains step by step and checks understanding",
                Instructions = "You are a patient tutor. Explain ideas step by step in plain words, use small examples, and finish with one question that checks the user understood.",
                PresetId = "balanced" },
        ];

        public static List<PromptEntry> Prompts() =>
        [
            new() { Id = "starter-summarise", Name = "Summarise text",
                Text = "Summarise the text below in [[length|five bullet points]] for [[audience|a busy reader]].\n\n[[text]]" },
            new() { Id = "starter-translate", Name = "Translate",
                Text = "Translate the text below into [[language]]. Keep the tone and formatting, and do not add notes.\n\n[[text]]" },
            new() { Id = "starter-review", Name = "Review code",
                Text = "Review this [[language|code]] for bugs, unclear names and missing error handling. List problems from most to least serious, each with a fix.\n\n```\n[[code]]\n```" },
        ];
    }
}
