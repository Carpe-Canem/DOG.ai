using DogAI.Core.Appearance;
using Microsoft.Win32;

namespace DogAI.App;

/// <summary>Reads the Windows "app mode" (dark or light) and reports when the user changes it.</summary>
public sealed class WindowsSystemTheme : ISystemTheme, IDisposable
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public WindowsSystemTheme()
    {
        IsDark = Read();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public bool IsDark { get; private set; }

    public event Action? Changed;

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var now = Read();
        if (now == IsDark) return;
        IsDark = now;
        Changed?.Invoke();
    }

    private static bool Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
