using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
/// persistent author preferences, document find/replace, shared editor commands,
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
    private readonly EditorCommandSet _commands = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _findTimer;
    private readonly List<DocumentMatch> _matches = [];

    private ManuscriptEditor? _editor;
    private LongFormEditorChrome? _host;
    private Control? _toolbar;
    private Control? _findPanel;
    private Control? _statusBar;
    private TextBox? _findBox;
    private TextBox? _replaceBox;
    private CheckBox? _matchCase;
    private CheckBox? _wholeWord;
    private TextBlock? _findStatus;
    private TextBlock? _contextText;
    private TextBlock? _statsText;
    private TextBlock? _zoomText;
    private int _currentMatch = -1;
    private bool _installed;
    private bool _menusInjected;
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
        InjectEditorMenus();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
        else ConstrainHostToViewport();

        if (!_menusInjected) InjectEditorMenus();
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

        _host = new LongFormEditorChrome(_commands)
        {
            MinWidth = editor.MinWidth,
            MinHeight = editor.MinHeight
        };
        _host.SetEditor(editor);

        _toolbar = _host.CommandBar;
        _findPanel = _host.FindPanel;
        _statusBar = _host.StatusBar;
        _findBox = _host.FindBox;
        _replaceBox = _host.ReplaceBox;
        _matchCase = _host.MatchCase;
        _wholeWord = _host.WholeWord;
        _findStatus = _host.FindStatus;
        _contextText = _host.ContextText;
        _statsText = _host.StatsText;
        _zoomText = _host.ZoomText;

        parent.Children.Insert(index, _host);
        Grid.SetRow(_host, row);
        Grid.SetColumn(_host, column);
        Grid.SetRowSpan(_host, rowSpan);
        Grid.SetColumnSpan(_host, columnSpan);
        ConstrainHostToViewport();

        ConfigureCommands();
        WireFindPanel();
        HookEditor();
        ExtendEditorContextMenu();
        InjectEditorMenus();
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

    private void ConfigureCommands()
    {
        bool HasDocument() => _editor is not null && _viewModel.HasDocument;
        bool CanEdit() => HasDocument() && _editor?.IsReadOnly != true;

        _commands.Bold.Bind(() => WrapSelection("**", "**"), CanEdit);
        _commands.Italic.Bind(() => WrapSelection("*", "*"), CanEdit);
        _commands.InlineCode.Bind(() => WrapSelection("`", "`"), CanEdit);
        _commands.Heading1.Bind(() => ApplyHeading(1), CanEdit);
        _commands.Heading2.Bind(() => ApplyHeading(2), CanEdit);
        _commands.Heading3.Bind(() => ApplyHeading(3), CanEdit);
        _commands.BlockQuote.Bind(() => PrefixSelectedLines("> "), CanEdit);
        _commands.BulletList.Bind(() => PrefixSelectedLines("- "), CanEdit);
        _commands.NumberedList.Bind(() => PrefixSelectedLines("1. "), CanEdit);

        _commands.FindReplace.Bind(OpenFindPanel, HasDocument);
        _commands.FindNext.Bind(() => ExecuteFindNavigation(1), HasDocument);
        _commands.FindPrevious.Bind(() => ExecuteFindNavigation(-1), HasDocument);
        _commands.ReplaceCurrent.Bind(ReplaceCurrent, HasDocument);
        _commands.ReplaceAll.Bind(ReplaceAll, HasDocument);
        _commands.CloseFind.Bind(CloseFindPanel, () => _findPanel?.IsVisible == true);

        _commands.FocusMode.BindToggle(value =>
        {
            _focusMode = value;
            if (_editor is not null) _editor.FocusCurrentParagraph = value;
            SavePreferences();
        }, HasDocument);
        _commands.TypewriterMode.BindToggle(value =>
        {
            _typewriterMode = value;
            if (_editor is not null) _editor.TypewriterScrolling = value;
            SavePreferences();
        }, HasDocument);
        _commands.MarkdownMarks.BindToggle(value =>
        {
            _markdownMarks = value;
            if (_editor is not null)
            {
                _editor.ShowMarkdownMarks = value;
                _editor.TextArea.TextView.Redraw();
            }
            SavePreferences();
        }, HasDocument);
        _commands.WordWrap.BindToggle(value =>
        {
            _wordWrap = value;
            if (_editor is not null) _editor.WordWrap = value;
            SavePreferences();
        }, HasDocument);
        _commands.LineNumbers.BindToggle(value =>
        {
            _lineNumbers = value;
            if (_editor is not null) _editor.ShowLineNumbers = value;
            WorkspaceUxCompletionFeature.PublishManuscriptLineNumbersPreference(value);
            SavePreferences();
        }, HasDocument);
        _commands.PageWidth.BindToggle(value =>
        {
            _pageWidth = value;
            ApplyPageWidth();
            SavePreferences();
        }, HasDocument);

        _commands.ZoomOut.Bind(() => ChangeFontSize(-1), HasDocument);
        _commands.ZoomIn.Bind(() => ChangeFontSize(1), HasDocument);
        _commands.ZoomReset.Bind(ResetFontSize, HasDocument);
        _commands.RefreshCanExecute();
    }

    private void WireFindPanel()
    {
        if (_findBox is null || _replaceBox is null || _matchCase is null || _wholeWord is null) return;
        _findBox.TextChanged += (_, _) => ScheduleFindRefresh();
        _findBox.KeyDown += FindBoxKeyDown;
        _replaceBox.KeyDown += ReplaceBoxKeyDown;
        _matchCase.Click += (_, _) => RefreshMatches(preserveCurrent: false);
        _wholeWord.Click += (_, _) => RefreshMatches(preserveCurrent: false);
    }

    private void ConstrainHostToViewport()
    {
        if (_host?.Parent is not Control parent || parent.Bounds.Height <= 0) return;

        var availableHeight = parent.Bounds.Height - _host.Bounds.Y;
        if (availableHeight <= 0 || double.IsInfinity(availableHeight) || double.IsNaN(availableHeight)) return;

        var maxHeight = Math.Max(180, availableHeight);
        if (double.IsInfinity(_host.MaxHeight) || Math.Abs(_host.MaxHeight - maxHeight) > 0.5)
        {
            _host.MaxHeight = maxHeight;
            _host.InvalidateMeasure();
        }
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
        _commands.RefreshCanExecute();
    }

    private void UpdateStatus()
    {
        var editor = _editor;
        if (editor is null || _statsText is null || _contextText is null) return;

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
        if (_editor is null || _findPanel is null || _findBox is null || !_viewModel.HasDocument) return;
        _findPanel.IsVisible = true;
        _commands.RefreshCanExecute();

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
        if (_findPanel is null || _findStatus is null) return;
        _findPanel.IsVisible = false;
        _findTimer.Stop();
        _matches.Clear();
        _currentMatch = -1;
        _findStatus.Text = string.Empty;
        _commands.RefreshCanExecute();
        _editor?.Focus();
    }

    private void ExecuteFindNavigation(int delta)
    {
        if (_findPanel?.IsVisible != true)
        {
            OpenFindPanel();
            return;
        }

        NavigateMatch(delta, focusEditor: true);
    }

    private void RefreshMatches(bool preserveCurrent)
    {
        var editor = _editor;
        if (editor is null || _findBox is null || _matchCase is null || _wholeWord is null || _findStatus is null) return;

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
        if (editor is null || _replaceBox is null) return;
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
        if (editor is null || _replaceBox is null || _findStatus is null) return;
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
        if (_findStatus is null) return;
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
        _findBox?.Focus();
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
            _replaceBox?.Focus();
        }
    }

    private void WindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        if (_commands.TryExecuteShortcut(e, _editor.IsKeyboardFocusWithin)) e.Handled = true;
    }

    private void WrapSelection(string prefix, string suffix)
    {
        var editor = _editor;
        if (editor is null || editor.IsReadOnly) return;

        if (prefix == "**" && suffix == "**") RichFormattingEngine.ToggleBold(editor);
        else if (prefix == "*" && suffix == "*") RichFormattingEngine.ToggleItalic(editor);
        else if (prefix == "`" && suffix == "`") RichFormattingEngine.ToggleCode(editor);
        editor.Focus();
    }

    private void ApplyHeading(int level)
    {
        if (_editor is null) return;
        RichFormattingEngine.ToggleHeading(_editor, level);
    }

    private void PrefixSelectedLines(string prefix)
    {
        var editor = _editor;
        if (editor is null || editor.IsReadOnly) return;

        if (prefix == "> ") RichFormattingEngine.ToggleQuote(editor);
        else if (prefix == "- ") RichFormattingEngine.ToggleBulletList(editor);
        else if (prefix == "1. ") RichFormattingEngine.ToggleNumberedList(editor);
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
        if (_zoomText is null) return;
        var percent = (int)Math.Round((_fontSize / DefaultFontSize) * 100, MidpointRounding.AwayFromZero);
        _zoomText.Text = $"{percent}%";
    }

    private void ApplyPreferences()
    {
        if (_editor is null) return;

        _editor.FontSize = _fontSize;
        _editor.FocusCurrentParagraph = _focusMode;
        _editor.TypewriterScrolling = _typewriterMode;
        _editor.ShowMarkdownMarks = _markdownMarks;
        _editor.WordWrap = _wordWrap;
        _editor.ShowLineNumbers = _lineNumbers;
        WorkspaceUxCompletionFeature.PublishManuscriptLineNumbersPreference(_lineNumbers);
        _editor.TextArea.TextView.Redraw();
        ApplyPageWidth();

        _commands.FocusMode.SetCheckedFromModel(_focusMode);
        _commands.TypewriterMode.SetCheckedFromModel(_typewriterMode);
        _commands.MarkdownMarks.SetCheckedFromModel(_markdownMarks);
        _commands.WordWrap.SetCheckedFromModel(_wordWrap);
        _commands.LineNumbers.SetCheckedFromModel(_lineNumbers);
        _commands.PageWidth.SetCheckedFromModel(_pageWidth);
        UpdateZoomText();
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
        if (items.OfType<MenuItem>().Any(item => HeaderEquals(item, _commands.FindReplace.Label))) return;

        items.Add(new Separator());
        items.Add(CommandMenuItem(_commands.FindReplace.Label, _commands.FindReplace));
        items.Add(new Separator());
        items.Add(CommandMenuItem(_commands.Bold.Label, _commands.Bold));
        items.Add(CommandMenuItem(_commands.Italic.Label, _commands.Italic));
        items.Add(CommandMenuItem(_commands.InlineCode.Label, _commands.InlineCode));
        items.Add(CommandMenuItem(_commands.Heading1.Label, _commands.Heading1));
        items.Add(CommandMenuItem(_commands.Heading2.Label, _commands.Heading2));
        items.Add(CommandMenuItem(_commands.BlockQuote.Label, _commands.BlockQuote));
        items.Add(CommandMenuItem(_commands.BulletList.Label, _commands.BulletList));
        items.Add(CommandMenuItem(_commands.NumberedList.Label, _commands.NumberedList));
        menu.ItemsSource = items.ToArray();
    }

    private void InjectEditorMenus()
    {
        if (_menusInjected) return;
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable source) return;

        var topLevel = source.Cast<object?>().OfType<MenuItem>().ToArray();
        var edit = topLevel.FirstOrDefault(item => HeaderEquals(item, "Edit"));
        var insert = topLevel.FirstOrDefault(item => HeaderEquals(item, "Insert"));
        var format = topLevel.FirstOrDefault(item => HeaderEquals(item, "Format"));
        if (edit is null || insert is null || format is null) return;

        WireEditMenu(edit);
        WireInsertMenu(insert);
        WireFormatMenu(format);
        _menusInjected = true;
    }

    private void WireEditMenu(MenuItem edit)
    {
        var items = MenuItems(edit.ItemsSource);
        if (items.OfType<MenuItem>().Any(item => HeaderEquals(item, _commands.FindReplace.Label)))
        {
            edit.ItemsSource = items.ToArray();
            return;
        }

        var insertion = items.FindIndex(item => item is Separator);
        if (insertion < 0) insertion = items.Count;
        else insertion++;

        items.Insert(insertion++, CommandMenuItem("Find / Replace in _Document…", _commands.FindReplace));
        items.Insert(insertion++, CommandMenuItem("Find _Next in Document", _commands.FindNext));
        items.Insert(insertion, CommandMenuItem("Find _Previous in Document", _commands.FindPrevious));
        edit.ItemsSource = items.ToArray();
    }

    private void WireInsertMenu(MenuItem insert)
    {
        var items = MenuItems(insert.ItemsSource);
        ReplaceMenuCommand(items, "Heading 1", _commands.Heading1);
        ReplaceMenuCommand(items, "Heading 2", _commands.Heading2);
        ReplaceMenuCommand(items, "Heading 3", _commands.Heading3);
        insert.ItemsSource = items.ToArray();
    }

    private void WireFormatMenu(MenuItem format)
    {
        var items = MenuItems(format.ItemsSource);
        ReplaceMenuCommand(items, "Bold", _commands.Bold);
        ReplaceMenuCommand(items, "Italic", _commands.Italic);
        ReplaceMenuCommand(items, "Inline Code", _commands.InlineCode);
        ReplaceMenuCommand(items, "Block Quote", _commands.BlockQuote);
        ReplaceMenuCommand(items, "Bullet List", _commands.BulletList);

        if (!items.OfType<MenuItem>().Any(item => HeaderEquals(item, _commands.NumberedList.Label)))
        {
            var bulletIndex = items.FindIndex(item => item is MenuItem menuItem && HeaderEquals(menuItem, "Bullet List"));
            items.Insert(bulletIndex >= 0 ? bulletIndex + 1 : items.Count, CommandMenuItem("_Numbered List", _commands.NumberedList));
        }

        format.ItemsSource = items.ToArray();
    }

    private static void ReplaceMenuCommand(List<object> items, string header, EditorCommand command)
    {
        var index = items.FindIndex(item => item is MenuItem menuItem && HeaderEquals(menuItem, header));
        if (index < 0) return;
        var current = (MenuItem)items[index];
        items[index] = CommandMenuItem(current.Header?.ToString() ?? command.Label, command);
    }

    private static MenuItem CommandMenuItem(string header, EditorCommand command)
        => new()
        {
            Header = header,
            Command = command,
            InputGesture = command.InputGesture
        };

    private static List<object> MenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) return [];
        return enumerable.Cast<object>().ToList();
    }

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals(
            (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("…", string.Empty, StringComparison.Ordinal)
                .Trim(),
            text.Replace("…", string.Empty, StringComparison.Ordinal).Trim(),
            StringComparison.OrdinalIgnoreCase);

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
        _commands.UnbindAll();

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
