using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DogAI.Core.Settings;
using DogAI.Core.Voice;
using Microsoft.Extensions.Logging;

namespace DogAI.App;

/// <summary>
/// The parts of DOG.ai that live in Windows itself, each off until asked for in Settings:
/// a notification-area (tray) icon, closing the window to the tray, and a key that works in every program.
///
/// The key is Ctrl+Alt+Space, held to talk. It needs a low-level keyboard hook, because Windows' simpler hot key call only says a
/// key went down, never that it was let go. The hook looks at nothing but this one combination, keeps no record of keys and sends
/// nothing anywhere. What was said is typed into the program that has focus when the keys are released.
/// </summary>
public sealed class WindowsIntegration : IWindowsIntegration, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly Dictation _dictation;
    private readonly ILogger<WindowsIntegration> _log;
    private System.Windows.Forms.NotifyIcon? _tray;
    private IntPtr _hook;
    private HookProc? _hookProc;
    private bool _combo;
    private Task _queue = Task.CompletedTask;
    private volatile bool _closeToTray;
    private volatile bool _quitting;

    public WindowsIntegration(ISettingsStore settings, Dictation dictation, ILogger<WindowsIntegration> log)
    {
        _settings = settings;
        _dictation = dictation;
        _log = log;
        _dictation.HeardForOtherPrograms += TypeIntoFocusedProgram;
    }

    public string? Problem { get; private set; }

    /// <summary>True when closing the window should hide it (the tray icon is there to bring it back).</summary>
    public bool ShouldHideOnClose => _closeToTray && !_quitting;

    public async Task ApplyAsync()
    {
        var s = await _settings.LoadAsync();
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Problem = null;
            _closeToTray = s is { TrayIcon: true, CloseToTray: true };
            SetTray(s.TrayIcon);
            SetHook(s.DictateAnywhere);
        });
    }

    // --- tray -------------------------------------------------------------------------------------------------------

    private void SetTray(bool on)
    {
        if (!on)
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            return;
        }
        if (_tray is not null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open DOG.ai", null, (_, _) => ShowWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "DOG.ai",
            Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow();
        };
    }

    private static void ShowWindow()
    {
        var window = Application.Current.MainWindow;
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        Application.Current.Shutdown();
    }

    // --- the key that works anywhere --------------------------------------------------------------------------------

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const int VkSpace = 0x20, VkControl = 0x11, VkMenu = 0x12, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHook
    {
        public int VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    private Thread? _hookThread;
    private uint _hookThreadId;

    /// <summary>
    /// The hook lives on a thread of its own with its own message loop. Windows calls a low-level hook before any key reaches any
    /// program and removes it, without a word, if it is slow; the window's thread is busy whenever a long reply streams in.
    /// </summary>
    private void SetHook(bool on)
    {
        if (!on)
        {
            if (_hookThread is null) return;
            PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(TimeSpan.FromSeconds(2));
            _hookThread = null;
            _combo = false;
            return;
        }
        if (_hookThread is not null) return;

        _hookProc = OnKey;
        using var ready = new ManualResetEventSlim();
        string? failure = null;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "DOG.ai key watcher" };
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));

        if (failure is not null)
        {
            _hookThread = null;
            Problem = "Windows would not let DOG.ai watch for Ctrl+Alt+Space (" + failure + ").";
            _log.LogWarning("{Problem}", Problem);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyboardHook>(lParam);
            var message = (int)wParam;
            var down = message is WmKeyDown or WmSysKeyDown;
            var up = message is WmKeyUp or WmSysKeyUp;
            // The text DOG.ai types is sent as Unicode characters (key code 0xE7), never as Space, so it cannot set this off again.
            {
                if (key.VkCode == VkSpace && down)
                {
                    if (_combo) return 1;                         // key repeat while talking
                    if (Held(VkControl) && Held(VkMenu) && !OwnWindowHasFocus())
                    {
                        _combo = true;
                        Enqueue(() => _dictation.StartForOtherProgramsAsync());
                        return 1;
                    }
                }
                else if (_combo && up && key.VkCode is VkSpace or VkLControl or VkRControl or VkLMenu or VkRMenu)
                {
                    var spaceUp = key.VkCode == VkSpace;
                    _combo = false;
                    Enqueue(() => _dictation.FinishAsync());
                    if (spaceUp) return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Start and finish run one after the other, so letting go early never leaves the microphone running.</summary>
    private void Enqueue(Func<Task> step)
    {
        lock (this)
        {
            _queue = _queue.ContinueWith(_ => step(), TaskScheduler.Default).Unwrap();
        }
    }

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Inside DOG.ai the message box has its own Ctrl+Space; the global key is for other programs.</summary>
    private static bool OwnWindowHasFocus()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == Environment.ProcessId;
    }

    // --- typing what was said ---------------------------------------------------------------------------------------

    private void TypeIntoFocusedProgram(string text)
    {
        if (text.Length == 0) return;
        // Typing while Ctrl or Alt is still down would press shortcuts in the other program; wait (at most 2 s) until they are up.
        for (var i = 0; i < 40 && (Held(VkControl) || Held(VkMenu)); i++) Thread.Sleep(50);
        var inputs = new List<Input>(text.Length * 2);
        foreach (var ch in text.Replace("\r", "").Replace('\n', ' '))
        {
            inputs.Add(Unicode(ch, up: false));
            inputs.Add(Unicode(ch, up: true));
        }
        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        if (sent != array.Length) _log.LogWarning("Typed {Sent} of {Total} key events into the focused program (it may be running with more rights than DOG.ai)", sent, array.Length);
    }

    private static Input Unicode(char ch, bool up) => new()
    {
        Type = 1,
        Keyboard = new KeyboardInput { Scan = ch, Flags = 0x4 | (up ? 0x2u : 0u) },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Keyboard;
        // The union is as large as its biggest member (a mouse event, 32 bytes on 64-bit Windows); pad the keyboard event to that size.
        private readonly long _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    public void Dispose()
    {
        _dictation.HeardForOtherPrograms -= TypeIntoFocusedProgram;
        SetHook(false);
        SetTray(false);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
