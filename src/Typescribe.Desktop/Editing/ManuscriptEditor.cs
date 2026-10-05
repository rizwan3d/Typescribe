using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Typescribe.Desktop.Editing;

public enum ManuscriptAnnotationKind
{
    Note,
    Comment,
    Warning,
    Error
}

public sealed record ManuscriptLineAnnotation(int Line, ManuscriptAnnotationKind Kind, string Message);
public sealed record ManuscriptTextDiagnostic(int Offset, int Length, string Message);
public sealed record ManuscriptRevisionSpan(int Offset, int Length, int Level);

/// <summary>
/// Typescribe's manuscript-first editor. AvaloniaEdit provides the text engine and
/// line virtualization; this control owns authoring semantics such as revision spans,
/// low-clutter Markdown presentation, typewriter scrolling, focus mode, annotations,
/// and local spelling/typo diagnostics.
/// </summary>
public sealed class ManuscriptEditor : TextEditor
{
    private static readonly IBrush MarkdownMarkBrush = new SolidColorBrush(Color.Parse("#78869A"));
    private static readonly IBrush HiddenMarkdownMarkBrush = new SolidColorBrush(Color.FromArgb(18, 120, 134, 154));
    private static readonly IBrush HeadingBrush = new SolidColorBrush(Color.Parse("#6D8BFF"));
    private static readonly IBrush QuoteBrush = new SolidColorBrush(Color.Parse("#7CA58A"));
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#D08B5B"));
    private static readonly IBrush FocusDimBrush = new SolidColorBrush(Color.FromArgb(95, 145, 153, 166));
    private static readonly IBrush AnnotationBrush = new SolidColorBrush(Color.FromArgb(28, 245, 158, 11));
    private static readonly IBrush SpellingBrush = new SolidColorBrush(Color.Parse("#E75A63"));
    private static readonly IBrush[] RevisionBrushes =
    [
        new SolidColorBrush(Color.Parse("#94A3B8")),
        new SolidColorBrush(Color.Parse("#2563EB")),
        new SolidColorBrush(Color.Parse("#7C3AED")),
        new SolidColorBrush(Color.Parse("#DB2777")),
        new SolidColorBrush(Color.Parse("#EA580C")),
        new SolidColorBrush(Color.Parse("#059669"))
    ];

    private static readonly HashSet<string> CommonTypos = new(StringComparer.OrdinalIgnoreCase)
    {
        "teh", "adn", "recieve", "recieved", "definately", "seperate", "seperately",
        "occured", "untill", "alot", "wierd", "becuase", "thier", "freind", "wich",
        "adress", "accomodate", "begining", "goverment", "enviroment", "existance"
    };

    private readonly ManuscriptColorizer _colorizer;
    private readonly List<ManuscriptRevisionSpan> _revisionSpans = [];
    private readonly List<ManuscriptTextDiagnostic> _diagnostics = [];
    private readonly Dictionary<int, ManuscriptLineAnnotation> _annotations = [];
    private CancellationTokenSource? _diagnosticCts;
    private bool _suppressRevisionTracking;
    private bool _compositionMode;
    private bool _userTypewriterScrolling;
    private bool _userFocusMode;
    private int _focusStartLine = 1;
    private int _focusEndLine = 1;
    private long _diagnosticVersion;
    private string? _documentIdentity;

    public ManuscriptEditor()
        : base(new ManuscriptTextArea())
    {
        WordWrap = true;
        ShowLineNumbers = false;
        FontFamily = new FontFamily("monospace");
        FontSize = 16;
        Background = Brushes.Transparent;
        SynchronizeScrollBarsWithWordWrap();

        Options.AcceptsTab = true;
        Options.AllowScrollBelowDocument = true;
        Options.EnableTextDragDrop = true;
        Options.HighlightCurrentLine = false;
        Options.ShowBoxForControlCharacters = false;
        TextArea.RightClickMovesCaret = true;

        _colorizer = new ManuscriptColorizer(this);
        TextArea.TextView.LineTransformers.Add(_colorizer);
        TextArea.Caret.PositionChanged += CaretPositionChanged;
        TextArea.SelectionChanged += SelectionChanged;
        Document.Changing += DocumentChanging;
        TextChanged += ManuscriptTextChanged;
        ContextMenu = BuildContextMenu();
        UpdateFocusParagraph();
    }

