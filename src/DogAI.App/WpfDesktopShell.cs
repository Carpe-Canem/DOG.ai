using System.IO;
using System.Windows;
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

    public void CopyText(string text) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            try { Clipboard.SetDataObject(text, copy: true); }
            catch (System.Runtime.InteropServices.COMException) { /* another program is holding the clipboard */ }
        });
}
