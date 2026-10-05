using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Typescribe.Domain.Models;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Rich visual editing layer for the manuscript editor. Formatting commands are delegated to
/// <see cref="RichFormattingEngine"/> so the persistent toolbar, floating toolbar, keyboard
/// shortcuts and slash palette all share the same reversible Markdown behavior.
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
    private readonly List<QuickRow> _quickRows = [];

    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Popup? _selectionPopup;
    private ComboBox? _headingPicker;
    private Popup? _slashPopup;
    private TextBox? _slashSearch;
    private ListBox? _slashList;
    private RichBlockColorizer? _richColorizer;
    private RichBlockBackgroundRenderer? _blockRenderer;
    private bool _installed;
    private bool _toolbarStyled;
    private bool _slashQueued;
    private bool _syncingHeadingPicker;
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
            RepositionSlashPopup();
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
        _blockRenderer = new RichBlockBackgroundRenderer(editor);
        editor.TextArea.TextView.LineTransformers.Add(_richColorizer);
        editor.TextArea.TextView.BackgroundRenderers.Add(_blockRenderer);

        BuildSelectionPopup();
        BuildSlashPopup();
        BuildQuickCommands();

        editor.TextChanged += EditorTextChanged;
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextArea.SelectionChanged += SelectionChanged;
        editor.TextArea.TextView.ScrollOffsetChanged += TextViewScrollOffsetChanged;
        editor.TextArea.TextView.PointerMoved += TextViewPointerMoved;
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

        row.Children.Add(FloatingFormatButton("B", "Toggle bold", "bold", () => RichFormattingEngine.ToggleBold(_editor), FontWeight.Bold));
        row.Children.Add(FloatingFormatButton("I", "Toggle italic", "italic", () => RichFormattingEngine.ToggleItalic(_editor), FontWeight.Normal, FontStyle.Italic));
        row.Children.Add(FloatingFormatButton("`", "Toggle inline code", "code", () => RichFormattingEngine.ToggleCode(_editor)));

        _headingPicker = new ComboBox
        {
            ItemsSource = new[] { "Text", "H1", "H2", "H3" },
            SelectedIndex = 0,
            MinWidth = 68,
            Height = 28,
            MinHeight = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _headingPicker.Classes.Add("rich-heading-picker");
        ToolTip.SetTip(_headingPicker, "Paragraph style");
        _headingPicker.SelectionChanged += HeadingPickerSelectionChanged;
        row.Children.Add(_headingPicker);

        row.Children.Add(FloatingFormatButton("•", "Toggle bullet list", "bullet", () => RichFormattingEngine.ToggleBulletList(_editor)));
        row.Children.Add(FloatingFormatButton("1.", "Toggle numbered list", "numbered", () => RichFormattingEngine.ToggleNumberedList(_editor)));
        row.Children.Add(FloatingFormatButton("❝", "Toggle block quote", "quote", () => RichFormattingEngine.ToggleQuote(_editor)));
        row.Children.Add(FloatingFormatButton("RTL", "Toggle right-to-left paragraph direction", "rtl", () => RichFormattingEngine.ToggleRightToLeft(_editor)));
        row.Children.Add(FloatingFormatButton("L", "Align paragraph left", "align-left", () => RichFormattingEngine.SetAlignment(_editor, TextAlignmentMode.Left)));
        row.Children.Add(FloatingFormatButton("C", "Align paragraph center", "align-center", () => RichFormattingEngine.SetAlignment(_editor, TextAlignmentMode.Center)));
        row.Children.Add(FloatingFormatButton("R", "Align paragraph right", "align-right", () => RichFormattingEngine.SetAlignment(_editor, TextAlignmentMode.Right)));
        row.Children.Add(FloatingAsyncButton("Link", "Insert or edit link (Ctrl+K)", "link", EditLinkAsync));
        row.Children.Add(FloatingFormatButton("Unlink", "Remove link but keep its text", "unlink", () => RichFormattingEngine.Unlink(_editor)));
        row.Children.Add(FloatingFormatButton("Clear", "Clear formatting", "clear", () => RichFormattingEngine.ClearFormatting(_editor)));

        var surface = new Border
        {
            Child = row,
            Padding = new Thickness(5, 4),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1)
        };
        surface.Classes.Add("rich-selection-toolbar");

        _selectionPopup = new Popup
        {
            Child = surface,
            PlacementTarget = _editor.TextArea.TextView,
            Placement = PlacementMode.AnchorAndGravity,
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
        FontWeight? weight = null,
        FontStyle? style = null)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontWeight = weight ?? FontWeight.Normal,
                FontStyle = style ?? FontStyle.Normal
            },
            MinWidth = label.Length > 3 ? 44 : 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(7, 2)
        };
        button.Classes.Add("rich-floating-format-button");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            action();
            _editor?.Focus();
            RefreshFormattingState();
            RefreshSelectionPopup();
        };
        _selectionControls[key] = button;
        return button;
    }

    private Button FloatingAsyncButton(string label, string tip, string key, Func<Task> action)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 44,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(7, 2)
        };
        button.Classes.Add("rich-floating-format-button");
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) =>
        {
            await action();
            _editor?.Focus();
            RefreshFormattingState();
            RefreshSelectionPopup();
        };
        _selectionControls[key] = button;
        return button;
    }

    private void HeadingPickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingHeadingPicker || _editor is null || _headingPicker is null) return;
        var state = RichFormattingEngine.ReadState(_editor);
        var requested = _headingPicker.SelectedIndex;
        if (requested <= 0)
        {
            if (state.HeadingLevel > 0)
                RichFormattingEngine.ToggleHeading(_editor, state.HeadingLevel);
        }
        else
        {
            RichFormattingEngine.ToggleHeading(_editor, requested);
        }
        _editor.Focus();
        RefreshFormattingState();
        RefreshSelectionPopup();
    }

    private void BuildSlashPopup()
    {
        if (_editor is null || _host is null) return;

        _slashSearch = new TextBox
        {
            Watermark = "Type a command…",
            MinWidth = 350,
            Margin = new Thickness(8, 8, 8, 5)
        };
        _slashList = new ListBox
        {
            MinWidth = 410,
            MinHeight = 220,
            MaxHeight = 330,
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
            MinWidth = 430
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
            PlacementTarget = _editor.TextArea.TextView,
            Placement = PlacementMode.AnchorAndGravity,
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
        _quickCommands.Add(new("Text", "Heading 1", "Large section heading", () => RunSync(() => RichFormattingEngine.ToggleHeading(_editor!, 1))));
        _quickCommands.Add(new("Text", "Heading 2", "Medium section heading", () => RunSync(() => RichFormattingEngine.ToggleHeading(_editor!, 2))));
        _quickCommands.Add(new("Text", "Heading 3", "Small section heading", () => RunSync(() => RichFormattingEngine.ToggleHeading(_editor!, 3))));
        _quickCommands.Add(new("Text", "Quote", "Start or remove a block quote", () => RunSync(() => RichFormattingEngine.ToggleQuote(_editor!))));
        _quickCommands.Add(new("Lists", "Bullet list", "Start or remove an unordered list", () => RunSync(() => RichFormattingEngine.ToggleBulletList(_editor!))));
        _quickCommands.Add(new("Lists", "Numbered list", "Start or remove an ordered list", () => RunSync(() => RichFormattingEngine.ToggleNumberedList(_editor!))));
        _quickCommands.Add(new("Text", "Link", "Insert or edit a Markdown link", EditLinkAsync));
        _quickCommands.Add(new("Blocks", "Code block", "Insert a fenced code block", () => RunSync(() => InsertBlock("```text\ncode\n```", "code"))));
        _quickCommands.Add(new("Academic", "Equation", "Insert a display equation", () => RunSync(() => InsertBlock("$$\nE = mc^2\n$$", "E = mc^2"))));
        _quickCommands.Add(new("Blocks", "Table", "Insert a 3-column editable table", () => RunSync(() => InsertBlock(
            "| Heading 1 | Heading 2 | Heading 3 |\n| --- | --- | --- |\n| Cell | Cell | Cell |",
            "Heading 1"))));
        _quickCommands.Add(new("Media", "Figure", "Insert an image/figure placeholder", () => RunSync(() => InsertBlock("![Caption](image-path)", "Caption"))));
        _quickCommands.Add(new("Academic", "Citation", "Insert a citation placeholder", () => RunSync(() => InsertTextAtCaret("[@citation-key]", "citation-key"))));
        _quickCommands.Add(new("Blocks", "Horizontal rule", "Insert a visual section divider", () => RunSync(() => InsertBlock("---"))));
        _quickCommands.Add(new("Media", "Mermaid diagram", "Insert a Mermaid fenced block", () => RunSync(() => InsertBlock(
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
        var matches = _quickCommands.Where(command =>
            query.Length == 0 ||
            command.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            command.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            command.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        _quickRows.Clear();
        foreach (var category in new[] { "Text", "Lists", "Media", "Academic", "Blocks" })
        {
            var group = matches.Where(command => command.Category == category).ToArray();
            if (group.Length == 0) continue;
            _quickRows.Add(new QuickRow($"── {category.ToUpperInvariant()} ──", null));
            _quickRows.AddRange(group.Select(command => new QuickRow($"{command.Title}   —   {command.Description}", command)));
        }

        _slashList.ItemsSource = _quickRows.Select(static row => row.Text).ToArray();
        _slashList.SelectedIndex = FirstCommandRow();
    }

    private int FirstCommandRow()
    {
        for (var index = 0; index < _quickRows.Count; index++)
            if (_quickRows[index].Command is not null) return index;
        return -1;
    }

    private void MoveSlashSelection(int delta)
    {
        if (_slashList is null || _quickRows.Count == 0) return;
        var index = _slashList.SelectedIndex;
        if (index < 0) index = delta > 0 ? -1 : _quickRows.Count;
        for (var attempts = 0; attempts < _quickRows.Count; attempts++)
        {
            index = Math.Clamp(index + delta, 0, _quickRows.Count - 1);
            if (_quickRows[index].Command is not null)
            {
                _slashList.SelectedIndex = index;
                _slashList.ScrollIntoView(index);
                return;
            }
            if ((delta > 0 && index == _quickRows.Count - 1) || (delta < 0 && index == 0)) return;
        }
    }

    private async void SlashSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_slashList is null || _slashPopup is null) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _slashPopup.IsOpen = false;
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveSlashSelection(1);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveSlashSelection(-1);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSelectedQuickCommandAsync();
        }
    }

    private async Task RunSelectedQuickCommandAsync()
    {
        if (_slashList is null || _slashPopup is null) return;
        var index = _slashList.SelectedIndex;
        if (index < 0 || index >= _quickRows.Count || _quickRows[index].Command is not { } command) return;
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
        _editor?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

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
        RepositionSlashPopup();
        _editor?.TextArea.TextView.Redraw();
    }

    private void SelectionChanged(object? sender, EventArgs e)
    {
        RefreshFormattingState();
        RefreshSelectionPopup();
    }

    private void TextViewScrollOffsetChanged(object? sender, EventArgs e)
    {
        RepositionSelectionPopup();
        RepositionSlashPopup();
    }

    private void TextViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_editor is null) return;
        var textView = _editor.TextArea.TextView;
        try
        {
            var point = e.GetPosition(textView) + textView.ScrollOffset;
            var position = textView.GetPosition(point);
            if (position is null)
            {
                ToolTip.SetTip(_editor, null);
                return;
            }
            var offset = _editor.Document.GetOffset(position.Value.Line, position.Value.Column);
            ToolTip.SetTip(_editor, TryGetLinkAtOffset(_editor, offset, out var range) ? range.Url : null);
        }
        catch (ArgumentOutOfRangeException)
        {
            ToolTip.SetTip(_editor, null);
        }
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
        if (slashOffset < 0 || slashOffset >= editor.Document.TextLength || editor.Document.GetCharAt(slashOffset) != '/') return;

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
        PositionSlashPopup();
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
        var shouldOpen = _viewModel.HasDocument && _editor.SelectionLength > 0 && _slashPopup?.IsOpen != true;
        _selectionPopup.IsOpen = shouldOpen;
        if (shouldOpen) RepositionSelectionPopup();
    }

    private void RepositionSelectionPopup()
    {
        if (_selectionPopup is null || _editor is null || !_selectionPopup.IsOpen || _editor.SelectionLength <= 0 || _editor.Document.TextLength == 0)
            return;

        var textView = _editor.TextArea.TextView;
        try
        {
            var position = _editor.TextArea.Caret.Position;
            var top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - textView.ScrollOffset;
            var viewportWidth = Math.Max(1, textView.Bounds.Width);
            var toolbarHalfWidth = Math.Min(260, viewportWidth / 2);
            var x = Math.Clamp(top.X, toolbarHalfWidth, Math.Max(toolbarHalfWidth, viewportWidth - toolbarHalfWidth));
            var y = Math.Clamp(top.Y, 0, Math.Max(0, textView.Bounds.Height));
            var height = Math.Max(1, bottom.Y - top.Y);

            _selectionPopup.PlacementTarget = textView;
            _selectionPopup.Placement = PlacementMode.AnchorAndGravity;
            _selectionPopup.PlacementRect = new Rect(x - 1, y, 2, height);
            _selectionPopup.HorizontalOffset = 0;
            if (y >= 46)
            {
                _selectionPopup.PlacementAnchor = PopupAnchor.Top;
                _selectionPopup.PlacementGravity = PopupGravity.Top;
                _selectionPopup.VerticalOffset = -8;
            }
            else
            {
                _selectionPopup.PlacementAnchor = PopupAnchor.Bottom;
                _selectionPopup.PlacementGravity = PopupGravity.Bottom;
                _selectionPopup.VerticalOffset = 8;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PositionSlashPopup()
    {
        if (_slashPopup is null || _editor is null || _editor.Document.TextLength == 0) return;
        var textView = _editor.TextArea.TextView;
        try
        {
            var position = _editor.TextArea.Caret.Position;
            var top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - textView.ScrollOffset;
            var viewportWidth = Math.Max(1, textView.Bounds.Width);
            var halfWidth = Math.Min(215, viewportWidth / 2);
            var x = Math.Clamp(bottom.X, halfWidth, Math.Max(halfWidth, viewportWidth - halfWidth));
            var y = Math.Clamp(top.Y, 0, Math.Max(0, textView.Bounds.Height));
            var height = Math.Max(1, bottom.Y - top.Y);

            _slashPopup.PlacementTarget = textView;
            _slashPopup.Placement = PlacementMode.AnchorAndGravity;
            _slashPopup.PlacementRect = new Rect(x - 1, y, 2, height);
            _slashPopup.HorizontalOffset = 0;
            var roomBelow = textView.Bounds.Height - bottom.Y;
            if (roomBelow >= 260)
            {
                _slashPopup.PlacementAnchor = PopupAnchor.Bottom;
                _slashPopup.PlacementGravity = PopupGravity.Bottom;
                _slashPopup.VerticalOffset = 8;
            }
            else
            {
                _slashPopup.PlacementAnchor = PopupAnchor.Top;
                _slashPopup.PlacementGravity = PopupGravity.Top;
                _slashPopup.VerticalOffset = -8;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RepositionSlashPopup()
    {
        if (_slashPopup?.IsOpen == true) PositionSlashPopup();
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
            if (!control.Classes.Contains("rich-toolbar-control")) control.Classes.Add("rich-toolbar-control");
            var text = ContentText(control);
            var key = text switch
            {
                "B" => "bold",
                "I" => "italic",
                "`" => "code",
                "H1" => "h1",
                "H2" => "h2",
                "❯" or "❝" => "quote",
                "•" => "bullet",
                "1." => "numbered",
                _ => string.Empty
            };
            if (key.Length == 0) continue;
            if (!control.Classes.Contains("rich-format-button")) control.Classes.Add("rich-format-button");
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
            groupRow.Children.Add(ToolbarFormatButton("H3", "Toggle Heading 3", "h3", () => RichFormattingEngine.ToggleHeading(_editor!, 3)));
            groupRow.Children.Add(ToolbarFormatButton("RTL", "Toggle right-to-left paragraph direction", "rtl", () => RichFormattingEngine.ToggleRightToLeft(_editor!)));
            groupRow.Children.Add(ToolbarFormatButton("L", "Align paragraph left", "align-left", () => RichFormattingEngine.SetAlignment(_editor!, TextAlignmentMode.Left)));
            groupRow.Children.Add(ToolbarFormatButton("C", "Align paragraph center", "align-center", () => RichFormattingEngine.SetAlignment(_editor!, TextAlignmentMode.Center)));
            groupRow.Children.Add(ToolbarFormatButton("R", "Align paragraph right", "align-right", () => RichFormattingEngine.SetAlignment(_editor!, TextAlignmentMode.Right)));
            groupRow.Children.Add(ToolbarAsyncButton("Link", "Edit link (Ctrl+K)", "link", EditLinkAsync));
            groupRow.Children.Add(ToolbarFormatButton("Unlink", "Remove current link", "unlink", () => RichFormattingEngine.Unlink(_editor!)));
            groupRow.Children.Add(ToolbarFormatButton("Clear", "Clear formatting", "clear", () => RichFormattingEngine.ClearFormatting(_editor!)));
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
            MinWidth = label.Length > 3 ? 44 : 30,
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
        PositionSlashPopup();
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
        var state = RichFormattingEngine.ReadState(_editor);
        SetFormatState("bold", state.Bold);
        SetFormatState("italic", state.Italic);
        SetFormatState("code", state.Code);
        SetFormatState("h1", state.HeadingLevel == 1);
        SetFormatState("h2", state.HeadingLevel == 2);
        SetFormatState("h3", state.HeadingLevel == 3);
        SetFormatState("quote", state.Quote);
        SetFormatState("bullet", state.BulletList);
        SetFormatState("numbered", state.NumberedList);
        SetFormatState("link", state.Link);
        SetFormatState("unlink", state.Link);
        SetFormatState("rtl", state.Direction == TextDirectionMode.RightToLeft);
        SetFormatState("align-left", state.Alignment == TextAlignmentMode.Left);
        SetFormatState("align-center", state.Alignment == TextAlignmentMode.Center);
        SetFormatState("align-right", state.Alignment == TextAlignmentMode.Right);

        if (_selectionControls.TryGetValue("unlink", out var unlink)) unlink.IsEnabled = state.Link;
        if (_toolbarControls.TryGetValue("unlink", out var toolbarUnlink)) toolbarUnlink.IsEnabled = state.Link;

        if (_headingPicker is not null)
        {
            _syncingHeadingPicker = true;
            try { _headingPicker.SelectedIndex = state.HeadingLevel is >= 1 and <= 3 ? state.HeadingLevel : 0; }
            finally { _syncingHeadingPicker = false; }
        }
    }

    private void SetFormatState(string key, bool active)
    {
        if (_toolbarControls.TryGetValue(key, out var toolbar)) SetClass(toolbar, "active-format", active);
        if (_selectionControls.TryGetValue(key, out var floating)) SetClass(floating, "active-format", active);
    }

    private static void SetClass(Control control, string className, bool enabled)
    {
        var has = control.Classes.Contains(className);
        if (enabled && !has) control.Classes.Add(className);
        else if (!enabled && has) control.Classes.Remove(className);
    }

    private void InsertBlock(string block, string? selectText = null)
    {
        var editor = _editor;
        if (editor is null || !_viewModel.HasDocument) return;
        var offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var selectionLength = Math.Max(0, editor.SelectionLength);
        var before = offset > 0 && editor.Document.GetCharAt(offset - 1) != '\n' ? "\n\n" : string.Empty;
        var afterOffset = offset + selectionLength;
        var after = afterOffset < editor.Document.TextLength && editor.Document.GetCharAt(afterOffset) != '\n' ? "\n\n" : "\n";
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

        var range = RichFormattingEngine.TryGetLinkRange(editor, out var existing)
            ? existing
            : new RichFormattingEngine.LinkRange(
                editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset,
                Math.Max(0, editor.SelectionLength),
                editor.SelectionLength > 0 ? editor.Document.GetText(editor.SelectionStart, editor.SelectionLength) : string.Empty,
                string.Empty);

        var labelBox = new TextBox { Text = range.Label, Watermark = "Link text", MinWidth = 380 };
        var urlBox = new TextBox { Text = range.Url, Watermark = "https://example.com", MinWidth = 380 };
        var apply = new Button { Content = "Apply link", MinWidth = 96 };
        var unlink = new Button { Content = "Unlink", MinWidth = 84, IsEnabled = range.Length > 0 && range.Url.Length > 0 };
        var cancel = new Button { Content = "Cancel", MinWidth = 84 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { unlink, cancel, apply }
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

        apply.Click += (_, _) =>
        {
            var url = urlBox.Text?.Trim() ?? string.Empty;
            if (url.Length == 0) return;
            var label = labelBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = url;
            dialog.Close(new LinkEditResult(label, url, false));
        };
        unlink.Click += (_, _) => dialog.Close(new LinkEditResult(range.Label, range.Url, true));
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) => { labelBox.Focus(); labelBox.SelectAll(); };

        var result = await dialog.ShowDialog<LinkEditResult?>(_window);
        if (result is null) return;
        if (result.Unlink)
        {
            editor.Document.Replace(range.Offset, range.Length, range.Label);
            editor.Select(range.Offset, range.Label.Length);
            editor.CaretOffset = range.Offset + range.Label.Length;
            editor.Focus();
            return;
        }

        var markdown = $"[{result.Label}]({result.Url})";
        editor.Document.Replace(range.Offset, range.Length, markdown);
        editor.CaretOffset = range.Offset + markdown.Length;
        editor.Select(editor.CaretOffset, 0);
        editor.Focus();
    }

    private static bool TryGetLinkAtOffset(ManuscriptEditor editor, int offset, out RichFormattingEngine.LinkRange range)
    {
        range = default;
        if (editor.Document.TextLength == 0) return false;
        var lookup = Math.Min(Math.Clamp(offset, 0, editor.Document.TextLength), Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        var text = editor.Document.GetText(line);
        var relative = Math.Clamp(offset - line.Offset, 0, text.Length);
        foreach (Match match in MarkdownLinkRegex.Matches(text))
        {
            if (relative < match.Index || relative > match.Index + match.Length) continue;
            range = new RichFormattingEngine.LinkRange(
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
        if (EditorInputRouting.IsFromNativeInlineEditor(e, _editor)) return;

        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (primary && e.Key == Key.K && (_editor.IsKeyboardFocusWithin || _selectionPopup?.IsOpen == true))
        {
            e.Handled = true;
            _ = EditLinkAsync();
            return;
        }
        if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.P && _editor.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            OpenQuickInsert();
            return;
        }
        if (_editor.IsKeyboardFocusWithin && RichFormattingEngine.TryHandleEditingKey(_editor, e))
        {
            e.Handled = true;
            RefreshFormattingState();
            RefreshSelectionPopup();
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
            _editor.TextArea.TextView.ScrollOffsetChanged -= TextViewScrollOffsetChanged;
            _editor.TextArea.TextView.PointerMoved -= TextViewPointerMoved;
            _editor.GotFocus -= EditorGotFocus;
            if (_richColorizer is not null) _editor.TextArea.TextView.LineTransformers.Remove(_richColorizer);
            if (_blockRenderer is not null) _editor.TextArea.TextView.BackgroundRenderers.Remove(_blockRenderer);
        }

        if (_host is not null)
        {
            if (_selectionPopup is not null) _host.Children.Remove(_selectionPopup);
            if (_slashPopup is not null) _host.Children.Remove(_slashPopup);
        }
    }

    private static int HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
        return level > 0 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
    }

    private static FenceState FenceAtLine(TextDocument document, int lineNumber)
    {
        var inFence = false;
        var language = string.Empty;
        for (var number = 1; number <= Math.Min(lineNumber, document.LineCount); number++)
        {
            var line = document.GetLineByNumber(number);
            var text = document.GetText(line).TrimStart();
            if (!text.StartsWith("```", StringComparison.Ordinal)) continue;
            if (!inFence)
            {
                inFence = true;
                language = text.Length > 3 ? text[3..].Trim() : string.Empty;
                if (number == lineNumber) return new FenceState(true, true, language);
            }
            else
            {
                if (number == lineNumber) return new FenceState(true, true, language);
                inFence = false;
                language = string.Empty;
            }
        }
        return new FenceState(inFence, false, language);
    }

    private readonly record struct FenceState(bool InFence, bool IsFenceLine, string Language);
    private sealed record LinkEditResult(string Label, string Url, bool Unlink);
    private sealed record QuickInsertCommand(string Category, string Title, string Description, Func<Task> Action);
    private sealed record QuickRow(string Text, QuickInsertCommand? Command);

    private sealed class RichBlockColorizer(ManuscriptEditor owner) : DocumentColorizingTransformer
    {
        private static readonly IBrush Heading1Brush = new SolidColorBrush(Color.Parse("#6D7DFF"));
        private static readonly IBrush Heading2Brush = new SolidColorBrush(Color.Parse("#8290F0"));
        private static readonly IBrush Heading3Brush = new SolidColorBrush(Color.Parse("#9AA5DD"));
        private static readonly IBrush QuoteBrush = new SolidColorBrush(Color.Parse("#78A88A"));
        private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#D5905D"));
        private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#6D8BFF"));
        private static readonly IBrush ListBrush = new SolidColorBrush(Color.Parse("#91A0FF"));
        private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromArgb(96, 125, 135, 150));
        private static readonly IBrush HiddenMarkerBrush = new SolidColorBrush(Color.FromArgb(0, 125, 135, 150));
        private static readonly FontFamily CodeFont = new("monospace");

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            var text = CurrentContext.Document.GetText(line);
            var start = line.Offset;
            var trimmed = text.TrimStart();
            var indent = text.Length - trimmed.Length;
            var fence = FenceAtLine(CurrentContext.Document, line.LineNumber);

            if (fence.InFence)
            {
                ChangeLinePart(start, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(CodeBrush);
                    element.TextRunProperties.SetTypeface(new Typeface(CodeFont, FontStyle.Normal, FontWeight.Normal));
                    element.TextRunProperties.SetFontRenderingEmSize(Math.Max(11, owner.FontSize * 0.94));
                });
                if (fence.IsFenceLine)
                {
                    var markerEnd = Math.Min(line.EndOffset, start + indent + 3);
                    ChangeLinePart(start + indent, markerEnd, element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
                }
                return;
            }

            var heading = HeadingLevel(trimmed);
            if (heading is >= 1 and <= 3)
            {
                var brush = heading switch { 1 => Heading1Brush, 2 => Heading2Brush, _ => Heading3Brush };
                var scale = heading switch { 1 => 1.55, 2 => 1.34, _ => 1.18 };
                var weight = heading == 1 ? FontWeight.Bold : FontWeight.SemiBold;
                ChangeLinePart(start, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    element.TextRunProperties.SetTypeface(new Typeface(owner.FontFamily, FontStyle.Normal, weight));
                    element.TextRunProperties.SetFontRenderingEmSize(owner.FontSize * scale);
                });
                var markerEnd = Math.Min(line.EndOffset, start + indent + heading + 1);
                ChangeLinePart(start + indent, markerEnd, element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                ChangeLinePart(start, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(QuoteBrush);
                    element.TextRunProperties.SetTypeface(new Typeface(owner.FontFamily, FontStyle.Italic, FontWeight.Normal));
                });
                ChangeLinePart(start + indent, Math.Min(line.EndOffset, start + indent + 2), element => element.TextRunProperties.SetForegroundBrush(MarkerBrush));
            }
            else if (trimmed == "---")
            {
                ChangeLinePart(start, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(HiddenMarkerBrush));
            }
            else
            {
                var list = ListPrefixRegex.Match(text);
                if (list.Success)
                {
                    ChangeLinePart(start + list.Index, start + list.Index + list.Length, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(ListBrush);
                        element.TextRunProperties.SetTypeface(new Typeface(owner.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                    });
                }
            }

            ApplyInlineCode(start, text);
            ApplyLinks(line, text);
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
                        element.TextRunProperties.SetTypeface(new Typeface(CodeFont, FontStyle.Normal, FontWeight.Normal));
                    });
                }
                search = close + 1;
            }
        }

        private void ApplyLinks(DocumentLine line, string text)
        {
            var caretLine = owner.TextArea.Caret.Line == line.LineNumber;
            var caretRelative = caretLine ? owner.CaretOffset - line.Offset : -1;
            foreach (Match match in MarkdownLinkRegex.Matches(text))
            {
                var label = match.Groups["label"];
                if (!label.Success || label.Length == 0) continue;
                var caretInside = caretRelative >= match.Index && caretRelative <= match.Index + match.Length;
                ChangeLinePart(line.Offset + label.Index, line.Offset + label.Index + label.Length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(LinkBrush);
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                });

                var marker = owner.ShowMarkdownMarks || caretInside ? MarkerBrush : HiddenMarkerBrush;
                ChangeLinePart(line.Offset + match.Index, line.Offset + label.Index, element => element.TextRunProperties.SetForegroundBrush(marker));
                var suffixStart = line.Offset + label.Index + label.Length;
                ChangeLinePart(suffixStart, line.Offset + match.Index + match.Length, element => element.TextRunProperties.SetForegroundBrush(marker));
            }
        }
    }

    private sealed class RichBlockBackgroundRenderer(ManuscriptEditor owner) : IBackgroundRenderer
    {
        private static readonly IBrush QuoteBackground = new SolidColorBrush(Color.FromArgb(13, 120, 168, 138));
        private static readonly IBrush QuoteRail = new SolidColorBrush(Color.Parse("#78A88A"));
        private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromArgb(20, 130, 115, 95));
        private static readonly Pen DividerPen = new(new SolidColorBrush(Color.FromArgb(90, 125, 135, 150)), 1.2);

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (textView.VisualLines.Count == 0) return;
            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine; line is not null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber; line = line.NextLine)
                {
                    var text = textView.Document.GetText(line);
                    var trimmed = text.TrimStart();
                    if (line.Length <= 0) continue;
                    var rects = BackgroundGeometryBuilder.GetRectsForSegment(textView, new SimpleSegment(line.Offset, line.Length));
                    foreach (var rect in rects)
                    {
                        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                        {
                            drawingContext.FillRectangle(QuoteBackground, new Rect(Math.Max(0, rect.Left - 10), rect.Top, Math.Max(1, textView.Bounds.Width - rect.Left), rect.Height), 3);
                            drawingContext.FillRectangle(QuoteRail, new Rect(Math.Max(0, rect.Left - 9), rect.Top, 3, rect.Height), 1.5);
                        }
                        else if (trimmed == "---")
                        {
                            var y = rect.Top + rect.Height / 2;
                            drawingContext.DrawLine(DividerPen, new Point(rect.Left, y), new Point(Math.Max(rect.Left + 80, textView.Bounds.Width - 22), y));
                        }
                        else if (FenceAtLine(textView.Document, line.LineNumber).InFence)
                        {
                            drawingContext.FillRectangle(CodeBackground, new Rect(Math.Max(0, rect.Left - 8), rect.Top, Math.Max(1, textView.Bounds.Width - rect.Left - 8), rect.Height), 4);
                        }
                    }
                    if (line == visualLine.LastDocumentLine) break;
                }
            }
        }
    }
}
