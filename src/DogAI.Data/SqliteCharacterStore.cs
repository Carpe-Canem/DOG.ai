using DogAI.Core.Characters;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

public sealed class SqliteCharacterStore(IDbContextFactory<DogDbContext> factory) : ICharacterStore
{
    public async Task<IReadOnlyList<Character>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // The full picture can be megabytes per card; lists only need the small avatar.
        return await db.Characters.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new Character
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                Personality = c.Personality,
                Scenario = c.Scenario,
                FirstMessage = c.FirstMessage,
                AlternateGreetings = c.AlternateGreetings,
                ExampleMessages = c.ExampleMessages,
                SystemPrompt = c.SystemPrompt,
                PostHistoryInstructions = c.PostHistoryInstructions,
                CreatorNotes = c.CreatorNotes,
                Creator = c.Creator,
                CharacterVersion = c.CharacterVersion,
                Tags = c.Tags,
                ExtraJson = c.ExtraJson,
                Avatar = c.Avatar,
                PresetId = c.PresetId,
                Voice = c.Voice,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
            })
            .ToListAsync(ct);
    }

    public async Task<Character?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Character> SaveAsync(Character character, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        character.UpdatedAt = DateTime.UtcNow;
        if (character.Id == 0)
        {
            if (character.CreatedAt == default) character.CreatedAt = character.UpdatedAt;
            db.Characters.Add(character);
        }
        else
        {
            db.Characters.Update(character);
        }
        await db.SaveChangesAsync(ct);
        return character;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var chats = db.Conversations.Where(c => c.CharacterId == id).Select(c => c.Id);
        await db.Messages.Where(m => chats.Contains(m.ConversationId)).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.CharacterId == id).ExecuteDeleteAsync(ct);
        // Group chats stay; the character just leaves them. Their old replies keep a "SpeakerId" nobody has any more.
        await db.ChatMembers.Where(m => m.CharacterId == id).ExecuteDeleteAsync(ct);
        await db.Characters.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Persona>> ListPersonasAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Personas.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Persona?> GetPersonaAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>Saves a persona. Making one the default clears the flag on the others; the first one is always the default.</summary>
    public async Task<Persona> SavePersonaAsync(Persona persona, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Personas.AnyAsync(p => p.Id != persona.Id, ct)) persona.IsDefault = true;
        if (persona.Id == 0)
        {
            persona.CreatedAt = DateTime.UtcNow;
            db.Personas.Add(persona);
        }
        else
        {
            db.Personas.Update(persona);
        }
        await db.SaveChangesAsync(ct);
        if (persona.IsDefault)
            await db.Personas.Where(p => p.Id != persona.Id && p.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), ct);
        return persona;
    }

    public async Task DeletePersonaAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.PersonaId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.PersonaId, (long?)null), ct);
        await db.Personas.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        // Keep exactly one default while any persona is left.
        if (!await db.Personas.AnyAsync(p => p.IsDefault, ct) && await db.Personas.OrderBy(p => p.Id).FirstOrDefaultAsync(ct) is { } first)
        {
            first.IsDefault = true;
            await db.SaveChangesAsync(ct);
        }
    }
}
