using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Protects in-progress Inspector edits from unrelated workspace StateChanged refreshes and
/// commits fields with a short debounce. StudioWorkspaceWindow owns the visual controls; this
/// feature only supplies edit-session semantics around them.
/// </summary>
internal sealed class InspectorEditingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly FileSystemProjectRepository _repository = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<Avalonia.Controls.TextBox, string> _drafts = [];
    private readonly Dictionary<Avalonia.Controls.TextBox, Func<string>> _modelValues = [];
    private AuthoringFeatureCoordinator? _features;
    private Avalonia.Controls.TextBox? _synopsis;
    private Avalonia.Controls.TextBox? _notes;
    private Avalonia.Controls.TextBox? _status;
    private Avalonia.Controls.TextBox? _label;
    private Avalonia.Controls.TextBox? _keywords;
    private Avalonia.Controls.TextBox? _target;
    private CancellationTokenSource? _saveCts;
    private MetadataDraft? _pendingDraft;
    private string? _selectionId;
    private bool _installed;
    private bool _restoring;
    private bool _disposed;

    private InspectorEditingFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        var feature = new InspectorEditingFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        WireCustomMetadataEditors();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
        if (_installed) WireCustomMetadataEditors();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        _features = typeof(StudioWorkspaceWindow)
            .GetField("_features", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_window) as AuthoringFeatureCoordinator;
        _synopsis = Field("_synopsisBox");
        _notes = Field("_notesBox");
        _status = Field("_statusBox");
        _label = Field("_labelBox");
        _keywords = Field("_keywordsBox");
        _target = Field("_targetBox");
        if (_features is null || new[] { _synopsis, _notes, _status, _label, _keywords, _target }.Any(static box => box is null)) return;

        Register(_synopsis!, () => _viewModel.SelectedSynopsis);
        Register(_notes!, () => _viewModel.SelectedNotes);
        Register(_status!, () => _viewModel.SelectedStatus);
        Register(_label!, () => _viewModel.SelectedLabel);
        Register(_keywords!, () => _viewModel.SelectedKeywords);
        Register(_target!, () => _viewModel.SelectedTargetWords.ToString(System.Globalization.CultureInfo.InvariantCulture));

        PolishInputs();
        _selectionId = _viewModel.SelectedRow?.Node.PersistentId;
        UpdateEnabledState();
        UpdateTargetValidation();
        _installed = true;
        WireCustomMetadataEditors();
    }

    private Avalonia.Controls.TextBox? Field(string name)
        => typeof(StudioWorkspaceWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_window) as Avalonia.Controls.TextBox;

    private void Register(Avalonia.Controls.TextBox box, Func<string> modelValue)
    {
        _modelValues[box] = modelValue;
        box.GotFocus += BoxGotFocus;
        box.LostFocus += BoxLostFocus;
        box.TextChanged += BoxTextChanged;
    }

    private void PolishInputs()
    {
        if (_synopsis is null || _notes is null || _status is null || _label is null || _keywords is null || _target is null)
            return;

        _synopsis.Watermark = "Short synopsis for this document…";
        _notes.Watermark = "Notes, reminders, research links…";
        _status.Watermark = "Draft / In Progress / Revised / Final";
        _label.Watermark = "Storyline, POV, character arc…";
        _keywords.Watermark = "Comma-separated keywords";
        _target.Watermark = "0";

        foreach (var box in new[] { _synopsis, _notes, _status, _label, _keywords, _target })
        {
            box.MinHeight = Math.Max(32, box.MinHeight);
            box.Padding = new Thickness(8, 4);
            box.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }

        ToolTip.SetTip(_synopsis, "Synopsis shown in Corkboard cards and project metadata.");
        ToolTip.SetTip(_notes, "Private document notes stored outside manuscript text.");
        ToolTip.SetTip(_status, "Free-form document status, for example Draft or Final.");
        ToolTip.SetTip(_label, "Project label such as POV, storyline or character arc.");
        ToolTip.SetTip(_keywords, "Comma-separated keywords used for project organization and search.");
        ToolTip.SetTip(_target, "Document word target. Enter 0 for no target.");
    }

    private void BoxGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.TextBox box)
            _drafts[box] = box.Text ?? string.Empty;
    }

    private void BoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_restoring || sender is not Avalonia.Controls.TextBox box || !box.IsKeyboardFocusWithin) return;

        var text = box.Text ?? string.Empty;
        var model = _modelValues[box]();
        if (_drafts.TryGetValue(box, out var draft) &&
            !string.Equals(draft, model, StringComparison.Ordinal) &&
            string.Equals(text, model, StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(() => RestoreDraft(box, draft), DispatcherPriority.Background);
            return;
        }

        _drafts[box] = text;
        if (ReferenceEquals(box, _target)) UpdateTargetValidation();
        ScheduleSave();
    }

    private async void BoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.TextBox box) return;
        var draft = _pendingDraft ?? CaptureDraft();
        CancelScheduledSave(clearPending: false);
        try
        {
            if (draft is not null) await CommitDraftSafelyAsync(draft, CancellationToken.None);
        }
        catch { }
        if (ReferenceEquals(_pendingDraft, draft)) _pendingDraft = null;
        _drafts.Remove(box);
        if (ReferenceEquals(box, _target)) UpdateTargetValidation();
    }

    private void RestoreDraft(Avalonia.Controls.TextBox box, string draft)
    {
        if (_disposed || !box.IsKeyboardFocusWithin || string.Equals(box.Text, draft, StringComparison.Ordinal)) return;
        _restoring = true;
        try
        {
            var caret = Math.Min(box.CaretIndex, draft.Length);
            box.Text = draft;
            box.CaretIndex = caret;
        }
        finally { _restoring = false; }
    }

    private void ScheduleSave()
    {
        var draft = CaptureDraft();
        if (draft is null) return;
        _pendingDraft = draft;
        CancelScheduledSave(clearPending: false);
        _saveCts = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(draft, _saveCts.Token);
    }

    private async Task SaveAfterDelayAsync(MetadataDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(280, cancellationToken);
            await CommitDraftSafelyAsync(draft, cancellationToken);
            if (ReferenceEquals(_pendingDraft, draft)) _pendingDraft = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task CommitDraftSafelyAsync(MetadataDraft draft, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await CommitDraftAsync(draft, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task CommitDraftAsync(MetadataDraft draft, CancellationToken cancellationToken)
    {
        var project = CurrentProject();
        if (project is null) return;

        var selected = _viewModel.SelectedRow?.Node;
        if (selected is not null && string.Equals(selected.PersistentId, draft.Node.PersistentId, StringComparison.Ordinal))
        {
            await _viewModel.SaveSelectedMetadataAsync(
                draft.Synopsis,
                draft.Notes,
                draft.Status,
                draft.Label,
                draft.Keywords,
                draft.TargetWords,
                cancellationToken);
            return;
        }

        await _repository.SaveNodeMetadataAsync(
            project,
            draft.Node,
            draft.Synopsis,
            draft.Notes,
            draft.Status,
            draft.Label,
            draft.Keywords,
            draft.TargetWords,
            cancellationToken);
    }

    private MetadataDraft? CaptureDraft()
    {
        if (!_installed || !_viewModel.HasSelection || _target is null) return null;
        var node = _viewModel.SelectedRow?.Node;
        if (node is null) return null;

        // An invalid target must not prevent the remaining metadata fields from being saved.
        var validTarget = int.TryParse(_target.Text, out var parsedTarget) && parsedTarget >= 0;
        var target = validTarget ? parsedTarget : node.TargetWords;
        return new MetadataDraft(
            node,
            _synopsis?.Text ?? string.Empty,
            _notes?.Text ?? string.Empty,
            _status?.Text ?? string.Empty,
            _label?.Text ?? string.Empty,
            _keywords?.Text ?? string.Empty,
            target);
    }

    private void CancelScheduledSave(bool clearPending)
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
        if (clearPending) _pendingDraft = null;
    }

    private void WireCustomMetadataEditors()
    {
        if (_disposed || !_installed || _features is null) return;
        var panel = typeof(StudioWorkspaceWindow)
            .GetField("_customFieldsPanel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_window) as StackPanel;
        var node = _viewModel.SelectedRow?.Node;
        if (panel is null || node is null) return;

        foreach (var row in panel.Children.OfType<Grid>().ToArray())
        {
            var old = row.Children.OfType<Avalonia.Controls.TextBox>().FirstOrDefault();
            if (old is null || old.Classes.Contains("durable-custom-metadata")) continue;

            var fieldName = row.Children.OfType<Avalonia.Controls.TextBlock>()
                .FirstOrDefault()?.Text?.Trim();
            var field = _features.CustomFields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (field is null) continue;

            // Replace the legacy LostFocus-only editor. Its anonymous handler resolves the
            // selected node at blur time, which can save an old field value into a newly
            // selected document. The replacement captures the node it was created for.
            var input = new Avalonia.Controls.TextBox
            {
                Text = old.Text,
                Watermark = old.Watermark,
                MinHeight = Math.Max(32, old.MinHeight),
                MinWidth = old.MinWidth,
                MaxWidth = old.MaxWidth,
                Margin = old.Margin,
                Padding = new Thickness(8, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                IsEnabled = old.IsEnabled,
                IsReadOnly = false
            };
            input.Classes.Add("durable-custom-metadata");
            ToolTip.SetTip(input, $"Custom metadata: {field.Name}");

            var column = Grid.GetColumn(old);
            row.Children.Remove(old);
            Grid.SetColumn(input, column);
            row.Children.Add(input);
            WireCustomMetadataInput(input, node, field.Key);
        }
    }

    private void WireCustomMetadataInput(Avalonia.Controls.TextBox input, ProjectNode node, string key)
    {
        CancellationTokenSource? cts = null;
        input.TextChanged += (_, _) =>
        {
            if (!input.IsKeyboardFocusWithin) return;
            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var value = input.Text ?? string.Empty;
            _ = SaveCustomAfterDelayAsync(node, key, value, cts.Token);
        };
        input.LostFocus += async (_, _) =>
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            await SaveCustomSafelyAsync(node, key, input.Text ?? string.Empty, CancellationToken.None);
        };
    }

    private async Task SaveCustomAfterDelayAsync(ProjectNode node, string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(280, cancellationToken);
            await SaveCustomSafelyAsync(node, key, value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task SaveCustomSafelyAsync(ProjectNode node, string key, string value, CancellationToken cancellationToken)
    {
        if (_features is null || string.Equals(node.CustomMetadata.GetValueOrDefault(key), value, StringComparison.Ordinal)) return;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(node.CustomMetadata.GetValueOrDefault(key), value, StringComparison.Ordinal)) return;
            await _features.SetCustomValueAsync(node, key, value, cancellationToken);
            RaiseState("Custom metadata saved");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void RaiseState(string status)
    {
        typeof(WorkspaceViewModel)
            .GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(_viewModel, [status]);
    }

    private void UpdateTargetValidation()
    {
        if (_target is null) return;
        var valid = int.TryParse(_target.Text, out var value) && value >= 0;
        if (valid || !_target.IsEnabled)
        {
            _target.ClearValue(Avalonia.Controls.TextBox.BorderBrushProperty);
            ToolTip.SetTip(_target, "Document word target. Enter 0 for no target.");
            return;
        }

        _target.BorderBrush = new SolidColorBrush(Color.Parse("#F14C4C"));
        ToolTip.SetTip(_target, "Enter a whole number of 0 or more. Other Inspector fields will still save.");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !_installed) return;
            var id = _viewModel.SelectedRow?.Node.PersistentId;
            if (!string.Equals(id, _selectionId, StringComparison.Ordinal))
            {
                var oldDraft = _pendingDraft;
                CancelScheduledSave(clearPending: true);
                if (oldDraft is not null)
                    _ = CommitDraftSafelyAsync(oldDraft, CancellationToken.None);
                _selectionId = id;
                _drafts.Clear();
            }

            UpdateEnabledState();
            UpdateTargetValidation();
            WireCustomMetadataEditors();
            foreach (var pair in _drafts.ToArray())
            {
                if (pair.Key.IsKeyboardFocusWithin)
                    RestoreDraft(pair.Key, pair.Value);
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateEnabledState()
    {
        var enabled = _viewModel.HasSelection;
        foreach (var box in _modelValues.Keys)
        {
            box.IsEnabled = enabled;
            box.IsReadOnly = false;
        }
    }

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private void OnClosed(object? sender, EventArgs e)
    {
        var pending = _pendingDraft;
        CancelScheduledSave(clearPending: true);
        if (pending is not null)
            _ = CommitDraftSafelyAsync(pending, CancellationToken.None);

        _disposed = true;
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        foreach (var box in _modelValues.Keys)
        {
            box.GotFocus -= BoxGotFocus;
            box.LostFocus -= BoxLostFocus;
            box.TextChanged -= BoxTextChanged;
        }
    }

    private sealed record MetadataDraft(
        ProjectNode Node,
        string Synopsis,
        string Notes,
        string Status,
        string Label,
        string Keywords,
        int TargetWords);
}
