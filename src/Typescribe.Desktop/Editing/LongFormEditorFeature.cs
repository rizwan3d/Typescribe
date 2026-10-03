using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Professional long-form authoring chrome for the primary ManuscriptEditor.
/// Keeps the AvaloniaEdit document as the source of editing behavior while adding
/// persistent author preferences, document find/replace, formatting commands,
/// navigation context, zoom/page-width controls, and live manuscript statistics.
/// </summary>
internal sealed class LongFormEditorFeature
{
    private const double DefaultFontSize = 16;
    private const double MinFontSize = 12;
    private const double MaxFontSize = 26;
    private const double PageWidth = 940;
    private const int MaximumMatches = 5000;

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _findTimer;

    private readonly TextBox _findBox = new()
    {
        Watermark = "Find in document",
        MinWidth = 190,
        Height = 28,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(7, 2)
    };

    private readonly TextBox _replaceBox = new()
    {
        Watermark = "Replace with",
        MinWidth = 190,
        Height = 28,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(7, 2)
    };

    private readonly CheckBox _matchCase = new() { Content = "Case", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _wholeWord = new() { Content = "Whole word", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _findStatus = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.68, MinWidth = 64 };
    private readonly TextBlock _contextText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statsText = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72 };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 42, TextAlignment = TextAlignment.Center };

    private readonly List<DocumentMatch> _matches = [];

    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Control? _toolbar;
    private Control? _findPanel;
    private Control? _statusBar;
    private ToggleButton? _focusToggle;
    private ToggleButton? _typewriterToggle;
    private ToggleButton? _markdownToggle;
    private ToggleButton? _wrapToggle;
    private ToggleButton? _lineNumbersToggle;
    private ToggleButton? _pageWidthToggle;
    private int _currentMatch = -1;
    private bool _installed;
    private bool _menuInjected;
    private bool _syncingPreferences;
    private bool _disposed;

    private double _fontSize = DefaultFontSize;
    private bool _focusMode;
    private bool _typewriterMode;
    private bool _markdownMarks;
    private bool _wordWrap = true;
    private bool _lineNumbers;
    private bool _pageWidth;

    private LongFormEditorFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            UpdateStatus();
        };

        _findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _findTimer.Tick += (_, _) =>
        {
            _findTimer.Stop();
            RefreshMatches(preserveCurrent: true);
        };
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new LongFormEditorFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnViewModelStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        InjectEditMenu();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
        if (!_menuInjected) InjectEditMenu();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateEnabledState();
            ScheduleStatusUpdate();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        var editors = _window.GetVisualDescendants().OfType<ManuscriptEditor>().ToArray();
        var editor = editors.FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
                     ?? editors.FirstOrDefault();
        if (editor?.Parent is not Panel parent) return;
        if (parent.Classes.Contains("long-form-editor-host")) return;

        _editor = editor;
        LoadPreferences();

        var row = Grid.GetRow(editor);
        var column = Grid.GetColumn(editor);
        var rowSpan = Grid.GetRowSpan(editor);
        var columnSpan = Grid.GetColumnSpan(editor);
        var index = parent.Children.IndexOf(editor);
        if (index < 0) return;

        parent.Children.RemoveAt(index);

        _host = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = editor.MinWidth,
            MinHeight = editor.MinHeight
        };
        _host.Classes.Add("long-form-editor-host");

        _toolbar = BuildToolbar();
        _findPanel = BuildFindPanel();
        _findPanel.IsVisible = false;
        _statusBar = BuildStatusBar();

        Grid.SetRow(_toolbar, 0);
        _host.Children.Add(_toolbar);
        Grid.SetRow(_findPanel, 1);
        _host.Children.Add(_findPanel);

        Grid.SetRow(editor, 2);
        Grid.SetColumn(editor, 0);
        Grid.SetRowSpan(editor, 1);
        Grid.SetColumnSpan(editor, 1);
        _host.Children.Add(editor);

        Grid.SetRow(_statusBar, 3);
        _host.Children.Add(_statusBar);

        parent.Children.Insert(index, _host);
        Grid.SetRow(_host, row);
        Grid.SetColumn(_host, column);
        Grid.SetRowSpan(_host, rowSpan);
        Grid.SetColumnSpan(_host, columnSpan);

        HookEditor();
        ExtendEditorContextMenu();
        ApplyPreferences();
        UpdateEnabledState();
        UpdateStatus();

        _window.AddHandler(
            InputElement.KeyDownEvent,
            WindowPreviewKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _installed = true;
    }

    private Control BuildToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(8, 4, 8, 3)
        };

        panel.Children.Add(CommandButton("B", "Bold (Ctrl+B)", () => WrapSelection("**", "**"), FontWeight.Bold));
        panel.Children.Add(CommandButton("I", "Italic (Ctrl+I)", () => WrapSelection("*", "*"), FontWeight.Normal, FontStyle.Italic));
        panel.Children.Add(CommandButton("`", "Inline code", () => WrapSelection("`", "`")));
        panel.Children.Add(CommandButton("H1", "Heading 1", () => ApplyHeading(1)));
        panel.Children.Add(CommandButton("H2", "Heading 2", () => ApplyHeading(2)));
        panel.Children.Add(CommandButton("❯", "Block quote", () => PrefixSelectedLines("> ")));
        panel.Children.Add(CommandButton("•", "Bullet list", () => PrefixSelectedLines("- ")));
        panel.Children.Add(ToolbarSeparator());
        panel.Children.Add(CommandButton("⌕", "Find / Replace in document (Ctrl+H)", OpenFindPanel));
        panel.Children.Add(ToolbarSeparator());

        _focusToggle = Toggle("Focus", "Dim everything outside the current paragraph", value =>
        {
            _focusMode = value;
            if (_editor is not null) _editor.FocusCurrentParagraph = value;
            SavePreferences();
        });
        _typewriterToggle = Toggle("Type", "Typewriter scrolling keeps the caret centered", value =>
        {
            _typewriterMode = value;
            if (_editor is not null) _editor.TypewriterScrolling = value;
            SavePreferences();
        });
        _markdownToggle = Toggle("MD", "Show Markdown punctuation", value =>
        {
            _markdownMarks = value;
            if (_editor is not null)
            {
                _editor.ShowMarkdownMarks = value;
                _editor.TextArea.TextView.Redraw();
            }
            SavePreferences();
        });
        _wrapToggle = Toggle("Wrap", "Toggle soft word wrapping", value =>
        {
            _wordWrap = value;
            if (_editor is not null) _editor.WordWrap = value;
            SavePreferences();
        });
        _lineNumbersToggle = Toggle("Ln", "Show line numbers", value =>
        {
            _lineNumbers = value;
            if (_editor is not null) _editor.ShowLineNumbers = value;
            SavePreferences();
        });
        _pageWidthToggle = Toggle("Page", "Comfortable centered manuscript width", value =>
        {
            _pageWidth = value;
            ApplyPageWidth();
            SavePreferences();
        });

        panel.Children.Add(_focusToggle);
        panel.Children.Add(_typewriterToggle);
        panel.Children.Add(_markdownToggle);
        panel.Children.Add(_wrapToggle);
        panel.Children.Add(_lineNumbersToggle);
        panel.Children.Add(_pageWidthToggle);
        panel.Children.Add(ToolbarSeparator());
        panel.Children.Add(CommandButton("−", "Decrease editor font size", () => ChangeFontSize(-1)));
        panel.Children.Add(_zoomText);
        panel.Children.Add(CommandButton("+", "Increase editor font size", () => ChangeFontSize(1)));
        panel.Children.Add(CommandButton("↺", "Reset editor font size", ResetFontSize));

        var scroll = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        return new Border
        {
            Child = scroll,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private Control BuildFindPanel()
    {
        var previous = SmallButton("↑", "Previous match (Shift+F3)", () => NavigateMatch(-1, focusEditor: false));
        var next = SmallButton("↓", "Next match (F3)", () => NavigateMatch(1, focusEditor: false));
        var replace = SmallButton("Replace", "Replace current match", ReplaceCurrent);
        var replaceAll = SmallButton("All", "Replace all matches", ReplaceAll);
        var close = SmallButton("×", "Close find / replace", CloseFindPanel);

        var firstRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 5
        };
        firstRow.Children.Add(_findBox);
        Grid.SetColumn(_findStatus, 1);
        firstRow.Children.Add(_findStatus);
        Grid.SetColumn(previous, 2);
        firstRow.Children.Add(previous);
        Grid.SetColumn(next, 3);
        firstRow.Children.Add(next);
        Grid.SetColumn(close, 4);
        firstRow.Children.Add(close);

        var secondRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 7,
            Margin = new Thickness(0, 5, 0, 0)
        };
        secondRow.Children.Add(_replaceBox);
        Grid.SetColumn(_matchCase, 1);
        secondRow.Children.Add(_matchCase);
        Grid.SetColumn(_wholeWord, 2);
        secondRow.Children.Add(_wholeWord);
        Grid.SetColumn(replace, 3);
        secondRow.Children.Add(replace);
        Grid.SetColumn(replaceAll, 4);
        secondRow.Children.Add(replaceAll);

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(10, 7, 10, 7)
        };
        content.Children.Add(firstRow);
        Grid.SetRow(secondRow, 1);
        content.Children.Add(secondRow);

        _findBox.TextChanged += (_, _) => ScheduleFindRefresh();
        _findBox.KeyDown += FindBoxKeyDown;
        _replaceBox.KeyDown += ReplaceBoxKeyDown;
        _matchCase.Click += (_, _) => RefreshMatches(preserveCurrent: false);
        _wholeWord.Click += (_, _) => RefreshMatches(preserveCurrent: false);

        return new Border
        {
            Child = content,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private Control BuildStatusBar()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 3, 10, 4),
            MinHeight = 24
        };
        grid.Children.Add(_contextText);
        Grid.SetColumn(_statsText, 1);
        _statsText.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(_statsText);
        return grid;
    }

    private Button CommandButton(
        string text,
        string toolTip,
        Action action,
        FontWeight? weight = null,
        FontStyle? style = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = weight ?? FontWeight.Normal,
            FontStyle = style ?? FontStyle.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var button = new Button
        {
            Content = label,
            MinWidth = 30,
            Height = 27,
            MinHeight = 27,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, toolTip);
        button.Click += (_, _) =>
        {
            action();
            if (_findPanel?.IsVisible != true) _editor?.Focus();
        };
        return button;
    }

    private static Border ToolbarSeparator()
        => new()
        {
            Width = 1,
            Margin = new Thickness(4, 4),
            Opacity = 0.28,
            Background = Avalonia.Media.Brushes.Gray
        };

    private ToggleButton Toggle(string text, string toolTip, Action<bool> changed)
    {
        var button = new ToggleButton
        {
            Content = text,
            MinWidth = 36,
            Height = 27,
            MinHeight = 27,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, toolTip);
        button.IsCheckedChanged += (_, _) =>
        {
            if (_syncingPreferences) return;
            changed(button.IsChecked == true);
        };
        return button;
    }

    private static Button SmallButton(string text, string toolTip, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = text.Length <= 1 ? 28 : 54,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, toolTip);
        button.Click += (_, _) => action();
        return button;
    }

    private void HookEditor()
    {
        if (_editor is null) return;
        _editor.TextChanged += EditorTextChanged;
        _editor.TextArea.Caret.PositionChanged += EditorCaretChanged;
        _editor.TextArea.SelectionChanged += EditorSelectionChanged;
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        ScheduleStatusUpdate();
        if (_findPanel?.IsVisible == true) ScheduleFindRefresh();
    }

    private void EditorCaretChanged(object? sender, EventArgs e) => UpdateStatus();
    private void EditorSelectionChanged(object? sender, EventArgs e) => UpdateStatus();

    private void ScheduleStatusUpdate()
    {
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ScheduleFindRefresh()
    {
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void UpdateEnabledState()
    {
        var enabled = _viewModel.HasDocument;
        if (_toolbar is not null)
        {
            _toolbar.IsEnabled = enabled;
            _toolbar.IsVisible = enabled;
        }
        if (_statusBar is not null) _statusBar.IsVisible = enabled;
        if (_findPanel is not null && !enabled) _findPanel.IsVisible = false;
    }

    private void UpdateStatus()
    {
        var editor = _editor;
        if (editor is null) return;

        var line = Math.Max(1, editor.TextArea.Caret.Line);
        var column = Math.Max(1, editor.TextArea.Caret.Column);
        var characters = editor.Document.TextLength;
        var selectionLength = Math.Max(0, editor.SelectionLength);
        var selectedWords = selectionLength > 0
            ? CountWords(editor.Document.GetText(editor.SelectionStart, selectionLength))
            : 0;

        var wordText = _viewModel.SelectedTargetWords > 0
            ? $"{_viewModel.WordCount:N0}/{_viewModel.SelectedTargetWords:N0} words"
            : $"{_viewModel.WordCount:N0} words";
        var selectionText = selectionLength > 0
            ? $" · {selectedWords:N0} selected"
            : string.Empty;
        _statsText.Text = $"Ln {line:N0}, Col {column:N0} · {wordText} · {characters:N0} chars{selectionText}";

        var heading = CurrentHeadingContext(editor, line);
        _contextText.Text = string.IsNullOrWhiteSpace(heading)
            ? _viewModel.SelectedTitle
            : heading;
    }

    private static string CurrentHeadingContext(ManuscriptEditor editor, int caretLine)
    {
        if (editor.Document.LineCount == 0) return string.Empty;
        var parts = new List<(int Level, string Title)>();
        var wantedLevel = 7;
        var floor = Math.Max(1, caretLine - 2500);

        for (var lineNumber = Math.Min(caretLine, editor.Document.LineCount); lineNumber >= floor; lineNumber--)
        {
            var line = editor.Document.GetLineByNumber(lineNumber);
            var text = editor.Document.GetText(line).TrimStart();
            var level = 0;
            while (level < text.Length && level < 6 && text[level] == '#') level++;
            if (level == 0 || level >= text.Length || text[level] != ' ' || level >= wantedLevel) continue;

            var title = text[(level + 1)..].Trim();
            if (title.Length == 0) continue;
            parts.Add((level, title));
            wantedLevel = level;
            if (level == 1) break;
        }

        if (parts.Count == 0) return string.Empty;
        parts.Reverse();
        return string.Join("  ›  ", parts.Select(static part => part.Title));
    }

    private void OpenFindPanel()
    {
        if (_editor is null || _findPanel is null || !_viewModel.HasDocument) return;
        _findPanel.IsVisible = true;

        if (_editor.SelectionLength is > 0 and <= 160)
        {
            var selected = _editor.Document.GetText(_editor.SelectionStart, _editor.SelectionLength);
            if (!selected.Contains('\n') && !selected.Contains('\r'))
                _findBox.Text = selected;
        }

        RefreshMatches(preserveCurrent: false);
        Dispatcher.UIThread.Post(() =>
        {
            _findBox.Focus();
            _findBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void CloseFindPanel()
    {
        if (_findPanel is null) return;
        _findPanel.IsVisible = false;
        _findTimer.Stop();
        _matches.Clear();
        _currentMatch = -1;
        _findStatus.Text = string.Empty;
        _editor?.Focus();
    }

    private void RefreshMatches(bool preserveCurrent)
    {
        var editor = _editor;
        if (editor is null) return;

        var query = _findBox.Text ?? string.Empty;
        var previousOffset = preserveCurrent && _currentMatch >= 0 && _currentMatch < _matches.Count
            ? _matches[_currentMatch].Offset
            : editor.CaretOffset;

        _matches.Clear();
        _currentMatch = -1;

        if (query.Length == 0)
        {
            _findStatus.Text = "0 matches";
            return;
        }

        var text = editor.Text ?? string.Empty;
        var comparison = _matchCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var offset = 0;

        while (offset <= text.Length - query.Length && _matches.Count < MaximumMatches)
        {
            var found = text.IndexOf(query, offset, comparison);
            if (found < 0) break;

            if (_wholeWord.IsChecked != true || IsWholeWord(text, found, query.Length))
                _matches.Add(new DocumentMatch(found, query.Length));

            offset = found + Math.Max(1, query.Length);
        }

        if (_matches.Count == 0)
        {
            _findStatus.Text = "No matches";
            return;
        }

        _currentMatch = _matches.FindIndex(match => match.Offset >= previousOffset);
        if (_currentMatch < 0) _currentMatch = 0;
        UpdateFindStatus();
    }

    private void NavigateMatch(int delta, bool focusEditor)
    {
        if (_editor is null) return;
        if (_matches.Count == 0) RefreshMatches(preserveCurrent: false);
        if (_matches.Count == 0) return;

        if (_currentMatch < 0) _currentMatch = 0;
        else if (delta != 0) _currentMatch = (_currentMatch + delta + _matches.Count) % _matches.Count;

        SelectCurrentMatch(focusEditor);
    }

    private void SelectCurrentMatch(bool focusEditor)
    {
        var editor = _editor;
        if (editor is null || _currentMatch < 0 || _currentMatch >= _matches.Count) return;

        var match = _matches[_currentMatch];
        editor.Select(match.Offset, match.Length);
        editor.CaretOffset = match.Offset + match.Length;
        var line = editor.Document.GetLineByOffset(Math.Min(match.Offset, Math.Max(0, editor.Document.TextLength))).LineNumber;
        editor.ScrollTo(line, 1);
        if (focusEditor) editor.Focus();
        UpdateFindStatus();
        UpdateStatus();
    }

    private void ReplaceCurrent()
    {
        var editor = _editor;
        if (editor is null) return;
        if (_matches.Count == 0) RefreshMatches(preserveCurrent: false);
        if (_matches.Count == 0 || _currentMatch < 0) return;

        var match = _matches[_currentMatch];
        var replacement = _replaceBox.Text ?? string.Empty;
        editor.Document.Replace(match.Offset, match.Length, replacement);
        editor.CaretOffset = Math.Min(match.Offset + replacement.Length, editor.Document.TextLength);
        RefreshMatches(preserveCurrent: false);
        if (_matches.Count > 0) SelectCurrentMatch(focusEditor: false);
    }

    private void ReplaceAll()
    {
        var editor = _editor;
        if (editor is null) return;
        RefreshMatches(preserveCurrent: false);
        if (_matches.Count == 0) return;

        var replacement = _replaceBox.Text ?? string.Empty;
        var replaced = _matches.Count;
        editor.Document.BeginUpdate();
        try
        {
            for (var index = _matches.Count - 1; index >= 0; index--)
            {
                var match = _matches[index];
                editor.Document.Replace(match.Offset, match.Length, replacement);
            }
        }
        finally
        {
            editor.Document.EndUpdate();
        }

        RefreshMatches(preserveCurrent: false);
        _findStatus.Text = $"Replaced {replaced:N0}";
    }

    private void UpdateFindStatus()
    {
        if (_matches.Count == 0)
        {
            _findStatus.Text = "No matches";
            return;
        }

        var capped = _matches.Count >= MaximumMatches ? "+" : string.Empty;
        _findStatus.Text = $"{Math.Max(0, _currentMatch) + 1:N0}/{_matches.Count:N0}{capped}";
    }

    private static bool IsWholeWord(string text, int offset, int length)
    {
        var before = offset - 1;
        var after = offset + length;
        return (before < 0 || !IsWordCharacter(text[before])) &&
               (after >= text.Length || !IsWordCharacter(text[after]));
    }

    private static bool IsWordCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '\'' or '’';

    private void FindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFindPanel();
            return;
        }

        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RefreshMatches(preserveCurrent: true);
        NavigateMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, focusEditor: false);
        _findBox.Focus();
    }

    private void ReplaceBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFindPanel();
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ReplaceCurrent();
            _replaceBox.Focus();
        }
    }

    private void WindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (primary && e.Key == Key.H)
        {
            e.Handled = true;
            OpenFindPanel();
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            if (_findPanel?.IsVisible != true) OpenFindPanel();
            else NavigateMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, focusEditor: true);
            return;
        }

        if (!_editor.IsKeyboardFocusWithin) return;

        if (primary && e.Key == Key.B)
        {
            e.Handled = true;
            WrapSelection("**", "**");
        }
        else if (primary && e.Key == Key.I)
        {
            e.Handled = true;
            WrapSelection("*", "*");
        }
        else if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.M)
        {
            e.Handled = true;
            _markdownToggle!.IsChecked = _markdownToggle.IsChecked != true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Z)
        {
            e.Handled = true;
            _wrapToggle!.IsChecked = _wrapToggle.IsChecked != true;
        }
    }

    private void WrapSelection(string prefix, string suffix)
    {
        var editor = _editor;
        if (editor is null || editor.IsReadOnly) return;

        var start = editor.SelectionStart;
        var length = editor.SelectionLength;
        if (length > 0)
        {
            var selected = editor.Document.GetText(start, length);
            editor.Document.Replace(start, length, prefix + selected + suffix);
            editor.Select(start + prefix.Length, selected.Length);
            editor.CaretOffset = start + prefix.Length + selected.Length;
        }
        else
        {
            var caret = editor.CaretOffset;
            editor.Document.Insert(caret, prefix + suffix);
            editor.CaretOffset = caret + prefix.Length;
            editor.Select(editor.CaretOffset, 0);
        }
        editor.Focus();
    }

    private void ApplyHeading(int level)
    {
        var editor = _editor;
        if (editor is null || editor.IsReadOnly || editor.Document.LineCount == 0) return;

        var line = editor.Document.GetLineByOffset(Math.Min(editor.CaretOffset, Math.Max(0, editor.Document.TextLength)));
        var raw = editor.Document.GetText(line);
        var content = raw.TrimStart();
        var existing = 0;
        while (existing < content.Length && existing < 6 && content[existing] == '#') existing++;
        if (existing > 0 && existing < content.Length && content[existing] == ' ')
            content = content[(existing + 1)..];

        var replacement = new string('#', Math.Clamp(level, 1, 6)) + " " + content;
        editor.Document.Replace(line.Offset, line.Length, replacement);
        editor.CaretOffset = line.Offset + replacement.Length;
        editor.Focus();
    }

    private void PrefixSelectedLines(string prefix)
    {
        var editor = _editor;
        if (editor is null || editor.IsReadOnly) return;

        var text = editor.Text ?? string.Empty;
        var start = Math.Clamp(editor.SelectionStart, 0, text.Length);
        var end = Math.Clamp(editor.SelectionStart + editor.SelectionLength, start, text.Length);
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = text.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = text.Length;
        var block = text[lineStart..lineEnd];
        var replacement = prefix + block.Replace("\n", "\n" + prefix, StringComparison.Ordinal);
        editor.Document.Replace(lineStart, lineEnd - lineStart, replacement);
        editor.Select(lineStart, replacement.Length);
        editor.CaretOffset = lineStart + replacement.Length;
        editor.Focus();
    }

    private void ChangeFontSize(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, MinFontSize, MaxFontSize);
        if (_editor is not null) _editor.FontSize = _fontSize;
        UpdateZoomText();
        SavePreferences();
    }

    private void ResetFontSize()
    {
        _fontSize = DefaultFontSize;
        if (_editor is not null) _editor.FontSize = _fontSize;
        UpdateZoomText();
        SavePreferences();
    }

    private void ApplyPageWidth()
    {
        if (_editor is null) return;
        _editor.MaxWidth = _pageWidth ? PageWidth : double.PositiveInfinity;
        _editor.HorizontalAlignment = _pageWidth ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }

    private void UpdateZoomText()
    {
        var percent = (int)Math.Round((_fontSize / DefaultFontSize) * 100, MidpointRounding.AwayFromZero);
        _zoomText.Text = $"{percent}%";
    }

    private void ApplyPreferences()
    {
        if (_editor is null) return;

        _syncingPreferences = true;
        try
        {
            _editor.FontSize = _fontSize;
            _editor.FocusCurrentParagraph = _focusMode;
            _editor.TypewriterScrolling = _typewriterMode;
            _editor.ShowMarkdownMarks = _markdownMarks;
            _editor.WordWrap = _wordWrap;
            _editor.ShowLineNumbers = _lineNumbers;
            _editor.TextArea.TextView.Redraw();
            ApplyPageWidth();

            if (_focusToggle is not null) _focusToggle.IsChecked = _focusMode;
            if (_typewriterToggle is not null) _typewriterToggle.IsChecked = _typewriterMode;
            if (_markdownToggle is not null) _markdownToggle.IsChecked = _markdownMarks;
            if (_wrapToggle is not null) _wrapToggle.IsChecked = _wordWrap;
            if (_lineNumbersToggle is not null) _lineNumbersToggle.IsChecked = _lineNumbers;
            if (_pageWidthToggle is not null) _pageWidthToggle.IsChecked = _pageWidth;
            UpdateZoomText();
        }
        finally
        {
            _syncingPreferences = false;
        }
    }

    private void LoadPreferences()
    {
        var path = PreferencesPath();
        if (!File.Exists(path)) return;

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var separator = raw.IndexOf('=');
                if (separator <= 0) continue;
                var key = raw[..separator].Trim();
                var value = raw[(separator + 1)..].Trim();
                switch (key)
                {
                    case "font" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size):
                        _fontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
                        break;
                    case "focus": _focusMode = ParseBool(value, _focusMode); break;
                    case "typewriter": _typewriterMode = ParseBool(value, _typewriterMode); break;
                    case "markdown": _markdownMarks = ParseBool(value, _markdownMarks); break;
                    case "wrap": _wordWrap = ParseBool(value, _wordWrap); break;
                    case "lines": _lineNumbers = ParseBool(value, _lineNumbers); break;
                    case "pagewidth": _pageWidth = ParseBool(value, _pageWidth); break;
                }
            }
        }
        catch
        {
        }
    }

    private void SavePreferences()
    {
        if (_syncingPreferences) return;
        try
        {
            var path = PreferencesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = string.Join(Environment.NewLine,
                $"font={_fontSize.ToString(CultureInfo.InvariantCulture)}",
                $"focus={_focusMode}",
                $"typewriter={_typewriterMode}",
                $"markdown={_markdownMarks}",
                $"wrap={_wordWrap}",
                $"lines={_lineNumbers}",
                $"pagewidth={_pageWidth}") + Environment.NewLine;
            File.WriteAllText(path, content);
        }
        catch
        {
            // Editor preferences must never interfere with manuscript editing.
        }
    }

    private static string PreferencesPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Typescribe",
            "editor-preferences.tsv");

    private static bool ParseBool(string text, bool fallback)
        => bool.TryParse(text, out var value) ? value : fallback;

    private void ExtendEditorContextMenu()
    {
        if (_editor?.ContextMenu is not { } menu) return;
        var items = MenuItems(menu.ItemsSource);
        if (items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Find / Replace in Document"))) return;

        items.Add(new Separator());
        items.Add(ContextAction("Find / Replace in Document", OpenFindPanel));
        items.Add(new Separator());
        items.Add(ContextAction("Bold", () => WrapSelection("**", "**")));
        items.Add(ContextAction("Italic", () => WrapSelection("*", "*")));
        items.Add(ContextAction("Inline Code", () => WrapSelection("`", "`")));
        items.Add(ContextAction("Heading 1", () => ApplyHeading(1)));
        items.Add(ContextAction("Heading 2", () => ApplyHeading(2)));
        items.Add(ContextAction("Block Quote", () => PrefixSelectedLines("> ")));
        items.Add(ContextAction("Bullet List", () => PrefixSelectedLines("- ")));
        menu.ItemsSource = items.ToArray();
    }

    private void InjectEditMenu()
    {
        if (_menuInjected) return;
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable source) return;

        var edit = source.Cast<object?>().OfType<MenuItem>()
            .FirstOrDefault(item => HeaderEquals(item, "Edit"));
        if (edit is null) return;

        var items = MenuItems(edit.ItemsSource);
        if (items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Find / Replace in Document")))
        {
            _menuInjected = true;
            return;
        }

        var insertion = items.FindIndex(item => item is Separator);
        if (insertion < 0) insertion = items.Count;
        else insertion++;

        items.Insert(insertion++, MenuAction(
            "Find / Replace in _Document…",
            OpenFindPanel,
            new KeyGesture(Key.H, PrimaryModifier())));
        items.Insert(insertion++, MenuAction(
            "Find _Next in Document",
            () => NavigateMatch(1, focusEditor: true),
            new KeyGesture(Key.F3)));
        items.Insert(insertion, MenuAction(
            "Find _Previous in Document",
            () => NavigateMatch(-1, focusEditor: true),
            new KeyGesture(Key.F3, KeyModifiers.Shift)));
        edit.ItemsSource = items.ToArray();
        _menuInjected = true;
    }

    private static MenuItem MenuAction(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem ContextAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static List<object> MenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) return [];
        return enumerable.Cast<object>().ToList();
    }

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals(
            (item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Replace("…", string.Empty, StringComparison.Ordinal).Trim(),
            text.Replace("…", string.Empty, StringComparison.Ordinal).Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static KeyModifiers PrimaryModifier()
        => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            var word = char.IsLetterOrDigit(ch) || ch is '_' or '\'' or '’';
            if (word && !inWord) count++;
            inWord = word;
        }
        return count;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _statusTimer.Stop();
        _findTimer.Stop();
        SavePreferences();

        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.RemoveHandler(InputElement.KeyDownEvent, WindowPreviewKeyDown);

        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= EditorCaretChanged;
            _editor.TextArea.SelectionChanged -= EditorSelectionChanged;
        }
    }

    private sealed record DocumentMatch(int Offset, int Length);
}
