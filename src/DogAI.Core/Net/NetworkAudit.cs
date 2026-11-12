namespace DogAI.Core.Net;

public sealed record NetworkAuditEntry(DateTimeOffset Time, string Source, string Target, bool Allowed, string? Reason);

/// <summary>In-memory record of recent outbound attempts, allowed and blocked, for the diagnostics view and logs.</summary>
public sealed class NetworkAudit
{
    private const int Capacity = 500;
    private readonly Queue<NetworkAuditEntry> _entries = new();
    private readonly object _lock = new();

    public event Action<NetworkAuditEntry>? Recorded;

    public void Record(string source, string target, bool allowed, string? reason = null)
    {
        var entry = new NetworkAuditEntry(DateTimeOffset.Now, source, target, allowed, reason);
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
        Recorded?.Invoke(entry);
    }

    public IReadOnlyList<NetworkAuditEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }
}
