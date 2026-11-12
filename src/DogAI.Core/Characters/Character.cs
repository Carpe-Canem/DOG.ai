namespace DogAI.Core.Characters;

/// <summary>
/// A character, holding the standard Character Card V2/V3 fields. Text may contain {{char}} and {{user}},
/// which are filled in when a chat is built. All of it is untrusted when imported.
/// </summary>
public sealed class Character
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Who the character is: appearance, background, manner. The main part of the card.</summary>
    public string Description { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Scenario { get; set; } = "";

    /// <summary>The character's opening message.</summary>
    public string FirstMessage { get; set; } = "";

    /// <summary>More opening messages to choose from, one per entry.</summary>
    public List<string> AlternateGreetings { get; set; } = [];

    /// <summary>Sample exchanges showing how the character talks, usually in "&lt;START&gt;" blocks.</summary>
    public string ExampleMessages { get; set; } = "";

    /// <summary>Replaces DOG AI's default role-play instruction when set.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Instructions placed after the chat history.</summary>
    public string PostHistoryInstructions { get; set; } = "";

    /// <summary>Notes for people reading the card. Never sent to the model.</summary>
    public string CreatorNotes { get; set; } = "";
    public string Creator { get; set; } = "";
    public string CharacterVersion { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Card parts DOG AI does not edit (extensions, the embedded lorebook, V3-only fields), kept as JSON
    /// so exporting gives them back unchanged.
    /// </summary>
    public string ExtraJson { get; set; } = "";

    /// <summary>The picture the card came with (PNG, JPEG or WebP), used when exporting. Null when none.</summary>
    public byte[]? Image { get; set; }

    /// <summary>A small square PNG made from <see cref="Image"/> for lists and chat.</summary>
    public byte[]? Avatar { get; set; }

    /// <summary>Style used for new chats with this character. Null means the default.</summary>
    public string? PresetId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>All greetings, the main one first, without empty entries.</summary>
    public IReadOnlyList<string> Greetings() =>
        new[] { FirstMessage }.Concat(AlternateGreetings).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
}

/// <summary>Who the user is in a chat: a name and an optional description the character knows.</summary>
public sealed class Persona
{
    public long Id { get; set; }
    public string Name { get; set; } = "You";
    public string Description { get; set; } = "";
    public byte[]? Avatar { get; set; }

    /// <summary>Used for new chats when none is picked.</summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }
}

public interface ICharacterStore
{
    /// <summary>Every character, newest change first. Images are left out; avatars are included.</summary>
    Task<IReadOnlyList<Character>> ListAsync(CancellationToken ct = default);

    /// <summary>One character with everything, including the full image.</summary>
    Task<Character?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Adds a character (Id 0) or replaces one.</summary>
    Task<Character> SaveAsync(Character character, CancellationToken ct = default);

    /// <summary>Deletes the character and all its chats.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<Persona>> ListPersonasAsync(CancellationToken ct = default);
    Task<Persona?> GetPersonaAsync(long id, CancellationToken ct = default);
    Task<Persona> SavePersonaAsync(Persona persona, CancellationToken ct = default);
    Task DeletePersonaAsync(long id, CancellationToken ct = default);
}

public static class CharacterStoreExtensions
{
    /// <summary>The persona with this id, else the default one, else the first; null when there are none.</summary>
    public static async Task<Persona?> PersonaForAsync(this ICharacterStore store, long? id, CancellationToken ct = default)
    {
        if (id is { } wanted && await store.GetPersonaAsync(wanted, ct) is { } persona) return persona;
        var all = await store.ListPersonasAsync(ct);
        return all.FirstOrDefault(p => p.IsDefault) ?? all.FirstOrDefault();
    }
}
