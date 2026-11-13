using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DogAI.Core;
using DogAI.Core.Privacy;
using DogAI.Data;
using Serilog;

namespace DogAI.App;

/// <summary>
/// The lock screen: the only thing DOG.ai shows while its data is locked. Nothing else is running behind it: no database,
/// no web view, no global key, no model (a tray icon with Quit only, when the tray icon is on). It asks for the password,
/// offers Windows Hello when that is on for this PC, and lets a forgotten password be replaced through the recovery key.
/// It wears the look chosen in Settings (copied into <c>lock.json</c>, since the settings themselves are locked away).
///
/// The same screen finishes turning the lock on (the database is encrypted the first time the new password is entered
/// here, so data is never encrypted under a password the owner cannot repeat) and off.
/// </summary>
public sealed class LockWindow : Window
{
    private readonly AppPaths _paths;
    private DataLockState _state;
    private readonly Unlocker _unlocker;
    private readonly LockPalette _look;
    private readonly bool _dark;

    private readonly TextBlock _heading = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _explanation = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };

    private readonly PasswordBox _password = new();
    private readonly Button _unlock = new() { IsDefault = true };
    private readonly Button _hello = new() { Content = "Use Windows Hello", Visibility = Visibility.Collapsed };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _forgot = new() { Content = "I forgot my password" };
    private readonly Button _cancelLock = new() { Content = "Don't lock my data after all", Visibility = Visibility.Collapsed };
    private readonly Button _keepLocked = new() { Content = "Keep my data locked", Visibility = Visibility.Collapsed };

    private readonly StackPanel _passwordPanel = new();
    private readonly StackPanel _recoveryPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _recoveryKey = new();
    private readonly PasswordBox _newPassword = new();
    private readonly PasswordBox _repeatPassword = new();
    private readonly Button _setPassword = new() { Content = "Set new password and unlock" };
    private readonly Button _back = new() { Content = "Back" };

    private readonly StackPanel _helloPanel = new() { Visibility = Visibility.Collapsed };
    private readonly Button _offerHello = new() { Content = "Set up Windows Hello" };
    private readonly Button _notNow = new() { Content = "Not now" };
    private byte[]? _keyWaiting;

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;

    /// <summary>Set when the window closes with a result: the key to run with (its bytes are null when the lock was just turned off).</summary>
    public DatabaseKey? Result { get; private set; }

    public LockWindow(AppPaths paths, LockFile file, DataLockState state, IHelloSigner hello)
    {
        _paths = paths;
        _state = state;
        _unlocker = new Unlocker(file, paths.LockFile, paths.UnlockTriesFile, hello);
        // The look saved with the lock; "Match Windows" (no theme saved) follows what Windows is set to.
        using (var windows = new WindowsSystemTheme()) _dark = file.Theme switch { "dark" => true, "light" => false, _ => windows.IsDark };
        _look = LockPalette.For(file.Skin, _dark);
        Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/DOG.ai;component/LockStyles.xaml", UriKind.Relative)));
        _look.ApplyTo(this);

        Title = "DOG.ai";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = _look.FontSize;
        FontFamily = (FontFamily)Resources["UiFont"];
        Background = (Brush)Resources["Bg"];
        Foreground = (Brush)Resources["Text"];
        Icon = AppIcon();
        SourceInitialized += (_, _) => ApplyTitleBar();

        Content = Build();
        ShowState();
        _unlock.Click += async (_, _) => await UnlockWithPasswordAsync();
        _hello.Click += async (_, _) => await UnlockWithHelloAsync();
        _forgot.Click += (_, _) => ShowRecovery(true);
        _back.Click += (_, _) => ShowRecovery(false);
        _setPassword.Click += async (_, _) => await RecoverAsync();
        _cancelLock.Click += (_, _) => CancelTurningOn();
        _keepLocked.Click += (_, _) => KeepLocked();
        _offerHello.Click += async (_, _) => await SetUpHelloAsync();
        _notNow.Click += async (_, _) => await NotNowAsync();
        _clock.Tick += (_, _) => ShowWait();
        Loaded += (_, _) =>
        {
            _password.Focus();
            ShowWait();
            _clock.Start();
            if (_unlocker.HelloIsOn && _state != DataLockState.TurningOff) _hello.Visibility = Visibility.Visible;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _password.Focus();
        };
        Closed += (_, _) => _clock.Stop();
    }

    private UIElement Build()
    {
        Id(_password, "Password");
        Id(_unlock, "Unlock");
        Id(_hello, "Hello");
        Id(_message, "Message");
        Id(_forgot, "Forgot");
        Id(_cancelLock, "CancelLock");
        Id(_keepLocked, "KeepLocked");
        Id(_recoveryKey, "RecoveryKey");
        Id(_newPassword, "NewPassword");
        Id(_repeatPassword, "RepeatPassword");
        Id(_setPassword, "SetPassword");
        Id(_back, "Back");
        Id(_offerHello, "OfferHello");
        Id(_notNow, "NotNow");
        Id(_heading, "Heading");

        _unlock.Style = (Style)Resources["Primary"];
        _setPassword.Style = (Style)Resources["Primary"];
        _offerHello.Style = (Style)Resources["Primary"];
        foreach (var link in new[] { _forgot, _cancelLock, _keepLocked }) link.Style = (Style)Resources["Link"];

        _heading.FontFamily = (FontFamily)Resources["HeadFont"];
        _explanation.Foreground = (Brush)Resources["Muted"];

        var card = new StackPanel();
        card.Children.Add(Wordmark());
        card.Children.Add(Spaced(_heading, 18));
        card.Children.Add(Spaced(_explanation, 6));

        _passwordPanel.Children.Add(Spaced(Caption("Password", _password), 20));
        _passwordPanel.Children.Add(Spaced(_password, 6));
        _passwordPanel.Children.Add(Spaced(Row(_unlock, _hello), 16));
        _passwordPanel.Children.Add(Spaced(Row(_forgot, _cancelLock, _keepLocked), 20));
        card.Children.Add(_passwordPanel);

        _recoveryPanel.Children.Add(Spaced(Words(
            "Enter the recovery key you printed or wrote down when you turned the lock on, then choose a new password."), 20));
        _recoveryPanel.Children.Add(Spaced(Words(
            "If you lose both the password and the recovery key, your data is gone for good: nobody can open it, not even DOG.ai.", muted: true), 8));
        _recoveryPanel.Children.Add(Spaced(Caption("Recovery key", _recoveryKey), 16));
        _recoveryPanel.Children.Add(Spaced(_recoveryKey, 6));
        _recoveryPanel.Children.Add(Spaced(Caption("New password", _newPassword), 12));
        _recoveryPanel.Children.Add(Spaced(_newPassword, 6));
        _recoveryPanel.Children.Add(Spaced(Caption("New password again", _repeatPassword), 12));
        _recoveryPanel.Children.Add(Spaced(_repeatPassword, 6));
        _recoveryPanel.Children.Add(Spaced(Row(_setPassword, _back), 18));
        card.Children.Add(_recoveryPanel);

        _helloPanel.Children.Add(Spaced(Words(
            "Next time, open DOG.ai on this PC with your face, fingerprint or Windows PIN instead of typing the password. " +
            "The password and recovery key keep working, and other PCs are not affected."), 20));
        _helloPanel.Children.Add(Spaced(Words("Windows asks for your face, fingerprint or PIN a few times while it sets this up.", muted: true), 8));
        _helloPanel.Children.Add(Spaced(Row(_offerHello, _notNow), 18));
        card.Children.Add(_helloPanel);

        card.Children.Add(Spaced(_message, 16));

        var frame = new Border
        {
            Background = (Brush)Resources["Surface"],
            BorderBrush = (Brush)Resources["Line"],
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Resources["Radius"],
            Padding = new Thickness(30, 26, 30, 26),
            Child = card,
        };
        return new Border { Padding = new Thickness(22), Child = frame };
    }

    /// <summary>Heading and wording for where the lock stands; called again after "Keep my data locked".</summary>
    private void ShowState()
    {
        (_heading.Text, _explanation.Text, _unlock.Content) = _state switch
        {
            DataLockState.TurningOn => ("Enter your new password",
                "Your data is locked as soon as you enter the password you just chose. Encrypting it can take up to a minute.",
                "Lock my data"),
            DataLockState.TurningOff => ("Enter your password to turn the lock off",
                "Your data is then stored as plain files again, readable by anyone with access to this PC.",
                "Turn the lock off"),
            _ => ("DOG.ai is locked", "Enter your password to open your chats, characters, stories and documents.", "Unlock"),
        };
        // While the lock is being turned on the data is still plain, so backing out loses nothing; and without a recovery
        // key in hand yet, "forgot" has no use.
        _cancelLock.Visibility = _state == DataLockState.TurningOn ? Visibility.Visible : Visibility.Collapsed;
        _forgot.Visibility = _state == DataLockState.TurningOn ? Visibility.Collapsed : Visibility.Visible;
        _keepLocked.Visibility = _state == DataLockState.TurningOff ? Visibility.Visible : Visibility.Collapsed;
        _hello.Visibility = _unlocker.HelloIsOn && _state != DataLockState.TurningOff ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement Wordmark()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (Icon is not null) row.Children.Add(new Image { Source = Icon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { FontSize = 19, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)Resources["HeadFont"] };
        name.Inlines.Add(new System.Windows.Documents.Run("DOG"));
        name.Inlines.Add(new System.Windows.Documents.Run(".ai") { Foreground = _look.AccentWordmark ? (Brush)Resources["Accent"] : (Brush)Resources["Text"] });
        row.Children.Add(name);
        return row;
    }

    private static ImageSource? AppIcon()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is ArgumentException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return null;
        }
    }

    private static void Id(DependencyObject control, string id) => AutomationProperties.SetAutomationId(control, id);

    private TextBlock Caption(string text, UIElement target)
    {
        AutomationProperties.SetName(target, text);
        return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold };
    }

    private TextBlock Words(string text, bool muted = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 21,
        Foreground = muted ? (Brush)Resources["Muted"] : (Brush)Resources["Text"],
    };

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    private static WrapPanel Row(params FrameworkElement[] items)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 0, 14, 0);
            item.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(item);
        }
        return row;
    }

    private void Say(string text, bool problem = true)
    {
        _message.Text = text;
        _message.Foreground = problem ? (Brush)Resources["Bad"] : (Brush)Resources["Muted"];
    }

    private void SetBusy(bool busy, string? saying = null)
    {
        _busy = busy;
        foreach (var control in new Control[] { _password, _unlock, _hello, _forgot, _cancelLock, _keepLocked, _recoveryKey, _newPassword, _repeatPassword, _setPassword, _back, _offerHello, _notNow })
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

    private void ShowPanel(StackPanel shown)
    {
        foreach (var panel in new[] { _passwordPanel, _recoveryPanel, _helloPanel })
            panel.Visibility = panel == shown ? Visibility.Visible : Visibility.Collapsed;
        _unlock.IsDefault = shown == _passwordPanel;
        _setPassword.IsDefault = shown == _recoveryPanel;
        _offerHello.IsDefault = shown == _helloPanel;
    }

    private void ShowRecovery(bool show)
    {
        ShowPanel(show ? _recoveryPanel : _passwordPanel);
        if (show)
        {
            _heading.Text = "Use your recovery key";
            _explanation.Text = "";
            _explanation.Visibility = Visibility.Collapsed;
        }
        else
        {
            _explanation.Visibility = Visibility.Visible;
            ShowState();
        }
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
        await AfterPasswordAsync(key);
    }

    /// <summary>
    /// The password (or recovery key) opened the lock. On a PC where Windows Hello is set up but not yet used for DOG.ai,
    /// offer it once before going on; "Not now" is remembered for this PC.
    /// </summary>
    private async Task AfterPasswordAsync(byte[] key)
    {
        if (_state != DataLockState.TurningOff && await _unlocker.ShouldOfferHelloAsync())
        {
            _keyWaiting = key;
            SetBusy(false);
            Say("", problem: false);
            _heading.Text = "Use Windows Hello on this PC?";
            _explanation.Visibility = Visibility.Collapsed;
            ShowPanel(_helloPanel);
            _offerHello.Focus();
            return;
        }
        await FinishAsync(key);
    }

    private async Task SetUpHelloAsync()
    {
        if (_keyWaiting is not { } key) return;
        SetBusy(true, "Waiting for Windows Hello…");
        if (await _unlocker.TurnOnHelloAsync(key)) Log.Information("Windows Hello was turned on for the data lock on this PC.");
        else Log.Information("Windows Hello was not turned on for the data lock (cancelled or not available).");
        await FinishAsync(key);
    }

    private async Task NotNowAsync()
    {
        if (_keyWaiting is not { } key) return;
        _unlocker.DeclineHello();
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
        if (LockPassword.Problem(password, _repeatPassword.Password) is { } problem)
        {
            Say(problem);
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
        await AfterPasswordAsync(key);
    }

    /// <summary>The key is known: finish whatever was pending (encrypt, decrypt), then hand the key to the app.</summary>
    private async Task FinishAsync(byte[] key)
    {
        SetBusy(true);
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
            Close();
        }
        catch (DataLockException e)
        {
            Log.Error(e, "The data lock could not be settled");
            SetBusy(false);
            ShowRecovery(false);
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
        Close();
    }

    /// <summary>Backs out of turning the lock off: the data stays encrypted and this becomes the ordinary lock screen.</summary>
    private void KeepLocked()
    {
        _unlocker.KeepLocked();
        Log.Information("Turning the data lock off was cancelled; the data stays locked.");
        _state = DataLockState.Locked;
        ShowState();
        Say("Your data stays locked. Enter your password to open DOG.ai.", problem: false);
        _password.Focus();
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
