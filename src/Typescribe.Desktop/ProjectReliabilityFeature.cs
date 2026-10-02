using System.Collections;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Project-facing reliability commands: transaction undo/redo, recoverable Trash,
/// and safe backup restoration into a separate project directory.
/// </summary>
internal sealed class ProjectReliabilityFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IProjectMutationService _mutations;
    private bool _menuInjected;
    private bool _disposed;

    private ProjectReliabilityFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IProjectMutationService mutations)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _mutations = mutations;
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IProjectMutationService mutations)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(mutations);
        var feature = new ProjectReliabilityFeature(window, viewModel, repository, mutations);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.TryInjectMenu();
    }

    private void OnOpened(object? sender, EventArgs e) => TryInjectMenu();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_menuInjected) TryInjectMenu();
    }

    private void TryInjectMenu()
    {
        if (_disposed || _menuInjected) return;
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable enumerable) return;
        var projectMenu = enumerable.Cast<object>()
            .OfType<MenuItem>()
            .FirstOrDefault(item => HeaderEquals(item, "Project"));
        if (projectMenu is null) return;

        var items = MenuItems(projectMenu.ItemsSource);
        if (items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Trash…")))
        {
            _menuInjected = true;
            return;
        }

        items.Add(new Separator());
        items.Add(Command("Undo Project Change", UndoAsync));
        items.Add(Command("Redo Project Change", RedoAsync));
        items.Add(Command("Trash…", ShowTrashAsync));
        items.Add(Command("Backup Manager…", ShowBackupManagerAsync));
        projectMenu.ItemsSource = items.ToArray();
        _menuInjected = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private async Task UndoAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        try { await _viewModel.SaveNowAsync(); } catch { }
        var root = project.RootPath;
        if (await _mutations.UndoAsync(root)) await _viewModel.OpenProjectAsync(root);
    }

    private async Task RedoAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        try { await _viewModel.SaveNowAsync(); } catch { }
        var root = project.RootPath;
        if (await _mutations.RedoAsync(root)) await _viewModel.OpenProjectAsync(root);
    }

    private async Task ShowTrashAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        var root = project.RootPath;
        var list = new ListBox { MinHeight = 300 };
        var summary = new TextBlock { Opacity = 0.72, Margin = new Thickness(0, 0, 0, 8) };
        var restore = new Button { Content = "Restore", MinWidth = 100 };
        var permanentlyDelete = new Button { Content = "Permanently Delete", MinWidth = 140, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { restore, permanentlyDelete, close }
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(14) };
        grid.Children.Add(summary);
        Grid.SetRow(list, 1); grid.Children.Add(list);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Project Trash",
            Width = 760,
            Height = 500,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = grid
        };

        IReadOnlyList<ProjectTrashItem> trash = [];
        async Task RefreshAsync()
        {
            trash = await _mutations.ListTrashAsync(root);
            list.ItemsSource = trash.Select(item =>
                $"{item.Title}   •   {item.Kind}   •   deleted {item.DeletedAt.LocalDateTime:g}\n{item.OriginalRelativePath}   {item.MetadataSummary}").ToArray();
            summary.Text = trash.Count == 1 ? "1 recoverable item" : $"{trash.Count} recoverable items";
            if (trash.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0;
            restore.IsEnabled = trash.Count > 0;
            permanentlyDelete.IsEnabled = trash.Count > 0;
        }

        restore.Click += async (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index < 0 || index >= trash.Count || _repository.CurrentProject is not { } current) return;
            if (await _mutations.RestoreTrashAsync(current, trash[index].TrashId))
            {
                await _viewModel.OpenProjectAsync(root);
                await RefreshAsync();
            }
        };
        permanentlyDelete.Click += async (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index < 0 || index >= trash.Count) return;
            var item = trash[index];
            if (!await ConfirmAsync(
                    "Permanently Delete",
                    $"Permanently delete '{item.Title}' from Trash? This cannot be undone."))
                return;
            await _mutations.PermanentlyDeleteTrashAsync(root, item.TrashId);
            await RefreshAsync();
        };
        close.Click += (_, _) => dialog.Close();
        await RefreshAsync();
        await dialog.ShowDialog(_window);
    }

    private async Task ShowBackupManagerAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        var root = project.RootPath;
        var backups = await LoadBackupsAsync(root);
        var list = new ListBox
        {
            ItemsSource = backups.Select(static backup => backup.Display).ToArray(),
            MinHeight = 300,
            SelectedIndex = backups.Count > 0 ? 0 : -1
        };
        var summary = new TextBlock
        {
            Text = backups.Count == 1 ? "1 backup" : $"{backups.Count} backups",
            Opacity = 0.72,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var openFolder = new Button { Content = "Open Folder", MinWidth = 100 };
        var restore = new Button { Content = "Restore", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var restoreAs = new Button { Content = "Restore Into New Directory…", MinWidth = 190, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        restore.IsEnabled = backups.Count > 0;
        restoreAs.IsEnabled = backups.Count > 0;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { openFolder, restore, restoreAs, close }
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(14) };
        grid.Children.Add(summary);
        Grid.SetRow(list, 1); grid.Children.Add(list);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Backup Manager",
            Width = 840,
            Height = 520,
            MinWidth = 620,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = grid
        };

        openFolder.Click += (_, _) => OpenFolder(BackupDirectory(root));
        restore.Click += async (_, _) =>
        {
            var selected = SelectedBackup(backups, list.SelectedIndex);
            if (selected is null) return;
            var destination = UniqueRestoreDirectory(root, selected.Timestamp);
            ExtractBackup(selected.Path, destination);
            dialog.Close();
            await _viewModel.OpenProjectAsync(destination);
        };
        restoreAs.Click += async (_, _) =>
        {
            var selected = SelectedBackup(backups, list.SelectedIndex);
            if (selected is null) return;
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a parent folder for the restored project",
                AllowMultiple = false
            });
            var parent = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(parent)) return;
            var projectName = SafeFileName(project.Title);
            var stamp = selected.Timestamp.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var destination = UniqueDirectory(Path.Combine(parent, $"{projectName}-restored-{stamp}"));
            ExtractBackup(selected.Path, destination);
            dialog.Close();
            await _viewModel.OpenProjectAsync(destination);
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(_window);
    }

    private static async Task<IReadOnlyList<BackupEntry>> LoadBackupsAsync(string root)
    {
        var directory = BackupDirectory(root);
        if (!Directory.Exists(directory)) return [];
        return await Task.Run(() =>
        {
            var result = new List<BackupEntry>();
            foreach (var file in new DirectoryInfo(directory).GetFiles("*.zip")
                         .OrderByDescending(static file => file.LastWriteTimeUtc))
            {
                var changed = CountFilesChangedSinceBackup(root, file.FullName, file.LastWriteTimeUtc);
                result.Add(new BackupEntry(file.FullName, file.LastWriteTime, file.Length, changed));
            }
            return (IReadOnlyList<BackupEntry>)result;
        });
    }

    private static int CountFilesChangedSinceBackup(string root, string archivePath, DateTime backupUtc)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var archived = archive.Entries
                .Where(static entry => !string.IsNullOrEmpty(entry.Name))
                .Select(static entry => entry.FullName.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
                .Where(static item => !ShouldSkipProjectPath(item.Relative))
                .ToArray();
            var currentNames = current.Select(static item => item.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = current.Count(item =>
                !archived.Contains(item.Relative) || File.GetLastWriteTimeUtc(item.Path) > backupUtc.AddSeconds(1));
            changed += archived.Count(item => !currentNames.Contains(item));
            return changed;
        }
        catch
        {
            return 0;
        }
    }

    private static void ExtractBackup(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.GetFullPath(Path.Combine(fullDestination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(fullDestination, target);
            if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidDataException("The backup contains an unsafe path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static string UniqueRestoreDirectory(string projectRoot, DateTime timestamp)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var parent = Path.GetDirectoryName(full) ?? full;
        var name = Path.GetFileName(full);
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return UniqueDirectory(Path.Combine(parent, $"{name}-restored-{stamp}"));
    }

    private static string UniqueDirectory(string desired)
    {
        if (!Directory.Exists(desired) && !File.Exists(desired)) return desired;
        for (var index = 2; index < 100_000; index++)
        {
            var candidate = desired + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to create a unique restore directory.");
    }

    private static BackupEntry? SelectedBackup(IReadOnlyList<BackupEntry> backups, int index)
        => index >= 0 && index < backups.Count ? backups[index] : null;

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 480,
            Margin = new Thickness(0, 0, 0, 16)
        };
        var yes = new Button { Content = "Delete", MinWidth = 90 };
        var no = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { yes, no }
        };
        var panel = new StackPanel { Margin = new Thickness(16), Children = { text, buttons } };
        var dialog = new Window
        {
            Title = title,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>( _window);
    }

    private static void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static string BackupDirectory(string root)
        => Path.Combine(root, ".typescribe", "backups");

    private static bool ShouldSkipProjectPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.StartsWith("build/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".typescribe/backups/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".typescribe/recovery/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Typescribe" : result;
    }

    private static MenuItem Command(string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { await ShowErrorAsync(item, ex.Message); }
        };
        return item;
    }

    private static Task ShowErrorAsync(MenuItem item, string message)
    {
        ToolTip.SetTip(item, message);
        return Task.CompletedTask;
    }

    private static List<object> MenuItems(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object>().ToList() : [];

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals((item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private sealed record BackupEntry(string Path, DateTime Timestamp, long SizeBytes, int ChangedFiles)
    {
        public string Display =>
            $"{Timestamp:g}   •   {FormatSize(SizeBytes)}   •   {ChangedFiles} file{(ChangedFiles == 1 ? string.Empty : "s")} changed since backup   •   {System.IO.Path.GetFileName(Path)}";

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        }
    }
}
