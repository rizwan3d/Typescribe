using System.Text.RegularExpressions;
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
/// Adds a richer visual editing layer on top of the manuscript-first editor without changing
/// canonical Markdown storage. The feature owns contextual formatting UI, caret-aware command
/// state, richer block presentation, link editing, and a lightweight slash-command palette.
/// </summary>
internal sealed class RichVisualEditingFeature
{
    private static readonly Regex MarkdownLinkRegex = new(
        @"\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex ListPrefixRegex = new(
        @"^\s*(?:[-*+]|\d+\.)\s+",
        RegexOptions.CultureInvariant);

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly Dictionary<string, Control> _toolbarControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _selectionControls = new(StringComparer.Ordinal);
    private readonly List<QuickInsertCommand> _quickCommands = [];
    private readonly List<QuickInsertCommand> _filteredCommands = [];

    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Popup? _selectionPopup;
    private Popup? _slashPopup;
    private TextBox? _slashSearch;
    private ListBox? _slashList;
    private RichBlockColorizer? _richColorizer;
    private bool _installed;
    private bool _toolbarStyled;
    private bool _slashQueued;
    private bool _disposed;
    private int _suppressSlashDetection;

    private RichVisualEditingFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new RichVisualEditingFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.TryInstall();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryInstall();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
        if (_installed && !_toolbarStyled) TryStyleToolbar();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            RefreshFormattingState();
            RefreshSelectionPopup();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        var editor = _window.GetVisualDescendants()
            .OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
            ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null || editor.Parent is not Grid host || !host.Classes.Contains("long-form-editor-host"))
            return;

        _editor = editor;
        _host = host;

        if (!editor.Classes.Contains("rich-manuscript-editor"))
            editor.Classes.Add("rich-manuscript-editor");

        _richColorizer = new RichBlockColorizer(editor);
        editor.TextArea.TextView.LineTransformers.Add(_richColorizer);

        BuildSelectionPopup();
        BuildSlashPopup();
        BuildQuickCommands();

        editor.TextChanged += EditorTextChanged;
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextArea.SelectionChanged += SelectionChanged;
        editor.GotFocus += EditorGotFocus;

        _window.AddHandler(
            InputElement.KeyDownEvent,
            WindowPreviewKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _installed = true;
        TryStyleToolbar();
        RefreshFormattingState();
        RefreshSelectionPopup();
    }

    private void BuildSelectionPopup()
    {
        if (_editor is null || _host is null) return;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(FloatingFormatButton("B", "Bold", "bold", () => WrapSelection("**", "**")));
        row.Children.Add(FloatingFormatButton("I", "Italic", "italic", () => WrapSelection("*", "*"), FontStyle.Italic));
        row.Children.Add(FloatingFormatButton("`", "Inline code", "code", () => WrapSelection("`", "`")));
        row.Children.Add(FloatingFormatButton("H1", "Heading 1", "h1", () => ApplyHeading(1)));
        row.Children.Add(FloatingFormatButton("H2", "Heading 2", "h2", () => ApplyHeading(2)));
        row.Children.Add(FloatingFormatButton("H3", "Heading 3", "h3", () => ApplyHeading(3)));
        row.Children.Add(FloatingFormatButton("❝", "Block quote", "quote", () => PrefixSelectedLines("> ")));
        row.Children.Add(FloatingFormatButton("•", "Bullet list", "list", () => PrefixSelectedLines("- ")));
        row.Children.Add(FloatingAsyncButton("Link", "Edit link (Ctrl+K)", "link", EditLinkAsync));

        var surface = new Border
        {
            Child = row,
            Padding = new Thickness(5, 4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        surface.Classes.Add("rich-selection-toolbar");

        _selectionPopup = new Popup
        {
            Child = surface,
            PlacementTarget = _editor,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = 10,
            IsLightDismissEnabled = false,
            IsOpen = false
        };

        _host.Children.Add(_selectionPopup);
    }

    private Button FloatingFormatButton(
        string label,
        string tip,
        string key,
        Action action,
        FontStyle style = FontStyle.Normal)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontStyle = style,
                FontWeight = label == "B" ? FontWeight.Bold : FontWeight.Normal
            },
            MinWidth = label.Length > 2 ? 42 : 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(7, 2)
        };
        button.Classes.Add("rich-floating-format-button");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            action();
            if (_selectionPopup is not null) _selectionPopup.IsOpen = false;
            _editor?.Focus();
            RefreshFormattingState();
        };
        _selectionControls[key] = button;
        return button;
    }

    private Button FloatingAsyncButton(string label, string tip, string key, Func<Task> action)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 46,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(7, 2)
        };
        button.Classes.Add("rich-floating-format-button");
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) =>
        {
            await action();
            if (_selectionPopup is not null) _selectionPopup.IsOpen = false;
            _editor?.Focus();
            RefreshFormattingState();
        };
        _selectionControls[key] = button;
        return button;
    }

    private void BuildSlashPopup()
    {
        if (_editor is null || _host is null) return;

        _slashSearch = new TextBox
        {
            Watermark = "Type a command…",
            MinWidth = 360,
            Margin = new Thickness(8, 8, 8, 5)
        };
        _slashList = new ListBox
        {
            MinWidth = 420,
            MinHeight = 230,
            MaxHeight = 320,
            Margin = new Thickness(6, 0, 6, 0)
        };

        var hint = new TextBlock
        {
            Text = "↑ ↓ navigate   Enter insert   Esc close",
            Opacity = 0.58,
            FontSize = 10.5,
            Margin = new Thickness(10, 5, 10, 8)
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            MinWidth = 440
        };
        root.Children.Add(_slashSearch);
        Grid.SetRow(_slashList, 1);
        root.Children.Add(_slashList);
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        var surface = new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        surface.Classes.Add("rich-quick-insert-palette");

        _slashPopup = new Popup
        {
            Child = surface,
            PlacementTarget = _editor,
            Placement = PlacementMode.Center,
            VerticalOffset = -90,
            IsLightDismissEnabled = true,
            IsOpen = false
        };

        _slashSearch.TextChanged += (_, _) => FilterQuickCommands();
        _slashSearch.KeyDown += SlashSearchKeyDown;
        _slashList.DoubleTapped += async (_, _) => await RunSelectedQuickCommandAsync();
        _slashPopup.Closed += (_, _) =>
        {
            if (_slashSearch is not null) _slashSearch.Text = string.Empty;
            _editor?.Focus();
        };

        _host.Children.Add(_slashPopup);
    }

    private void BuildQuickCommands()
    {
        _quickCommands.Clear();
        _quickCommands.Add(new("Heading 1", "Large section heading", () => RunSync(() => ApplyHeading(1))));
        _quickCommands.Add(new("Heading 2", "Medium section heading", () => RunSync(() => ApplyHeading(2))));
        _quickCommands.Add(new("Heading 3", "Small section heading", () => RunSync(() => ApplyHeading(3))));
        _quickCommands.Add(new("Quote", "Start a block quote", () => RunSync(() => PrefixSelectedLines("> "))));
        _quickCommands.Add(new("Bullet list", "Start an unordered list", () => RunSync(() => PrefixSelectedLines("- "))));
        _quickCommands.Add(new("Numbered list", "Start an ordered list", () => RunSync(() => PrefixSelectedLines("1. "))));
        _quickCommands.Add(new("Link", "Insert or edit a Markdown link", EditLinkAsync));
        _quickCommands.Add(new("Code block", "Insert a fenced code block", () => RunSync(() => InsertBlock("```\ncode\n```", "code"))));
        _quickCommands.Add(new("Equation", "Insert a display equation", () => RunSync(() => InsertBlock("$$\nE = mc^2\n$$", "E = mc^2"))));
        _quickCommands.Add(new("Table", "Insert a 3-column Markdown table", () => RunSync(() => InsertBlock(
            "| Heading 1 | Heading 2 | Heading 3 |\n| --- | --- | --- |\n| Cell | Cell | Cell |",
            "Heading 1"))));
        _quickCommands.Add(new("Figure", "Insert an image/figure placeholder", () => RunSync(() => InsertBlock("![Caption](image-path)", "Caption"))));
        _quickCommands.Add(new("Citation", "Insert a citation placeholder", () => RunSync(() => InsertTextAtCaret("[@citation-key]", "citation-key"))));
        _quickCommands.Add(new("Horizontal rule", "Insert a thematic break", () => RunSync(() => InsertBlock("---"))));
        _quickCommands.Add(new("Mermaid diagram", "Insert a Mermaid fenced block", () => RunSync(() => InsertBlock(
            "```mermaid\ngraph TD\n    A --> B\n```",
            "graph TD\n    A --> B"))));
        FilterQuickCommands();
    }

    private static Task RunSync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private void FilterQuickCommands()
    {
        if (_slashList is null) return;
        var query = _slashSearch?.Text?.Trim() ?? string.Empty;

        _filteredCommands.Clear();
        _filteredCommands.AddRange(_quickCommands.Where(command =>
            query.Length == 0 ||
            command.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            command.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));

        _slashList.ItemsSource = _filteredCommands
            .Select(command => $"{command.Title}   —   {command.Description}")
            .ToArray();
        _slashList.SelectedIndex = _filteredCommands.Count > 0 ? 0 : -1;
    }

    private async void SlashSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_slashList is null || _slashPopup is null) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _slashPopup.IsOpen = false;
            return;
        }

        if (e.Key == Key.Down && _filteredCommands.Count > 0)
        {
            e.Handled = true;
            _slashList.SelectedIndex = Math.Min(_filteredCommands.Count - 1, Math.Max(0, _slashList.SelectedIndex + 1));
            return;
        }

        if (e.Key == Key.Up && _filteredCommands.Count > 0)
        {
            e.Handled = true;
            _slashList.SelectedIndex = Math.Max(0, _slashList.SelectedIndex - 1);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSelectedQuickCommandAsync();
        }
    }

    private async Task RunSelectedQuickCommandAsync()
    {
        if (_slashList is null || _slashPopup is null) return;
        var index = _slashList.SelectedIndex;
        if (index < 0 || index >= _filteredCommands.Count) return;

        var command = _filteredCommands[index];
        _slashPopup.IsOpen = false;
        await command.Action();
        _editor?.Focus();
        RefreshFormattingState();
    }

    private void EditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        RefreshFormattingState();
        RefreshSelectionPopup();
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        RefreshFormattingState();
        RefreshSelectionPopup();

        if (_disposed || _suppressSlashDetection > 0 || _slashQueued || _slashPopup?.IsOpen == true || sender is not ManuscriptEditor editor)
            return;
        if (!TryGetSlashOffset(editor, out var slashOffset)) return;

        _slashQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _slashQueued = false;
            if (!_disposed) OpenSlashPalette(editor, slashOffset);
        }, DispatcherPriority.Input);
    }

    private void CaretPositionChanged(object? sender, EventArgs e)
    {
        RefreshFormattingState();
        RefreshSelectionPopup();
    }

    private void SelectionChanged(object? sender, EventArgs e)
    {
        RefreshFormattingState();
        RefreshSelectionPopup();
    }

    private static bool TryGetSlashOffset(ManuscriptEditor editor, out int slashOffset)
    {
        slashOffset = -1;
        if (editor.CaretOffset <= 0 || editor.Document.TextLength == 0) return false;

        var caret = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var line = editor.Document.GetLineByOffset(Math.Max(0, caret - 1));
        var prefix = editor.Document.GetText(line.Offset, caret - line.Offset);
        if (!string.Equals(prefix.TrimStart(), "/", StringComparison.Ordinal)) return false;

        var relative = prefix.LastIndexOf('/');
        if (relative < 0) return false;
        slashOffset = line.Offset + relative;
        return true;
    }

    private void OpenSlashPalette(ManuscriptEditor editor, int slashOffset)
    {
        if (_slashPopup is null || _slashSearch is null || !_viewModel.HasDocument) return;
        if (slashOffset < 0 || slashOffset >= editor.Document.TextLength || editor.Document.GetCharAt(slashOffset) != '/')
            return;

        _suppressSlashDetection++;
        try
        {
            editor.Document.Remove(slashOffset, 1);
            editor.CaretOffset = Math.Min(slashOffset, editor.Document.TextLength);
        }
        finally
        {
            _suppressSlashDetection--;
        }

        if (_selectionPopup is not null) _selectionPopup.IsOpen = false;
        _slashSearch.Text = string.Empty;
        FilterQuickCommands();
        _slashPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            _slashSearch.Focus();
            _slashSearch.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void RefreshSelectionPopup()
    {
        if (_selectionPopup is null || _editor is null) return;
        var shouldOpen = _viewModel.HasDocument &&
                         _editor.SelectionLength > 0 &&
                         _slashPopup?.IsOpen != true;
        _selectionPopup.IsOpen = shouldOpen;
    }

    private void TryStyleToolbar()
    {
        if (_host is null || _toolbarStyled) return;
        var commandBar = _host.Children
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("editor-command-bar"));
        if (commandBar?.Child is not WrapPanel wrap) return;

        foreach (var control in commandBar.GetVisualDescendants().OfType<Control>())
        {
            if (control is not Button && control is not ToggleButton) continue;
            if (!control.Classes.Contains("rich-toolbar-control"))
                control.Classes.Add("rich-toolbar-control");

            var text = ContentText(control);
            var key = text switch
            {
                "B" => "bold",
                "I" => "italic",
                "`" => "code",
                "H1" => "h1",
                "H2" => "h2",
                "❝" => "quote",
                "•" => "list",
                _ => string.Empty
            };
            if (key.Length == 0) continue;

            if (!control.Classes.Contains("rich-format-button"))
                control.Classes.Add("rich-format-button");
            _toolbarControls[key] = control;
        }

        if (!wrap.Children.OfType<Border>().Any(border => border.Classes.Contains("rich-format-group")))
        {
            var groupRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2
            };
            groupRow.Children.Add(new TextBlock
            {
                Text = "RICH",
                FontSize = 8.5,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.48,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 4, 0)
            });
            groupRow.Children.Add(ToolbarFormatButton("H3", "Heading 3", "h3", () => ApplyHeading(3)));
            groupRow.Children.Add(ToolbarAsyncButton("Link", "Edit link (Ctrl+K)", "link", EditLinkAsync));
            groupRow.Children.Add(ToolbarFormatButton("+", "Quick insert palette", "insert", OpenQuickInsert));

            var group = new Border
            {
                Child = groupRow,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 3),
                Padding = new Thickness(4, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            group.Classes.Add("rich-format-group");
            wrap.Children.Add(group);
        }

        _toolbarStyled = true;
        RefreshFormattingState();
    }

    private Button ToolbarFormatButton(string label, string tip, string key, Action action)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = label.Length > 2 ? 42 : 30,
            Height = 25,
            MinHeight = 25,
            Padding = new Thickness(6, 1)
        };
        button.Classes.Add("rich-toolbar-control");
        button.Classes.Add("rich-format-button");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            action();
            _editor?.Focus();
            RefreshFormattingState();
        };
        _toolbarControls[key] = button;
        return button;
    }

    private Button ToolbarAsyncButton(string label, string tip, string key, Func<Task> action)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 42,
            Height = 25,
            MinHeight = 25,
            Padding = new Thickness(6, 1)
        };
        button.Classes.Add("rich-toolbar-control");
        button.Classes.Add("rich-format-button");
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) =>
        {
            await action();
            _editor?.Focus();
            RefreshFormattingState();
        };
        _toolbarControls[key] = button;
        return button;
    }

    private void OpenQuickInsert()
    {
        if (_slashPopup is null || _slashSearch is null || !_viewModel.HasDocument) return;
        if (_selectionPopup is not null) _selectionPopup.IsOpen = false;
        _slashSearch.Text = string.Empty;
        FilterQuickCommands();
        _slashPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _slashSearch.Focus(), DispatcherPriority.Input);
    }

    private static string ContentText(Control control)
        => control switch
        {
            ToggleButton { Content: TextBlock text } => text.Text ?? string.Empty,
            ToggleButton { Content: string text } => text,
            Button { Content: TextBlock text } => text.Text ?? string.Empty,
            Button { Content: string text } => text,
            _ => string.Empty
        };

    private void RefreshFormattingState()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var state = ReadFormattingState(_editor);

        SetFormatState("bold", state.Bold);
        SetFormatState("italic", state.Italic);
        SetFormatState("code", state.Code);
        SetFormatState("h1", state.HeadingLevel == 1);
        SetFormatState("h2", state.HeadingLevel == 2);
        SetFormatState("h3", state.HeadingLevel == 3);
        SetFormatState("quote", state.Quote);
        SetFormatState("list", state.List);
        SetFormatState("link", state.Link);
    }

    private void SetFormatState(string key, bool active)
    {
        if (_toolbarControls.TryGetValue(key, out var toolbar))
            SetClass(toolbar, "active-format", active);
        if (_selectionControls.TryGetValue(key, out var floating))
            SetClass(floating, "active-format", active);
    }

    private static void SetClass(Control control, string className, bool enabled)
    {
        var has = control.Classes.Contains(className);
        if (enabled && !has) control.Classes.Add(className);
        else if (!enabled && has) control.Classes.Remove(className);
    }

    private static FormattingState ReadFormattingState(ManuscriptEditor editor)
    {
        if (editor.Document.TextLength == 0) return default;

        var caret = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var lookup = Math.Min(caret, Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        var text = editor.Document.GetText(line);
        var relative = Math.Clamp(caret - line.Offset, 0, text.Length);
        var trimmed = text.TrimStart();

        var heading = HeadingLevel(trimmed);
        var quote = trimmed.StartsWith("> ", StringComparison.Ordinal);
        var list = ListPrefixRegex.IsMatch(text);
        var bold = IsInsideDelimited(text, relative, "**");
        var code = IsInsideDelimited(text, relative, "`");
        var italic = !bold && (IsInsideDelimited(text, relative, "*") || IsInsideDelimited(text, relative, "_"));
        var link = MarkdownLinkRegex.Matches(text).Cast<Match>()
            .Any(match => relative >= match.Index && relative <= match.Index + match.Length);

        return new FormattingState(bold, italic, code, heading, quote, list, link);
    }

    private static int HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
        return level > 0 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
    }

    private static bool IsInsideDelimited(string text, int position, string marker)
    {
        if (string.IsNullOrEmpty(text) || marker.Length == 0) return false;
        var beforeIndex = Math.Min(Math.Max(0, position - 1), Math.Max(0, text.Length - 1));
        var open = text.LastIndexOf(marker, beforeIndex, StringComparison.Ordinal);
        if (open < 0) return false;
        var contentStart = open + marker.Length;
        if (position < contentStart) return false;
        var close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
        return close >= 0 && position <= close;
    }

    private void WrapSelection(string before, string after)
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument) return;

        var start = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var length = Math.Max(0, editor.SelectionLength);
        var selected = length > 0 ? editor.Document.GetText(start, length) : string.Empty;
        var replacement = before + selected + after;
        editor.Document.Replace(start, length, replacement);

        if (length > 0)
        {
            editor.Select(start + before.Length, selected.Length);
            editor.CaretOffset = start + before.Length + selected.Length;
        }
        else
        {
            editor.CaretOffset = start + before.Length;
            editor.Select(editor.CaretOffset, 0);
        }
        editor.Focus();
    }

    private void ApplyHeading(int level)
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument || editor.Document.LineCount == 0) return;

        var lookup = Math.Min(editor.CaretOffset, Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        var text = editor.Document.GetText(line);
        var indentLength = 0;
        while (indentLength < text.Length && char.IsWhiteSpace(text[indentLength])) indentLength++;

        var markerLength = 0;
        while (indentLength + markerLength < text.Length &&
               markerLength < 6 &&
               text[indentLength + markerLength] == '#')
            markerLength++;

        var existingLength = markerLength > 0 &&
                             indentLength + markerLength < text.Length &&
                             text[indentLength + markerLength] == ' '
            ? markerLength + 1
            : 0;

        var marker = new string('#', Math.Clamp(level, 1, 6)) + " ";
        editor.Document.Replace(line.Offset + indentLength, existingLength, marker);
        editor.CaretOffset = Math.Min(editor.Document.TextLength, line.Offset + indentLength + marker.Length);
        editor.Focus();
    }

    private void PrefixSelectedLines(string prefix)
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument || editor.Document.LineCount == 0) return;

        var start = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var end = editor.SelectionLength > 0
            ? editor.SelectionStart + editor.SelectionLength
            : editor.CaretOffset;
        var maxOffset = Math.Max(0, editor.Document.TextLength - 1);
        var startLine = editor.Document.GetLineByOffset(Math.Min(start, maxOffset));
        var endLookup = end > start ? end - 1 : end;
        var endLine = editor.Document.GetLineByOffset(Math.Min(Math.Max(0, endLookup), maxOffset));

        for (var lineNumber = endLine.LineNumber; lineNumber >= startLine.LineNumber; lineNumber--)
        {
            var line = editor.Document.GetLineByNumber(lineNumber);
            var text = editor.Document.GetText(line);
            var indent = 0;
            while (indent < text.Length && char.IsWhiteSpace(text[indent])) indent++;
            editor.Document.Insert(line.Offset + indent, prefix);
        }

        editor.Focus();
    }

    private void InsertBlock(string block, string? selectText = null)
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument) return;

        var offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var selectionLength = Math.Max(0, editor.SelectionLength);
        var before = offset > 0 && editor.Document.GetCharAt(offset - 1) != '\n' ? "\n\n" : string.Empty;
        var afterOffset = offset + selectionLength;
        var after = afterOffset < editor.Document.TextLength && editor.Document.GetCharAt(afterOffset) != '\n'
            ? "\n\n"
            : "\n";
        var insertion = before + block + after;
        editor.Document.Replace(offset, selectionLength, insertion);

        var blockStart = offset + before.Length;
        if (!string.IsNullOrEmpty(selectText))
        {
            var index = block.IndexOf(selectText, StringComparison.Ordinal);
            if (index >= 0)
            {
                editor.Select(blockStart + index, selectText.Length);
                editor.CaretOffset = blockStart + index + selectText.Length;
                editor.Focus();
                return;
            }
        }

        editor.CaretOffset = blockStart + block.Length;
        editor.Focus();
    }

    private void InsertTextAtCaret(string text, string? selectText = null)
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument) return;

        var offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var length = Math.Max(0, editor.SelectionLength);
        editor.Document.Replace(offset, length, text);
        if (!string.IsNullOrEmpty(selectText))
        {
            var index = text.IndexOf(selectText, StringComparison.Ordinal);
            if (index >= 0)
            {
                editor.Select(offset + index, selectText.Length);
                editor.CaretOffset = offset + index + selectText.Length;
                editor.Focus();
                return;
            }
        }

        editor.CaretOffset = offset + text.Length;
        editor.Focus();
    }

    private async Task EditLinkAsync()
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument) return;

        var range = TryGetLinkAtCaret(editor, out var existing)
            ? existing
            : new LinkRange(
                editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset,
                Math.Max(0, editor.SelectionLength),
                editor.SelectionLength > 0
                    ? editor.Document.GetText(editor.SelectionStart, editor.SelectionLength)
                    : string.Empty,
                string.Empty);

        var labelBox = new TextBox
        {
            Text = range.Label,
            Watermark = "Link text",
            MinWidth = 380
        };
        var urlBox = new TextBox
        {
            Text = range.Url,
            Watermark = "https://example.com",
            MinWidth = 380
        };
        var ok = new Button { Content = "Apply link", MinWidth = 96 };
        var cancel = new Button { Content = "Cancel", MinWidth = 84 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, ok }
        };

        var content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Text", FontWeight = FontWeight.SemiBold, Opacity = 0.72 },
                labelBox,
                new TextBlock { Text = "URL", FontWeight = FontWeight.SemiBold, Opacity = 0.72 },
                urlBox,
                buttons
            }
        };

        var dialog = new Window
        {
            Title = "Edit Link",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            MinHeight = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };

        ok.Click += (_, _) =>
        {
            var url = urlBox.Text?.Trim() ?? string.Empty;
            if (url.Length == 0) return;
            var label = labelBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = url;
            dialog.Close(new LinkEditResult(label, url));
        };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) =>
        {
            labelBox.Focus();
            labelBox.SelectAll();
        };

        var result = await dialog.ShowDialog<LinkEditResult?>(_window);
        if (result is null) return;

        var markdown = $"[{result.Label}]({result.Url})";
        editor.Document.Replace(range.Offset, range.Length, markdown);
        editor.CaretOffset = range.Offset + markdown.Length;
        editor.Select(editor.CaretOffset, 0);
        editor.Focus();
    }

    private static bool TryGetLinkAtCaret(ManuscriptEditor editor, out LinkRange range)
    {
        range = default;
        if (editor.Document.TextLength == 0) return false;

        var caret = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var lookup = Math.Min(caret, Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        var text = editor.Document.GetText(line);
        var relative = Math.Clamp(caret - line.Offset, 0, text.Length);

        foreach (Match match in MarkdownLinkRegex.Matches(text))
        {
            if (relative < match.Index || relative > match.Index + match.Length) continue;
            range = new LinkRange(
                line.Offset + match.Index,
                match.Length,
                match.Groups["label"].Value,
                match.Groups["url"].Value);
            return true;
        }
        return false;
    }

    private void WindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is null || !_viewModel.HasDocument) return;

        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                      e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (primary && e.Key == Key.K &&
            (_editor.IsKeyboardFocusWithin || _selectionPopup?.IsOpen == true))
        {
            e.Handled = true;
            _ = EditLinkAsync();
            return;
        }

        if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.P &&
            _editor.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            OpenQuickInsert();
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _viewModel.StateChanged -= ViewModelStateChanged;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
        _window.RemoveHandler(InputElement.KeyDownEvent, WindowPreviewKeyDown);

        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextArea.SelectionChanged -= SelectionChanged;
            _editor.GotFocus -= EditorGotFocus;
            if (_richColorizer is not null)
                _editor.TextArea.TextView.LineTransformers.Remove(_richColorizer);
        }

        if (_host is not null)
        {
            if (_selectionPopup is not null) _host.Children.Remove(_selectionPopup);
            if (_slashPopup is not null) _host.Children.Remove(_slashPopup);
        }
    }

    private readonly record struct FormattingState(
        bool Bold,
        bool Italic,
        bool Code,
        int HeadingLevel,
        bool Quote,
        bool List,
        bool Link);

    private readonly record struct LinkRange(int Offset, int Length, string Label, string Url);
    private sealed record LinkEditResult(string Label, string Url);
    private sealed record QuickInsertCommand(string Title, string Description, Func<Task> Action);

    private sealed class RichBlockColorizer(ManuscriptEditor owner) : DocumentColorizingTransformer
    {
        private static readonly IBrush Heading1Brush = new SolidColorBrush(Color.Parse("#7D89FF"));
        private static readonly IBrush Heading2Brush = new SolidColorBrush(Color.Parse("#8E9AE8"));
        private static readonly IBrush Heading3Brush = new SolidColorBrush(Color.Parse("#A7B0D6"));
        private static readonly IBrush Heading1Background = new SolidColorBrush(Color.FromArgb(18, 109, 139, 255));
        private static readonly IBrush Heading2Background = new SolidColorBrush(Color.FromArgb(11, 109, 139, 255));
        private static readonly IBrush QuoteBrush = new SolidColorBrush(Color.Parse("#78A88A"));
        private static readonly IBrush QuoteBackground = new SolidColorBrush(Color.FromArgb(14, 120, 168, 138));
        private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#D5905D"));
        private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromArgb(24, 213, 144, 93));
        private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#6D8BFF"));
        private static readonly IBrush ListBrush = new SolidColorBrush(Color.Parse("#91A0FF"));
        private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromArgb(88, 125, 135, 150));

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;

            var text = CurrentContext.Document.GetText(line);
            var start = line.Offset;
            var trimmed = text.TrimStart();
            var indent = text.Length - trimmed.Length;
            var headingLevel = HeadingLevel(trimmed);

            if (headingLevel is >= 1 and <= 3)
            {
                var brush = headingLevel switch
                {
                    1 => Heading1Brush,
                    2 => Heading2Brush,
                    _ => Heading3Brush
                };
                var background = headingLevel switch
                {
                    1 => Heading1Background,
                    2 => Heading2Background,
                    _ => null
                };

                ChangeLinePart(start, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    element.TextRunProperties.SetTypeface(new Typeface(
                        owner.FontFamily,
                        FontStyle.Normal,
                        FontWeight.SemiBold));
                    if (background is not null)
                        element.TextRunProperties.SetBackgroundBrush(background);
                });

                var markerEnd = Math.Min(line.EndOffset, start + indent + headingLevel + 1);
                ChangeLinePart(start + indent, markerEnd, element =>
                    element.TextRunProperties.SetForegroundBrush(MarkerBrush));
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                ChangeLinePart(start, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(QuoteBrush);
                    element.TextRunProperties.SetBackgroundBrush(QuoteBackground);
                    element.TextRunProperties.SetTypeface(new Typeface(
                        owner.FontFamily,
                        FontStyle.Italic,
                        FontWeight.Normal));
                });
                ChangeLinePart(start + indent, Math.Min(line.EndOffset, start + indent + 2), element =>
                    element.TextRunProperties.SetForegroundBrush(MarkerBrush));
            }
            else
            {
                var list = ListPrefixRegex.Match(text);
                if (list.Success)
                {
                    ChangeLinePart(start + list.Index, start + list.Index + list.Length, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(ListBrush);
                        element.TextRunProperties.SetTypeface(new Typeface(
                            owner.FontFamily,
                            FontStyle.Normal,
                            FontWeight.SemiBold));
                    });
                }
            }

            ApplyInlineCode(start, text);
            ApplyLinks(start, text);
        }

        private void ApplyInlineCode(int lineStart, string text)
        {
            var search = 0;
            while (search < text.Length)
            {
                var open = text.IndexOf('`', search);
                if (open < 0) break;
                var close = text.IndexOf('`', open + 1);
                if (close < 0) break;

                if (close > open + 1)
                {
                    ChangeLinePart(lineStart + open + 1, lineStart + close, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(CodeBrush);
                        element.TextRunProperties.SetBackgroundBrush(CodeBackground);
                    });
                }
                search = close + 1;
            }
        }

        private void ApplyLinks(int lineStart, string text)
        {
            foreach (Match match in MarkdownLinkRegex.Matches(text))
            {
                var label = match.Groups["label"];
                if (!label.Success || label.Length == 0) continue;

                ChangeLinePart(lineStart + label.Index, lineStart + label.Index + label.Length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(LinkBrush);
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                });
            }
        }
    }
}
