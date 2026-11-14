namespace DogAI.Core.Net;

/// <summary>
/// Decides when the <see cref="OnlineGate"/> is open. The user turns it on inside the Models section.
/// It stays open while they are there, and while a download or engine update they started is running.
/// Leaving the section with nothing running turns it off, as does the last download finishing after they left.
/// </summary>
public sealed class OnlineSession(OnlineGate gate)
{
    private readonly object _lock = new();
    private bool _wanted;
    private bool _inModels;
    private int _work;

    /// <summary>
    /// Raised when the connection closes. Work in progress must stop, since the gate only checks
    /// new requests, not transfers already streaming. Raised outside the session's lock.
    /// </summary>
    public event Action? Closed;

    /// <summary>The user's choice in the Models section.</summary>
    public bool Wanted { get { lock (_lock) return _wanted; } }

    /// <summary>Downloads or updates currently holding the connection open.</summary>
    public int ActiveWork { get { lock (_lock) return _work; } }

    public void SetWanted(bool online) => Update(() =>
    {
        // Turning it off while downloads run closes the gate; the downloads then pause.
        _wanted = online && (_inModels || _work > 0);
    });

    public void EnterModels() => Update(() => _inModels = true);

    public void LeaveModels() => Update(() =>
    {
        _inModels = false;
        if (_work == 0) _wanted = false;
    });

    /// <summary>Call when a download starts; returns a handle to dispose when it ends.</summary>
    public IDisposable BeginWork()
    {
        Update(() => _work++);
        return new WorkHandle(this);
    }

    private void EndWork() => Update(() =>
    {
        _work = Math.Max(0, _work - 1);
        if (_work == 0 && !_inModels) _wanted = false;
    });

    private void Update(Action change)
    {
        bool closed;
        lock (_lock)
        {
            var was = gate.IsOnline;
            change();
            var now = _wanted && (_inModels || _work > 0);
            gate.Set(now);
            closed = was && !now;
        }
        if (closed) Closed?.Invoke();
    }

    private sealed class WorkHandle(OnlineSession owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.EndWork();
        }
    }
}
