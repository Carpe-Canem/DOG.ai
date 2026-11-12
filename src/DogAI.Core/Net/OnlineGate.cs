namespace DogAI.Core.Net;

/// <summary>
/// The single switch that decides whether DOG AI may reach the internet.
/// Off by default; only the Models section turns it on.
/// </summary>
public sealed class OnlineGate
{
    private volatile bool _isOnline;

    public bool IsOnline => _isOnline;

    public event Action? Changed;

    public void Set(bool online)
    {
        if (_isOnline == online) return;
        _isOnline = online;
        Changed?.Invoke();
    }
}

/// <summary>Thrown when code tries to go online while the gate is closed.</summary>
public sealed class OfflineException(string message) : InvalidOperationException(message);

/// <summary>Thrown when code tries to reach a host that is not on the allow-list.</summary>
public sealed class HostNotAllowedException(string message) : InvalidOperationException(message);
