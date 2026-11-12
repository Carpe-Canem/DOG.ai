using DogAI.Core.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DogAI.Data;

public sealed class DogDbContext(DbContextOptions<DogDbContext> options) : DbContext(options)
{
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Conversation>(e =>
        {
            e.Property(c => c.Mode).HasMaxLength(20);
            e.Property(c => c.PresetId).HasMaxLength(40);
            e.HasIndex(c => new { c.Mode, c.UpdatedAt });
        });
        model.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.PresetId).HasMaxLength(40);
            e.HasIndex(m => m.ConversationId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SettingEntry>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });
    }
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
