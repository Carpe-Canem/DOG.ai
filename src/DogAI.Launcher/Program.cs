using System.Diagnostics;
using System.Runtime.InteropServices;

// Starts app\DogAI.exe from the folder this launcher sits in. Nothing else.
var app = Path.Combine(AppContext.BaseDirectory, "app", "DogAI.exe");

if (!File.Exists(app))
{
    Launcher.Say("DOG.ai could not find its program files.\n\nExpected: " + app + "\n\nKeep DogAI.exe together with its app folder.");
    return 1;
}

try
{
    var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
    foreach (var argument in args) start.ArgumentList.Add(argument);
    Process.Start(start)?.Dispose();
    return 0;
}
catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
{
    Launcher.Say("DOG.ai could not start.\n\n" + e.Message);
    return 1;
}

internal static partial class Launcher
{
    public static void Say(string text) => MessageBoxW(IntPtr.Zero, text, "DOG.ai", 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
