using System.Text.Json;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Documents;
using DogAI.Core.Models;
using DogAI.Core.Stories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DogAI.Data;

public sealed partial class DogDbContext(DbContextOptions<DogDbContext> options) : DbContext(options)
{
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ModelRecord> ModelRecords => Set<ModelRecord>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Persona> Personas => Set<Persona>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<Story> Stories => Set<Story>();
    public DbSet<DocumentCollection> Collections => Set<DocumentCollection>();
    public DbSet<SourceDocument> Documents => Set<SourceDocument>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<ChatCollection> ChatCollections => Set<ChatCollection>();
    public DbSet<MessageVector> MessageVectors => Set<MessageVector>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Conversation>(e =>
        {
            e.Property(c => c.Mode).HasMaxLength(20);
            e.Property(c => c.PresetId).HasMaxLength(40);
            e.HasIndex(c => new { c.Mode, c.UpdatedAt });
            // No foreign keys to characters or personas: adding them would make SQLite rebuild this table on upgrade.
            // SqliteCharacterStore removes or clears the links itself.
            e.HasIndex(c => c.CharacterId);
        });
        model.Entity<ChatMember>(e =>
        {
            // A new table, so links cost no rebuild here: members go with their chat and with their character.
            e.HasIndex(m => new { m.ConversationId, m.Position });
            e.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Character>().WithMany().HasForeignKey(m => m.CharacterId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Story>(e =>
        {
            // One per "story" conversation; a new table, so the link costs no rebuild.
            e.HasIndex(s => s.ConversationId).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(s => s.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.Bible).HasConversion(Json<StoryBible>(), JsonComparer<StoryBible>());
            e.Property(s => s.Outline).HasConversion(Json<List<StoryChapter>>(), JsonComparer<List<StoryChapter>>());
        });
        // Documents (M5). All new tables, so their links cost no rebuild: a chat's own collection, its documents and
        // their passages go with the chat; links and message vectors go with either end.
        model.Entity<DocumentCollection>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.HasIndex(c => c.ConversationId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(c => c.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(c => c.IsPrivate);
        });
        model.Entity<SourceDocument>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(400);
            e.Property(d => d.Kind).HasMaxLength(20);
            e.Property(d => d.Sha256).HasMaxLength(64);
            e.Property(d => d.VectorModel).HasMaxLength(300);
            e.HasIndex(d => new { d.CollectionId, d.Sha256 });
            e.HasOne<DocumentCollection>().WithMany().HasForeignKey(d => d.CollectionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DocumentChunk>(e =>
        {
            e.Property(c => c.VectorModel).HasMaxLength(300);
            e.HasIndex(c => new { c.DocumentId, c.Ordinal });
            e.HasOne<SourceDocument>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ChatCollection>(e =>
        {
            e.HasIndex(l => new { l.ConversationId, l.CollectionId }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(l => l.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DocumentCollection>().WithMany().HasForeignKey(l => l.CollectionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<MessageVector>(e =>
        {
            e.HasKey(v => v.MessageId);
            e.Property(v => v.MessageId).ValueGeneratedNever();
            e.Property(v => v.Model).HasMaxLength(300);
            e.HasIndex(v => v.ConversationId);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(v => v.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Character>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.PresetId).HasMaxLength(40);
            e.Property(c => c.AlternateGreetings).HasConversion(StringList, StringListComparer);
            e.Property(c => c.Tags).HasConversion(StringList, StringListComparer);
            e.HasIndex(c => c.UpdatedAt);
        });
        model.Entity<Persona>(e => e.Property(p => p.Name).HasMaxLength(200));
        model.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.PresetId).HasMaxLength(40);
            e.HasIndex(m => m.ConversationId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ModelRecord>(e =>
        {
            e.Property(m => m.Path).HasMaxLength(1024);
            e.HasIndex(m => m.Path).IsUnique();
            e.Property(m => m.Repo).HasMaxLength(200);
            e.Property(m => m.Quant).HasMaxLength(40);
            e.Property(m => m.UseCase).HasMaxLength(20);
        });
        model.Entity<SettingEntry>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });
    }
}

public sealed partial class DogDbContext
{
    /// <summary>Lists of text (greetings, tags) are kept as one JSON column.</summary>
    private static readonly ValueConverter<List<string>, string> StringList = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

    /// <summary>Small settings objects (a story's bible and outline) are kept as one JSON column each.</summary>
    private static ValueConverter<T, string> Json<T>() where T : class, new() => new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => Read<T>(v));

    private static ValueComparer<T> JsonComparer<T>() where T : class, new() => new(
        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
        v => Read<T>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null)));

    private static T Read<T>(string json) where T : class, new()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, (JsonSerializerOptions?)null) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    private static readonly ValueComparer<List<string>> StringListComparer = new(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        v => v.ToList());
}

/// <summary>A named JSON document, e.g. the "app" settings object.</summary>
public sealed class SettingEntry
{
    public required string Key { get; set; }
    public required string Json { get; set; }
}

/// <summary>Used only by the `dotnet ef` tooling to create migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DogDbContext>
{
    public DogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DogDbContext>().UseSqlite("Data Source=design.db").Options);
}
