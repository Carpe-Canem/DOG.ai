using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DogAI.Core;
using DogAI.Core.Appearance;
using DogAI.Core.Net;
using DogAI.UI;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace DogAI.App;

public partial class MainWindow : Window
{
    private readonly NetworkAudit _audit;

    public MainWindow(IServiceProvider services)
    {
        _audit = services.GetRequiredService<NetworkAudit>();
        var paths = services.GetRequiredService<AppPaths>();

        InitializeComponent();

        // With "keep running in the tray" on, closing the window only hides it; Quit in the tray menu really exits.
        var integration = services.GetRequiredService<WindowsIntegration>();
        Closing += (_, e) =>
        {
            if (!integration.ShouldHideOnClose) return;
            e.Cancel = true;
            Hide();
        };

        WebView.BlazorWebViewInitializing += (_, e) =>
        {
            e.UserDataFolder = paths.WebViewDataDirectory;
            // Turn off the browser engine's own background network features.
            e.EnvironmentOptions = new CoreWebView2EnvironmentOptions(
                "--disable-features=msSmartScreenProtection,msEdgeTranslate,OptimizationHints " +
                "--disable-background-networking --disable-component-update --disable-domain-reliability --no-pings");
        };
        WebView.BlazorWebViewInitialized += (_, e) => LockDown(e.WebView.CoreWebView2);
        WebView.UrlLoading += (_, e) =>
        {
            if (OriginPolicy.IsAppUri(e.Url.AbsoluteUri)) return;
            // External links never open inside the app. (A confirm-then-open-in-browser prompt comes with the real UI.)
            e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
            _audit.Record("webview", e.Url.GetLeftPart(UriPartial.Path), allowed: false, "external link");
        };

        // Ctrl+L locks DOG.ai (when the lock is on). The page listens for it too (LockKey); whichever sees it first wins,
        // and a second call does nothing.
        var appLock = services.GetRequiredService<DogAI.Core.Privacy.IAppLock>();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.L || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            if (!appLock.CanLock) return;
            e.Handled = true;
            appLock.LockNow();
        };

        var appearance = services.GetRequiredService<AppearanceState>();
        SourceInitialized += (_, _) => ApplyTitleBar(appearance);
        appearance.Changed += () => Dispatcher.Invoke(() => ApplyTitleBar(appearance));

        WebView.Services = services;
        WebView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Root) });
    }

    /// <summary>Stops the embedded browser from loading or navigating to anything except the app's own pages.</summary>
    private void LockDown(CoreWebView2 core)
    {
        var s = core.Settings;
        s.IsReputationCheckingRequired = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
#if !DEBUG
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
#endif

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (OriginPolicy.IsAppUri(e.Request.Uri)) return;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by DOG.ai", "");
            _audit.Record("webview", StripQuery(e.Request.Uri), allowed: false, "outside app origin");
        };
        core.NavigationStarting += (_, e) =>
        {
            if (OriginPolicy.IsAppUri(e.Uri)) return;
            e.Cancel = true;
            _audit.Record("webview", StripQuery(e.Uri), allowed: false, "navigation outside app");
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
    }

    /// <summary>Makes the Windows title bar dark or light to match the app.</summary>
    private void ApplyTitleBar(AppearanceState appearance)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = appearance.EffectiveTheme == Themes.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static string StripQuery(string uri)
    {
        var i = uri.IndexOfAny(['?', '#']);
        return i < 0 ? uri : uri[..i];
    }
}
