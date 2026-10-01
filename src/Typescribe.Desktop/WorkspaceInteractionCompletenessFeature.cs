using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Fills small interaction gaps left by the base workspace: comments and snapshots get visible
/// text-entry fields instead of prompt-only creation, action buttons track selection state, and
/// the new controls reuse the existing authoring/view-model services rather than duplicating data.
/// </summary>
internal sealed class WorkspaceInteractionCompletenessFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private AuthoringFeatureCoordinator? _features;

    private TextBox? _commentInput;
    private Button? _commentAdd;
    private ListBox? _commentList;
    private Button? _commentResolve;
    private Button? _commentDelete;

    private TextBox? _snapshotInput;
    private Button? _snapshotAdd;
    private ListBox? _snapshotList;
    private Button? _snapshotCompare;
    private Button? _snapshotRestore;
    private Button? _snapshotDelete;

    private bool _commentsInstalled;
    private bool _snapshotsInstalled;
    private bool _disposed;

    private WorkspaceInteractionCompletenessFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new WorkspaceInteractionCompletenessFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        UpdateActionStates();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed || (_commentsInstalled && _snapshotsInstalled)) return;
        TryInstall();
    }

    private void TryInstall()
    {
        if (_disposed) return;
        _features ??= typeof(StudioWorkspaceWindow)
            .GetField("_features", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_window) as AuthoringFeatureCoordinator;

        if (!_commentsInstalled) InstallCommentsComposer();
        if (!_snapshotsInstalled) InstallSnapshotsComposer();
        UpdateActionStates();

        if (_commentsInstalled && _snapshotsInstalled)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void InstallCommentsComposer()
    {
        if (_features is null) return;
        var tab = FindInspectorTab("Comments");
        if (tab?.Content is not Control oldContent || oldContent.Classes.Contains("interaction-comments-complete"))
            return;

        _commentList = EnumerateControls(oldContent).OfType<ListBox>().FirstOrDefault();
        var buttons = EnumerateControls(oldContent).OfType<Button>().ToArray();
        var legacyAdd = buttons.FirstOrDefault(button =>
            ButtonText(button) is "Add at Caret" or "+ Comment");
        _commentResolve = buttons.FirstOrDefault(button =>
            ButtonText(button) is "Resolve / Reopen" or "Toggle Resolved");
        _commentDelete = buttons.FirstOrDefault(button => string.Equals(ButtonText(button), "Delete", StringComparison.Ordinal));
        if (legacyAdd is not null) legacyAdd.IsVisible = false;

        _commentInput = new TextBox
        {
            PlaceholderText = "Write a comment for the current caret…",
            MinHeight = 32,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _commentAdd = new Button
        {
            Content = "Add Comment",
            MinHeight = 32,
            MinWidth = 98,
            Margin = new Thickness(6, 0, 0, 0)
        };
        ToolTip.SetTip(_commentInput, "The comment is attached to the current editor line and stored outside manuscript text.");
        ToolTip.SetTip(_commentAdd, "Add this comment at the current editor caret");

        _commentInput.TextChanged += (_, _) => UpdateActionStates();
        _commentInput.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            await AddCommentFromComposerAsync();
        };
        _commentAdd.Click += async (_, _) => await AddCommentFromComposerAsync();
        if (_commentList is not null)
            _commentList.SelectionChanged += (_, _) => UpdateActionStates();

        var composer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 8, 8, 4)
        };
        composer.Children.Add(_commentInput);
        Grid.SetColumn(_commentAdd, 1);
        composer.Children.Add(_commentAdd);

        tab.Content = null;
        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        wrapper.Classes.Add("interaction-comments-complete");
        wrapper.Children.Add(composer);
        Grid.SetRow(oldContent, 1);
        wrapper.Children.Add(oldContent);
        tab.Content = wrapper;
        _commentsInstalled = true;
    }

    private async Task AddCommentFromComposerAsync()
    {
        if (_features is null || _commentInput is null) return;
        var node = _viewModel.SelectedRow?.Node;
        var text = _commentInput.Text?.Trim();
        if (node?.IsDocument != true || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var line = CurrentEditorLine();
            await _features.AddCommentAsync(node, line, text);
            _commentInput.Text = string.Empty;
            typeof(StudioWorkspaceWindow)
                .GetMethod("RefreshComments", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(_window, null);
            SetStatus($"Comment added at line {line}");
        }
        catch (Exception ex)
        {
            SetStatus("Could not add comment: " + Unwrap(ex).Message);
        }
        UpdateActionStates();
    }

    private void InstallSnapshotsComposer()
    {
        var tab = FindInspectorTab("Snapshots");
        if (tab?.Content is not Control oldContent || oldContent.Classes.Contains("interaction-snapshots-complete"))
            return;

        _snapshotList = EnumerateControls(oldContent).OfType<ListBox>().FirstOrDefault();
        var buttons = EnumerateControls(oldContent).OfType<Button>().ToArray();
        var legacyTake = buttons.FirstOrDefault(button =>
            ButtonText(button) is "Take Snapshot" or "+ Snapshot");
        _snapshotCompare = buttons.FirstOrDefault(button => string.Equals(ButtonText(button), "Compare", StringComparison.Ordinal));
        _snapshotRestore = buttons.FirstOrDefault(button =>
            ButtonText(button) is "Restore" or "Restore Full");
        _snapshotDelete = buttons.FirstOrDefault(button => string.Equals(ButtonText(button), "Delete", StringComparison.Ordinal));
        if (legacyTake is not null) legacyTake.IsVisible = false;

        _snapshotInput = new TextBox
        {
            PlaceholderText = "Snapshot label (optional)",
            MinHeight = 32,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _snapshotAdd = new Button
        {
            Content = "Create Snapshot",
            MinHeight = 32,
            MinWidth = 110,
            Margin = new Thickness(6, 0, 0, 0)
        };
        ToolTip.SetTip(_snapshotInput, "Give the snapshot a useful name, or leave blank to use the current date and time.");
        ToolTip.SetTip(_snapshotAdd, "Capture the current manuscript text as a snapshot");

        _snapshotInput.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await CreateSnapshotFromComposerAsync();
        };
        _snapshotAdd.Click += async (_, _) => await CreateSnapshotFromComposerAsync();
        if (_snapshotList is not null)
            _snapshotList.SelectionChanged += (_, _) => UpdateActionStates();

        var composer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 8, 8, 4)
        };
        composer.Children.Add(_snapshotInput);
        Grid.SetColumn(_snapshotAdd, 1);
        composer.Children.Add(_snapshotAdd);

        tab.Content = null;
        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        wrapper.Classes.Add("interaction-snapshots-complete");
        wrapper.Children.Add(composer);
        Grid.SetRow(oldContent, 1);
        wrapper.Children.Add(oldContent);
        tab.Content = wrapper;
        _snapshotsInstalled = true;
    }

    private async Task CreateSnapshotFromComposerAsync()
    {
        if (_snapshotInput is null || !_viewModel.HasDocument) return;
        var label = _snapshotInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label))
            label = $"Snapshot {DateTime.Now:g}";

        try
        {
            await _viewModel.CreateSnapshotAsync(label);
            _snapshotInput.Text = string.Empty;
            SetStatus($"Snapshot created: {label}");
        }
        catch (Exception ex)
        {
            SetStatus("Could not create snapshot: " + Unwrap(ex).Message);
        }
        UpdateActionStates();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateActionStates, DispatcherPriority.Background);

    private void UpdateActionStates()
    {
        if (_disposed) return;
        var hasDocument = _viewModel.SelectedRow?.Node.IsDocument == true;

        if (_commentInput is not null) _commentInput.IsEnabled = hasDocument;
        if (_commentAdd is not null)
            _commentAdd.IsEnabled = hasDocument && !string.IsNullOrWhiteSpace(_commentInput?.Text);
        var hasComment = _commentList?.SelectedIndex >= 0;
        if (_commentResolve is not null) _commentResolve.IsEnabled = hasDocument && hasComment;
        if (_commentDelete is not null) _commentDelete.IsEnabled = hasDocument && hasComment;

        if (_snapshotInput is not null) _snapshotInput.IsEnabled = _viewModel.HasDocument;
        if (_snapshotAdd is not null) _snapshotAdd.IsEnabled = _viewModel.HasDocument;
        var hasSnapshot = _snapshotList?.SelectedIndex >= 0;
        if (_snapshotCompare is not null) _snapshotCompare.IsEnabled = _viewModel.HasDocument && hasSnapshot;
        if (_snapshotRestore is not null) _snapshotRestore.IsEnabled = _viewModel.HasDocument && hasSnapshot;
        if (_snapshotDelete is not null) _snapshotDelete.IsEnabled = _viewModel.HasDocument && hasSnapshot;
    }

    private int CurrentEditorLine()
    {
        try
        {
            return typeof(StudioWorkspaceWindow)
                .GetMethod("CurrentEditorLine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(_window, null) is int line
                ? Math.Max(1, line)
                : 1;
        }
        catch
        {
            return 1;
        }
    }

    private void SetStatus(string message)
    {
        try
        {
            typeof(WorkspaceViewModel)
                .GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(_viewModel, [message]);
        }
        catch
        {
        }
    }

    private TabItem? FindInspectorTab(string header)
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (items.Any(static item => HeaderEquals(item, "Editor"))) continue;
            if (!items.Any(static item => HeaderEquals(item, "Comments")) ||
                !items.Any(static item => HeaderEquals(item, "Snapshots")))
                continue;
            var match = items.FirstOrDefault(item => HeaderEquals(item, header));
            if (match is not null) return match;
        }
        return null;
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
            yield return control;
    }

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>().ToList();
        return tabs.Items.Cast<object?>().OfType<TabItem>().ToList();
    }

    private static string ButtonText(Button button)
        => button.Content is TextBlock text ? text.Text ?? string.Empty : button.Content?.ToString() ?? string.Empty;

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase);

    private static Exception Unwrap(Exception exception)
        => exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }
}
