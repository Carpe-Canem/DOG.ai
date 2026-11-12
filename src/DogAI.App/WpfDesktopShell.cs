using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DogAI.Core.Settings;
using Microsoft.Win32;

namespace DogAI.App;

public sealed class WpfDesktopShell : IDesktopShell
{
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

    private static Task<string?> PickFileAsync(string title, string filter) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;

    public async Task<string?> SaveFileAsync(string suggestedName, string filter, byte[] content)
    {
        var path = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog { Title = "Save", Filter = filter, FileName = SafeFileName(suggestedName), OverwritePrompt = true };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
        if (path is not null) await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private const int AvatarSize = 256;

    public Task<(byte[] Png, byte[] Avatar)> PreparePictureAsync(byte[] picture) => Task.Run(() =>
    {
        BitmapSource frame;
        try
        {
            using var input = new MemoryStream(picture);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            throw new InvalidOperationException("That picture could not be read.");
        }

        // Keep a PNG as it is (it may be a card); convert anything else so it can carry a card when exported.
        var png = Core.Characters.PngText.IsPng(picture) ? picture : Encode(frame);

        // Square crop: centred across, a little below the top for tall pictures, where faces usually are.
        int w = frame.PixelWidth, h = frame.PixelHeight, side = Math.Min(w, h);
        var crop = new CroppedBitmap(frame, new Int32Rect((w - side) / 2, (h - side) / 4, side, side));
        var scale = Math.Min(1.0, (double)AvatarSize / side);
        var small = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
        RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.HighQuality);
        return (png, Encode(small));
    });

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
