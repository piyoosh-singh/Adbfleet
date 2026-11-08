using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbFleet.Views;

public static class Dialogs
{
    public static async Task<bool> Confirm(string title, string body, string primary, bool destructive = false, CheckBox? extra = null)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        if (extra != null) content.Children.Add(extra);
        var dlg = new ContentDialog
        {
            XamlRoot = Fleet.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> Prompt(string title, string label, string initial = "", string primary = "Save")
    {
        var box = new TextBox { Header = label, Text = initial };
        var dlg = new ContentDialog
        {
            XamlRoot = Fleet.XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    public static async Task<List<string>> PickFiles(params string[] extensions)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Fleet.Hwnd);
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? new();
    }

    public static async Task<string?> PickFile(params string[] extensions)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Fleet.Hwnd);
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        var f = await picker.PickSingleFileAsync();
        return f?.Path;
    }

    public static async Task<string?> PickFolder()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Fleet.Hwnd);
        picker.FileTypeFilter.Add("*");
        var f = await picker.PickSingleFolderAsync();
        return f?.Path;
    }

    public static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
