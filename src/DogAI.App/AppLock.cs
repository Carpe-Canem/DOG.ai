using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DogAI.Core.Appearance;
using DogAI.Core.Chat;
using DogAI.Core.Privacy;
using DogAI.Core.Settings;
using DogAI.Core.Voice;
using DogAI.Engine;
using Microsoft.Extensions.Logging;

namespace DogAI.App;

/// <summary>
/// Locking a running DOG.ai: Lock now (Settings, the tray menu, Ctrl+L) and the idle lock. Locking restarts DOG.ai into
/// the lock screen, which clears the key and every decrypted chat from memory and unloads the model with the old process.
/// Also keeps <c>lock.json</c>'s copy of the look and tray setting in step, so the lock screen matches the app.
/// </summary>
public sealed class AppLock : IAppLock, IDisposable
{
    private readonly PrivacyLock _lock;
    private readonly ISettingsStore _settings;
    private readonly AppearanceState _appearance;
    private readonly ChatService _chat;
    private readonly ReadAloud _reader;
    private readonly Dictation _dictation;
    private readonly EngineManager _engine;
    private readonly DogAI.Hub.DownloadManager _downloads;
    private readonly DogAI.Core.Images.IImageEngine _pictures;
    private readonly ILogger<AppLock> _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TimeSpan? _limit;
    private bool _tray;
    private bool _locking;
    private long _startedMs = Environment.TickCount64;

    public AppLock(PrivacyLock privacyLock, ISettingsStore settings, AppearanceState appearance, ChatService chat, ReadAloud reader,
        Dictation dictation, EngineManager engine, DogAI.Hub.DownloadManager downloads, DogAI.Core.Images.IImageEngine pictures, ILogger<AppLock> log)
    {
        _pictures = pictures;
        _lock = privacyLock;
        _settings = settings;
        _appearance = appearance;
        _chat = chat;
        _reader = reader;
        _dictation = dictation;
        _engine = engine;
        _downloads = downloads;
        _log = log;
        _timer.Tick += (_, _) => CheckIdle();
        _appearance.Changed += SaveLook;
    }

    public bool CanLock => _lock.CanLock;

    /// <summary>Called once the main window is up.</summary>
    public async Task StartAsync()
    {
        await ApplyAsync();
        _startedMs = Environment.TickCount64;
        _timer.Start();
    }

    public async Task ApplyAsync()
    {
        var s = await _settings.LoadAsync();
        _limit = IdleLock.Limit(s.IdleLockMinutes, Environment.GetEnvironmentVariable(IdleLock.TestSecondsVariable));
        _tray = s.TrayIcon;
        SaveLook();
    }

    public void LockNow() => Lock("Lock now");

    private void Lock(string why)
    {
        if (_locking || !CanLock) return;
        _locking = true;
        var hidden = Application.Current.Dispatcher.Invoke(() => Application.Current.MainWindow is { IsVisible: false });
        _log.LogInformation("Locking DOG.ai ({Why}); restarting into the lock screen{Hidden}.", why, hidden ? " in the tray" : "");
        WpfDesktopShell.RestartProcess(hidden);
    }

    private void CheckIdle()
    {
        if (_limit is null || _locking) return;
        // Counted from the unlock at the earliest: Windows Hello by face, for one, involves no key press or mouse move.
        var sinceUnlock = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedMs);
        var pc = IdleTime();
        var idle = pc < sinceUnlock ? pc : sinceUnlock;
        // A download cut by the lock is kept and paused; Resume in Models carries on from where it stopped.
        // A picture counts from the moment it is asked for: waiting for its turn, making room, drawing.
        var busy = _chat.IsBusy || _engine.IsBusy || _pictures.IsBusy || _reader.IsSpeaking || _dictation.IsOn || _downloads.HasPending;
        if (IdleLock.Due(_limit, idle, busy) && CanLock) Lock($"nobody used the PC for {(int)idle.TotalSeconds} seconds");
    }

    private void SaveLook()
    {
        try
        {
            var theme = _appearance.Theme == Themes.System ? null : _appearance.Theme;
            _lock.SaveLook(new LockLook(_appearance.Skin, theme, _tray));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or LockFileException)
        {
            _log.LogWarning("The lock screen's look could not be saved: {Problem}", e.Message);
        }
    }

    /// <summary>How long since anyone used the keyboard or mouse anywhere on this PC.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var ticks = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(ticks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);

    public void Dispose()
    {
        _timer.Stop();
        _appearance.Changed -= SaveLook;
    }
}
