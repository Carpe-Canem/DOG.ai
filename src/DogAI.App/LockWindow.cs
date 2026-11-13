using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DogAI.Core;
using DogAI.Core.Privacy;
using DogAI.Data;
using Serilog;

namespace DogAI.App;

/// <summary>
/// The lock screen, in its first plain form (M8 part 1): the only thing DOG.ai shows while its data is locked. Nothing
/// else is running behind it: no database, no tray icon, no global key, no model. It asks for the password, offers Windows
/// Hello when that is on for this PC, and lets a forgotten password be replaced through the recovery key.
///
/// The same screen finishes turning the lock on (the database is encrypted the first time the new password is entered
/// here, so data is never encrypted under a password the owner cannot repeat) and off.
/// </summary>
public sealed class LockWindow : Window
{
    private readonly AppPaths _paths;
    private readonly DataLockState _state;
    private readonly Unlocker _unlocker;

    private readonly PasswordBox _password = new();
    private readonly Button _unlock = new() { IsDefault = true };
    private readonly Button _hello = new() { Content = "Use Windows Hello", Visibility = Visibility.Collapsed };
    private readonly CheckBox _useHello = new() { Content = "Use Windows Hello on this PC from now on", Visibility = Visibility.Collapsed };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _forgot = new() { Content = "I forgot my password" };
    private readonly Button _cancelLock = new() { Content = "Don't lock my data after all", Visibility = Visibility.Collapsed };

    private readonly StackPanel _passwordPanel = new();
    private readonly StackPanel _recoveryPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _recoveryKey = new();
    private readonly PasswordBox _newPassword = new();
    private readonly PasswordBox _repeatPassword = new();
    private readonly Button _setPassword = new() { Content = "Set new password and unlock" };
    private readonly Button _back = new() { Content = "Back" };

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _dark;
    private bool _busy;

    /// <summary>Set when the window closes with a result: the key to run with (its bytes are null when the lock was just turned off).</summary>
    public DatabaseKey? Result { get; private set; }

    public LockWindow(AppPaths paths, LockFile file, DataLockState state, IHelloSigner hello)
    {
        _paths = paths;
        _state = state;
        _unlocker = new Unlocker(file, paths.LockFile, paths.UnlockTriesFile, hello);
        // The look saved with the lock, else what Windows is set to.
        using (var windows = new WindowsSystemTheme()) _dark = file.Theme is { } theme ? theme == "dark" : windows.IsDark;

        Title = "DOG.ai";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 14;
        Background = new SolidColorBrush(_dark ? Color.FromRgb(0x1f, 0x1f, 0x22) : Color.FromRgb(0xf6, 0xf5, 0xf2));
        Foreground = new SolidColorBrush(_dark ? Color.FromRgb(0xea, 0xea, 0xea) : Color.FromRgb(0x1c, 0x1c, 0x1c));
        SourceInitialized += (_, _) => ApplyTitleBar();

        _useHello.Foreground = Foreground;
        Content = Build();
        _unlock.Click += async (_, _) => await UnlockWithPasswordAsync();
        _hello.Click += async (_, _) => await UnlockWithHelloAsync();
        _forgot.Click += (_, _) => ShowRecovery(true);
        _back.Click += (_, _) => ShowRecovery(false);
        _setPassword.Click += async (_, _) => await RecoverAsync();
        _cancelLock.Click += (_, _) => CancelTurningOn();
        _clock.Tick += (_, _) => ShowWait();
        Loaded += async (_, _) =>
        {
            _password.Focus();
            ShowWait();
            _clock.Start();
            if (_unlocker.HelloIsOn) _hello.Visibility = Visibility.Visible;
            // Hello is offered once the password has opened the lock on this PC; not while the lock is being turned off.
            else if (_state != DataLockState.TurningOff && await _unlocker.CanTurnOnHelloAsync()) _useHello.Visibility = Visibility.Visible;
        };
        Closed += (_, _) => _clock.Stop();
    }

