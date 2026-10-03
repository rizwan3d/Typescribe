using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Persists dirty editor buffers independently of autosave. Recovery files are
/// never copied over canonical manuscripts unless the user explicitly chooses a
/// restore action after a later launch.
/// </summary>
internal sealed partial class RecoveryJournalFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private string? _loadedRoot;
    private bool _blockedByPreviousRecovery;
    private bool _checkingRecovery;
    private bool _disposed;

    private RecoveryJournalFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await JournalCurrentBufferAsync();
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);
        var feature = new RecoveryJournalFeature(window, viewModel, repository);
        window.Opened += feature.OnOpened;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.QueueProjectCheck();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _timer.Start();
        QueueProjectCheck();
    }

    private void OnStateChanged(object? sender, EventArgs e) => QueueProjectCheck();

    private void QueueProjectCheck()
    {
        if (_disposed || _checkingRecovery) return;
        _checkingRecovery = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await EnsureProjectRecoveryCheckedAsync(); }
            finally { _checkingRecovery = false; }
        }, DispatcherPriority.Background);
    }

    private async Task EnsureProjectRecoveryCheckedAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        var root = project.RootPath;
        if (string.Equals(root, _loadedRoot, StringComparison.Ordinal)) return;
        _loadedRoot = root;
        _blockedByPreviousRecovery = false;

        var session = await LoadSessionAsync(root);
        if (session is null || session.Documents.Count == 0 ||
            string.Equals(session.SessionId, _sessionId, StringComparison.Ordinal))
            return;

        _blockedByPreviousRecovery = true;
        var action = await ShowRecoveryPromptAsync(session);
        switch (action)
        {
            case RecoveryAction.RestoreAll:
                await RestoreAllAsync(project, session);
                _blockedByPreviousRecovery = false;
                await _viewModel.OpenProjectAsync(root);
                break;
            case RecoveryAction.Review:
                await ReviewRecoveryAsync(project, session);
                _blockedByPreviousRecovery = File.Exists(SessionPath(root));
                if (!_blockedByPreviousRecovery) await _viewModel.OpenProjectAsync(root);
                break;
            case RecoveryAction.Discard:
                DeleteRecoveryDirectory(root);
                _blockedByPreviousRecovery = false;
                break;
            default:
                // Keep the previous session untouched if the prompt is dismissed.
                break;
        }
    }

    private async Task JournalCurrentBufferAsync()
    {
        if (_disposed || _blockedByPreviousRecovery || _repository.CurrentProject is not { } project) return;
        var node = _viewModel.SelectedRow?.Node;
        if (node?.IsDocument != true) return;

        await _gate.WaitAsync();
        try
        {
            string canonical;
            try { canonical = await _repository.ReadDocumentAsync(project, node); }
            catch { return; }
            var buffer = _viewModel.EditorText;
            var session = await LoadSessionAsync(project.RootPath) ?? new RecoverySession
            {
                Version = 1,
                SessionId = _sessionId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            // A foreign session is preserved until the user makes an explicit recovery decision.
            if (!string.Equals(session.SessionId, _sessionId, StringComparison.Ordinal) && session.Documents.Count > 0)
            {
                _blockedByPreviousRecovery = true;
                return;
            }

            var existing = session.Documents.FindIndex(item =>
                string.Equals(item.PersistentId, node.PersistentId, StringComparison.Ordinal));
            if (string.Equals(buffer, canonical, StringComparison.Ordinal))
            {
                DeleteIfExists(RecoveryDocumentPath(project.RootPath, node.PersistentId));
                if (existing >= 0) session.Documents.RemoveAt(existing);
            }
            else
            {
                Directory.CreateDirectory(RecoveryRoot(project.RootPath));
                await AtomicWriteTextAsync(RecoveryDocumentPath(project.RootPath, node.PersistentId), buffer);
                var document = new RecoveryDocument
                {
                    PersistentId = node.PersistentId,
                    Title = node.Title,
                    RelativePath = node.RelativePath ?? string.Empty,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                if (existing >= 0) session.Documents[existing] = document;
                else session.Documents.Add(document);
            }

            if (session.Documents.Count == 0)
            {
                DeleteIfExists(SessionPath(project.RootPath));
                TryDeleteEmptyRecoveryRoot(project.RootPath);
                return;
            }

            session.SessionId = _sessionId;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveSessionAsync(project.RootPath, session);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecoveryAction> ShowRecoveryPromptAsync(RecoverySession session)
    {
        var message = new TextBlock
        {
            Text = "TypeScribe found unsaved work from your previous session.",
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var detail = new TextBlock
        {
            Text = session.Documents.Count == 1
                ? "1 document has recoverable changes. Recovery will not overwrite the manuscript unless you choose Restore."
                : $"{session.Documents.Count} documents have recoverable changes. Recovery will not overwrite manuscripts unless you choose Restore.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 8, 0, 18)
        };
        var restore = new Button { Content = "Restore All", MinWidth = 110 };
        var review = new Button { Content = "Review Changes", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        var discard = new Button { Content = "Discard Recovery", MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { restore, review, discard }
        };
        var content = new StackPanel { Margin = new Thickness(18), Children = { message, detail, buttons } };
        var dialog = new Window
        {
            Title = "Recover Unsaved Work",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        restore.Click += (_, _) => dialog.Close(RecoveryAction.RestoreAll);
        review.Click += (_, _) => dialog.Close(RecoveryAction.Review);
        discard.Click += (_, _) => dialog.Close(RecoveryAction.Discard);
        return await dialog.ShowDialog<RecoveryAction>(_window);
    }

    private async Task RestoreAllAsync(BookProject project, RecoverySession session)
    {
        foreach (var document in session.Documents.ToArray())
        {
            var node = FindNode(project.Root, document.PersistentId);
            var path = RecoveryDocumentPath(project.RootPath, document.PersistentId);
            if (node?.IsDocument != true || !File.Exists(path)) continue;
            var content = await File.ReadAllTextAsync(path);
            await _repository.SaveDocumentAsync(project, node, content);
        }
        DeleteRecoveryDirectory(project.RootPath);
    }

    private async Task ReviewRecoveryAsync(BookProject project, RecoverySession session)
    {
        var documents = session.Documents.ToList();
        var list = new ListBox
        {
            ItemsSource = documents.Select(item => $"{item.Title}   •   {item.RelativePath}").ToArray(),
            MinWidth = 220,
            SelectedIndex = documents.Count > 0 ? 0 : -1
        };
        var canonical = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 280,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var recovered = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 280,
            Margin = new Thickness(5, 0, 0, 0)
        };
        var compare = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 8, 0, 0) };
        compare.Children.Add(canonical);
        Grid.SetColumn(recovered, 1); compare.Children.Add(recovered);
        var restore = new Button { Content = "Restore Selected", MinWidth = 120 };
        var discard = new Button { Content = "Discard Selected", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        var done = new Button { Content = "Done", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { restore, discard, done }
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(14) };
        body.Children.Add(list);
        Grid.SetColumn(compare, 1); body.Children.Add(compare);
        Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 2); body.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Review Recovery",
            Width = 980,
            Height = 600,
            MinWidth = 720,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = body
        };

        async Task RefreshPreviewAsync()
        {
            var index = list.SelectedIndex;
            if (index < 0 || index >= documents.Count)
            {
                canonical.Text = string.Empty;
                recovered.Text = string.Empty;
                restore.IsEnabled = false;
                discard.IsEnabled = false;
                return;
            }
            restore.IsEnabled = true;
            discard.IsEnabled = true;
            var entry = documents[index];
            var node = FindNode(project.Root, entry.PersistentId);
            canonical.Text = node?.IsDocument == true
                ? await _repository.ReadDocumentAsync(project, node)
                : "Document no longer exists in the project.";
            var recoveryPath = RecoveryDocumentPath(project.RootPath, entry.PersistentId);
            recovered.Text = File.Exists(recoveryPath) ? await File.ReadAllTextAsync(recoveryPath) : string.Empty;
        }

        async Task RemoveSelectedAsync(bool restoreSelected)
        {
            var index = list.SelectedIndex;
            if (index < 0 || index >= documents.Count) return;
            var entry = documents[index];
            var recoveryPath = RecoveryDocumentPath(project.RootPath, entry.PersistentId);
            if (restoreSelected)
            {
                var node = FindNode(project.Root, entry.PersistentId);
                if (node?.IsDocument == true && File.Exists(recoveryPath))
                    await _repository.SaveDocumentAsync(project, node, await File.ReadAllTextAsync(recoveryPath));
            }
            DeleteIfExists(recoveryPath);
            documents.RemoveAt(index);
            session.Documents.RemoveAll(item => string.Equals(item.PersistentId, entry.PersistentId, StringComparison.Ordinal));
            if (session.Documents.Count == 0)
                DeleteRecoveryDirectory(project.RootPath);
            else
                await SaveSessionAsync(project.RootPath, session);
            list.ItemsSource = documents.Select(item => $"{item.Title}   •   {item.RelativePath}").ToArray();
            list.SelectedIndex = documents.Count == 0 ? -1 : Math.Min(index, documents.Count - 1);
            await RefreshPreviewAsync();
        }

        list.SelectionChanged += async (_, _) => await RefreshPreviewAsync();
        restore.Click += async (_, _) => await RemoveSelectedAsync(restoreSelected: true);
        discard.Click += async (_, _) => await RemoveSelectedAsync(restoreSelected: false);
        done.Click += (_, _) => dialog.Close();
        await RefreshPreviewAsync();
        await dialog.ShowDialog(_window);
    }

    private static ProjectNode? FindNode(ProjectNode root, string persistentId)
    {
        foreach (var child in root.Children)
        {
            if (string.Equals(child.PersistentId, persistentId, StringComparison.Ordinal)) return child;
            var found = FindNode(child, persistentId);
            if (found is not null) return found;
        }
        return null;
    }

    private static string RecoveryRoot(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "recovery");

    private static string SessionPath(string projectRoot)
        => Path.Combine(RecoveryRoot(projectRoot), "session.json");

    private static string RecoveryDocumentPath(string projectRoot, string persistentId)
        => Path.Combine(RecoveryRoot(projectRoot), persistentId + ".recovery.md");

    private static async Task<RecoverySession?> LoadSessionAsync(string projectRoot)
    {
        var path = SessionPath(projectRoot);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(path),
                RecoveryJournalJsonContext.Default.RecoverySession);
        }
        catch
        {
            return null;
        }
    }

    private static Task SaveSessionAsync(string projectRoot, RecoverySession session)
    {
        Directory.CreateDirectory(RecoveryRoot(projectRoot));
        return AtomicWriteTextAsync(
            SessionPath(projectRoot),
            JsonSerializer.Serialize(session, RecoveryJournalJsonContext.Default.RecoverySession));
    }

    private static async Task AtomicWriteTextAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteRecoveryDirectory(string projectRoot)
    {
        var directory = RecoveryRoot(projectRoot);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteEmptyRecoveryRoot(string root)
    {
        var directory = RecoveryRoot(root);
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _timer.Stop();
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.Closed -= OnClosed;
        if (_loadedRoot is { Length: > 0 } root && !_blockedByPreviousRecovery)
            DeleteRecoveryDirectory(root);
        _gate.Dispose();
    }

    private enum RecoveryAction
    {
        None,
        RestoreAll,
        Review,
        Discard
    }

    private sealed class RecoverySession
    {
        public int Version { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<RecoveryDocument> Documents { get; set; } = [];
    }

    private sealed class RecoveryDocument
    {
        public string PersistentId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(RecoverySession))]
    private sealed partial class RecoveryJournalJsonContext : JsonSerializerContext
    {
    }
}
