using System.Data.Common;
using DogAI.Core;
using DogAI.Core.Privacy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DogAI.Data;

/// <summary>
/// Makes "removed" mean gone from the database files. Three places could keep what the owner deleted:
/// <list type="bullet">
/// <item>The database file: nothing to do here. The SQLite build DOG.ai ships is compiled with secure delete, so deleted
/// content is overwritten with zeros (a test pins this).</item>
/// <item>The search index: the <c>SearchWipe</c> migration turns on its own secure delete, so a deleted message's words
/// leave the index at once.</item>
/// <item>The log file beside the database (<c>-wal</c>): it holds earlier versions of changed pages until SQLite reuses
/// it, so a picture or chat that was added and then deleted would stay readable there for as long as DOG.ai runs, and
/// after a crash. This class empties that file shortly after any delete. (When DOG.ai closes normally SQLite removes the
/// file itself.)</item>
/// </list>
/// Emptying is done a moment after the last delete of a burst, on a connection of its own, and never fails the delete:
/// if the database is busy it is tried again a few times. On an SSD the emptied space is freed, not overwritten, like any
/// deleted file.
/// </summary>
public sealed class DatabaseTidy(AppPaths paths, DatabaseKey? key = null) : IDisposable
{
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;
    private bool _active, _wanted;

    /// <summary>How long after the last delete the log file is emptied, so a run of deletes is tidied once.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>How often a busy database is tried again before giving up until the next delete.</summary>
    public int Tries { get; set; } = 6;

    /// <summary>Something was deleted: empty the log file soon.</summary>
    public void Deleted()
    {
        lock (_lock)
        {
            if (_stop.IsCancellationRequested) return;
            _wanted = true;
            if (_active) return;
            _active = true;
            _running = Task.Run(RunAsync);
        }
    }

    /// <summary>Completes when no tidying is waiting or running. For tests; the app never waits for it.</summary>
    public Task SettledAsync()
    {
        lock (_lock) return _running;
    }

    private async Task RunAsync()
    {
        try
        {
            var tries = 0;
            while (true)
            {
                lock (_lock)
                {
                    if (!_wanted && tries == 0)
                    {
                        _active = false;
                        return;
                    }
                    // A new delete starts the count again.
                    if (_wanted) tries = 0;
                    _wanted = false;
                }
                await Task.Delay(tries == 0 ? Delay : TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    // More deletes came in while waiting: wait once more, so the burst is tidied together.
                    if (_wanted) continue;
                }
                tries = Empty() || tries + 1 >= Tries ? 0 : tries + 1;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _active = false;
        }
    }

    /// <summary>
    /// Folds the log file into the database and cuts it to nothing. False when something was still reading older pages, so
    /// part of it had to stay.
    /// </summary>
    private bool Empty()
    {
        try
        {
            if (!File.Exists(paths.DatabaseFile)) return true;
            using var db = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, key?.Bytes));
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var reader = command.ExecuteReader();
            // First column: 1 when the checkpoint could not finish because the database was in use.
            return !reader.Read() || reader.GetInt64(0) == 0;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Task running;
        lock (_lock)
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            running = _running;
        }
        // Whatever is in flight lets go of the database before the caller goes on (a test deleting the folder, a restart).
        try { running.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }
}

/// <summary>Tells <see cref="DatabaseTidy"/> whenever a command that deletes rows has run on a DOG.ai database connection.</summary>
public sealed class DeleteWatcher(DatabaseTidy tidy) : DbCommandInterceptor
{
    private void Look(DbCommand command)
    {
        // Every delete in DOG.ai is a DELETE statement sent through EF Core; rows removed by triggers ride along with one.
        if (command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)) tidy.Deleted();
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Look(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Look(command);
        return new(result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Look(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Look(command);
        return new(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Look(command);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Look(command);
        return new(result);
    }
}
