using System.Text.Json;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Models;
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
