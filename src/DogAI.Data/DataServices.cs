using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Data;

public static class DataServices
{
    public static IServiceCollection AddDogData(this IServiceCollection services)
    {
        services.AddDbContextFactory<DogDbContext>((sp, o) =>
            o.UseSqlite($"Data Source={sp.GetRequiredService<AppPaths>().DatabaseFile}"));
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IChatStore, SqliteChatStore>();
        return services;
    }

    /// <summary>Creates the database on first run and applies any pending migrations after an update.</summary>
    public static async Task MigrateDogDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}

public sealed class SqliteSettingsStore(IDbContextFactory<DogDbContext> factory) : ISettingsStore
{
    private const string Key = "app";

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (entry is null) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(entry.Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(settings);
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (entry is null) db.Settings.Add(new SettingEntry { Key = Key, Json = json });
        else entry.Json = json;
        await db.SaveChangesAsync(ct);
    }
}