    public int CaretIndex
    {
        get => CaretOffset;
        set => CaretOffset = Math.Clamp(value, 0, Document.TextLength);
    }

    public int SelectionEnd
    {
        get => SelectionStart + SelectionLength;
        set
        {
            var clamped = Math.Clamp(value, 0, Document.TextLength);
            var start = SelectionStart;
            if (clamped >= start) Select(start, clamped - start);
            else Select(clamped, start - clamped);
        }
    }

    public bool TypewriterScrolling
    {
        get => _userTypewriterScrolling;
        set
        {
            if (_userTypewriterScrolling == value) return;
            _userTypewriterScrolling = value;
            if (EffectiveTypewriterScrolling) CenterCaretLine();
            RefreshContextMenuChecks();
        }
    }

    public bool FocusCurrentParagraph
    {
        get => _userFocusMode;
        set
        {
            if (_userFocusMode == value) return;
            _userFocusMode = value;
            UpdateFocusParagraph();
            RefreshContextMenuChecks();
        }
    }

    public bool ShowMarkdownMarks { get; set; }
    public bool SpellIndicatorsEnabled { get; set; } = true;
    public int RevisionLevel { get; set; }
    public string? DocumentIdentity => _documentIdentity;

    public IReadOnlyList<ManuscriptRevisionSpan> RevisionSpans => _revisionSpans;
    public IReadOnlyList<ManuscriptTextDiagnostic> Diagnostics => _diagnostics;
    public IReadOnlyCollection<ManuscriptLineAnnotation> LineAnnotations => _annotations.Values;

    private bool EffectiveTypewriterScrolling => _userTypewriterScrolling || _compositionMode;
    private bool EffectiveFocusMode => _userFocusMode || _compositionMode;

    private sealed class ManuscriptTextArea : TextArea
    {
        public ManuscriptTextArea()
            : base(new ManuscriptTextView())
        {
        }

        protected override Type StyleKeyOverride => typeof(TextArea);
    }

    private sealed class ManuscriptTextView : TextView
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var constrained = availableSize;
            if (double.IsInfinity(constrained.Height))
            {
                var height = FindFiniteViewportSize(static size => size.Height);
                if (height > 0)
                    constrained = constrained.WithHeight(height);
            }

