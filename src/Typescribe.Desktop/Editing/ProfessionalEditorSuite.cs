using System.Collections;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Review-grade editing tools layered directly on the manuscript editor: persistent
/// insertion/deletion tracking, accept/reject workflow, style analysis, semantic
/// structure insertion, and a low-noise WYSIWYM presentation mode.
/// </summary>
internal sealed class ProfessionalEditorSuite
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly IReadOnlyDictionary<string, string> CommonCorrections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["teh"] = "the",
            ["adn"] = "and",
            ["recieve"] = "receive",
            ["recieved"] = "received",
            ["definately"] = "definitely",
            ["seperate"] = "separate",
            ["seperately"] = "separately",
            ["occured"] = "occurred",
            ["untill"] = "until",
            ["alot"] = "a lot",
            ["wierd"] = "weird",
            ["becuase"] = "because",
            ["thier"] = "their",
            ["freind"] = "friend",
            ["wich"] = "which",
            ["adress"] = "address",
            ["accomodate"] = "accommodate",
            ["begining"] = "beginning",
            ["goverment"] = "government",
            ["enviroment"] = "environment",
            ["existance"] = "existence"
        };

    private static readonly (string Phrase, string Replacement, string Message)[] ConcisionRules =
    [
        ("in order to", "to", "Consider the shorter 'to'."),
        ("due to the fact that", "because", "This phrase can usually be simplified to 'because'."),
        ("at this point in time", "now", "This phrase can usually be simplified to 'now'."),
        ("for the purpose of", "to", "Consider a more direct construction."),
        ("has the ability to", "can", "Consider the more direct 'can'."),
        ("a large number of", "many", "Consider the more concise 'many'.")
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly DispatcherTimer _analysisTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _uiTimer;
    private readonly List<DeletedRevision> _deletions = [];
    private readonly List<WritingIssue> _issues = [];

    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Control? _reviewBar;
    private ComboBox? _revisionPicker;
    private ToggleButton? _trackToggle;
    private ToggleButton? _analysisToggle;
    private ToggleButton? _wysiwymToggle;
    private TextBlock? _changeStatus;
    private TextBlock? _issueStatus;
    private SemanticMarkupColorizer? _semanticColorizer;
    private IReadOnlyList<ManuscriptRevisionSpan> _lastKnownInsertions = [];
    private string? _loadedIdentity;
    private string? _loadedProjectRoot;
    private int _lastRevisionLevel = 1;
    private int _reviewIndex = -1;
    private int _issueIndex = -1;
    private long _analysisVersion;
    private bool _installed;
    private bool _suppressTracking;
    private bool _loadingState;
    private bool _analysisEnabled = true;
    private bool _wysiwymMode = true;
    private bool _disposed;

    private ProfessionalEditorSuite(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;

        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(680) };
        _analysisTimer.Tick += async (_, _) =>
        {
            _analysisTimer.Stop();
            await RunAnalysisAsync();
        };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            PersistCurrentState();
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _uiTimer.Tick += (_, _) =>
        {
            _uiTimer.Stop();
            RefreshStatus();
        };
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);

        var suite = new ProfessionalEditorSuite(window, viewModel, repository);
        window.Opened += suite.OnOpened;
        window.LayoutUpdated += suite.OnLayoutUpdated;
        window.Closed += suite.OnClosed;
        viewModel.StateChanged += suite.OnViewModelStateChanged;
        suite.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        Dispatcher.UIThread.Post(SynchronizeDocumentState, DispatcherPriority.Background);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateEnabledState();
            SynchronizeDocumentState();
            ScheduleAnalysis();
            ScheduleUiRefresh();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;
        var editor = _window.GetVisualDescendants().OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
            ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        var host = editor.Parent as Grid;
        if (host is null || !host.Classes.Contains("long-form-editor-host")) return;
        if (host.Classes.Contains("professional-editor-host")) return;

        _editor = editor;
        _host = host;
        _revisionPicker = FindRevisionPicker();
        if ((_revisionPicker?.SelectedIndex ?? 0) > 0)
            _lastRevisionLevel = Math.Clamp(_revisionPicker!.SelectedIndex, 1, 5);

        var existingChildren = host.Children.ToArray();
        host.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto");
        foreach (var child in existingChildren)
        {
            var row = Grid.GetRow(child);
            if (row >= 1) Grid.SetRow(child, row + 1);
        }

        _reviewBar = BuildReviewBar();
        Grid.SetRow(_reviewBar, 1);
        host.Children.Add(_reviewBar);
        host.Classes.Add("professional-editor-host");

        _semanticColorizer = new SemanticMarkupColorizer(this);
        editor.TextArea.TextView.LineTransformers.Add(_semanticColorizer);
        editor.Document.Changing += DocumentChanging;
        editor.TextChanged += EditorTextChanged;
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextArea.SelectionChanged += SelectionChanged;

        ExtendContextMenu();
        _window.AddHandler(
            InputElement.KeyDownEvent,
            WindowPreviewKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _installed = true;
        ApplyWysiwymMode();
        UpdateEnabledState();
        SynchronizeDocumentState();
        ScheduleAnalysis();
        RefreshStatus();
    }

    private void UpdateEnabledState()
    {
        var enabled = _viewModel.HasDocument;
        if (_reviewBar is null) return;
        _reviewBar.IsEnabled = enabled;
        _reviewBar.IsVisible = enabled;
    }

    private Control BuildReviewBar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(8, 3, 8, 3)
        };

        _trackToggle = Toggle("Track", "Track insertions and deletions (Ctrl+Shift+E)", ToggleTracking);
        panel.Children.Add(_trackToggle);
        panel.Children.Add(Button("‹", "Previous tracked change", () => NavigateChange(-1)));
        panel.Children.Add(Button("›", "Next tracked change", () => NavigateChange(1)));
        panel.Children.Add(Button("Accept", "Accept current tracked change (Ctrl+Alt+A)", AcceptCurrentChange));
        panel.Children.Add(Button("Reject", "Reject current tracked change (Ctrl+Alt+R)", RejectCurrentChange));
        panel.Children.Add(Button("✓All", "Accept every tracked change", AcceptAllChanges));
        panel.Children.Add(Button("×All", "Reject every tracked change", RejectAllChanges));
        _changeStatus = StatusText(72);
        panel.Children.Add(_changeStatus);
        panel.Children.Add(Separator());

        _analysisToggle = Toggle("Analysis", "Grammar, style and readability analysis", value =>
        {
            _analysisEnabled = value;
            if (_editor is not null)
            {
                _editor.SpellIndicatorsEnabled = value;
                if (!value) _editor.SetSpellingDiagnostics([]);
            }
            if (value) ScheduleAnalysis();
            RefreshStatus();
        });
        _analysisToggle.IsChecked = true;
        panel.Children.Add(_analysisToggle);
        panel.Children.Add(Button("◁", "Previous writing issue (Shift+F7)", () => NavigateIssue(-1)));
        panel.Children.Add(Button("▷", "Next writing issue (F7)", () => NavigateIssue(1)));
        panel.Children.Add(Button("Fix", "Apply the current issue's suggested fix", ApplyCurrentFix));
        _issueStatus = StatusText(92);
        panel.Children.Add(_issueStatus);
        panel.Children.Add(Separator());

        panel.Children.Add(Button("Footnote", "Insert a semantic footnote", async () => await InsertFootnoteAsync()));
        panel.Children.Add(Button("Table", "Insert a 3-column semantic table", InsertTable));
        panel.Children.Add(Button("Figure", "Insert a semantic figure", async () => await InsertFigureAsync()));
        panel.Children.Add(Button("Cite", "Insert a citation", async () => await InsertCitationAsync()));
        panel.Children.Add(Separator());

        _wysiwymToggle = Toggle("WYSIWYM", "Semantic low-noise manuscript view", value =>
        {
            _wysiwymMode = value;
            ApplyWysiwymMode();
        });
        _wysiwymToggle.IsChecked = _wysiwymMode;
        panel.Children.Add(_wysiwymToggle);

        return new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer
            {
                Content = panel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
    }

    private static TextBlock StatusText(double width)
        => new()
        {
            MinWidth = width,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72,
            Margin = new Thickness(4, 0)
        };

    private static Button Button(string label, string tip, Action action)
    {
        var button = new Button
        {
            Content = label,
            Height = 27,
            MinHeight = 27,
            MinWidth = label.Length <= 1 ? 28 : 44,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static ToggleButton Toggle(string label, string tip, Action<bool> changed)
    {
        var button = new ToggleButton
        {
            Content = label,
            Height = 27,
            MinHeight = 27,
            MinWidth = 44,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.IsCheckedChanged += (_, _) => changed(button.IsChecked == true);
        return button;
    }

    private static Border Separator()
        => new()
        {
            Width = 1,
            Margin = new Thickness(5, 4),
            Opacity = 0.25,
            Background = Brushes.Gray
        };

    private ComboBox? FindRevisionPicker()
    {
        foreach (var combo in _window.GetVisualDescendants().OfType<ComboBox>())
        {
            if (combo.ItemsSource is not IEnumerable items) continue;
            var labels = items.Cast<object?>().Select(item => item?.ToString()).Where(text => text is not null).ToArray();
            if (labels.Contains("Revisions Off", StringComparer.Ordinal) && labels.Contains("Revision 1", StringComparer.Ordinal))
                return combo;
        }
        return null;
    }

    private void ToggleTracking(bool enabled)
    {
        if (_revisionPicker is not null)
        {
            if (_revisionPicker.SelectedIndex > 0) _lastRevisionLevel = Math.Clamp(_revisionPicker.SelectedIndex, 1, 5);
            _revisionPicker.SelectedIndex = enabled ? Math.Clamp(_lastRevisionLevel, 1, 5) : 0;
        }
        else if (_editor is not null)
        {
            if (_editor.RevisionLevel > 0) _lastRevisionLevel = _editor.RevisionLevel;
            _editor.RevisionLevel = enabled ? Math.Clamp(_lastRevisionLevel, 1, 5) : 0;
        }
        RefreshStatus();
    }

    private void DocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        AdjustDeletionAnchors(e);
        if (_suppressTracking || _loadingState || _editor is null || _editor.RevisionLevel <= 0 || e.RemovalLength <= 0) return;
        if (e.Offset < 0 || e.Offset + e.RemovalLength > _editor.Document.TextLength) return;

        var removed = _editor.Document.GetText(e.Offset, e.RemovalLength);
        if (removed.Length == 0) return;
        _deletions.Add(new DeletedRevision(
            Guid.NewGuid().ToString("N"),
            e.Offset,
            removed,
            Math.Clamp(_editor.RevisionLevel, 1, 5),
            DateTimeOffset.UtcNow));
    }

    private void AdjustDeletionAnchors(DocumentChangeEventArgs change)
    {
        if (_deletions.Count == 0) return;
        var delta = change.InsertionLength - change.RemovalLength;
        var removedEnd = change.Offset + change.RemovalLength;
        for (var index = 0; index < _deletions.Count; index++)
        {
            var deletion = _deletions[index];
            var offset = deletion.Offset;
            if (change.RemovalLength == 0)
            {
                if (offset >= change.Offset) offset += change.InsertionLength;
            }
            else if (offset > removedEnd)
            {
                offset += delta;
            }
            else if (offset >= change.Offset)
            {
                offset = change.Offset + change.InsertionLength;
            }
            _deletions[index] = deletion with { Offset = Math.Max(0, offset) };
        }
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (_editor is null) return;
        _lastKnownInsertions = _editor.RevisionSpans.ToArray();
        if (!_loadingState) ScheduleSave();
        ScheduleAnalysis();
        ScheduleUiRefresh();
    }

    private void CaretPositionChanged(object? sender, EventArgs e) => ScheduleUiRefresh();
    private void SelectionChanged(object? sender, EventArgs e) => ScheduleUiRefresh();

    private void SynchronizeDocumentState()
    {
        if (_editor is null) return;
        var identity = _editor.DocumentIdentity;
        var root = _repository.CurrentProject?.RootPath;
        if (string.Equals(identity, _loadedIdentity, StringComparison.Ordinal) &&
            string.Equals(root, _loadedProjectRoot, StringComparison.Ordinal))
            return;

        PersistCurrentState();
        _loadedIdentity = identity;
        _loadedProjectRoot = root;
        _deletions.Clear();
        _reviewIndex = -1;

        _loadingState = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(root))
            {
                var state = LoadRevisionState(root!, identity!);
                _deletions.AddRange(state.Deletions.Select(deletion => deletion with
                {
                    Offset = Math.Clamp(deletion.Offset, 0, _editor.Document.TextLength)
                }));
                _editor.SetRevisionSpans(state.Insertions.Select(insertion =>
                    new ManuscriptRevisionSpan(insertion.Offset, insertion.Length, insertion.Level)));
            }
            else
            {
                _editor.ClearRevisionSpans();
            }
            _lastKnownInsertions = _editor.RevisionSpans.ToArray();
        }
        finally
        {
            _loadingState = false;
        }

        ScheduleAnalysis();
        RefreshStatus();
    }

    private IReadOnlyList<ReviewChange> CurrentChanges()
    {
        if (_editor is null) return [];
        var changes = new List<ReviewChange>(_editor.RevisionSpans.Count + _deletions.Count);
        changes.AddRange(_editor.RevisionSpans.Select(span => new ReviewChange(
            ReviewChangeKind.Insertion,
            span.Offset,
            span.Length,
            span.Level,
            null,
            null)));
        changes.AddRange(_deletions.Select(deletion => new ReviewChange(
            ReviewChangeKind.Deletion,
            deletion.Offset,
            deletion.Text.Length,
            deletion.Level,
            deletion.Text,
            deletion.Id)));
        return changes
            .OrderBy(change => change.Offset)
            .ThenBy(change => change.Kind == ReviewChangeKind.Deletion ? 0 : 1)
            .ToArray();
    }

    private void NavigateChange(int direction)
    {
        if (_editor is null) return;
        var changes = CurrentChanges();
        if (changes.Count == 0) return;
        var caret = _editor.CaretOffset;

        if (_reviewIndex < 0 || _reviewIndex >= changes.Count)
        {
            var start = -1;
            for (var index = 0; index < changes.Count; index++)
            {
                if (changes[index].Offset >= caret)
                {
                    start = index;
                    break;
                }
            }
            _reviewIndex = start >= 0 ? start : 0;
            if (direction < 0) _reviewIndex = (_reviewIndex - 1 + changes.Count) % changes.Count;
        }
        else
        {
            _reviewIndex = (_reviewIndex + direction + changes.Count) % changes.Count;
        }

        SelectChange(changes[_reviewIndex]);
        RefreshStatus();
    }

    private ReviewChange? ResolveCurrentChange()
    {
        if (_editor is null) return null;
        var changes = CurrentChanges();
        if (changes.Count == 0) return null;

        var selectionStart = _editor.SelectionStart;
        var selectionEnd = selectionStart + _editor.SelectionLength;
        var caret = _editor.CaretOffset;
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            var end = change.Offset + Math.Max(1, change.Length);
            var intersects = _editor.SelectionLength > 0
                ? change.Offset < selectionEnd && end > selectionStart
                : caret >= change.Offset && caret <= end;
            if (!intersects) continue;
            _reviewIndex = index;
            return change;
        }

        if (_reviewIndex >= 0 && _reviewIndex < changes.Count) return changes[_reviewIndex];
        var nearest = changes
            .Select((change, index) => (change, index, distance: Math.Abs(change.Offset - caret)))
            .OrderBy(item => item.distance)
            .First();
        _reviewIndex = nearest.index;
        return nearest.change;
    }

    private void SelectChange(ReviewChange change)
    {
        if (_editor is null) return;
        var offset = Math.Clamp(change.Offset, 0, _editor.Document.TextLength);
        var length = change.Kind == ReviewChangeKind.Insertion
            ? Math.Clamp(change.Length, 0, _editor.Document.TextLength - offset)
            : 0;
        _editor.Select(offset, length);
        _editor.CaretOffset = offset + length;
        var location = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.Focus();
        if (change.Kind == ReviewChangeKind.Deletion && _changeStatus is not null)
            ToolTip.SetTip(_changeStatus, "Deleted: " + Compact(change.DeletedText ?? string.Empty, 160));
    }

    private void AcceptCurrentChange()
    {
        var change = ResolveCurrentChange();
        if (change is null || _editor is null) return;
        if (change.Kind == ReviewChangeKind.Insertion)
            RemoveInsertionRevision(change);
        else
            RemoveDeletionRevision(change);
        ScheduleSave();
        RefreshStatus();
    }

    private void RejectCurrentChange()
    {
        var change = ResolveCurrentChange();
        if (change is null || _editor is null) return;
        RejectChange(change);
        ScheduleSave();
        ScheduleAnalysis();
        RefreshStatus();
    }

    private void AcceptAllChanges()
    {
        if (_editor is null) return;
        _editor.ClearRevisionSpans();
        _deletions.Clear();
        _lastKnownInsertions = [];
        _reviewIndex = -1;
        ScheduleSave();
        RefreshStatus();
    }

    private void RejectAllChanges()
    {
        if (_editor is null) return;
        var guard = 0;
        while (guard++ < 100_000)
        {
            var change = CurrentChanges()
                .OrderByDescending(item => item.Offset)
                .ThenBy(item => item.Kind == ReviewChangeKind.Insertion ? 0 : 1)
                .FirstOrDefault();
            if (change is null) break;
            RejectChange(change);
        }
        _reviewIndex = -1;
        ScheduleSave();
        ScheduleAnalysis();
        RefreshStatus();
    }

    private void RejectChange(ReviewChange change)
    {
        if (_editor is null) return;
        if (change.Kind == ReviewChangeKind.Insertion)
        {
            RemoveInsertionRevision(change);
            var offset = Math.Clamp(change.Offset, 0, _editor.Document.TextLength);
            var length = Math.Clamp(change.Length, 0, _editor.Document.TextLength - offset);
            if (length <= 0) return;
            WithTrackingSuppressed(() => _editor.Document.Remove(offset, length));
        }
        else
        {
            RemoveDeletionRevision(change);
            var offset = Math.Clamp(change.Offset, 0, _editor.Document.TextLength);
            if (string.IsNullOrEmpty(change.DeletedText)) return;
            WithTrackingSuppressed(() => _editor.Document.Insert(offset, change.DeletedText));
        }
        _lastKnownInsertions = _editor.RevisionSpans.ToArray();
    }

    private void RemoveInsertionRevision(ReviewChange change)
    {
        if (_editor is null) return;
        var removed = false;
        var remaining = new List<ManuscriptRevisionSpan>();
        foreach (var span in _editor.RevisionSpans)
        {
            if (!removed && span.Offset == change.Offset && span.Length == change.Length && span.Level == change.Level)
            {
                removed = true;
                continue;
            }
            remaining.Add(span);
        }
        _editor.SetRevisionSpans(remaining);
        _lastKnownInsertions = remaining;
    }

    private void RemoveDeletionRevision(ReviewChange change)
    {
        if (!string.IsNullOrWhiteSpace(change.DeletionId))
        {
            _deletions.RemoveAll(item => string.Equals(item.Id, change.DeletionId, StringComparison.Ordinal));
            return;
        }
        var index = _deletions.FindIndex(item => item.Offset == change.Offset && item.Text == change.DeletedText);
        if (index >= 0) _deletions.RemoveAt(index);
    }

    private void WithTrackingSuppressed(Action action)
    {
        if (_editor is null) return;
        var level = _editor.RevisionLevel;
        _suppressTracking = true;
        _editor.RevisionLevel = 0;
        try
        {
            action();
        }
        finally
        {
            _editor.RevisionLevel = level;
            _suppressTracking = false;
        }
    }

    private void ScheduleAnalysis()
    {
        if (!_installed || !_analysisEnabled || _editor is null) return;
        Interlocked.Increment(ref _analysisVersion);
        _analysisTimer.Stop();
        _analysisTimer.Start();
    }

    private async Task RunAnalysisAsync()
    {
        if (!_analysisEnabled || _editor is null) return;
        var version = _analysisVersion;
        var snapshot = _editor.Text ?? string.Empty;
        var issues = await Task.Run(() => AnalyzeWriting(snapshot));
        if (_disposed || version != _analysisVersion || _editor is null) return;

        _issues.Clear();
        _issues.AddRange(issues);
        if (_issueIndex >= _issues.Count) _issueIndex = _issues.Count - 1;
        _editor.SpellIndicatorsEnabled = true;
        _editor.SetSpellingDiagnostics(_issues.Select(issue =>
            new ManuscriptTextDiagnostic(issue.Offset, issue.Length, issue.Message)));
        RefreshStatus();
    }

    private static IReadOnlyList<WritingIssue> AnalyzeWriting(string text)
    {
        var issues = new List<WritingIssue>();
        var words = ExtractWords(text);

        foreach (var word in words)
        {
            if (CommonCorrections.TryGetValue(word.Text, out var correction))
                issues.Add(new WritingIssue(word.Start, word.Length, WritingIssueKind.Spelling, $"Possible spelling issue: {word.Text}", correction));
        }

        for (var index = 1; index < words.Count; index++)
        {
            if (!string.Equals(words[index - 1].Text, words[index].Text, StringComparison.OrdinalIgnoreCase)) continue;
            var start = words[index - 1].Start + words[index - 1].Length;
            var length = words[index].Start + words[index].Length - start;
            issues.Add(new WritingIssue(start, length, WritingIssueKind.Grammar, $"Repeated word: {words[index].Text}", string.Empty));
        }

        for (var index = 0; index + 1 < text.Length; index++)
        {
            if (text[index] == ' ' && text[index + 1] == ' ')
            {
                var runEnd = index + 2;
                while (runEnd < text.Length && text[runEnd] == ' ') runEnd++;
                issues.Add(new WritingIssue(index, runEnd - index, WritingIssueKind.Grammar, "Multiple spaces.", " "));
                index = runEnd - 1;
                continue;
            }
            if (text[index] == ' ' && ",.;:!?".Contains(text[index + 1]))
                issues.Add(new WritingIssue(index, 1, WritingIssueKind.Grammar, "Remove the space before punctuation.", string.Empty));
        }

        foreach (var rule in ConcisionRules)
        {
            var search = 0;
            while (search < text.Length)
            {
                var found = text.IndexOf(rule.Phrase, search, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                if (IsBoundary(text, found - 1) && IsBoundary(text, found + rule.Phrase.Length))
                    issues.Add(new WritingIssue(found, rule.Phrase.Length, WritingIssueKind.Style, rule.Message, rule.Replacement));
                search = found + rule.Phrase.Length;
            }
        }

        for (var index = 0; index + 1 < words.Count; index++)
        {
            if (!IsBeVerb(words[index].Text) || !LooksLikeParticiple(words[index + 1].Text)) continue;
            var start = words[index].Start;
            var end = words[index + 1].Start + words[index + 1].Length;
            issues.Add(new WritingIssue(start, end - start, WritingIssueKind.Style, "Possible passive construction. Check whether a more direct subject/verb is clearer.", null));
        }

        AddLongSentenceIssues(text, words, issues);
        AddLongParagraphIssues(text, words, issues);

        return issues
            .GroupBy(issue => (issue.Offset, issue.Length, issue.Message))
            .Select(group => group.First())
            .OrderBy(issue => issue.Offset)
            .ThenBy(issue => issue.Kind)
            .ToArray();
    }

    private static void AddLongSentenceIssues(string text, IReadOnlyList<WordSpan> words, ICollection<WritingIssue> issues)
    {
        var sentenceStart = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var boundary = index == text.Length || text[index] is '.' or '!' or '?';
            if (!boundary) continue;
            var sentenceEnd = index == text.Length ? text.Length : index + 1;
            var count = words.Count(word => word.Start >= sentenceStart && word.Start < sentenceEnd);
            if (count > 38)
            {
                var first = words.FirstOrDefault(word => word.Start >= sentenceStart && word.Start < sentenceEnd);
                if (first is not null)
                {
                    var length = Math.Min(sentenceEnd - first.Start, 180);
                    issues.Add(new WritingIssue(first.Start, Math.Max(1, length), WritingIssueKind.Readability, $"Long sentence ({count} words). Consider splitting it.", null));
                }
            }
            sentenceStart = sentenceEnd;
        }
    }

    private static void AddLongParagraphIssues(string text, IReadOnlyList<WordSpan> words, ICollection<WritingIssue> issues)
    {
        var start = 0;
        while (start < text.Length)
        {
            var separator = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            var end = separator < 0 ? text.Length : separator;
            var count = words.Count(word => word.Start >= start && word.Start < end);
            if (count > 180)
            {
                var first = words.FirstOrDefault(word => word.Start >= start && word.Start < end);
                if (first is not null)
                    issues.Add(new WritingIssue(first.Start, Math.Min(Math.Max(1, end - first.Start), 180), WritingIssueKind.Readability, $"Dense paragraph ({count} words). Consider a paragraph break.", null));
            }
            if (separator < 0) break;
            start = separator + 2;
        }
    }

    private static List<WordSpan> ExtractWords(string text)
    {
        var words = new List<WordSpan>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && !IsWordCharacter(text[index])) index++;
            if (index >= text.Length) break;
            var start = index;
            while (index < text.Length && IsWordCharacter(text[index])) index++;
            words.Add(new WordSpan(start, index - start, text[start..index]));
        }
        return words;
    }

    private static bool IsWordCharacter(char value) => char.IsLetter(value) || value is '\'' or '’' or '-';
    private static bool IsBoundary(string text, int index) => index < 0 || index >= text.Length || !IsWordCharacter(text[index]);
    private static bool IsBeVerb(string word) => word.Equals("is", StringComparison.OrdinalIgnoreCase) || word.Equals("are", StringComparison.OrdinalIgnoreCase) || word.Equals("was", StringComparison.OrdinalIgnoreCase) || word.Equals("were", StringComparison.OrdinalIgnoreCase) || word.Equals("be", StringComparison.OrdinalIgnoreCase) || word.Equals("been", StringComparison.OrdinalIgnoreCase) || word.Equals("being", StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeParticiple(string word) => word.Length > 4 && (word.EndsWith("ed", StringComparison.OrdinalIgnoreCase) || word.EndsWith("en", StringComparison.OrdinalIgnoreCase));

    private void NavigateIssue(int direction)
    {
        if (_editor is null || _issues.Count == 0) return;
        var caret = _editor.CaretOffset;
        if (_issueIndex < 0 || _issueIndex >= _issues.Count)
        {
            _issueIndex = _issues.FindIndex(issue => issue.Offset >= caret);
            if (_issueIndex < 0) _issueIndex = 0;
            if (direction < 0) _issueIndex = (_issueIndex - 1 + _issues.Count) % _issues.Count;
        }
        else
        {
            _issueIndex = (_issueIndex + direction + _issues.Count) % _issues.Count;
        }
        SelectIssue(_issues[_issueIndex]);
        RefreshStatus();
    }

    private WritingIssue? ResolveCurrentIssue()
    {
        if (_editor is null || _issues.Count == 0) return null;
        var caret = _editor.CaretOffset;
        for (var index = 0; index < _issues.Count; index++)
        {
            var issue = _issues[index];
            if (caret < issue.Offset || caret > issue.Offset + Math.Max(1, issue.Length)) continue;
            _issueIndex = index;
            return issue;
        }
        if (_issueIndex >= 0 && _issueIndex < _issues.Count) return _issues[_issueIndex];
        _issueIndex = _issues
            .Select((issue, index) => (index, distance: Math.Abs(issue.Offset - caret)))
            .OrderBy(item => item.distance)
            .First().index;
        return _issues[_issueIndex];
    }

    private void SelectIssue(WritingIssue issue)
    {
        if (_editor is null) return;
        var offset = Math.Clamp(issue.Offset, 0, _editor.Document.TextLength);
        var length = Math.Clamp(issue.Length, 0, _editor.Document.TextLength - offset);
        _editor.Select(offset, length);
        _editor.CaretOffset = offset + length;
        var location = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.Focus();
        if (_issueStatus is not null) ToolTip.SetTip(_issueStatus, issue.Message);
    }

    private void ApplyCurrentFix()
    {
        if (_editor is null) return;
        var issue = ResolveCurrentIssue();
        if (issue?.Replacement is null) return;
        var offset = Math.Clamp(issue.Offset, 0, _editor.Document.TextLength);
        var length = Math.Clamp(issue.Length, 0, _editor.Document.TextLength - offset);
        _editor.Document.Replace(offset, length, issue.Replacement);
        _editor.CaretOffset = offset + issue.Replacement.Length;
        _editor.Focus();
    }

    private async Task InsertFootnoteAsync()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var selected = _editor.SelectionLength > 0
            ? _editor.Document.GetText(_editor.SelectionStart, _editor.SelectionLength)
            : string.Empty;
        var values = await PromptAsync("Insert Footnote", [("Footnote text", selected, true)]);
        if (values is null || string.IsNullOrWhiteSpace(values[0])) return;

        var identifier = NextFootnoteIdentifier(_editor.Text ?? string.Empty);
        var reference = $"[^{identifier}]";
        if (_editor.SelectionLength > 0)
            _editor.Document.Replace(_editor.SelectionStart, _editor.SelectionLength, reference);
        else
            _editor.Document.Insert(_editor.CaretOffset, reference);

        var suffix = (_editor.Text ?? string.Empty).EndsWith("\n", StringComparison.Ordinal) ? "\n" : "\n\n";
        _editor.Document.Insert(_editor.Document.TextLength, $"{suffix}[^{identifier}]: {values[0].Trim()}\n");
        _editor.CaretOffset = Math.Min(_editor.Document.TextLength, _editor.CaretOffset + reference.Length);
        _editor.Focus();
    }

    private void InsertTable()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        const string table = "| Heading 1 | Heading 2 | Heading 3 |\n| --- | --- | --- |\n| Cell | Cell | Cell |\n| Cell | Cell | Cell |";
        InsertBlock(table, selectionOffsetInBlock: 2, selectionLength: "Heading 1".Length);
    }

    private async Task InsertFigureAsync()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var selectedCaption = _editor.SelectionLength > 0
            ? _editor.Document.GetText(_editor.SelectionStart, _editor.SelectionLength)
            : string.Empty;
        var values = await PromptAsync(
            "Insert Figure",
            [("Image path", string.Empty, false), ("Caption", selectedCaption, false), ("Identifier (optional)", string.Empty, false)]);
        if (values is null || string.IsNullOrWhiteSpace(values[0])) return;
        var identifier = string.IsNullOrWhiteSpace(values[2]) ? string.Empty : $"{{#{SanitizeIdentifier(values[2])}}}";
        InsertBlock($"![{values[1].Trim()}]({values[0].Trim()}){identifier}");
    }

    private async Task InsertCitationAsync()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var values = await PromptAsync(
            "Insert Citation",
            [("Citation key", string.Empty, false), ("Locator (optional, e.g. p. 42)", string.Empty, false)]);
        if (values is null || string.IsNullOrWhiteSpace(values[0])) return;
        var key = SanitizeCitationKey(values[0]);
        if (key.Length == 0) return;
        var citation = string.IsNullOrWhiteSpace(values[1]) ? $"[@{key}]" : $"[@{key}, {values[1].Trim()}]";
        var offset = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;
        if (_editor.SelectionLength > 0) _editor.Document.Replace(offset, _editor.SelectionLength, citation);
        else _editor.Document.Insert(offset, citation);
        _editor.CaretOffset = offset + citation.Length;
        _editor.Focus();
    }

    private void InsertBlock(string block, int selectionOffsetInBlock = -1, int selectionLength = 0)
    {
        if (_editor is null) return;
        var caret = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;
        var before = caret > 0 && _editor.Document.GetCharAt(caret - 1) != '\n' ? "\n\n" : string.Empty;
        var after = caret < _editor.Document.TextLength && _editor.Document.GetCharAt(caret) != '\n' ? "\n\n" : "\n";
        var insert = before + block + after;
        if (_editor.SelectionLength > 0) _editor.Document.Replace(caret, _editor.SelectionLength, insert);
        else _editor.Document.Insert(caret, insert);
        var blockStart = caret + before.Length;
        if (selectionOffsetInBlock >= 0 && selectionLength > 0)
        {
            _editor.Select(blockStart + selectionOffsetInBlock, selectionLength);
            _editor.CaretOffset = blockStart + selectionOffsetInBlock + selectionLength;
        }
        else
        {
            _editor.CaretOffset = blockStart + block.Length;
        }
        _editor.Focus();
    }

    private async Task<string[]?> PromptAsync(
        string title,
        IReadOnlyList<(string Label, string Value, bool Multiline)> fields)
    {
        var stack = new StackPanel { Spacing = 7, Margin = new Thickness(14) };
        var boxes = new List<TextBox>(fields.Count);
        foreach (var field in fields)
        {
            stack.Children.Add(new TextBlock { Text = field.Label, Opacity = 0.78 });
            var box = new TextBox
            {
                Text = field.Value,
                MinWidth = 420,
                MinHeight = field.Multiline ? 96 : 30,
                AcceptsReturn = field.Multiline,
                TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
            };
            boxes.Add(box);
            stack.Children.Add(box);
        }

        var ok = new Button { Content = "Insert", MinWidth = 84 };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { ok, cancel }
        };
        stack.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            MinHeight = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = stack
        };
        ok.Click += (_, _) => dialog.Close(boxes.Select(box => box.Text ?? string.Empty).ToArray());
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) =>
        {
            boxes.FirstOrDefault()?.Focus();
            boxes.FirstOrDefault()?.SelectAll();
        };
        return await dialog.ShowDialog<string[]?>(_window);
    }

    private void ApplyWysiwymMode()
    {
        if (_editor is null) return;
        if (_wysiwymMode) _editor.ShowMarkdownMarks = false;
        _editor.TextArea.TextView.Redraw();
    }

    private void ExtendContextMenu()
    {
        if (_editor?.ContextMenu is null) return;
        var existing = _editor.ContextMenu.ItemsSource is IEnumerable enumerable
            ? enumerable.Cast<object>().ToList()
            : [];
        if (existing.OfType<MenuItem>().Any(item => string.Equals(item.Header?.ToString(), "Accept Change", StringComparison.Ordinal))) return;

        var track = new MenuItem { Header = "Track Changes", ToggleType = MenuItemToggleType.CheckBox };
        track.Click += (_, _) => ToggleTracking(track.IsChecked == true);
        var accept = new MenuItem { Header = "Accept Change" };
        accept.Click += (_, _) => AcceptCurrentChange();
        var reject = new MenuItem { Header = "Reject Change" };
        reject.Click += (_, _) => RejectCurrentChange();
        var nextIssue = new MenuItem { Header = "Next Writing Issue" };
        nextIssue.Click += (_, _) => NavigateIssue(1);
        var fix = new MenuItem { Header = "Apply Suggested Fix" };
        fix.Click += (_, _) => ApplyCurrentFix();
        var footnote = new MenuItem { Header = "Insert Footnote" };
        footnote.Click += async (_, _) => await InsertFootnoteAsync();
        var citation = new MenuItem { Header = "Insert Citation" };
        citation.Click += async (_, _) => await InsertCitationAsync();

        existing.Add(new Separator());
        existing.Add(track);
        existing.Add(accept);
        existing.Add(reject);
        existing.Add(nextIssue);
        existing.Add(fix);
        existing.Add(new Separator());
        existing.Add(footnote);
        existing.Add(citation);
        _editor.ContextMenu.ItemsSource = existing;
    }

    private void WindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.E)
        {
            e.Handled = true;
            ToggleTracking(!IsTrackingEnabled());
            return;
        }
        if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.A)
        {
            e.Handled = true;
            AcceptCurrentChange();
            return;
        }
        if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.R)
        {
            e.Handled = true;
            RejectCurrentChange();
            return;
        }
        if (e.Key == Key.F7)
        {
            e.Handled = true;
            NavigateIssue(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
    }

    private bool IsTrackingEnabled() => (_revisionPicker?.SelectedIndex ?? _editor?.RevisionLevel ?? 0) > 0;

    private void RefreshStatus()
    {
        if (_editor is null) return;
        if (_trackToggle is not null)
        {
            var enabled = IsTrackingEnabled();
            if (_trackToggle.IsChecked != enabled) _trackToggle.IsChecked = enabled;
        }
        if (_analysisToggle is not null && _analysisToggle.IsChecked != _analysisEnabled)
            _analysisToggle.IsChecked = _analysisEnabled;
        if (_wysiwymToggle is not null && _wysiwymToggle.IsChecked != _wysiwymMode)
            _wysiwymToggle.IsChecked = _wysiwymMode;

        var changes = CurrentChanges();
        if (_changeStatus is not null)
        {
            _changeStatus.Text = changes.Count == 1 ? "1 change" : $"{changes.Count} changes";
            if (_reviewIndex >= 0 && _reviewIndex < changes.Count)
            {
                var current = changes[_reviewIndex];
                ToolTip.SetTip(_changeStatus, current.Kind == ReviewChangeKind.Insertion
                    ? $"Insertion, revision {current.Level}"
                    : $"Deletion, revision {current.Level}: {Compact(current.DeletedText ?? string.Empty, 160)}");
            }
        }

        if (_issueStatus is not null)
        {
            _issueStatus.Text = !_analysisEnabled ? "analysis off" : _issues.Count == 1 ? "1 issue" : $"{_issues.Count} issues";
            if (_issueIndex >= 0 && _issueIndex < _issues.Count)
                ToolTip.SetTip(_issueStatus, $"{_issues[_issueIndex].Kind}: {_issues[_issueIndex].Message}");
        }
    }

    private void ScheduleUiRefresh()
    {
        _uiTimer.Stop();
        _uiTimer.Start();
    }

    private void ScheduleSave()
    {
        if (_loadingState || string.IsNullOrWhiteSpace(_loadedIdentity) || string.IsNullOrWhiteSpace(_loadedProjectRoot)) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PersistCurrentState()
    {
        if (string.IsNullOrWhiteSpace(_loadedIdentity) || string.IsNullOrWhiteSpace(_loadedProjectRoot)) return;
        try
        {
            var path = RevisionPath(_loadedProjectRoot!, _loadedIdentity!);
            var insertions = (_editor is not null && string.Equals(_editor.DocumentIdentity, _loadedIdentity, StringComparison.Ordinal))
                ? _editor.RevisionSpans.ToArray()
                : _lastKnownInsertions;
            var state = new RevisionState(
                1,
                insertions.Select(span => new StoredInsertion(span.Offset, span.Length, span.Level)).ToArray(),
                _deletions.ToArray());

            if (state.Insertions.Count == 0 && state.Deletions.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Revision persistence must never interrupt authoring; the document itself
            // remains protected by the workspace autosave path.
        }
    }

    private static RevisionState LoadRevisionState(string root, string identity)
    {
        try
        {
            var path = RevisionPath(root, identity);
            if (!File.Exists(path)) return RevisionState.Empty;
            return JsonSerializer.Deserialize<RevisionState>(File.ReadAllText(path), JsonOptions) ?? RevisionState.Empty;
        }
        catch
        {
            return RevisionState.Empty;
        }
    }

    private static string RevisionPath(string root, string identity)
    {
        var safe = new string(identity.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        return Path.Combine(root, ".typescribe", "revisions", safe + ".json");
    }

    private static string NextFootnoteIdentifier(string text)
    {
        var number = 1;
        while (text.Contains($"[^note{number}]", StringComparison.OrdinalIgnoreCase)) number++;
        return "note" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or ':' or '.') builder.Append(character);
        }
        return builder.Length == 0 ? "figure" : builder.ToString();
    }

    private static string SanitizeCitationKey(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or ':' or '.' or '/') builder.Append(character);
        }
        return builder.ToString();
    }

    private static string Compact(string text, int maximum)
    {
        var value = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _analysisTimer.Stop();
        _saveTimer.Stop();
        _uiTimer.Stop();
        PersistCurrentState();

        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _window.RemoveHandler(InputElement.KeyDownEvent, WindowPreviewKeyDown);

        if (_editor is not null)
        {
            _editor.Document.Changing -= DocumentChanging;
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextArea.SelectionChanged -= SelectionChanged;
            if (_semanticColorizer is not null)
                _editor.TextArea.TextView.LineTransformers.Remove(_semanticColorizer);
        }
    }

    private sealed class SemanticMarkupColorizer(ProfessionalEditorSuite owner) : DocumentColorizingTransformer
    {
        private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromArgb(52, 125, 135, 150));
        private static readonly IBrush SemanticBrush = new SolidColorBrush(Color.Parse("#5B7CFA"));
        private static readonly IBrush FigureBrush = new SolidColorBrush(Color.Parse("#5B9673"));

        protected override void ColorizeLine(DocumentLine line)
        {
            if (!owner._wysiwymMode || owner._editor is null || line.Length == 0) return;
            var text = CurrentContext.Document.GetText(line);
            var start = line.Offset;

            if (text.TrimStart().StartsWith("![", StringComparison.Ordinal))
            {
                ChangeLinePart(start, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(FigureBrush));
                DimSyntax(start, text, "![", 2);
                DimCharacter(start, text, ']');
                DimCharacter(start, text, '(');
                DimCharacter(start, text, ')');
                DimCharacter(start, text, '{');
                DimCharacter(start, text, '}');
            }

            if (text.Contains('|'))
            {
                foreach (var index in CharacterIndexes(text, '|'))
                    ChangeLinePart(start + index, start + index + 1, element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
                if (IsTableSeparator(text))
                    ChangeLinePart(start, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
            }

            ColorReferences(start, text, "[^", SemanticBrush);
            ColorReferences(start, text, "[@", SemanticBrush);
        }

        private void DimSyntax(int lineStart, string text, string marker, int length)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
                ChangeLinePart(lineStart + index, lineStart + Math.Min(text.Length, index + length), element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
        }

        private void DimCharacter(int lineStart, string text, char marker)
        {
            foreach (var index in CharacterIndexes(text, marker))
                ChangeLinePart(lineStart + index, lineStart + index + 1, element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
        }

        private void ColorReferences(int lineStart, string text, string marker, IBrush brush)
        {
            var search = 0;
            while (search < text.Length)
            {
                var open = text.IndexOf(marker, search, StringComparison.Ordinal);
                if (open < 0) break;
                var close = text.IndexOf(']', open + marker.Length);
                if (close < 0) break;
                ChangeLinePart(lineStart + open, lineStart + close + 1, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    element.TextRunProperties.SetTypeface(new Typeface(owner._editor!.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                });
                search = close + 1;
            }
        }

        private static IEnumerable<int> CharacterIndexes(string text, char value)
        {
            for (var index = 0; index < text.Length; index++)
                if (text[index] == value) yield return index;
        }

        private static bool IsTableSeparator(string text)
        {
            var trimmed = text.Trim().Trim('|').Trim();
            if (trimmed.Length == 0) return false;
            return trimmed.All(character => character is '-' or ':' or '|' or ' ');
        }
    }

    private enum ReviewChangeKind { Insertion, Deletion }
    private enum WritingIssueKind { Spelling, Grammar, Style, Readability }

    private sealed record ReviewChange(
        ReviewChangeKind Kind,
        int Offset,
        int Length,
        int Level,
        string? DeletedText,
        string? DeletionId);

    private sealed record DeletedRevision(
        string Id,
        int Offset,
        string Text,
        int Level,
        DateTimeOffset CreatedUtc);

    private sealed record StoredInsertion(int Offset, int Length, int Level);

    private sealed record RevisionState(
        int Version,
        IReadOnlyList<StoredInsertion> Insertions,
        IReadOnlyList<DeletedRevision> Deletions)
    {
        public static RevisionState Empty { get; } = new(1, [], []);
    }

    private sealed record WritingIssue(
        int Offset,
        int Length,
        WritingIssueKind Kind,
        string Message,
        string? Replacement);

    private sealed record WordSpan(int Start, int Length, string Text);
}
