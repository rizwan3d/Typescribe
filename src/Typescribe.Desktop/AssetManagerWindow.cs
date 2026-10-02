using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

internal static class AssetManagerWindow
{
    public static async Task ShowAsync(Window owner, BookProject project, AssetManagerService service)
    {
        var list = new ListBox { MinWidth = 300 };
        var preview = new Image { Stretch = Avalonia.Media.Stretch.Uniform, MaxHeight = 320, Margin = new Thickness(12) };
        var details = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 12) };
        Bitmap? bitmap = null;

        var import = new Button { Content = "Import…" };
        var rename = new Button { Content = "Rename…", Margin = new Thickness(6, 0, 0, 0) };
        var replace = new Button { Content = "Replace…", Margin = new Thickness(6, 0, 0, 0) };
        var close = new Button { Content = "Close", Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), Children = { import, rename, replace, close } };
        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { new StackPanel { Children = { preview, details } } } };
        Grid.SetRow(buttons, 1);
        right.Children.Add(buttons);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
        root.Children.Add(list);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        var dialog = new Window { Title = "Project Assets", Width = 900, Height = 600, MinWidth = 660, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };

        async Task RefreshAsync(string? selectPath = null)
        {
            var assets = await service.ListAsync(project);
            var rows = assets.Select(static asset => new AssetRow(asset)).ToArray();
            list.ItemsSource = rows;
            list.SelectedItem = selectPath is null
                ? rows.FirstOrDefault()
                : rows.FirstOrDefault(row => string.Equals(row.Asset.RelativePath, selectPath, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        }

        void RefreshDetails()
        {
            bitmap?.Dispose();
            bitmap = null;
            preview.Source = null;
            if (list.SelectedItem is not AssetRow selected)
            {
                details.Text = "No assets.";
                return;
            }
            var asset = selected.Asset;
            var usage = asset.Usages.Count == 0
                ? "Not used in included manuscript content."
                : "Used at:\n" + string.Join("\n", asset.Usages.Select(static location => $"• {location.DocumentTitle}:{location.Line}"));
            details.Text = $"{asset.RelativePath}\n{asset.Dimensions}\n{FormatBytes(asset.FileSize)}\n{(asset.Missing ? "Missing" : asset.IsUnused ? "Unused" : "In use")}\n\n{usage}";
            if (!asset.Missing)
            {
                try
                {
                    bitmap = new Bitmap(service.ResolveManagedPath(project, asset.RelativePath));
                    preview.Source = bitmap;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    details.Text += "\n\nPreview unavailable.";
                }
            }
        }

        list.SelectionChanged += (_, _) => RefreshDetails();
        import.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Image",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.tif", "*.tiff"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            var relative = await service.ImportAsync(project, path);
            await RefreshAsync(relative);
        };
        rename.Click += async (_, _) =>
        {
            if (list.SelectedItem is not AssetRow selected || selected.Asset.Missing) return;
            var name = await PromptAsync(dialog, "Rename Asset", "File name", selected.Asset.FileName);
            if (string.IsNullOrWhiteSpace(name)) return;
            var relative = await service.RenameAsync(project, selected.Asset.RelativePath, name);
            await RefreshAsync(relative);
        };
        replace.Click += async (_, _) =>
        {
            if (list.SelectedItem is not AssetRow selected || selected.Asset.Missing) return;
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Replace Asset",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.tif", "*.tiff"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            await service.ReplaceAsync(project, selected.Asset.RelativePath, path);
            await RefreshAsync(selected.Asset.RelativePath);
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => bitmap?.Dispose();

        await RefreshAsync();
        await dialog.ShowDialog(owner);
    }

    private static async Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", MinWidth = 86 };
        var cancel = new Button { Content = "Cancel", MinWidth = 86, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { new TextBlock { Text = label }, box, buttons } };
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(owner);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / (1024d * 1024d):0.##} MB";
    }

    private sealed record AssetRow(ProjectAsset Asset)
    {
        public override string ToString()
            => $"{(Asset.Missing ? "✕" : Asset.IsUnused ? "⚠" : "✓")} {Asset.FileName}  {Asset.Dimensions}";
    }
}
