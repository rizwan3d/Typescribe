using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;

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
    private readonly Dictionary<Avalonia.Controls.TextBox, string> _drafts = [];
    private readonly Dictionary<Avalonia.Controls.TextBox, Func<string>> _modelValues = [];
    private Avalonia.Controls.TextBox? _synopsis;
    private Avalonia.Controls.TextBox? _notes;
    private Avalonia.Controls.TextBox? _status;
    private Avalonia.Controls.TextBox? _label;
    private Avalonia.Controls.TextBox? _keywords;
    private Avalonia.Controls.TextBox? _target;
    private CancellationTokenSource? _saveCts;
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

    private void OnOpened(object? sender, EventArgs e) => TryInstall();
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        _synopsis = Field("_synopsisBox");
        _notes = Field("_notesBox");
        _status = Field("_statusBox");
        _label = Field("_labelBox");
        _keywords = Field("_keywordsBox");
        _target = Field("_targetBox");
        if (new[] { _synopsis, _notes, _status, _label, _keywords, _target }.Any(static box => box is null)) return;

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
        _window.LayoutUpdated -= OnLayoutUpdated;
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
        try { await CommitAsync(CancellationToken.None); }
        catch { }
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
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_saveCts.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(280, cancellationToken);
            await CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (!_installed || !_viewModel.HasSelection || _target is null) return;

        // An invalid target must not prevent Synopsis/Notes/Status/Label/Keywords from saving.
        // Keep the previously persisted target until the target field itself becomes valid.
        var validTarget = int.TryParse(_target.Text, out var parsedTarget) && parsedTarget >= 0;
        var target = validTarget ? parsedTarget : _viewModel.SelectedTargetWords;

        await _viewModel.SaveSelectedMetadataAsync(
            _synopsis?.Text ?? string.Empty,
            _notes?.Text ?? string.Empty,
            _status?.Text ?? string.Empty,
            _label?.Text ?? string.Empty,
            _keywords?.Text ?? string.Empty,
            target,
            cancellationToken);
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
                _selectionId = id;
                _drafts.Clear();
            }

            UpdateEnabledState();
            UpdateTargetValidation();
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
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
}
