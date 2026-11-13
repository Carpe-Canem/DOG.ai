using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DogAI.Core.Settings;
using Microsoft.Win32;

namespace DogAI.App;

public sealed class WpfDesktopShell(ISettingsStore settings, DogAI.Core.Privacy.DatabaseKey key) : IDesktopShell
{
    /// <summary>Set once DOG.ai is quitting or restarting, so closing the window really closes it (never hides it in the tray).</summary>
    public static bool Exiting { get; private set; }

    public Task<string?> PickModelFileAsync(string? startFolder) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a model file",
                Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
                dialog.InitialDirectory = startFolder;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;

    public Task<string?> PickFolderAsync(string? startFolder) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder with model files" };
            if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
                dialog.InitialDirectory = startFolder;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
        }).Task;

    public Task<string?> PickCardFileAsync() => PickFileAsync("Choose a character card",
        "Character cards (*.png;*.json)|*.png;*.json|All files (*.*)|*.*");

    public Task<string?> PickImageFileAsync() => PickFileAsync("Choose a picture",
        "Pictures (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|All files (*.*)|*.*");

    public Task<IReadOnlyList<string>> PickDocumentFilesAsync() =>
        Application.Current.Dispatcher.InvokeAsync<IReadOnlyList<string>>(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add documents",
                Filter = DogAI.Core.Documents.TextExtractor.DialogFilter,
                CheckFileExists = true,
                Multiselect = true,
            };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
        }).Task;

    private static readonly HashSet<string> ViewableTypes = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".md", ".markdown", ".csv", ".log" };

    public bool OpenFile(string path)
    {
        if (!File.Exists(path)) return false;
        var full = Path.GetFullPath(path);
        // Documents open in the program Windows has for them. Anything else (code, scripts, web pages) opens in Notepad,
        // so "opening" a .bat, .ps1 or .js file shows it instead of running it.
        var start = ViewableTypes.Contains(Path.GetExtension(full))
            ? new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true }
            : new System.Diagnostics.ProcessStartInfo("notepad.exe") { ArgumentList = { full }, UseShellExecute = false };
        System.Diagnostics.Process.Start(start)?.Dispose();
        return true;
    }

    public Task<string?> PickSavePathAsync(string suggestedName, string filter) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog { Title = "Save", Filter = filter, FileName = SafeFileName(suggestedName), OverwritePrompt = true };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;

    public Task<string?> PickOpenPathAsync(string title, string filter) => PickFileAsync(title, filter);

    public bool OpenUrl(string url)
    {
        // Only web and mail addresses, never a file path or another program's scheme.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public void Restart() => RestartProcess(hidden: false);

    /// <summary>
    /// Starts a new DOG.ai (which waits for this one to end) and ends this one. With <paramref name="hidden"/> a locked
    /// DOG.ai keeps its lock screen in the tray, as the window was.
    /// </summary>
    public static void RestartProcess(bool hidden)
    {
        Exiting = true;
        var self = Environment.ProcessPath;
        if (self is not null)
        {
            var start = new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false, ArgumentList = { App.AfterArgument, Environment.ProcessId.ToString() } };
            if (hidden) start.ArgumentList.Add(App.HiddenArgument);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
    }

    /// <summary>Quitting from the tray: the window must close, not hide.</summary>
    public static void MarkExiting() => Exiting = true;

    private static Task<string?> PickFileAsync(string title, string filter) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;

    public async Task<string?> SaveFileAsync(string suggestedName, string filter, byte[] content)
    {
        // Exports (chats, cards, stories) are ordinary files. With the data lock on, say so once, the first time.
        var noteFirst = false;
        if (key.Locked)
        {
            var s = await settings.LoadAsync();
            if (!s.PlainExportNoted)
            {
                noteFirst = true;
                await settings.SaveAsync(s with { PlainExportNoted = true });
            }
        }
        var path = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (noteFirst)
                MessageBox.Show(Application.Current.MainWindow!,
                    "The file you save next is not locked: anyone who can open it can read it. Your data inside DOG.ai stays locked. " +
                    "(This note is shown once.)",
                    "Exported files are plain files", MessageBoxButton.OK, MessageBoxImage.Information);
            var dialog = new SaveFileDialog { Title = "Save", Filter = filter, FileName = SafeFileName(suggestedName), OverwritePrompt = true };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
        if (path is not null) await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private const int AvatarSize = 256;

    public Task<(byte[] Png, byte[] Avatar)> PreparePictureAsync(byte[] picture) => Task.Run(() =>
    {
        var frame = Decode(picture);
        // Keep a PNG as it is (it may be a card); convert anything else so it can carry a card when exported.
        var png = Core.Characters.PngText.IsPng(picture) ? picture : Encode(frame);
        return (png, Avatar(frame, Core.Characters.AvatarCrop.Default(frame.PixelWidth, frame.PixelHeight)));
    });

    public Task<byte[]> CropAvatarAsync(byte[] picture, Core.Characters.AvatarCrop crop) => Task.Run(() =>
    {
        var frame = Decode(picture);
        return Avatar(frame, crop.Within(frame.PixelWidth, frame.PixelHeight));
    });

    private static BitmapSource Decode(byte[] picture)
    {
        try
        {
            using var input = new MemoryStream(picture);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            throw new InvalidOperationException("That picture could not be read.");
        }
    }

    /// <summary>The square cut out and scaled down to at most <see cref="AvatarSize"/> pixels.</summary>
    private static byte[] Avatar(BitmapSource frame, Core.Characters.AvatarCrop square)
    {
        var crop = new CroppedBitmap(frame, new Int32Rect(square.X, square.Y, square.Side, square.Side));
        var scale = Math.Min(1.0, (double)AvatarSize / square.Side);
        var small = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
        RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.HighQuality);
        return Encode(small);
    }

    private static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    public void CopyText(string text) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            try { Clipboard.SetDataObject(text, copy: true); }
            catch (System.Runtime.InteropServices.COMException) { /* another program is holding the clipboard */ }
        });
}
