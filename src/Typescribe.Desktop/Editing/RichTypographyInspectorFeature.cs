using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Character/paragraph typography inspector backed by Markdown-safe TypeScribe metadata.
/// System fonts are enumerated from Avalonia. Project/custom fonts are stored by family and
/// project-relative path so the manuscript stays portable and plain Markdown remains readable.
/// </summary>
internal sealed class RichTypographyInspectorFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly ComboBox _systemFont = new();
    private readonly TextBox _customFamily = new() { Watermark = "Custom font family (optional)" };
    private readonly TextBox _projectFontPath = new() { Watermark = "fonts/MyFont.ttf (project-relative)" };
    private readonly TextBox _fontSize = new() { Text = "11" };
    private readonly ComboBox _language = new();
    private readonly ComboBox _script = new();
    private readonly ComboBox _direction = new();
    private readonly CheckBox _bold = new() { Content = "Bold" };
    private readonly CheckBox _italic = new() { Content = "Italic" };
    private readonly CheckBox _underline = new() { Content = "Underline" };
    private readonly CheckBox _smallCaps = new() { Content = "Small caps" };
    private readonly CheckBox _ligatures = new() { Content = "Ligatures", IsChecked = true };
    private readonly CheckBox _kerning = new() { Content = "Kerning", IsChecked = true };
    private readonly TextBox _tracking = new() { Text = "0", Watermark = "em" };
    private readonly TextBox _features = new() { Watermark = "liga=1, kern=1, ss01=1" };
    private readonly TextBox _axes = new() { Watermark = "wght=500, wdth=100" };
    private readonly TextBlock _preview = new()
    {
        Text = "Aa — اردو — العربية — فارسی",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 20,
        Margin = new Thickness(8)
    };
    private readonly TextBlock _status = new()
    {
        Text = "Select text for character formatting, or place the caret in a paragraph.",
        TextWrapping = TextWrapping.Wrap,
        Opacity = .68,
        FontSize = 10.5
    };

    private ManuscriptEditor? _editor;
    private TabControl? _inspectorTabs;
    private RichMetadataElementGenerator? _metadataGenerator;
    private RichMetadataColorizer? _metadataColorizer;
    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private RichTypographyInspectorFeature(StudioWorkspaceWindow window)
    {
        _window = window;
        ConfigureInputs();
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new RichTypographyInspectorFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.QueueInstall();
    }

    private void ConfigureInputs()
    {
        _systemFont.ItemsSource = FontManager.Current.SystemFonts
            .OrderBy(static family => family.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _systemFont.SelectedItem = FontManager.Current.DefaultFontFamily;

        _language.ItemsSource = new[]
        {
            new Choice("Auto", string.Empty),
            new Choice("English", "en"),
            new Choice("Arabic — العربية", "ar"),
            new Choice("Urdu — اردو", "ur-PK"),
            new Choice("Persian — فارسی", "fa-IR")
        };
        _language.SelectedIndex = 0;

        _script.ItemsSource = Enum.GetValues<ScriptMode>();
        _script.SelectedItem = ScriptMode.Auto;
        _direction.ItemsSource = Enum.GetValues<TextDirectionMode>();
        _direction.SelectedItem = TextDirectionMode.Auto;

        _systemFont.SelectionChanged += (_, _) => RefreshPreview();
        _customFamily.TextChanged += (_, _) => RefreshPreview();
        _fontSize.TextChanged += (_, _) => RefreshPreview();
        _bold.IsCheckedChanged += (_, _) => RefreshPreview();
        _italic.IsCheckedChanged += (_, _) => RefreshPreview();
        _direction.SelectionChanged += (_, _) => RefreshPreview();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueInstall();
    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) QueueInstall();
    }

    private void QueueInstall()
    {
        if (_disposed || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (!_disposed) TryInstall();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        _editor ??= controls.OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.Parent is Grid grid && grid.Classes.Contains("long-form-editor-host"));

        foreach (var tabs in controls.OfType<TabControl>())
        {
            var items = GetTabItems(tabs);
            if (items.Any(static item => string.Equals(item.Header?.ToString(), "Inspector", StringComparison.Ordinal)))
            {
                _inspectorTabs = tabs;
                break;
            }
        }

        if (_editor is null || _inspectorTabs is null) return;

        var tabsNow = GetTabItems(_inspectorTabs);
        if (!tabsNow.Any(static item => string.Equals(item.Header?.ToString(), "Typography", StringComparison.Ordinal)))
        {
            tabsNow.Add(new TabItem { Header = "Typography", Content = BuildInspector() });
            _inspectorTabs.ItemsSource = tabsNow.ToArray();
        }

        _metadataGenerator = new RichMetadataElementGenerator(_editor);
        _metadataColorizer = new RichMetadataColorizer(_editor);
        _editor.TextArea.TextView.ElementGenerators.Add(_metadataGenerator);
        _editor.TextArea.TextView.LineTransformers.Add(_metadataColorizer);
        _editor.TextChanged += EditorTextChanged;
        _installed = true;
        RefreshPreview();
    }

    private Control BuildInspector()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        panel.Children.Add(Section("FONT"));
        panel.Children.Add(Labelled("System font", _systemFont));
        panel.Children.Add(Labelled("Custom family", _customFamily));

        var browse = new Button { Content = "Choose font…", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += async (_, _) => await ChooseFontAsync();
        var custom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        custom.Children.Add(_projectFontPath);
        Grid.SetColumn(browse, 1);
        custom.Children.Add(browse);
        panel.Children.Add(Labelled("Project font", custom));
        panel.Children.Add(Labelled("Size (pt)", _fontSize));

        panel.Children.Add(Section("LANGUAGE & DIRECTION"));
        panel.Children.Add(Labelled("Language", _language));
        panel.Children.Add(Labelled("Script", _script));
        panel.Children.Add(Labelled("Paragraph direction", _direction));

        panel.Children.Add(Section("CHARACTER"));
        panel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _bold, _italic, _underline, _smallCaps, _ligatures, _kerning }
        });
        panel.Children.Add(Labelled("Tracking", _tracking));
        panel.Children.Add(Labelled("OpenType", _features));
        panel.Children.Add(Labelled("Variable axes", _axes));

        var previewBorder = new Border
        {
            Child = _preview,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2),
            MinHeight = 72
        };
        panel.Children.Add(previewBorder);

        var load = new Button { Content = "Read current" };
        var selection = new Button { Content = "Apply to selection" };
        var paragraph = new Button { Content = "Apply to paragraph" };
        load.Click += (_, _) => LoadCurrent();
        selection.Click += (_, _) => ApplySelection();
        paragraph.Click += (_, _) => ApplyParagraph();
        panel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { load, selection, paragraph }
        });
        panel.Children.Add(_status);
        panel.Children.Add(new TextBlock
        {
            Text = "RTL note: language/script metadata is preserved now. True mixed-direction paragraph layout requires the AvaloniaEdit BiDi engine hook described in docs/RICH_DOCUMENT_MODEL.md.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Opacity = .58
        });

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private static TextBlock Section(string text)
        => new()
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            Opacity = .62,
            Margin = new Thickness(0, 7, 0, 0)
        };

    private static Control Labelled(string label, Control control)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 3 };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Opacity = .7 });
        Grid.SetRow(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private async Task ChooseFontAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose project font",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("OpenType fonts") { Patterns = ["*.otf", "*.ttf", "*.ttc", "*.woff2"] }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _projectFontPath.Text = path;
        _status.Text = "Font selected. For portable projects, copy it into the project and store a project-relative path before packaging.";
    }

    private CharacterFormatting ReadControls()
    {
        var selected = _systemFont.SelectedItem as FontFamily;
        var family = !string.IsNullOrWhiteSpace(_customFamily.Text)
            ? _customFamily.Text!.Trim()
            : selected?.Name ?? FontManager.Current.DefaultFontFamily.Name;
        var projectPath = Clean(_projectFontPath.Text);
        var font = new FontReference(
            family,
            projectPath is null ? FontSourceKind.System : FontSourceKind.Project,
            projectPath);

        var size = ParseDouble(_fontSize.Text);
        var language = (_language.SelectedItem as Choice)?.Value;
        var script = _script.SelectedItem is ScriptMode scriptValue ? scriptValue : ScriptMode.Auto;
        var direction = _direction.SelectedItem is TextDirectionMode directionValue ? directionValue : TextDirectionMode.Auto;

        return new CharacterFormatting(
            Font: font,
            FontSizePoints: size,
            Bold: _bold.IsChecked == true,
            Italic: _italic.IsChecked == true,
            Underline: _underline.IsChecked == true,
            SmallCaps: _smallCaps.IsChecked == true,
            Ligatures: _ligatures.IsChecked != false,
            Kerning: _kerning.IsChecked != false,
            TrackingEm: ParseDouble(_tracking.Text),
            Language: Clean(language),
            Script: script,
            Direction: direction,
            OpenTypeFeatures: ParseFeatures(_features.Text),
            VariableAxes: ParseAxes(_axes.Text));
    }

    private void ApplySelection()
    {
        if (_editor is null || _editor.IsReadOnly) return;
        if (_editor.SelectionLength <= 0)
        {
            _status.Text = "Select text first, then apply character formatting.";
            return;
        }

        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        var selected = _editor.Document.GetText(start, length);
        var formatting = ReadControls();
        var open = RichMarkdownFormattingCodec.CreateInlineOpen(formatting);
        var wrapped = open + selected + RichMarkdownFormattingCodec.InlineClose;
        _editor.Document.Replace(start, length, wrapped);
        _editor.Select(start + open.Length, selected.Length);
        _editor.CaretOffset = start + open.Length + selected.Length;
        _editor.Focus();
        _status.Text = "Character formatting stored as Markdown-safe inline metadata.";
        Redraw();
    }

    private void ApplyParagraph()
    {
        if (_editor is null || _editor.IsReadOnly || _editor.Document.LineCount == 0) return;
        var caret = Math.Clamp(_editor.CaretOffset, 0, Math.Max(0, _editor.Document.TextLength));
        if (caret == _editor.Document.TextLength && caret > 0) caret--;
        var line = _editor.Document.GetLineByOffset(caret);
        var character = ReadControls();
        var direction = _direction.SelectedItem is TextDirectionMode d ? d : TextDirectionMode.Auto;
        var language = (_language.SelectedItem as Choice)?.Value;
        var script = _script.SelectedItem is ScriptMode s ? s : ScriptMode.Auto;
        var paragraph = new ParagraphFormatting(
            CharacterDefaults: character,
            Direction: direction,
            Language: Clean(language),
            Script: script);
        var metadata = RichMarkdownFormattingCodec.CreateBlockMetadata(new RichBlockFormatting(Paragraph: paragraph));

        var previous = line.PreviousLine;
        if (previous is not null)
        {
            var previousText = _editor.Document.GetText(previous.Offset, previous.Length);
            if (RichMarkdownFormattingCodec.IsBlockMetadata(previousText))
            {
                _editor.Document.Replace(previous.Offset, previous.Length, metadata);
                _status.Text = "Paragraph formatting updated.";
                Redraw();
                return;
            }
        }

        var insertion = metadata + Environment.NewLine;
        _editor.Document.Insert(line.Offset, insertion);
        _editor.CaretOffset = caret + insertion.Length;
        _editor.Focus();
        _status.Text = "Paragraph formatting stored in a hidden Markdown metadata line.";
        Redraw();
    }

    private void LoadCurrent()
    {
        if (_editor is null || _editor.Document.TextLength == 0) return;
        var caret = Math.Clamp(_editor.CaretOffset, 0, _editor.Document.TextLength);
        var lookup = Math.Min(caret, Math.Max(0, _editor.Document.TextLength - 1));
        var line = _editor.Document.GetLineByOffset(lookup);
        var text = _editor.Document.GetText(line.Offset, line.Length);
        var relative = Math.Clamp(caret - line.Offset, 0, text.Length);

        if (TryInlineAt(text, relative, out var inline) && inline is not null)
        {
            Populate(inline);
            _status.Text = "Loaded character formatting at the caret.";
            return;
        }

        var previous = line.PreviousLine;
        if (previous is not null)
        {
            var previousText = _editor.Document.GetText(previous.Offset, previous.Length);
            if (RichMarkdownFormattingCodec.TryReadBlockMetadata(previousText, out var block) &&
                block?.Paragraph is { } paragraph)
            {
                Populate(paragraph.CharacterDefaults ?? new CharacterFormatting(
                    Language: paragraph.Language,
                    Script: paragraph.Script,
                    Direction: paragraph.Direction));
                if (paragraph.Direction is not null) _direction.SelectedItem = paragraph.Direction.Value;
                if (paragraph.Script is not null) _script.SelectedItem = paragraph.Script.Value;
                SelectLanguage(paragraph.Language);
                _status.Text = "Loaded paragraph formatting.";
                return;
            }
        }

        _status.Text = "No TypeScribe rich formatting metadata at the caret.";
    }

    private void Populate(CharacterFormatting formatting)
    {
        if (formatting.Font is { } font)
        {
            _customFamily.Text = font.Source == FontSourceKind.System ? string.Empty : font.Family;
            _projectFontPath.Text = font.ProjectPath ?? string.Empty;
            if (font.Source == FontSourceKind.System)
            {
                var match = FontManager.Current.SystemFonts.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, font.Family, StringComparison.CurrentCultureIgnoreCase));
                if (match is not null) _systemFont.SelectedItem = match;
            }
        }
        if (formatting.FontSizePoints is { } size) _fontSize.Text = size.ToString("0.##", CultureInfo.InvariantCulture);
        _bold.IsChecked = formatting.Bold == true;
        _italic.IsChecked = formatting.Italic == true;
        _underline.IsChecked = formatting.Underline == true;
        _smallCaps.IsChecked = formatting.SmallCaps == true;
        _ligatures.IsChecked = formatting.Ligatures != false;
        _kerning.IsChecked = formatting.Kerning != false;
        if (formatting.TrackingEm is { } tracking) _tracking.Text = tracking.ToString("0.###", CultureInfo.InvariantCulture);
        if (formatting.Script is { } script) _script.SelectedItem = script;
        if (formatting.Direction is { } direction) _direction.SelectedItem = direction;
        SelectLanguage(formatting.Language);
        _features.Text = FormatFeatures(formatting.OpenTypeFeatures);
        _axes.Text = FormatAxes(formatting.VariableAxes);
        RefreshPreview();
    }

    private void SelectLanguage(string? language)
    {
        var items = (_language.ItemsSource as IEnumerable)?.Cast<object>().OfType<Choice>().ToArray() ?? [];
        var selected = items.FirstOrDefault(item => string.Equals(item.Value, language ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) _language.SelectedItem = selected;
    }

    private void RefreshPreview()
    {
        var family = !string.IsNullOrWhiteSpace(_customFamily.Text)
            ? _customFamily.Text!.Trim()
            : (_systemFont.SelectedItem as FontFamily)?.Name;
        if (!string.IsNullOrWhiteSpace(family)) _preview.FontFamily = new FontFamily(family);
        if (ParseDouble(_fontSize.Text) is { } size && size > 0) _preview.FontSize = Math.Clamp(size, 6, 96);
        _preview.FontWeight = _bold.IsChecked == true ? FontWeight.Bold : FontWeight.Normal;
        _preview.FontStyle = _italic.IsChecked == true ? FontStyle.Italic : FontStyle.Normal;
        var direction = _direction.SelectedItem is TextDirectionMode value ? value : TextDirectionMode.Auto;
        _preview.TextAlignment = direction == TextDirectionMode.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
    }

    private void EditorTextChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        _metadataGenerator?.Invalidate();
        _editor?.TextArea.TextView.Redraw();
    }

    private static bool TryInlineAt(string text, int relative, out CharacterFormatting? formatting)
    {
        formatting = null;
        var search = 0;
        while (search < text.Length)
        {
            var open = text.IndexOf(RichMarkdownFormattingCodec.InlinePrefix, search, StringComparison.Ordinal);
            if (open < 0) return false;
            var openEnd = text.IndexOf(" -->", open, StringComparison.Ordinal);
            if (openEnd < 0) return false;
            openEnd += 4;
            var close = text.IndexOf(RichMarkdownFormattingCodec.InlineClose, openEnd, StringComparison.Ordinal);
            if (close < 0) return false;
            if (relative >= openEnd && relative <= close)
            {
                var marker = text[open..openEnd];
                return RichMarkdownFormattingCodec.TryReadInlineOpen(marker, out formatting);
            }
            search = close + RichMarkdownFormattingCodec.InlineClose.Length;
        }
        return false;
    }

    private static IReadOnlyList<OpenTypeFeatureSetting>? ParseFeatures(string? text)
    {
        var values = ParseTagValues(text)
            .Select(static item => new OpenTypeFeatureSetting(item.Tag, (int)Math.Round(item.Value)))
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static IReadOnlyList<VariableFontAxisSetting>? ParseAxes(string? text)
    {
        var values = ParseTagValues(text)
            .Select(static item => new VariableFontAxisSetting(item.Tag, item.Value))
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static IReadOnlyList<(string Tag, double Value)> ParseTagValues(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<(string Tag, double Value)>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
            var tag = pieces[0].Trim();
            if (tag.Length != 4) continue;
            var value = pieces.Length == 1 ? 1 : ParseDouble(pieces[1]) ?? 1;
            result.Add((tag, value));
        }
        return result;
    }

    private static string FormatFeatures(IReadOnlyList<OpenTypeFeatureSetting>? values)
        => values is null ? string.Empty : string.Join(", ", values.Select(static value => $"{value.Tag}={value.Value}"));

    private static string FormatAxes(IReadOnlyList<VariableFontAxisSetting>? values)
        => values is null ? string.Empty : string.Join(", ", values.Select(static value => $"{value.Tag}={value.Value.ToString(CultureInfo.InvariantCulture)}"));

    private static double? ParseDouble(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Clean(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<TabItem> GetTabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable source
            ? source.Cast<object>().OfType<TabItem>().ToList()
            : [];

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            if (_metadataGenerator is not null)
                _editor.TextArea.TextView.ElementGenerators.Remove(_metadataGenerator);
            if (_metadataColorizer is not null)
                _editor.TextArea.TextView.LineTransformers.Remove(_metadataColorizer);
        }
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private sealed record Choice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>Visually removes TypeScribe-only metadata tokens while keeping source offsets stable.</summary>
    private sealed class RichMetadataElementGenerator(ManuscriptEditor editor) : VisualLineElementGenerator
    {
        private const string CommentEnd = " -->";
        private int _version;

        public void Invalidate() => _version++;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            _ = _version;
            var document = CurrentContext.Document;
            var text = document.Text;
            var search = Math.Max(0, startOffset);
            while (search < text.Length)
            {
                var block = text.IndexOf(RichMarkdownFormattingCodec.BlockPrefix, search, StringComparison.Ordinal);
                var inline = text.IndexOf(RichMarkdownFormattingCodec.InlinePrefix, search, StringComparison.Ordinal);
                var close = text.IndexOf(RichMarkdownFormattingCodec.InlineClose, search, StringComparison.Ordinal);
                var table = text.IndexOf(TableMarkupCodec.MetadataPrefix, search, StringComparison.Ordinal);
                var next = Smallest(block, inline, close, table);
                if (next < 0) return -1;
                if (TryGetHiddenTokenLength(document, text, next, out _)) return next;
                search = next + 1;
            }

            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var document = CurrentContext.Document;
            if (!TryGetHiddenTokenLength(document, document.Text, offset, out var length))
                return null;

            return new InlineObjectElement(length, new Border
            {
                Width = 0,
                Height = 0,
                MinWidth = 0,
                MinHeight = 0,
                IsHitTestVisible = false
            });
        }

        private static bool TryGetHiddenTokenLength(TextDocument document, string text, int offset, out int length)
        {
            length = 0;
            if (offset < 0 || offset >= text.Length) return false;
            if (text.AsSpan(offset).StartsWith(RichMarkdownFormattingCodec.InlineClose.AsSpan(), StringComparison.Ordinal))
            {
                length = RichMarkdownFormattingCodec.InlineClose.Length;
            }
            else if (text.AsSpan(offset).StartsWith(RichMarkdownFormattingCodec.BlockPrefix.AsSpan(), StringComparison.Ordinal) ||
                     text.AsSpan(offset).StartsWith(RichMarkdownFormattingCodec.InlinePrefix.AsSpan(), StringComparison.Ordinal) ||
                     text.AsSpan(offset).StartsWith(TableMarkupCodec.MetadataPrefix.AsSpan(), StringComparison.Ordinal))
            {
                var end = text.IndexOf(CommentEnd, offset, StringComparison.Ordinal);
                if (end < 0) return false;
                length = end + CommentEnd.Length - offset;
            }
            else
            {
                return false;
            }

            var line = document.GetLineByOffset(offset);
            return offset + length <= line.EndOffset;
        }

        private static int Smallest(params int[] values)
        {
            var result = int.MaxValue;
            foreach (var value in values)
                if (value >= 0 && value < result) result = value;
            return result == int.MaxValue ? -1 : result;
        }
    }

    /// <summary>
    /// Applies the subset of rich metadata AvaloniaEdit can render safely today. Direction is
    /// intentionally not faked here: AvaloniaEdit's paragraph properties are currently LTR-only.
    /// </summary>
    private sealed class RichMetadataColorizer(ManuscriptEditor editor) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length <= 0) return;
            var text = CurrentContext.Document.GetText(line.Offset, line.Length);
            if (RichMarkdownFormattingCodec.IsBlockMetadata(text) ||
                text.TrimStart().StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal))
            {
                ChangeLinePart(line.Offset, line.EndOffset, static element =>
                {
                    element.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
                    element.TextRunProperties.SetFontRenderingEmSize(1);
                });
                return;
            }

            var previous = line.PreviousLine;
            if (previous is not null)
            {
                var previousText = CurrentContext.Document.GetText(previous.Offset, previous.Length);
                if (RichMarkdownFormattingCodec.TryReadBlockMetadata(previousText, out var block) &&
                    block?.Paragraph?.CharacterDefaults is { } defaults)
                {
                    ChangeLinePart(line.Offset, line.EndOffset, element => Apply(element, defaults));
                }
            }

            var search = 0;
            while (search < text.Length)
            {
                var open = text.IndexOf(RichMarkdownFormattingCodec.InlinePrefix, search, StringComparison.Ordinal);
                if (open < 0) break;
                var openEnd = text.IndexOf(" -->", open, StringComparison.Ordinal);
                if (openEnd < 0) break;
                openEnd += 4;
                var close = text.IndexOf(RichMarkdownFormattingCodec.InlineClose, openEnd, StringComparison.Ordinal);
                if (close < 0) break;
                var marker = text[open..openEnd];
                if (RichMarkdownFormattingCodec.TryReadInlineOpen(marker, out var formatting) && formatting is not null && close > openEnd)
                    ChangeLinePart(line.Offset + openEnd, line.Offset + close, element => Apply(element, formatting));
                search = close + RichMarkdownFormattingCodec.InlineClose.Length;
            }
        }

        private static void Apply(VisualLineElement element, CharacterFormatting formatting)
        {
            var family = formatting.Font?.Family;
            var current = element.TextRunProperties.Typeface;
            var style = formatting.Italic == true ? FontStyle.Italic : current.Style;
            var weight = formatting.Bold == true ? FontWeight.Bold : current.Weight;
            if (!string.IsNullOrWhiteSpace(family))
                element.TextRunProperties.SetTypeface(new Typeface(new FontFamily(family), style, weight));
            else if (formatting.Bold is not null || formatting.Italic is not null)
                element.TextRunProperties.SetTypeface(new Typeface(current.FontFamily, style, weight));
            if (formatting.FontSizePoints is { } size && size > 0)
                element.TextRunProperties.SetFontRenderingEmSize(Math.Clamp(size, 5, 144));
        }
    }
}
