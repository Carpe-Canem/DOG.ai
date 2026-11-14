using DogAI.Core.Chat;
using DogAI.Core.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

/// <summary>Searches message text with SQLite's FTS5 index (kept up to date by triggers, see the Polish migration) and chat titles.</summary>
public sealed class SqliteSearchStore(IDbContextFactory<DogDbContext> factory) : ISearchStore
{
    private const int PerChat = 3;

    public async Task<IReadOnlyList<ChatHit>> SearchAsync(string query, int limit = 40, CancellationToken ct = default)
    {
        var fts = ChatSearch.ToFtsQuery(query);
        if (fts is null) return [];
        var words = ChatSearch.Words(query);

        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        var hits = new List<ChatHit>();
        var perChat = new Dictionary<long, int>();

        // Titles first: someone who types a chat's name wants the chat.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Mode, Title, UpdatedAt FROM Conversations WHERE " +
                string.Join(" AND ", words.Select((_, i) => $"Title LIKE $w{i} ESCAPE '\\'")) + " ORDER BY UpdatedAt DESC LIMIT $n";
            for (var i = 0; i < words.Count; i++)
                command.Parameters.Add(new SqliteParameter($"$w{i}", "%" + words[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
            command.Parameters.Add(new SqliteParameter("$n", limit));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var title = reader.GetString(2);
                hits.Add(new ChatHit(reader.GetInt64(0), reader.GetString(1), title, null, null, ChatSearch.MarkTitle(title, words), reader.GetDateTime(3)));
                perChat[reader.GetInt64(0)] = 0;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT m.Id, m.ConversationId, c.Mode, c.Title, c.UpdatedAt, m.Role, " +
                "snippet(MessageSearch, 0, char(1), char(2), '…', 24) " +
                "FROM MessageSearch JOIN Messages m ON m.Id = MessageSearch.rowid JOIN Conversations c ON c.Id = m.ConversationId " +
                "WHERE MessageSearch MATCH $q ORDER BY rank LIMIT $n";
            command.Parameters.Add(new SqliteParameter("$q", fts));
            command.Parameters.Add(new SqliteParameter("$n", limit * PerChat * 2));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct) && hits.Count < limit)
            {
                var conversationId = reader.GetInt64(1);
                perChat.TryGetValue(conversationId, out var seen);
                if (seen >= PerChat) continue;
                perChat[conversationId] = seen + 1;
                hits.Add(new ChatHit(conversationId, reader.GetString(2), reader.GetString(3), reader.GetInt64(0), (ChatRole)reader.GetInt32(5),
                    reader.GetString(6), reader.GetDateTime(4)));
            }
        }
        return hits;
    }
}
