using System.Text.Json;

namespace DogAI.Core.Privacy;

/// <summary>
/// Slows down guessing at the lock screen. The first five wrong tries cost nothing; after the fifth the next try must wait
/// 30 seconds, after the sixth one minute, and after every later one five minutes. A right answer clears the count.
/// Nothing is ever wiped. The count is kept in a small file so restarting DOG.ai does not clear it.
/// (Someone who copies the data folder can guess without DOG.ai; what stops them is the password itself.)
/// </summary>
public sealed class UnlockThrottle
{
    public const int FreeTries = 5;

    public int Failures { get; private set; }
    public DateTime? LastFailureUtc { get; private set; }

    public UnlockThrottle(int failures = 0, DateTime? lastFailureUtc = null)
    {
        Failures = Math.Max(0, failures);
        LastFailureUtc = lastFailureUtc;
    }

    /// <summary>How long the try after <paramref name="failures"/> wrong ones in a row must wait.</summary>
    public static TimeSpan DelayAfter(int failures)
    {
        if (failures < FreeTries) return TimeSpan.Zero;
        if (failures == FreeTries) return TimeSpan.FromSeconds(30);
        if (failures == FreeTries + 1) return TimeSpan.FromMinutes(1);
        return TimeSpan.FromMinutes(5);
    }

    /// <summary>How much longer to wait before the next try is allowed; zero when it may go ahead.</summary>
    public TimeSpan WaitLeft(DateTime nowUtc)
    {
        var delay = DelayAfter(Failures);
        if (delay == TimeSpan.Zero || LastFailureUtc is not { } last) return TimeSpan.Zero;
        // A clock set back must not shorten the wait.
        var since = nowUtc < last ? TimeSpan.Zero : nowUtc - last;
        return since >= delay ? TimeSpan.Zero : delay - since;
    }

    public void Failed(DateTime nowUtc)
    {
        Failures++;
        LastFailureUtc = nowUtc;
    }

    public void Succeeded()
    {
        Failures = 0;
        LastFailureUtc = null;
    }

    private sealed record Saved(int Failures, DateTime? LastFailureUtc);

    /// <summary>Reads the saved count. A missing or unreadable file counts as no wrong tries.</summary>
    public static UnlockThrottle Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is { } saved)
                return new UnlockThrottle(saved.Failures, saved.LastFailureUtc?.ToUniversalTime());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new UnlockThrottle();
    }

    public void Save(string file)
    {
        try
        {
            if (Failures == 0) File.Delete(file);
            else File.WriteAllText(file, JsonSerializer.Serialize(new Saved(Failures, LastFailureUtc)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
