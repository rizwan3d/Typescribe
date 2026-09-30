using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Makes editor analysis results self-explanatory. The professional editor owns the
/// diagnostics; this surface exposes the current diagnostic, the offending text, and
/// a concise explanation of why the analyzer flagged it.
/// </summary>
internal sealed class EditorAnalysisDetailsFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Border? _panel;
    private TextBlock? _heading;
    private TextBlock? _location;
    private TextBlock? _offendingText;
    private TextBlock? _message;
    private TextBlock? _explanation;
    private Button? _previous;
    private Button? _next;
    private int _issueIndex;
    private string _lastSignature = string.Empty;
    private bool _installed;
    private bool _disposed;

    private EditorAnalysisDetailsFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new EditorAnalysisDetailsFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnViewModelStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        Refresh();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        var editor = _window.GetVisualDescendants().OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
            ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor?.Parent is not Grid host) return;
        if (!host.Classes.Contains("long-form-editor-host") || !host.Classes.Contains("professional-editor-host")) return;
        if (host.Classes.Contains("analysis-details-host")) return;

        _editor = editor;
        _host = host;

        var existingChildren = host.Children.ToArray();
        host.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto");
        foreach (var child in existingChildren)
        {
            var row = Grid.GetRow(child);
            if (row >= 2) Grid.SetRow(child, row + 1);
        }

        _panel = BuildPanel();
        Grid.SetRow(_panel, 2);
        host.Children.Add(_panel);
        host.Classes.Add("analysis-details-host");

        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextChanged += EditorTextChanged;

        _installed = true;
        _refreshTimer.Start();
        Refresh();
    }

    private Border BuildPanel()
    {
        _heading = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _location = new TextBlock
        {
            Opacity = 0.62,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _heading, _location }
        };

        _offendingText = new TextBlock
        {
            FontFamily = new FontFamily("monospace"),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };
        _message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };
        _explanation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76,
            MaxLines = 3
        };

        var text = new StackPanel
        {
            Spacing = 3,
            Children = { headingRow, _offendingText, _message, _explanation }
        };

        _previous = new Button
        {
            Content = "‹",
            MinWidth = 34,
            Height = 28,
            Padding = new Thickness(7, 1)
        };
        _next = new Button
        {
            Content = "›",
            MinWidth = 34,
            Height = 28,
            Padding = new Thickness(7, 1),
            Margin = new Thickness(4, 0, 0, 0)
        };
        var goTo = new Button
        {
            Content = "Show",
            MinWidth = 62,
            Height = 28,
            Padding = new Thickness(8, 1),
            Margin = new Thickness(8, 0, 0, 0)
        };

        _previous.Click += (_, _) => MoveIssue(-1);
        _next.Click += (_, _) => MoveIssue(1);
        goTo.Click += (_, _) => SelectCurrentIssue();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _previous, _next, goTo }
        };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 6)
        };
        content.Children.Add(text);
        Grid.SetColumn(buttons, 1);
        content.Children.Add(buttons);

        return new Border
        {
            IsVisible = false,
            Margin = new Thickness(8, 0, 8, 4),
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(88, 231, 90, 99)),
            Background = new SolidColorBrush(Color.FromArgb(18, 231, 90, 99)),
            CornerRadius = new CornerRadius(5),
            Child = content
        };
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        _lastSignature = string.Empty;
        Refresh();
    }

    private void CaretPositionChanged(object? sender, EventArgs e)
    {
        if (_editor is null || _editor.Diagnostics.Count == 0) return;
        var caret = _editor.CaretOffset;
        for (var index = 0; index < _editor.Diagnostics.Count; index++)
        {
            var issue = _editor.Diagnostics[index];
            if (caret < issue.Offset || caret > issue.Offset + Math.Max(1, issue.Length)) continue;
            if (_issueIndex != index)
            {
                _issueIndex = index;
                _lastSignature = string.Empty;
                Refresh();
            }
            break;
        }
    }

    private void Refresh()
    {
        if (!_installed || _disposed || _editor is null || _panel is null) return;

        var issues = _editor.Diagnostics;
        if (issues.Count == 0)
        {
            _panel.IsVisible = false;
            _issueIndex = 0;
            _lastSignature = string.Empty;
            return;
        }

        _issueIndex = Math.Clamp(_issueIndex, 0, issues.Count - 1);
        if (issues.Count == 1) _issueIndex = 0;

        var issue = issues[_issueIndex];
        var signature = $"{issues.Count}|{_issueIndex}|{issue.Offset}|{issue.Length}|{issue.Message}|{_editor.DocumentIdentity}";
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal) && _panel.IsVisible) return;
        _lastSignature = signature;

        var offset = Math.Clamp(issue.Offset, 0, _editor.Document.TextLength);
        var length = Math.Clamp(issue.Length, 0, _editor.Document.TextLength - offset);
        var excerpt = length > 0 ? _editor.Document.GetText(offset, length) : string.Empty;
        excerpt = Compact(excerpt, 140);
        var location = _editor.Document.GetLocation(offset);

        _heading!.Text = issues.Count == 1
            ? "Analysis: 1 issue"
            : $"Analysis: {_issueIndex + 1} of {issues.Count} issues";
        _location!.Text = $"Line {location.Line}, column {location.Column}";
        _offendingText!.Text = string.IsNullOrWhiteSpace(excerpt)
            ? "Flagged passage"
            : $"Flagged: “{excerpt}”";
        _message!.Text = $"Problem: {issue.Message}";
        _explanation!.Text = $"Why this is flagged: {Explain(issue.Message, excerpt)}";
        _previous!.IsEnabled = issues.Count > 1;
        _next!.IsEnabled = issues.Count > 1;
        _panel.IsVisible = true;
    }

    private void MoveIssue(int direction)
    {
        if (_editor is null || _editor.Diagnostics.Count == 0) return;
        _issueIndex = (_issueIndex + direction + _editor.Diagnostics.Count) % _editor.Diagnostics.Count;
        _lastSignature = string.Empty;
        Refresh();
        SelectCurrentIssue();
    }

    private void SelectCurrentIssue()
    {
        if (_editor is null || _editor.Diagnostics.Count == 0) return;
        _issueIndex = Math.Clamp(_issueIndex, 0, _editor.Diagnostics.Count - 1);
        var issue = _editor.Diagnostics[_issueIndex];
        var offset = Math.Clamp(issue.Offset, 0, _editor.Document.TextLength);
        var length = Math.Clamp(issue.Length, 0, _editor.Document.TextLength - offset);
        _editor.Select(offset, length);
        _editor.CaretOffset = offset + length;
        var location = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.Focus();
    }

    private static string Explain(string message, string excerpt)
    {
        if (message.StartsWith("Possible spelling issue:", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(excerpt)
                ? "The word matches the editor's list of common misspellings and should be checked."
                : $"“{excerpt}” matches a known common misspelling, so the editor is asking you to verify its spelling.";

        if (message.StartsWith("Repeated word:", StringComparison.OrdinalIgnoreCase))
            return "The same word appears twice in succession. This is often an accidental duplication.";

        if (message.Contains("Multiple spaces", StringComparison.OrdinalIgnoreCase))
            return "Normal prose uses one space between words. Extra spaces can create inconsistent typesetting.";

        if (message.Contains("space before punctuation", StringComparison.OrdinalIgnoreCase))
            return "In standard English punctuation such as commas and periods normally follows the preceding word without a space.";

        if (message.Contains("passive construction", StringComparison.OrdinalIgnoreCase))
            return "The phrase looks like a form of “be” followed by a participle. Passive voice is not always wrong, but a direct subject-and-verb construction may be clearer.";

        if (message.StartsWith("Long sentence", StringComparison.OrdinalIgnoreCase))
            return "The sentence exceeds the editor's readability threshold. Long sentences can make relationships between ideas harder to follow.";

        if (message.StartsWith("Dense paragraph", StringComparison.OrdinalIgnoreCase))
            return "The paragraph exceeds the editor's density threshold. Breaking it at a natural idea boundary can improve readability.";

        if (message.Contains("shorter", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("simplified", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("concise", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("direct", StringComparison.OrdinalIgnoreCase))
            return "The wording is grammatically possible, but the analyzer found a shorter or more direct alternative that may read more clearly.";

        return "The editor's grammar, style, or readability rule matched this passage. Review the highlighted text together with the problem description before deciding whether to change it.";
    }

    private static string Compact(string text, int maximum)
    {
        var value = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _refreshTimer.Stop();
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;

        if (_editor is not null)
        {
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextChanged -= EditorTextChanged;
        }
    }
}
