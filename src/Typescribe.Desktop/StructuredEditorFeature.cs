using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;
using ToolTip = Avalonia.Controls.ToolTip;

namespace Typescribe.Desktop;

internal sealed class StructuredEditorFeature
{
    private readonly Window _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly ManuscriptEditor _editor;
    private readonly AssetManagerService _assets;
    private readonly BibTeXDatabase _bibliography;
    private readonly IDocumentParser _parser;

    private StructuredEditorFeature(
        Window window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        ManuscriptEditor editor,
        AssetManagerService assets,
        BibTeXDatabase bibliography,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _editor = editor;
        _assets = assets;
        _bibliography = bibliography;
        _parser = parser;
    }

    public static void Install(
        Window window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        ManuscriptEditor editor,
        AssetManagerService assets,
        BibTeXDatabase bibliography,
        IDocumentParser parser)
    {
        if (window.GetVisualDescendants().OfType<Button>().Any(static button => string.Equals(button.Name, "StructuredTableButton", StringComparison.Ordinal))) return;
        var feature = new StructuredEditorFeature(window, viewModel, repository, editor, assets, bibliography, parser);
        feature.InstallToolbar();
    }

    private void InstallToolbar()
    {
        var old = _window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Content?.ToString() is "Table" or "Figure" or "Cite")
            .ToArray();
        var host = old.FirstOrDefault()?.Parent as Panel;
        foreach (var button in old) button.IsVisible = false;
        if (host is null) return;

