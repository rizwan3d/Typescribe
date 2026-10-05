using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Corrects the rich visual editor's interaction details without changing manuscript storage:
/// formatting buttons become reversible toggles and the selection toolbar follows the active
/// selection/caret instead of being anchored to the whole editor surface.
/// </summary>
public sealed class RichVisualEditingCorrections : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<RichVisualEditingCorrections, ManuscriptEditor, bool>(
            "IsEnabled",
            defaultValue: false);

    private static readonly ConditionalWeakTable<ManuscriptEditor, CorrectionSession> Sessions = new();

    static RichVisualEditingCorrections()
    {
        IsEnabledProperty.Changed.AddClassHandler<ManuscriptEditor>((editor, _) =>
        {
            if (GetIsEnabled(editor))
                Sessions.GetValue(editor, static value => new CorrectionSession(value)).Start();
        });
    }

    public static void SetIsEnabled(ManuscriptEditor editor, bool value)
        => editor.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(ManuscriptEditor editor)
        => editor.GetValue(IsEnabledProperty);

    private sealed class CorrectionSession
    {
        private readonly ManuscriptEditor _editor;
        private readonly Dictionary<string, List<Button>> _formatButtons = new(StringComparer.Ordinal);
        private Popup? _selectionPopup;
        private bool _started;
        private bool _installQueued;
        private bool _disposed;

        public CorrectionSession(ManuscriptEditor editor) => _editor = editor;

        public void Start()
        {
            if (_started) return;
            _started = true;

            _editor.AttachedToVisualTree += EditorAttached;
            _editor.DetachedFromVisualTree += EditorDetached;
            _editor.LayoutUpdated += EditorLayoutUpdated;
            _editor.TextChanged += EditorTextChanged;
            _editor.TextArea.SelectionChanged += EditorSelectionChanged;
            _editor.TextArea.Caret.PositionChanged += EditorCaretChanged;
            _editor.TextArea.TextView.ScrollOffsetChanged += TextViewScrollOffsetChanged;
            QueueInstall();
        }

        private void EditorAttached(object? sender, VisualTreeAttachmentEventArgs e) => QueueInstall();
        private void EditorLayoutUpdated(object? sender, EventArgs e) => QueueInstall();
        private void EditorDetached(object? sender, VisualTreeAttachmentEventArgs e) => Dispose();

        private void EditorTextChanged(object? sender, EventArgs e)
        {
            RefreshButtonStates();
            RepositionSelectionPopup();
        }

        private void EditorSelectionChanged(object? sender, EventArgs e)
        {
            RefreshButtonStates();
            RepositionSelectionPopup();
        }

        private void EditorCaretChanged(object? sender, EventArgs e)
        {
            RefreshButtonStates();
            RepositionSelectionPopup();
        }

        private void TextViewScrollOffsetChanged(object? sender, EventArgs e)
            => RepositionSelectionPopup();

        private void QueueInstall()
        {
            if (_disposed || _installQueued) return;
            _installQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _installQueued = false;
                if (!_disposed) TryInstallCorrections();
            }, DispatcherPriority.Background);
        }

        private void TryInstallCorrections()
        {
            var host = _editor.GetVisualAncestors()
                .OfType<Grid>()
                .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
            if (host is null) return;

            _selectionPopup ??= host.Children
                .OfType<Popup>()
                .FirstOrDefault(static popup =>
                    popup.Child is Border border && border.Classes.Contains("rich-selection-toolbar"));

            if (_selectionPopup is not null)
            {
                ConfigureSelectionPopup(_selectionPopup);
                if (_selectionPopup.Child is Control selectionSurface)
                    ReplaceFormattingButtons(selectionSurface);
            }

            var commandBar = host.Children
                .OfType<Border>()
                .FirstOrDefault(static border => border.Classes.Contains("editor-command-bar"));
            if (commandBar is not null)
                ReplaceFormattingButtons(commandBar);

            RefreshButtonStates();
            RepositionSelectionPopup();
        }

        private void ConfigureSelectionPopup(Popup popup)
        {
            popup.PlacementTarget = _editor.TextArea.TextView;
            popup.Placement = PlacementMode.AnchorAndGravity;
            popup.HorizontalOffset = 0;
            popup.IsLightDismissEnabled = false;
        }

        private void RepositionSelectionPopup()
        {
            var popup = _selectionPopup;
            if (popup is null || !popup.IsOpen || _editor.SelectionLength <= 0 || _editor.Document.TextLength == 0)
                return;

            var textView = _editor.TextArea.TextView;
            var position = _editor.TextArea.Caret.Position;

            try
            {
                var top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - textView.ScrollOffset;
                var bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - textView.ScrollOffset;
                var viewportWidth = Math.Max(1, textView.Bounds.Width);
                var toolbarHalfWidth = Math.Min(190, viewportWidth / 2);
                var minX = toolbarHalfWidth;
                var maxX = Math.Max(minX, viewportWidth - toolbarHalfWidth);
                var x = Math.Clamp(top.X, minX, maxX);
                var y = Math.Clamp(top.Y, 0, Math.Max(0, textView.Bounds.Height));
                var height = Math.Max(1, bottom.Y - top.Y);

                popup.PlacementTarget = textView;
                popup.PlacementRect = new Rect(x - 1, y, 2, height);
                popup.HorizontalOffset = 0;

                if (y >= 46)
                {
                    popup.PlacementAnchor = PopupAnchor.Top;
                    popup.PlacementGravity = PopupGravity.Top;
                    popup.VerticalOffset = -8;
                }
                else
                {
                    popup.PlacementAnchor = PopupAnchor.Bottom;
                    popup.PlacementGravity = PopupGravity.Bottom;
                    popup.VerticalOffset = 8;
                }
            }
            catch (InvalidOperationException)
            {
                // Visual lines can be temporarily unavailable during document/layout replacement.
                // The next caret, selection, scroll, or layout update will retry positioning.
            }
        }

        private void ReplaceFormattingButtons(Control root)
        {
            foreach (var oldButton in root.GetVisualDescendants().OfType<Button>().ToArray())
            {
                if (oldButton.Classes.Contains("toggle-format-fixed")) continue;
                var label = ContentText(oldButton);
                var key = FormatKey(label);
                if (key is null || oldButton.Parent is not Panel parent) continue;

                var index = parent.Children.IndexOf(oldButton);
                if (index < 0) continue;

                var replacement = CreateToggleButton(oldButton, label, key);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, replacement);

                if (!_formatButtons.TryGetValue(key, out var buttons))
                {
                    buttons = [];
                    _formatButtons[key] = buttons;
                }
                buttons.Add(replacement);
            }
        }

        private Button CreateToggleButton(Button source, string label, string key)
        {
            Control content = label switch
            {
                "B" => new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.Bold },
                "I" => new TextBlock { Text = label, FontStyle = Avalonia.Media.FontStyle.Italic },
                _ => new TextBlock { Text = label }
            };

            var button = new Button
            {
                Content = content,
                MinWidth = source.MinWidth,
                MinHeight = source.MinHeight,
                MaxWidth = source.MaxWidth,
                MaxHeight = source.MaxHeight,
                Width = source.Width,
                Height = source.Height,
                Margin = source.Margin,
                Padding = source.Padding,
                HorizontalAlignment = source.HorizontalAlignment,
                VerticalAlignment = source.VerticalAlignment,
                HorizontalContentAlignment = source.HorizontalContentAlignment,
                VerticalContentAlignment = source.VerticalContentAlignment,
                FontSize = source.FontSize
            };

            foreach (var className in source.Classes)
            {
                if (!string.IsNullOrWhiteSpace(className) && className[0] != ':')
                    button.Classes.Add(className);
            }
            button.Classes.Add("toggle-format-fixed");
            ToolTip.SetTip(button, ToggleTip(key));
            button.Click += (_, _) =>
            {
                ToggleFormat(key);
                _editor.Focus();
                RefreshButtonStates();
                RepositionSelectionPopup();
            };
            return button;
        }

        private void ToggleFormat(string key)
        {
            switch (key)
            {
                case "bold":
                    ToggleInline("**");
                    break;
                case "italic":
                    ToggleInline("*");
                    break;
                case "code":
                    ToggleInline("`");
                    break;
                case "h1":
                    ToggleHeading(1);
                    break;
                case "h2":
                    ToggleHeading(2);
                    break;
                case "h3":
                    ToggleHeading(3);
                    break;
                case "quote":
                    ToggleQuote();
                    break;
                case "list":
                    ToggleBulletList();
                    break;
            }
        }

        private void ToggleInline(string marker)
        {
            var document = _editor.Document;
            var markerLength = marker.Length;
            var selectionStart = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;
            var selectionLength = Math.Max(0, _editor.SelectionLength);
            var selectionEnd = selectionStart + selectionLength;

            if (selectionLength > 0)
            {
                var selected = document.GetText(selectionStart, selectionLength);
                if (selected.Length >= markerLength * 2 &&
                    selected.StartsWith(marker, StringComparison.Ordinal) &&
                    selected.EndsWith(marker, StringComparison.Ordinal))
                {
                    var inner = selected[markerLength..^markerLength];
                    document.Replace(selectionStart, selectionLength, inner);
                    _editor.Select(selectionStart, inner.Length);
                    _editor.CaretOffset = selectionStart + inner.Length;
                    return;
                }

                if (selectionStart >= markerLength &&
                    selectionEnd + markerLength <= document.TextLength &&
                    document.GetText(selectionStart - markerLength, markerLength) == marker &&
                    document.GetText(selectionEnd, markerLength) == marker &&
                    IsValidMarkerAt(document.Text, selectionStart - markerLength, marker) &&
                    IsValidMarkerAt(document.Text, selectionEnd, marker))
                {
                    document.Remove(selectionEnd, markerLength);
                    document.Remove(selectionStart - markerLength, markerLength);
                    _editor.Select(selectionStart - markerLength, selectionLength);
                    _editor.CaretOffset = selectionStart - markerLength + selectionLength;
                    return;
                }

                if (TryFindContainingInlineRange(marker, selectionStart, selectionEnd, out var open, out var close))
                {
                    document.Remove(close, markerLength);
                    document.Remove(open, markerLength);
                    var adjustedStart = Math.Max(open, selectionStart - markerLength);
                    _editor.Select(adjustedStart, selectionLength);
                    _editor.CaretOffset = adjustedStart + selectionLength;
                    return;
                }

                document.Insert(selectionEnd, marker);
                document.Insert(selectionStart, marker);
                _editor.Select(selectionStart + markerLength, selectionLength);
                _editor.CaretOffset = selectionStart + markerLength + selectionLength;
                return;
            }

            var caret = Math.Clamp(_editor.CaretOffset, 0, document.TextLength);
            if (TryFindContainingInlineRange(marker, caret, caret, out var containingOpen, out var containingClose))
            {
                document.Remove(containingClose, markerLength);
                document.Remove(containingOpen, markerLength);
                _editor.CaretOffset = Math.Max(containingOpen, caret - markerLength);
                _editor.Select(_editor.CaretOffset, 0);
                return;
            }

            document.Insert(caret, marker + marker);
            _editor.CaretOffset = caret + markerLength;
            _editor.Select(_editor.CaretOffset, 0);
        }

        private bool TryFindContainingInlineRange(
            string marker,
            int absoluteStart,
            int absoluteEnd,
            out int openOffset,
            out int closeOffset)
        {
            openOffset = -1;
            closeOffset = -1;
            if (_editor.Document.TextLength == 0) return false;

            var lookup = Math.Min(Math.Max(0, absoluteStart), Math.Max(0, _editor.Document.TextLength - 1));
            var line = _editor.Document.GetLineByOffset(lookup);
            if (absoluteEnd > line.EndOffset) return false;

            var text = _editor.Document.GetText(line);
            var relativeStart = Math.Clamp(absoluteStart - line.Offset, 0, text.Length);
            var relativeEnd = Math.Clamp(absoluteEnd - line.Offset, relativeStart, text.Length);
            var markerPositions = MarkerPositions(text, marker).ToArray();

            for (var index = 0; index + 1 < markerPositions.Length; index += 2)
            {
                var open = markerPositions[index];
                var close = markerPositions[index + 1];
                if (relativeStart < open + marker.Length || relativeEnd > close) continue;
                openOffset = line.Offset + open;
                closeOffset = line.Offset + close;
                return true;
            }
            return false;
        }

        private static IEnumerable<int> MarkerPositions(string text, string marker)
        {
            var search = 0;
            while (search <= text.Length - marker.Length)
            {
                var index = text.IndexOf(marker, search, StringComparison.Ordinal);
                if (index < 0) yield break;
                if (IsValidMarkerAt(text, index, marker))
                    yield return index;
                search = index + marker.Length;
            }
        }

        private static bool IsValidMarkerAt(string text, int index, string marker)
        {
            if (index < 0 || index + marker.Length > text.Length) return false;
            if (!text.AsSpan(index, marker.Length).SequenceEqual(marker.AsSpan())) return false;
            if (marker != "*") return true;

            var beforeStar = index > 0 && text[index - 1] == '*';
            var afterStar = index + 1 < text.Length && text[index + 1] == '*';
            return !beforeStar && !afterStar;
        }

        private void ToggleHeading(int level)
        {
            var lines = SelectedLines();
            if (lines.Count == 0) return;
            var remove = lines.All(line => HeadingLevel(_editor.Document.GetText(line).TrimStart()) == level);

            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var line = lines[index];
                var text = _editor.Document.GetText(line);
                var indent = LeadingWhitespace(text);
                var existingLevel = HeadingLevel(text[indent..]);
                var existingLength = existingLevel > 0 ? existingLevel + 1 : 0;
                var marker = remove ? string.Empty : new string('#', level) + " ";
                _editor.Document.Replace(line.Offset + indent, existingLength, marker);
            }
        }

        private void ToggleQuote()
        {
            var lines = SelectedLines();
            if (lines.Count == 0) return;
            var remove = lines.All(line => HasQuotePrefix(_editor.Document.GetText(line)));

            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var line = lines[index];
                var text = _editor.Document.GetText(line);
                var indent = LeadingWhitespace(text);
                var has = text.AsSpan(indent).StartsWith("> ".AsSpan(), StringComparison.Ordinal);
                if (remove && has)
                    _editor.Document.Remove(line.Offset + indent, 2);
                else if (!remove && !has)
                    _editor.Document.Insert(line.Offset + indent, "> ");
            }
        }

        private void ToggleBulletList()
        {
            var lines = SelectedLines();
            if (lines.Count == 0) return;
            var prefixes = lines.Select(line => ListPrefixLength(_editor.Document.GetText(line))).ToArray();
            var remove = prefixes.All(length => length > 0);

            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var line = lines[index];
                var text = _editor.Document.GetText(line);
                var indent = LeadingWhitespace(text);
                var prefixLength = ListPrefixLength(text);
                if (remove && prefixLength > 0)
                {
                    _editor.Document.Remove(line.Offset + indent, prefixLength);
                }
                else if (!remove)
                {
                    if (prefixLength > 0)
                        _editor.Document.Replace(line.Offset + indent, prefixLength, "- ");
                    else
                        _editor.Document.Insert(line.Offset + indent, "- ");
                }
            }
        }

        private IReadOnlyList<AvaloniaEdit.Document.DocumentLine> SelectedLines()
        {
            if (_editor.Document.LineCount == 0) return [];
            var start = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;
            var end = _editor.SelectionLength > 0
                ? _editor.SelectionStart + _editor.SelectionLength
                : _editor.CaretOffset;
            var maxOffset = Math.Max(0, _editor.Document.TextLength - 1);
            var startLine = _editor.Document.GetLineByOffset(Math.Min(Math.Max(0, start), maxOffset));
            var endLookup = end > start ? end - 1 : end;
            var endLine = _editor.Document.GetLineByOffset(Math.Min(Math.Max(0, endLookup), maxOffset));
            var result = new List<AvaloniaEdit.Document.DocumentLine>();
            for (var line = startLine.LineNumber; line <= endLine.LineNumber; line++)
                result.Add(_editor.Document.GetLineByNumber(line));
            return result;
        }

        private void RefreshButtonStates()
        {
            if (_editor.Document.TextLength == 0)
            {
                foreach (var buttons in _formatButtons.Values)
                    foreach (var button in buttons)
                        SetClass(button, "active-format", false);
                return;
            }

            var caret = Math.Clamp(_editor.CaretOffset, 0, _editor.Document.TextLength);
            var lookup = Math.Min(caret, Math.Max(0, _editor.Document.TextLength - 1));
            var line = _editor.Document.GetLineByOffset(lookup);
            var text = _editor.Document.GetText(line);
            var relative = Math.Clamp(caret - line.Offset, 0, text.Length);
            var trimmed = text.TrimStart();

            SetButtonState("bold", IsInsideInline(text, relative, "**"));
            SetButtonState("italic", IsInsideInline(text, relative, "*"));
            SetButtonState("code", IsInsideInline(text, relative, "`"));
            var heading = HeadingLevel(trimmed);
            SetButtonState("h1", heading == 1);
            SetButtonState("h2", heading == 2);
            SetButtonState("h3", heading == 3);
            SetButtonState("quote", trimmed.StartsWith("> ", StringComparison.Ordinal));
            SetButtonState("list", ListPrefixLength(text) > 0);
        }

        private static bool IsInsideInline(string text, int relative, string marker)
        {
            var positions = MarkerPositions(text, marker).ToArray();
            for (var index = 0; index + 1 < positions.Length; index += 2)
            {
                if (relative >= positions[index] + marker.Length && relative <= positions[index + 1])
                    return true;
            }
            return false;
        }

        private void SetButtonState(string key, bool active)
        {
            if (!_formatButtons.TryGetValue(key, out var buttons)) return;
            foreach (var button in buttons)
                SetClass(button, "active-format", active);
        }

        private static void SetClass(Control control, string className, bool enabled)
        {
            var has = control.Classes.Contains(className);
            if (enabled && !has) control.Classes.Add(className);
            else if (!enabled && has) control.Classes.Remove(className);
        }

        private static string? FormatKey(string label)
            => label switch
            {
                "B" => "bold",
                "I" => "italic",
                "`" => "code",
                "H1" => "h1",
                "H2" => "h2",
                "H3" => "h3",
                "❝" => "quote",
                "•" => "list",
                _ => null
            };

        private static string ToggleTip(string key)
            => key switch
            {
                "bold" => "Toggle bold (Ctrl+B)",
                "italic" => "Toggle italic (Ctrl+I)",
                "code" => "Toggle inline code",
                "h1" => "Toggle Heading 1",
                "h2" => "Toggle Heading 2",
                "h3" => "Toggle Heading 3",
                "quote" => "Toggle block quote",
                "list" => "Toggle bullet list",
                _ => "Toggle formatting"
            };

        private static string ContentText(Button button)
            => button.Content switch
            {
                TextBlock text => text.Text ?? string.Empty,
                string text => text,
                _ => string.Empty
            };

        private static int HeadingLevel(string trimmed)
        {
            var level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            return level > 0 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
        }

        private static bool HasQuotePrefix(string text)
        {
            var indent = LeadingWhitespace(text);
            return text.AsSpan(indent).StartsWith("> ".AsSpan(), StringComparison.Ordinal);
        }

        private static int ListPrefixLength(string text)
        {
            var indent = LeadingWhitespace(text);
            var span = text.AsSpan(indent);
            if (span.Length >= 2 && span[0] is '-' or '*' or '+' && char.IsWhiteSpace(span[1]))
                return 2;

            var digits = 0;
            while (digits < span.Length && char.IsDigit(span[digits])) digits++;
            if (digits > 0 && digits + 1 < span.Length && span[digits] == '.' && char.IsWhiteSpace(span[digits + 1]))
                return digits + 2;
            return 0;
        }

        private static int LeadingWhitespace(string text)
        {
            var index = 0;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            return index;
        }

        private void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _editor.AttachedToVisualTree -= EditorAttached;
            _editor.DetachedFromVisualTree -= EditorDetached;
            _editor.LayoutUpdated -= EditorLayoutUpdated;
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.SelectionChanged -= EditorSelectionChanged;
            _editor.TextArea.Caret.PositionChanged -= EditorCaretChanged;
            _editor.TextArea.TextView.ScrollOffsetChanged -= TextViewScrollOffsetChanged;
        }
    }
}
