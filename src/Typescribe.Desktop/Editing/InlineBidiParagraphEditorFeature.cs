using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Gives right-to-left manuscript lines a real BiDi-aware editing surface without changing the
/// canonical Markdown model. AvaloniaEdit 12 currently fixes its visual-line paragraph properties
/// to LTR, so RTL lines are replaced in-flow by a native Avalonia TextBox. Avalonia's normal text
/// formatter then owns the Unicode BiDi algorithm, caret navigation, selection and OpenType
/// shaping while the TextBox continues to edit the exact logical Unicode source string.
/// </summary>
internal sealed class InlineBidiParagraphEditorFeature
{
    private const string BidiParagraphClass = "typescribe-bidi-paragraph-editor";
    private readonly StudioWorkspaceWindow _window;
    private readonly DispatcherTimer _refreshTimer;
    private ManuscriptEditor? _editor;
    private BidiParagraphElementGenerator? _generator;
    private bool _installed;
    private bool _disposed;
    private bool _committing;

    private InlineBidiParagraphEditorFeature(StudioWorkspaceWindow window)
    {
        _window = window;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshIndex();
        };
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new InlineBidiParagraphEditorFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.TryInstall();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryInstall();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed)
        {
            TryInstall();
            return;
        }

        if (_editor is null || _generator is null) return;
        var width = Math.Max(300, _editor.TextArea.TextView.Bounds.Width - 18);
        if (Math.Abs(width - _generator.ViewportWidth) >= 1)
            _generator.UpdateViewportWidth(width);

        // Inline table cells are also native TextBoxes. Give Arabic-script cells a correct base
        // direction automatically without coupling the table feature to this implementation.
        PolishInlineTextBoxes();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        var host = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
        var editor = host?.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _editor = editor;
        _generator = new BidiParagraphElementGenerator(this)
        {
            ViewportWidth = Math.Max(300, editor.TextArea.TextView.Bounds.Width - 18)
        };
        editor.TextArea.TextView.ElementGenerators.Add(_generator);
        editor.TextChanged += EditorTextChanged;
        _installed = true;
        RefreshIndex();
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (!_committing) QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || !_installed) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshIndex()
    {
        if (_editor is null || _generator is null || _committing) return;
        _generator.SetParagraphs(BuildParagraphs());
        _editor.TextArea.TextView.Redraw();
        Dispatcher.UIThread.Post(PolishInlineTextBoxes, DispatcherPriority.Background);
    }

    private IReadOnlyList<BidiParagraph> BuildParagraphs()
    {
        if (_editor is null || _editor.Document.LineCount == 0) return [];

        var result = new List<BidiParagraph>();
        var inCodeFence = false;
        var inDisplayMath = false;

        for (var lineNumber = 1; lineNumber <= _editor.Document.LineCount; lineNumber++)
        {
            var line = _editor.Document.GetLineByNumber(lineNumber);
            if (line.Length <= 0) continue;
            var source = _editor.Document.GetText(line.Offset, line.Length);
            var trimmed = source.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            if (string.Equals(trimmed.Trim(), "$$", StringComparison.Ordinal))
            {
                inDisplayMath = !inDisplayMath;
                continue;
            }
            if (inDisplayMath) continue;

            if (!IsEditableTextLine(trimmed)) continue;

            var paragraphFormatting = ReadParagraphFormatting(lineNumber);
            var requestedDirection = paragraphFormatting?.Direction
                ?? paragraphFormatting?.CharacterDefaults?.Direction
                ?? TextDirectionMode.Auto;
            var direction = requestedDirection == TextDirectionMode.Auto
                ? UnicodeScriptClassifier.DetectDirection(source)
                : requestedDirection;
            if (direction != TextDirectionMode.RightToLeft) continue;

            var language = paragraphFormatting?.Language ?? paragraphFormatting?.CharacterDefaults?.Language;
            var script = paragraphFormatting?.Script
                ?? paragraphFormatting?.CharacterDefaults?.Script
                ?? UnicodeScriptClassifier.DetectScript(source, language);
            if (script == ScriptMode.Auto)
                script = UnicodeScriptClassifier.DetectScript(source, language);

            var character = paragraphFormatting?.CharacterDefaults;
            var fontFamily = character?.Font?.Family;
            if (string.IsNullOrWhiteSpace(fontFamily))
                fontFamily = PreferredSystemFont(script);

            result.Add(new BidiParagraph(
                lineNumber,
                line.Offset,
                line.Length,
                source,
                language,
                script,
                fontFamily,
                character?.FontSizePoints,
                character?.Bold,
                character?.Italic));
        }

        return result;
    }

    private ParagraphFormatting? ReadParagraphFormatting(int lineNumber)
    {
        if (_editor is null || lineNumber <= 1) return null;
        var previous = _editor.Document.GetLineByNumber(lineNumber - 1);
        if (previous.Length <= 0) return null;
        var text = _editor.Document.GetText(previous.Offset, previous.Length);
        return RichMarkdownFormattingCodec.TryReadBlockMetadata(text, out var block)
            ? block?.Paragraph
            : null;
    }

    private static bool IsEditableTextLine(string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal)) return false;
        if (trimmed.StartsWith("![", StringComparison.Ordinal)) return false;
        if (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.EndsWith("$$", StringComparison.Ordinal)) return false;
        if (trimmed.StartsWith('|') || trimmed.EndsWith('|')) return false;
        if (trimmed is "---" or "***" or "___") return false;
        return true;
    }

    private Control BuildControl(BidiParagraph paragraph)
    {
        var box = new TextBox
        {
            Text = paragraph.Source,
            AcceptsReturn = false,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.RightToLeft,
            TextAlignment = TextAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 5),
            MinHeight = 34,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = paragraph.FontSizePoints is > 0
                ? Math.Clamp(paragraph.FontSizePoints.Value, 6, 144)
                : Math.Max(12, _editor?.FontSize ?? 16),
            FontWeight = paragraph.Bold == true ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = paragraph.Italic == true ? FontStyle.Italic : FontStyle.Normal
        };
        box.Classes.Add(BidiParagraphClass);
        if (!string.IsNullOrWhiteSpace(paragraph.FontFamily))
            box.FontFamily = new FontFamily(paragraph.FontFamily);

        box.LostFocus += (_, _) => Commit(paragraph, box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                box.Text = paragraph.Source;
                ReturnFocusToManuscript(paragraph.LineNumber, commit: false, box);
                return;
            }

            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            ReturnFocusToManuscript(paragraph.LineNumber, commit: true, box);
        };

        var label = new TextBlock
        {
            Text = DirectionLabel(paragraph),
            FontSize = 9,
            Opacity = .42,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Width = Math.Max(300, _generator?.ViewportWidth ?? 720)
        };
        grid.Children.Add(label);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        return new Border
        {
            Child = grid,
            Width = grid.Width,
            MinHeight = 34,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0, 0, 0, .5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
            Background = Brushes.Transparent
        };
    }

    private static string DirectionLabel(BidiParagraph paragraph)
        => paragraph.Script switch
        {
            ScriptMode.UrduNastaliq => "RTL · UR",
            ScriptMode.Persian => "RTL · FA",
            ScriptMode.Arabic => "RTL · AR",
            ScriptMode.Hebrew => "RTL · HE",
            _ => "RTL"
        };

    private void ReturnFocusToManuscript(int lineNumber, bool commit, TextBox box)
    {
        if (_editor is null) return;
        if (commit)
        {
            var paragraph = _generator?.Paragraphs.FirstOrDefault(candidate => candidate.LineNumber == lineNumber);
            if (paragraph is not null) Commit(paragraph, box);
        }

        if (lineNumber >= 1 && lineNumber <= _editor.Document.LineCount)
        {
            var line = _editor.Document.GetLineByNumber(lineNumber);
            _editor.CaretOffset = line.EndOffset;
        }
        _editor.Focus();
    }

    private void Commit(BidiParagraph paragraph, TextBox box)
    {
        if (_editor is null || _editor.IsReadOnly || _committing) return;
        var next = box.Text ?? string.Empty;
        if (string.Equals(next, paragraph.Source, StringComparison.Ordinal)) return;
        if (paragraph.LineNumber < 1 || paragraph.LineNumber > _editor.Document.LineCount) return;

        var line = _editor.Document.GetLineByNumber(paragraph.LineNumber);
        var current = _editor.Document.GetText(line.Offset, line.Length);
        if (!string.Equals(current, paragraph.Source, StringComparison.Ordinal))
        {
            // Never overwrite source that changed outside this inline editor.
            QueueRefresh();
            return;
        }

        _committing = true;
        try
        {
            _editor.Document.Replace(line.Offset, line.Length, next);
        }
        finally
        {
            _committing = false;
        }
        QueueRefresh();
    }

    private void PolishInlineTextBoxes()
    {
        if (_editor is null) return;
        foreach (var box in _editor.GetVisualDescendants().OfType<TextBox>())
        {
            if (box.Classes.Contains(BidiParagraphClass)) continue;
            var direction = UnicodeScriptClassifier.DetectDirection(box.Text);
            if (direction == TextDirectionMode.RightToLeft)
            {
                box.FlowDirection = FlowDirection.RightToLeft;
                box.TextAlignment = TextAlignment.Right;
                var script = UnicodeScriptClassifier.DetectScript(box.Text);
                var family = PreferredSystemFont(script);
                if (!string.IsNullOrWhiteSpace(family)) box.FontFamily = new FontFamily(family);
            }
            else if (direction == TextDirectionMode.LeftToRight)
            {
                box.FlowDirection = FlowDirection.LeftToRight;
                box.TextAlignment = TextAlignment.Left;
            }
        }
    }

    private static string? PreferredSystemFont(ScriptMode script)
    {
        string[] candidates = script switch
        {
            ScriptMode.UrduNastaliq =>
            [
                "Noto Nastaliq Urdu",
                "Awami Nastaliq",
                "Jameel Noori Nastaleeq",
                "Nafees Nastaleeq",
                "Urdu Typesetting",
                "Mehr Nastaliq Web"
            ],
            ScriptMode.Persian =>
            [
                "Noto Naskh Arabic",
                "Vazirmatn",
                "Amiri",
                "Noto Sans Arabic",
                "Segoe UI"
            ],
            ScriptMode.Arabic =>
            [
                "Noto Naskh Arabic",
                "Amiri",
                "Scheherazade New",
                "Noto Sans Arabic",
                "Segoe UI"
            ],
            ScriptMode.Hebrew => ["Noto Sans Hebrew", "Noto Serif Hebrew", "Arial"],
            _ => []
        };
        if (candidates.Length == 0) return null;

        var installed = FontManager.Current.SystemFonts.ToArray();
        foreach (var candidate in candidates)
        {
            var exact = installed.FirstOrDefault(font => string.Equals(font.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Name;
        }
        foreach (var candidate in candidates)
        {
            var partial = installed.FirstOrDefault(font => font.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial.Name;
        }
        return null;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _refreshTimer.Stop();
        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            if (_generator is not null)
                _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        }
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private sealed record BidiParagraph(
        int LineNumber,
        int Offset,
        int Length,
        string Source,
        string? Language,
        ScriptMode Script,
        string? FontFamily,
        double? FontSizePoints,
        bool? Bold,
        bool? Italic);

    private sealed class BidiParagraphElementGenerator(InlineBidiParagraphEditorFeature owner) : VisualLineElementGenerator
    {
        private IReadOnlyList<BidiParagraph> _paragraphs = [];
        private readonly Dictionary<int, Control> _controls = [];

        public IReadOnlyList<BidiParagraph> Paragraphs => _paragraphs;
        public double ViewportWidth { get; set; } = 720;

        public void SetParagraphs(IReadOnlyList<BidiParagraph> paragraphs)
        {
            _paragraphs = paragraphs;
            _controls.Clear();
        }

        public void UpdateViewportWidth(double width)
        {
            ViewportWidth = width;
            foreach (var control in _controls.Values)
                control.Width = width;
            owner._editor?.TextArea.TextView.Redraw();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            for (var index = 0; index < _paragraphs.Count; index++)
                if (_paragraphs[index].Offset >= startOffset)
                    return _paragraphs[index].Offset;
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var paragraph = _paragraphs.FirstOrDefault(candidate => candidate.Offset == offset);
            if (paragraph is null || paragraph.Length <= 0) return null;
            if (!_controls.TryGetValue(offset, out var control))
            {
                control = owner.BuildControl(paragraph);
                control.VerticalAlignment = VerticalAlignment.Center;
                _controls[offset] = control;
            }
            return new InlineObjectElement(paragraph.Length, control);
        }
    }
}