    private UIElement Build()
    {
        var (heading, explanation, action) = _state switch
        {
            DataLockState.TurningOn => ("Enter your new password",
                "Your data is locked as soon as you enter the password you just chose. Encrypting it can take up to a minute.",
                "Lock my data"),
            DataLockState.TurningOff => ("Enter your password to turn the lock off",
                "Your data is then stored as plain files again, readable by anyone with access to this PC.",
                "Turn the lock off"),
            _ => ("DOG.ai is locked", "Enter your password to open your chats, characters, stories and documents.", "Unlock"),
        };
        _unlock.Content = action;

        Id(_password, "Password");
        Id(_unlock, "Unlock");
        Id(_hello, "Hello");
        Id(_useHello, "UseHello");
        Id(_message, "Message");
        Id(_forgot, "Forgot");
        Id(_cancelLock, "CancelLock");
        Id(_recoveryKey, "RecoveryKey");
        Id(_newPassword, "NewPassword");
        Id(_repeatPassword, "RepeatPassword");
        Id(_setPassword, "SetPassword");
        Id(_back, "Back");

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        root.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Spaced(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }, 8));

        _passwordPanel.Children.Add(Spaced(Caption("Password", _password), 18));
        _passwordPanel.Children.Add(Spaced(Padded(_password), 4));
        _passwordPanel.Children.Add(Spaced(_useHello, 12));
        _passwordPanel.Children.Add(Spaced(Row(Padded(_unlock), Padded(_hello)), 16));
        _passwordPanel.Children.Add(Spaced(Row(Plain(_forgot), Plain(_cancelLock)), 18));
        if (_state == DataLockState.TurningOn)
        {
            // The data is still plain, so backing out loses nothing; and without a recovery key in hand yet, "forgot" has no use.
            _cancelLock.Visibility = Visibility.Visible;
            _forgot.Visibility = Visibility.Collapsed;
        }
        root.Children.Add(_passwordPanel);

        _recoveryPanel.Children.Add(Spaced(new TextBlock
        {
            Text = "Enter the recovery key you printed or wrote down when you turned the lock on, then choose a new password. " +
                   "Without the password and without the recovery key, the data cannot be opened by anyone.",
            TextWrapping = TextWrapping.Wrap,
        }, 18));
        _recoveryPanel.Children.Add(Spaced(Caption("Recovery key", _recoveryKey), 14));
        _recoveryPanel.Children.Add(Spaced(Padded(_recoveryKey), 4));
        _recoveryPanel.Children.Add(Spaced(Caption("New password", _newPassword), 12));
        _recoveryPanel.Children.Add(Spaced(Padded(_newPassword), 4));
        _recoveryPanel.Children.Add(Spaced(Caption("New password again", _repeatPassword), 12));
        _recoveryPanel.Children.Add(Spaced(Padded(_repeatPassword), 4));
        _recoveryPanel.Children.Add(Spaced(Row(Padded(_setPassword), Padded(_back)), 16));
        root.Children.Add(_recoveryPanel);

        root.Children.Add(Spaced(_message, 16));
        return root;
    }

    private static void Id(DependencyObject control, string id) => AutomationProperties.SetAutomationId(control, id);

    private TextBlock Caption(string text, UIElement target)
    {
        AutomationProperties.SetName(target, text);
        return new TextBlock { Text = text, Foreground = Foreground };
    }

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    private static T Padded<T>(T control) where T : Control
    {
        control.Padding = new Thickness(10, 6, 10, 6);
        return control;
    }

    private Button Plain(Button link)
    {
        link.Background = Brushes.Transparent;
        link.BorderThickness = new Thickness(0);
        link.Foreground = Foreground;
        link.Cursor = System.Windows.Input.Cursors.Hand;
        link.Padding = new Thickness(0);
        link.Content = new TextBlock { Text = (string)link.Content, TextDecorations = TextDecorations.Underline };
        return link;
    }

    private static StackPanel Row(params FrameworkElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(item);
        }
        return row;
    }

    private void Say(string text, bool problem = true)
    {
        _message.Text = text;
        _message.Foreground = problem ? new SolidColorBrush(_dark ? Color.FromRgb(0xff, 0x9d, 0x8a) : Color.FromRgb(0xa3, 0x1d, 0x0a)) : Foreground;
    }

    private void SetBusy(bool busy, string? saying = null)
    {
        _busy = busy;
        foreach (var control in new Control[] { _password, _unlock, _hello, _useHello, _forgot, _cancelLock, _recoveryKey, _newPassword, _repeatPassword, _setPassword, _back })
            control.IsEnabled = !busy;
        if (saying is not null) Say(saying, problem: false);
        if (!busy) ShowWait();
    }

    /// <summary>After too many wrong tries the buttons that take a guess are held back until the wait is over.</summary>
    private void ShowWait()
    {
        if (_busy) return;
        var wait = _unlocker.WaitLeft;
        var waiting = wait > TimeSpan.Zero;
        _unlock.IsEnabled = !waiting;
        _setPassword.IsEnabled = !waiting;
        if (waiting)
            Say($"Too many wrong tries. You can try again in {(int)wait.TotalMinutes}:{wait.Seconds:00}. Nothing is deleted, however often a wrong password is entered.");
        else if (_message.Text.StartsWith("Too many wrong tries", StringComparison.Ordinal))
            Say("");
    }

    private void ShowRecovery(bool show)
    {
        _passwordPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _recoveryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _unlock.IsDefault = !show;
        _setPassword.IsDefault = show;
        Say("");
        if (show) _recoveryKey.Focus();
        else _password.Focus();
        ShowWait();
    }

    private async Task UnlockWithPasswordAsync()
    {
        var password = _password.Password;
        if (password.Length == 0 || _unlocker.WaitLeft > TimeSpan.Zero) return;
        SetBusy(true, "Checking…");
        // Turning a password into a key is slow on purpose (600,000 rounds); keep the window alive meanwhile.
        var key = await Task.Run(() => _unlocker.TryPassword(password));
        if (key is null)
        {
            _password.Clear();
            SetBusy(false);
            if (_unlocker.WaitLeft == TimeSpan.Zero) Say("That is not the password.");
            _password.Focus();
            return;
        }
        if (_useHello.Visibility == Visibility.Visible && _useHello.IsChecked == true)
        {
            Say("Windows Hello will ask for your face, fingerprint or PIN a few times to set itself up.", problem: false);
            if (!await _unlocker.TurnOnHelloAsync(key)) Log.Information("Windows Hello was not turned on for the data lock (cancelled or not available).");
        }
        await FinishAsync(key);
    }

    private async Task UnlockWithHelloAsync()
    {
        SetBusy(true, "Waiting for Windows Hello…");
        var key = await _unlocker.TryHelloAsync();
        if (key is null)
        {
            SetBusy(false);
            Say("Windows Hello did not open the lock. Enter your password instead.");
            _password.Focus();
            return;
        }
        await FinishAsync(key);
    }

    private async Task RecoverAsync()
    {
        if (_unlocker.WaitLeft > TimeSpan.Zero) return;
        if (RecoveryKey.Read(_recoveryKey.Text) is null)
        {
            Say($"A recovery key has {RecoveryKey.Length} letters and digits, in groups of {RecoveryKey.GroupSize}. Check what you typed.");
            return;
        }
        var password = _newPassword.Password;
        if (password.Length == 0)
        {
            Say("Choose a new password.");
            return;
        }
        if (password != _repeatPassword.Password)
        {
            Say("The two new passwords are not the same.");
            return;
        }
        var typed = _recoveryKey.Text;
        SetBusy(true, "Checking…");
        var key = await Task.Run(() =>
        {
            var opened = _unlocker.TryRecoveryKey(typed);
            if (opened is not null) _unlocker.ChangePassword(opened, password);
            return opened;
        });
        if (key is null)
        {
            SetBusy(false);
            if (_unlocker.WaitLeft == TimeSpan.Zero) Say("That is not the recovery key for this data.");
            return;
        }
        Log.Information("The data lock's password was replaced using the recovery key.");
        await FinishAsync(key);
    }

    /// <summary>The key is known: finish whatever was pending (encrypt, decrypt), then hand the key to the app.</summary>
    private async Task FinishAsync(byte[] key)
    {
        Say(_state switch
        {
            DataLockState.TurningOn => "Encrypting your data. This can take up to a minute; please leave DOG.ai open.",
            DataLockState.TurningOff => "Decrypting your data. This can take up to a minute; please leave DOG.ai open.",
            _ => "Opening…",
        }, problem: false);
        try
        {
            var runWith = await Task.Run(() => DataLock.Settle(_paths, _unlocker.File, key));
            if (_state == DataLockState.TurningOn) Log.Information("The data lock was turned on: the database is now encrypted.");
            if (_state == DataLockState.TurningOff) Log.Information("The data lock was turned off: the database is a plain file again.");
            Result = new DatabaseKey(runWith);
            DialogResult = true;
        }
        catch (DataLockException e)
        {
            Log.Error(e, "The data lock could not be settled");
            SetBusy(false);
            Say(e.Message);
        }
    }

    private void CancelTurningOn()
    {
        var answer = MessageBox.Show(this,
            "Your data has not been encrypted yet, so nothing is lost. The password you chose is forgotten and DOG.ai opens as before. You can turn the lock on again in Settings.",
            "Don't lock my data after all?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        if (!DataLock.CancelTurningOn(_paths)) return;
        Log.Information("Turning the data lock on was cancelled before anything was encrypted.");
        Result = DatabaseKey.None;
        DialogResult = true;
    }

    private void ApplyTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = _dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
