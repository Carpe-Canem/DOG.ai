using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Data;

public static class DataServices
{
    public static IServiceCollection AddDogData(this IServiceCollection services)
    {
        // Locked data (M8): the app registers a DatabaseKey once the owner has unlocked; without one the database is plain.
        // What is deleted leaves the database files too: see DatabaseTidy.
        services.AddSingleton(sp => new DatabaseTidy(sp.GetRequiredService<AppPaths>(), sp.GetService<DogAI.Core.Privacy.DatabaseKey>()));
        services.AddDbContextFactory<DogDbContext>((sp, o) =>
            o.UseSqlite(DatabaseLock.ConnectionString(sp.GetRequiredService<AppPaths>().DatabaseFile,
                    sp.GetService<DogAI.Core.Privacy.DatabaseKey>()?.Bytes, pooling: true))
                .AddInterceptors(new DeleteWatcher(sp.GetRequiredService<DatabaseTidy>())));
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IChatStore, SqliteChatStore>();
        services.AddSingleton<DogAI.Core.Search.ISearchStore, SqliteSearchStore>();
        services.AddSingleton<DogAI.Core.Backup.IBackupService, SqliteBackupService>();
        services.AddSingleton<ICharacterStore, SqliteCharacterStore>();
        services.AddSingleton<DogAI.Core.Stories.IStoryStore, SqliteStoryStore>();
        services.AddSingleton<IModelRecordStore, SqliteModelRecordStore>();
        services.AddSingleton<DogAI.Core.Documents.ICollectionStore, SqliteCollectionStore>();
        services.AddSingleton<DogAI.Core.Images.IPictureStore, SqlitePictureStore>();
        services.AddSingleton<IDocumentStore>(sp => (SqliteSettingsStore)sp.GetRequiredService<ISettingsStore>());
        return services;
    }

    /// <summary>Creates the database on first run and applies any pending migrations after an update.</summary>
    public static async Task MigrateDogDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        // An update can rewrite stored data (the search index, for one); the log file should not keep the old pages
        // for as long as DOG.ai then runs.
        services.GetService<DatabaseTidy>()?.Deleted();
    }
}

public sealed class SqliteSettingsStore(IDbContextFactory<DogDbContext> factory) : ISettingsStore, IDocumentStore
{
    private const string Key = "app";

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
        await LoadAsync<AppSettings>(Key, ct) ?? new AppSettings();

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => SaveAsync(Key, settings, ct);

    public async Task<T?> LoadAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(entry.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken ct = default) where T : class
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(value);
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null) db.Settings.Add(new SettingEntry { Key = key, Json = json });
        else entry.Json = json;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SqliteModelRecordStore(IDbContextFactory<DogDbContext> factory) : IModelRecordStore
{
    public async Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ModelRecords.AsNoTracking().ToListAsync(ct);
    }

    public async Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = Normalize(path);
        return await db.ModelRecords.AsNoTracking().FirstOrDefaultAsync(m => m.Path == key, ct);
    }

    public async Task SaveAsync(ModelRecord record, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        record.Path = Normalize(record.Path);
        var existing = await db.ModelRecords.FirstOrDefaultAsync(m => m.Path == record.Path, ct);
        if (existing is null)
        {
            record.Id = 0;
            if (record.AddedAt == default) record.AddedAt = DateTime.UtcNow;
            db.ModelRecords.Add(record);
        }
        else
        {
            record.Id = existing.Id;
            db.Entry(existing).CurrentValues.SetValues(record);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = Normalize(path);
        await db.ModelRecords.Where(m => m.Path == key).ExecuteDeleteAsync(ct);
    }

    /// <summary>Windows paths are case-insensitive; store one spelling so lookups match.</summary>
    private static string Normalize(string path) => Path.GetFullPath(path).ToLowerInvariant();
}