            return base.MeasureOverride(constrained);
        }

        private double FindFiniteViewportSize(Func<Size, double> selector)
        {
            const double minimumUsableViewport = 96;
            var candidates = this.GetVisualAncestors()
                .OfType<Control>()
                .Select(control => selector(control.Bounds.Size))
                .Where(static value => value > 0 && !double.IsInfinity(value) && !double.IsNaN(value))
                .ToArray();

            return candidates
                .Where(static value => value >= minimumUsableViewport)
                .DefaultIfEmpty(candidates.DefaultIfEmpty(0).Min())
                .Min();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WordWrapProperty)
            SynchronizeScrollBarsWithWordWrap();
    }

    private void SynchronizeScrollBarsWithWordWrap()
    {
        var horizontalScrollEnabled = !WordWrap;
        var logicalScroll = (ILogicalScrollable)TextArea;
        logicalScroll.CanHorizontallyScroll = horizontalScrollEnabled;
        logicalScroll.CanVerticallyScroll = true;

        VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        HorizontalScrollBarVisibility = horizontalScrollEnabled
            ? ScrollBarVisibility.Visible
            : ScrollBarVisibility.Disabled;

        InvalidateMeasure();
        TextArea.TextView.InvalidateMeasure();
        TextArea.TextView.Redraw();
        TextArea.RaiseScrollInvalidated(EventArgs.Empty);
    }

    public void SetDocumentIdentity(string? identity)
    {
        if (string.Equals(_documentIdentity, identity, StringComparison.Ordinal)) return;
        _documentIdentity = identity;
        ClearRevisionSpans();
    }

    public void SetCompositionMode(bool enabled)
    {
        if (_compositionMode == enabled) return;
        _compositionMode = enabled;
        UpdateFocusParagraph();
        if (enabled) CenterCaretLine();
    }

    public void ApplyProxyText(string? text, bool resetDecorations = false)
    {
        text ??= string.Empty;
        if (string.Equals(Text, text, StringComparison.Ordinal)) return;

        if (resetDecorations || IsLikelyDocumentReplacement(Text, text))
        {
            _suppressRevisionTracking = true;
            try
            {
                Text = text;
                _revisionSpans.Clear();
            }
            finally
            {
                _suppressRevisionTracking = false;
            }
            QueueDiagnostics();
            Redraw();
            return;
        }

        var oldText = Text;
        var prefix = CommonPrefixLength(oldText, text);
        var suffix = CommonSuffixLength(oldText, text, prefix);
        var removeLength = oldText.Length - prefix - suffix;
        var inserted = text.Substring(prefix, text.Length - prefix - suffix);
        Document.Replace(prefix, removeLength, inserted);
    }

    public void SetRevisionSpans(IEnumerable<ManuscriptRevisionSpan> spans)
    {
        _revisionSpans.Clear();
        foreach (var span in spans)
        {
            if (span.Length <= 0 || span.Level <= 0) continue;
            var start = Math.Clamp(span.Offset, 0, Document.TextLength);
            var end = Math.Clamp(span.Offset + span.Length, start, Document.TextLength);
            if (end > start) _revisionSpans.Add(new ManuscriptRevisionSpan(start, end - start, Math.Clamp(span.Level, 1, 5)));
        }
        MergeRevisionSpans();
        Redraw();
    }

    public void ClearRevisionSpans()
    {
        if (_revisionSpans.Count == 0) return;
        _revisionSpans.Clear();
        Redraw();
    }

    public void SetSpellingDiagnostics(IEnumerable<ManuscriptTextDiagnostic> diagnostics)
    {
        _diagnostics.Clear();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Length <= 0) continue;
            var start = Math.Clamp(diagnostic.Offset, 0, Document.TextLength);
            var end = Math.Clamp(diagnostic.Offset + diagnostic.Length, start, Document.TextLength);
            if (end > start) _diagnostics.Add(diagnostic with { Offset = start, Length = end - start });
        }
        Redraw();
    }

    public void SetLineAnnotations(IEnumerable<ManuscriptLineAnnotation> annotations)
    {
        _annotations.Clear();
        foreach (var annotation in annotations)
        {
            if (annotation.Line <= 0 || annotation.Line > Document.LineCount) continue;
            _annotations[annotation.Line] = annotation;
        }
        Redraw();
    }

    public void NavigateToLine(int line, int column = 1)
    {
        if (Document.LineCount == 0) return;
        var clampedLine = Math.Clamp(line, 1, Document.LineCount);
        var documentLine = Document.GetLineByNumber(clampedLine);
        var columnOffset = Math.Clamp(column - 1, 0, documentLine.Length);
        CaretOffset = documentLine.Offset + columnOffset;
        Select(CaretOffset, 0);
        ScrollTo(clampedLine, columnOffset + 1);
        Focus();
    }

    private void DocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        AdjustRevisionSpans(e);
        if (_suppressRevisionTracking || RevisionLevel <= 0 || e.InsertionLength <= 0) return;
        _revisionSpans.Add(new ManuscriptRevisionSpan(e.Offset, e.InsertionLength, Math.Clamp(RevisionLevel, 1, 5)));
        MergeRevisionSpans();
    }

    private void ManuscriptTextChanged(object? sender, EventArgs e)
    {
        UpdateFocusParagraph();
        QueueDiagnostics();
        Redraw();
    }

    private void CaretPositionChanged(object? sender, EventArgs e)
    {
        UpdateFocusParagraph();
        if (EffectiveTypewriterScrolling) CenterCaretLine();
        Redraw();
    }

    private void SelectionChanged(object? sender, EventArgs e)
    {
        Redraw();
    }

    private void CenterCaretLine()
    {
        if (Document.LineCount == 0) return;
        ScrollTo(Math.Clamp(TextArea.Caret.Line, 1, Document.LineCount), Math.Max(1, TextArea.Caret.Column));
    }

    private void UpdateFocusParagraph()
    {
        if (!EffectiveFocusMode || Document.LineCount == 0)
        {
            _focusStartLine = 1;
            _focusEndLine = Math.Max(1, Document.LineCount);
            return;
        }

        var caretLine = Math.Clamp(TextArea.Caret.Line, 1, Document.LineCount);
        var start = caretLine;
        var end = caretLine;
        while (start > 1 && !IsBlankLine(start - 1)) start--;
        while (end < Document.LineCount && !IsBlankLine(end + 1)) end++;
        _focusStartLine = start;
        _focusEndLine = end;
    }

    private bool IsBlankLine(int lineNumber)
    {
        var line = Document.GetLineByNumber(lineNumber);
        return string.IsNullOrWhiteSpace(Document.GetText(line));
    }

    private bool IsFocusedLine(int lineNumber)
        => !EffectiveFocusMode || (lineNumber >= _focusStartLine && lineNumber <= _focusEndLine);

    private bool IsCaretLine(int lineNumber) => TextArea.Caret.Line == lineNumber;

    private void QueueDiagnostics()
    {
        _diagnosticCts?.Cancel();
        _diagnosticCts?.Dispose();
        _diagnosticCts = null;

        if (!SpellIndicatorsEnabled)
        {
            if (_diagnostics.Count > 0)
            {
                _diagnostics.Clear();
                Redraw();
            }
            return;
        }

        var version = Interlocked.Increment(ref _diagnosticVersion);
        var snapshot = Text;
        var cts = new CancellationTokenSource();
        _diagnosticCts = cts;
        _ = ComputeDiagnosticsAsync(snapshot, version, cts.Token);
    }

    private async Task ComputeDiagnosticsAsync(string text, long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(420, cancellationToken);
            var diagnostics = await Task.Run(() => BuildLocalDiagnostics(text, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested || version != _diagnosticVersion) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _diagnosticVersion) return;
                SetSpellingDiagnostics(diagnostics);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static IReadOnlyList<ManuscriptTextDiagnostic> BuildLocalDiagnostics(string text, CancellationToken cancellationToken)
    {
        var diagnostics = new List<ManuscriptTextDiagnostic>();
        var previousWord = string.Empty;
        var previousStart = -1;
        var index = 0;

        while (index < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (index < text.Length && !IsWordCharacter(text[index])) index++;
            if (index >= text.Length) break;
            var start = index;
            while (index < text.Length && IsWordCharacter(text[index])) index++;
            var length = index - start;
            if (length <= 1) continue;

            var word = text.Substring(start, length);
            if (CommonTypos.Contains(word))
                diagnostics.Add(new ManuscriptTextDiagnostic(start, length, $"Possible spelling issue: {word}"));

            if (previousStart >= 0 && string.Equals(previousWord, word, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new ManuscriptTextDiagnostic(start, length, $"Repeated word: {word}"));

            if (HasSuspiciousRun(word))
                diagnostics.Add(new ManuscriptTextDiagnostic(start, length, $"Check spelling: {word}"));

            previousWord = word;
            previousStart = start;
        }

        return diagnostics;
    }

    private static bool IsWordCharacter(char value)
        => char.IsLetter(value) || value is '\'' or '’' or '-';

    private static bool HasSuspiciousRun(string word)
    {
        if (word.Length < 4) return false;
        var run = 1;
        for (var index = 1; index < word.Length; index++)
        {
            if (char.ToLowerInvariant(word[index]) == char.ToLowerInvariant(word[index - 1]))
            {
                run++;
                if (run >= 3) return true;
            }
            else run = 1;
        }
        return false;
    }

    private void AdjustRevisionSpans(DocumentChangeEventArgs change)
    {
        if (_revisionSpans.Count == 0) return;
        var delta = change.InsertionLength - change.RemovalLength;
        var removedEnd = change.Offset + change.RemovalLength;
        var adjusted = new List<ManuscriptRevisionSpan>(_revisionSpans.Count);

        foreach (var span in _revisionSpans)
        {
            var spanStart = span.Offset;
            var spanEnd = span.Offset + span.Length;
            if (spanEnd <= change.Offset)
            {
                adjusted.Add(span);
                continue;
            }
            if (spanStart >= removedEnd)
            {
                adjusted.Add(span with { Offset = Math.Max(0, spanStart + delta) });
                continue;
            }

            var newStart = Math.Min(spanStart, change.Offset);
            var newEnd = Math.Max(newStart, spanEnd + delta);
            if (newEnd > newStart)
                adjusted.Add(span with { Offset = newStart, Length = newEnd - newStart });
        }

        _revisionSpans.Clear();
        _revisionSpans.AddRange(adjusted);
    }

    private void MergeRevisionSpans()
    {
        if (_revisionSpans.Count < 2) return;
        _revisionSpans.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        var merged = new List<ManuscriptRevisionSpan>(_revisionSpans.Count);
        foreach (var span in _revisionSpans)
        {
            if (merged.Count == 0)
            {
                merged.Add(span);
                continue;
            }
            var previous = merged[^1];
            var previousEnd = previous.Offset + previous.Length;
            if (previous.Level == span.Level && span.Offset <= previousEnd + 1)
            {
                var end = Math.Max(previousEnd, span.Offset + span.Length);
                merged[^1] = previous with { Length = end - previous.Offset };
            }
            else merged.Add(span);
        }
        _revisionSpans.Clear();
        _revisionSpans.AddRange(merged);
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < limit && left[index] == right[index]) index++;
        return index;
    }

    private static int CommonSuffixLength(string left, string right, int prefix)
    {
        var max = Math.Min(left.Length, right.Length) - prefix;
        var count = 0;
        while (count < max && left[left.Length - 1 - count] == right[right.Length - 1 - count]) count++;
        return count;
    }

    private static bool IsLikelyDocumentReplacement(string oldText, string newText)
    {
        if (oldText.Length == 0 || newText.Length == 0) return true;
        var prefix = CommonPrefixLength(oldText, newText);
        var suffix = CommonSuffixLength(oldText, newText, prefix);
        var shared = prefix + suffix;
        var larger = Math.Max(oldText.Length, newText.Length);
        return larger > 0 && shared < larger * 0.35;
    }

    private void Redraw() => TextArea.TextView.Redraw();

    private ContextMenu BuildContextMenu()
    {
        var typewriter = new MenuItem { Header = "Typewriter Scrolling", ToggleType = MenuItemToggleType.CheckBox };
        var focus = new MenuItem { Header = "Focus Current Paragraph", ToggleType = MenuItemToggleType.CheckBox };
        var markdown = new MenuItem { Header = "Show Markdown Marks", ToggleType = MenuItemToggleType.CheckBox };
        var spelling = new MenuItem { Header = "Spelling Indicators", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };

        typewriter.Click += (_, _) => TypewriterScrolling = typewriter.IsChecked == true;
        focus.Click += (_, _) => FocusCurrentParagraph = focus.IsChecked == true;
        markdown.Click += (_, _) =>
        {
            ShowMarkdownMarks = markdown.IsChecked == true;
            Redraw();
        };
        spelling.Click += (_, _) =>
        {
            SpellIndicatorsEnabled = spelling.IsChecked == true;
            QueueDiagnostics();
        };

        ContextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                typewriter,
                focus,
                markdown,
                spelling
            }
        };
        return ContextMenu;
    }

    private void RefreshContextMenuChecks()
    {
        if (ContextMenu?.ItemsSource is not IEnumerable<object> items) return;
        foreach (var item in items.OfType<MenuItem>())
        {
            var header = item.Header?.ToString();
            if (header == "Typewriter Scrolling") item.IsChecked = _userTypewriterScrolling;
            else if (header == "Focus Current Paragraph") item.IsChecked = _userFocusMode;
        }
    }

    private sealed class ManuscriptColorizer(ManuscriptEditor owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            if (lineEnd <= lineStart) return;

            if (!owner.IsFocusedLine(line.LineNumber))
            {
                ChangeLinePart(lineStart, lineEnd, element => element.TextRunProperties.SetForegroundBrush(FocusDimBrush));
            }

            if (owner._annotations.ContainsKey(line.LineNumber))
            {
                ChangeLinePart(lineStart, lineEnd, element => element.TextRunProperties.SetBackgroundBrush(AnnotationBrush));
            }

            var text = CurrentContext.Document.GetText(line);
            ApplyMarkdown(line, text);
            ApplyDiagnostics(lineStart, lineEnd);
            ApplyRevisions(lineStart, lineEnd);
        }

        private void ApplyMarkdown(DocumentLine line, string text)
        {
            var lineStart = line.Offset;
            var caretLine = owner.IsCaretLine(line.LineNumber);
            var markerBrush = owner.ShowMarkdownMarks || caretLine ? MarkdownMarkBrush : HiddenMarkdownMarkBrush;

            var headingMarks = 0;
            while (headingMarks < text.Length && headingMarks < 6 && text[headingMarks] == '#') headingMarks++;
            if (headingMarks > 0 && headingMarks < text.Length && text[headingMarks] == ' ')
            {
                ChangeLinePart(lineStart, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(HeadingBrush);
                    element.TextRunProperties.SetTypeface(new Typeface(owner.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                });
                ChangeLinePart(lineStart, lineStart + headingMarks + 1, element => element.TextRunProperties.SetForegroundBrush(markerBrush));
            }
            else if (text.StartsWith("> ", StringComparison.Ordinal))
            {
                ChangeLinePart(lineStart, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(QuoteBrush));
                ChangeLinePart(lineStart, Math.Min(line.EndOffset, lineStart + 2), element => element.TextRunProperties.SetForegroundBrush(markerBrush));
            }
            else if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
            {
                ChangeLinePart(lineStart, Math.Min(line.EndOffset, lineStart + 2), element => element.TextRunProperties.SetForegroundBrush(markerBrush));
            }

            ApplyPairedMarker(lineStart, text, "**", markerBrush, FontWeight.SemiBold, FontStyle.Normal);
            ApplyPairedMarker(lineStart, text, "*", markerBrush, FontWeight.Normal, FontStyle.Italic);
            ApplyInlineCode(lineStart, text, markerBrush);
        }

        private void ApplyPairedMarker(int lineStart, string text, string marker, IBrush markerBrush, FontWeight weight, FontStyle style)
        {
            var search = 0;
            while (search < text.Length)
            {
                var open = text.IndexOf(marker, search, StringComparison.Ordinal);
                if (open < 0) break;
                var contentStart = open + marker.Length;
                var close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
                if (close < 0) break;

                var openStart = lineStart + open;
                var openEnd = Math.Min(lineStart + text.Length, openStart + marker.Length);
                var closeStart = lineStart + close;
                var closeEnd = Math.Min(lineStart + text.Length, closeStart + marker.Length);
                ChangeLinePart(openStart, openEnd, element => element.TextRunProperties.SetForegroundBrush(markerBrush));
                ChangeLinePart(closeStart, closeEnd, element => element.TextRunProperties.SetForegroundBrush(markerBrush));
                if (closeStart > openEnd)
                {
                    ChangeLinePart(openEnd, closeStart, element =>
                    {
                        element.TextRunProperties.SetTypeface(new Typeface(owner.FontFamily, style, weight));
                    });
                }
                search = close + marker.Length;
            }
        }

        private void ApplyInlineCode(int lineStart, string text, IBrush markerBrush)
        {
            var search = 0;
            while (search < text.Length)
            {
                var open = text.IndexOf('`', search);
                if (open < 0) break;
                var close = text.IndexOf('`', open + 1);
                if (close < 0) break;
                ChangeLinePart(lineStart + open, lineStart + open + 1, element => element.TextRunProperties.SetForegroundBrush(markerBrush));
                if (close > open + 1)
                    ChangeLinePart(lineStart + open + 1, lineStart + close, element => element.TextRunProperties.SetForegroundBrush(CodeBrush));
                ChangeLinePart(lineStart + close, lineStart + close + 1, element => element.TextRunProperties.SetForegroundBrush(markerBrush));
                search = close + 1;
            }
        }

        private void ApplyDiagnostics(int lineStart, int lineEnd)
        {
            if (!owner.SpellIndicatorsEnabled || owner._diagnostics.Count == 0) return;
            foreach (var diagnostic in owner._diagnostics)
            {
                var start = Math.Max(lineStart, diagnostic.Offset);
                var end = Math.Min(lineEnd, diagnostic.Offset + diagnostic.Length);
                if (end <= start) continue;
                ChangeLinePart(start, end, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(SpellingBrush);
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                });
            }
        }

        private void ApplyRevisions(int lineStart, int lineEnd)
        {
            if (owner._revisionSpans.Count == 0) return;
            foreach (var revision in owner._revisionSpans)
            {
                var start = Math.Max(lineStart, revision.Offset);
                var end = Math.Min(lineEnd, revision.Offset + revision.Length);
                if (end <= start) continue;
                var brush = RevisionBrushes[Math.Clamp(revision.Level, 1, 5)];
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