        host.Children.Add(Button("Table", "Create or visually edit the table at the caret", EditTableAsync, "StructuredTableButton"));
        host.Children.Add(Button("Figure", "Insert a figure from Project Assets", InsertFigureAsync, "StructuredFigureButton"));
        host.Children.Add(Button("Cross-ref", "Insert a semantic cross-reference", InsertCrossReferenceAsync, "StructuredReferenceButton"));
        host.Children.Add(Button("Cite", "Search project references and insert a citation", InsertCitationAsync, "StructuredCitationButton"));
        host.Children.Add(Button("ID", "Assign an identifier to the current heading", AssignHeadingIdentifierAsync, "StructuredIdentifierButton"));
        host.Children.Add(Button("Equation", "Insert a labeled display equation", InsertEquationAsync, "StructuredEquationButton"));
    }

    private async Task EditTableAsync()
    {
        if (!_viewModel.HasDocument) return;
        var span = FindCurrentTableSpan();
        TableEditResult seed;
        if (span is not null)
        {
            var ast = _parser.Parse(span.Text);
            var table = ast.Blocks.OfType<TableBlock>().FirstOrDefault();
            if (table is null) return;
            var rows = new List<IReadOnlyList<string>> { table.Header.Select(static cell => cell.Inlines.ToPlainText()).ToArray() };
            rows.AddRange(table.Rows.Select(static row => (IReadOnlyList<string>)row.Select(static cell => cell.Inlines.ToPlainText()).ToArray()));
            seed = new TableEditResult(rows, HeaderRow: true, table.Alignments ?? [], table.Caption ?? string.Empty, table.Identifier ?? string.Empty);
        }
        else if (_editor.SelectionLength > 0)
        {
            var selected = _editor.Document.GetText(_editor.SelectionStart, _editor.SelectionLength);
            seed = SeedFromSelection(selected);
        }
        else
        {
            seed = new TableEditResult(
                [new[] { "Heading 1", "Heading 2", "Heading 3" }, new[] { "Cell", "Cell", "Cell" }, new[] { "Cell", "Cell", "Cell" }],
                HeaderRow: true,
                [TableAlignment.Default, TableAlignment.Default, TableAlignment.Default],
                string.Empty,
                string.Empty);
        }

        var result = await TableEditorWindow.ShowAsync(_window, seed);
        if (result is null) return;
        var markup = SerializeTable(result);
        if (span is not null)
        {
            _editor.Document.Replace(span.Offset, span.Length, markup);
            _editor.Select(span.Offset, markup.Length);
        }
        else if (_editor.SelectionLength > 0)
        {
            var offset = _editor.SelectionStart;
            _editor.Document.Replace(offset, _editor.SelectionLength, markup);
            _editor.Select(offset, markup.Length);
        }
        else
        {
            InsertBlock(markup);
        }
        _editor.Focus();
    }

    private async Task InsertFigureAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null || !_viewModel.HasDocument) return;
        var assets = (await _assets.ListAsync(project)).Where(static asset => !asset.Missing).ToArray();
        if (assets.Length == 0)
        {
            await AssetManagerWindow.ShowAsync(_window, project, _assets);
            assets = (await _assets.ListAsync(project)).Where(static asset => !asset.Missing).ToArray();
            if (assets.Length == 0) return;
        }

        var rows = assets.Select(static asset => new AssetChoice(asset)).ToArray();
        var picker = new ComboBox { ItemsSource = rows, SelectedIndex = 0 };
        var caption = new TextBox { Watermark = "Caption" };
        var identifier = new TextBox { Watermark = "Identifier, e.g. fig-network" };
        var chosen = await FormDialogAsync("Insert Figure", [("Asset", (Control)picker), ("Caption", caption), ("Identifier", identifier)]);
        if (!chosen || picker.SelectedItem is not AssetChoice selected) return;
        var suffix = string.IsNullOrWhiteSpace(identifier.Text) ? string.Empty : $" {{#{identifier.Text!.Trim()}}}";
        InsertBlock($"![{EscapeInline(caption.Text ?? string.Empty)}]({selected.Asset.RelativePath}){suffix}");
    }

    private async Task InsertCrossReferenceAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null || !_viewModel.HasDocument) return;
        var targets = new List<TargetChoice>();
        await foreach (var (node, content) in _repository.EnumerateDocumentsAsync(project))
        {
            var ast = _parser.Parse(content);
            foreach (var block in ast.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock { Identifier: not null } heading:
                        targets.Add(new TargetChoice(heading.Identifier, heading.Level == 1 ? ReferenceTargetKind.Chapter : ReferenceTargetKind.Heading, node.Title, heading.Inlines.ToPlainText()));
                        break;
                    case FigureBlock { Identifier: not null } figure:
                        targets.Add(new TargetChoice(figure.Identifier, ReferenceTargetKind.Figure, node.Title, figure.Caption));
                        break;
                    case TableBlock { Identifier: not null } table:
                        targets.Add(new TargetChoice(table.Identifier, ReferenceTargetKind.Table, node.Title, table.Caption ?? "Table"));
                        break;
                    case DisplayMathBlock { Identifier: not null } equation:
                        targets.Add(new TargetChoice(equation.Identifier, ReferenceTargetKind.Equation, node.Title, equation.Text));
                        break;
                }
            }
        }
        if (targets.Count == 0) return;
        var selection = await SearchChoiceAsync("Insert Cross-reference", targets.OrderBy(static item => item.Identifier, StringComparer.OrdinalIgnoreCase).ToArray());
        if (selection is TargetChoice target) InsertText($"[@ref:{target.Identifier}]");
    }

    private async Task InsertCitationAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null || !_viewModel.HasDocument) return;
        var entries = await _bibliography.LoadAsync(project.RootPath);
        if (entries.Count == 0)
        {
            await BibliographyManagerWindow.ShowAsync(_window, project, _bibliography);
            entries = await _bibliography.LoadAsync(project.RootPath);
            if (entries.Count == 0) return;
        }
        var choices = entries.Select(static entry => new CitationChoice(entry)).ToArray();
        var selected = await SearchChoiceAsync("Insert Citation", choices);
        if (selected is not CitationChoice citation) return;
        var locator = await PromptAsync("Citation Locator", "Optional locator (for example p. 42)", string.Empty);
        var suffix = string.IsNullOrWhiteSpace(locator) ? string.Empty : ", " + locator.Trim();
        InsertText($"[@{citation.Entry.CitationKey}{suffix}]");
    }

    private async Task AssignHeadingIdentifierAsync()
    {
        if (!_viewModel.HasDocument) return;
        var line = _editor.Document.GetLineByOffset(_editor.CaretIndex);
        var text = _editor.Document.GetText(line.Offset, line.Length);
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('#')) return;
        var id = await PromptAsync("Heading Identifier", "Stable identifier", ExtractIdentifier(text) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(id)) return;
        var clean = RemoveIdentifier(text).TrimEnd();
        var replacement = clean + $" {{#{id.Trim()}}}";
        _editor.Document.Replace(line.Offset, line.Length, replacement);
        _editor.CaretIndex = line.Offset + replacement.Length;
    }

    private async Task InsertEquationAsync()
    {
        if (!_viewModel.HasDocument) return;
        var equation = new TextBox { Watermark = "Equation, e.g. E = mc^2" };
        var identifier = new TextBox { Watermark = "Identifier, e.g. eq-energy" };
        if (!await FormDialogAsync("Insert Equation", [("Equation", (Control)equation), ("Identifier", identifier)])) return;
        if (string.IsNullOrWhiteSpace(equation.Text)) return;
        var suffix = string.IsNullOrWhiteSpace(identifier.Text) ? string.Empty : $" {{#{identifier.Text!.Trim()}}}";
        InsertBlock($"$$ {equation.Text!.Trim()} $${suffix}");
    }

    private TableSpan? FindCurrentTableSpan()
    {
        if (_editor.Document.TextLength == 0) return null;
        var line = _editor.Document.GetLineByOffset(Math.Clamp(_editor.CaretIndex, 0, _editor.Document.TextLength));
        var lineNumber = line.LineNumber;
        bool TableLine(int number)
        {
            if (number < 1 || number > _editor.Document.LineCount) return false;
            var candidate = _editor.Document.GetLineByNumber(number);
            return _editor.Document.GetText(candidate.Offset, candidate.Length).Contains('|');
        }

        if (!TableLine(lineNumber))
        {
            var currentText = _editor.Document.GetText(line.Offset, line.Length).Trim();
            if (currentText.StartsWith("<!-- typescribe:table ", StringComparison.Ordinal) && TableLine(lineNumber + 1)) lineNumber++;
            else return null;
        }

        var start = lineNumber;
        var end = lineNumber;
        while (TableLine(start - 1)) start--;
        while (TableLine(end + 1)) end++;
        if (start > 1)
        {
            var metadata = _editor.Document.GetLineByNumber(start - 1);
            var metadataText = _editor.Document.GetText(metadata.Offset, metadata.Length).Trim();
            if (metadataText.StartsWith("<!-- typescribe:table ", StringComparison.Ordinal)) start--;
        }
        var first = _editor.Document.GetLineByNumber(start);
        var last = _editor.Document.GetLineByNumber(end);
        var endOffset = last.EndOffset;
        var length = endOffset - first.Offset;
        var text = _editor.Document.GetText(first.Offset, length);
        return _parser.Parse(text).Blocks.OfType<TableBlock>().Any() ? new TableSpan(first.Offset, length, text) : null;
    }

    private static TableEditResult SeedFromSelection(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in lines)
        {
            var cells = line.Contains('\t')
                ? line.Split('\t').Select(static cell => cell.Trim()).ToArray()
                : line.Contains(',')
                    ? line.Split(',').Select(static cell => cell.Trim()).ToArray()
                    : [line.Trim()];
            rows.Add(cells);
        }
        if (rows.Count == 0) rows.Add([string.Empty, string.Empty]);
        var columns = rows.Max(static row => row.Count);
        rows = rows.Select(row => (IReadOnlyList<string>)row.Concat(Enumerable.Repeat(string.Empty, columns - row.Count)).ToArray()).ToList();
        return new TableEditResult(rows, HeaderRow: true, Enumerable.Repeat(TableAlignment.Default, columns).ToArray(), string.Empty, string.Empty);
    }

    private static string SerializeTable(TableEditResult table)
    {
        var rows = table.Cells.Select(static row => row.Select(EscapeCell).ToArray()).ToArray();
        var columns = rows.Length == 0 ? 1 : Math.Max(1, rows.Max(static row => row.Length));
        var header = table.HeaderRow && rows.Length > 0 ? rows[0] : Enumerable.Repeat(string.Empty, columns).ToArray();
        var body = table.HeaderRow ? rows.Skip(1) : rows;
        string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Concat(Enumerable.Repeat(string.Empty, Math.Max(0, columns - cells.Count()))).Take(columns)) + " |";
        var separator = Enumerable.Range(0, columns).Select(index => index < table.Alignments.Count ? table.Alignments[index] switch
        {
            TableAlignment.Left => ":---",
            TableAlignment.Center => ":---:",
            TableAlignment.Right => "---:",
            _ => "---"
        } : "---");
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(table.Caption) || !string.IsNullOrWhiteSpace(table.Identifier))
            lines.Add(AdvancedDocumentParser.CreateTableMetadata(table.Identifier, table.Caption));
        lines.Add(Row(header));
        lines.Add(Row(separator));
        lines.AddRange(body.Select(Row));
        return string.Join(Environment.NewLine, lines);
    }

    private void InsertBlock(string text)
    {
        var offset = _editor.CaretIndex;
        var prefix = offset > 0 && _editor.Document.GetCharAt(offset - 1) != '\n' ? Environment.NewLine + Environment.NewLine : string.Empty;
        var suffix = offset < _editor.Document.TextLength && _editor.Document.GetCharAt(offset) != '\n' ? Environment.NewLine + Environment.NewLine : Environment.NewLine;
        var insertion = prefix + text + suffix;
        _editor.Document.Insert(offset, insertion);
        _editor.CaretIndex = offset + prefix.Length + text.Length;
    }

    private void InsertText(string text)
    {
        var offset = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretIndex;
        if (_editor.SelectionLength > 0) _editor.Document.Replace(offset, _editor.SelectionLength, text);
        else _editor.Document.Insert(offset, text);
        _editor.CaretIndex = offset + text.Length;
    }

    private async Task<object?> SearchChoiceAsync<T>(string title, IReadOnlyList<T> choices) where T : class
    {
        var search = new TextBox { Watermark = "Search…", Margin = new Thickness(10) };
        var list = new ListBox { Margin = new Thickness(10, 0, 10, 0), ItemsSource = choices, SelectedIndex = choices.Count > 0 ? 0 : -1 };
        var insert = new Button { Content = "Insert", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), Children = { insert, cancel } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(search); Grid.SetRow(list, 1); root.Children.Add(list); Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 720, Height = 520, MinWidth = 480, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? string.Empty;
            list.ItemsSource = choices.Where(item => query.Length == 0 || item.ToString()!.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SelectedIndex = 0;
        };
        insert.Click += (_, _) => dialog.Close(list.SelectedItem);
        cancel.Click += (_, _) => dialog.Close(null);
        list.DoubleTapped += (_, _) => dialog.Close(list.SelectedItem);
        return await dialog.ShowDialog<object?>(_window);
    }

    private async Task<bool> FormDialogAsync(string title, IReadOnlyList<(string Label, Control Control)> fields)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", fields.Count + 1))), Margin = new Thickness(12) };
        for (var row = 0; row < fields.Count; row++)
        {
            var label = new TextBlock { Text = fields[row].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(label, row); grid.Children.Add(label);
            Grid.SetRow(fields[row].Control, row); Grid.SetColumn(fields[row].Control, 1); fields[row].Control.Margin = new Thickness(0, 3); grid.Children.Add(fields[row].Control);
        }
        var ok = new Button { Content = "Insert", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        Grid.SetRow(buttons, fields.Count); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 620, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(_window);
    }

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial };
        if (!await FormDialogAsync(title, [(label, (Control)box)])) return null;
        return box.Text;
    }

    private static Button Button(string label, string tip, Func<Task> action, string name)
    {
        var button = new Button { Name = name, Content = label, Height = 27, MinHeight = 27, MinWidth = 44, Padding = new Thickness(6, 1) };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static string EscapeCell(string value) => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    private static string EscapeInline(string value) => value.Replace("]", "\\]", StringComparison.Ordinal);

    private static string? ExtractIdentifier(string heading)
    {
        var trimmed = heading.TrimEnd();
        var start = trimmed.LastIndexOf(" {#", StringComparison.Ordinal);
        return start >= 0 && trimmed.EndsWith('}') ? trimmed[(start + 3)..^1].Trim() : null;
    }

    private static string RemoveIdentifier(string heading)
    {
        var trimmed = heading.TrimEnd();
        var start = trimmed.LastIndexOf(" {#", StringComparison.Ordinal);
        return start >= 0 && trimmed.EndsWith('}') ? trimmed[..start] : heading;
    }

    private sealed record TableSpan(int Offset, int Length, string Text);
    private sealed record AssetChoice(ProjectAsset Asset)
    {
        public override string ToString() => $"{Asset.FileName} — {Asset.Dimensions} — {Asset.RelativePath}";
    }
    private sealed record TargetChoice(string Identifier, ReferenceTargetKind Kind, string Document, string Label)
    {
        public override string ToString() => $"{Kind}  {Identifier} — {Label}  [{Document}]";
    }
    private sealed record CitationChoice(BibliographyEntry Entry)
    {
        public override string ToString() => $"{Entry.CitationKey} — {Entry.Author} — {Entry.Title} ({Entry.Year})";
    }
}
