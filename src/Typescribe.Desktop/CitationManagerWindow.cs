using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

internal static class CitationManagerWindow
{
    public static async Task ShowAsync(
        Window owner,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser,
        BibTeXDatabase database)
    {
        var project = repository.CurrentProject;
        if (project is null) return;

        var search = new TextBox { Watermark = "Search key, author, title, DOI…" };
        var filter = new ComboBox { ItemsSource = new[] { "All references", "Used", "Unused" }, SelectedIndex = 0, MinWidth = 150 };
        var list = new ListBox { MinWidth = 360 };
        var details = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(12) };
        var status = new TextBlock { Opacity = .72, Margin = new Thickness(12, 0, 12, 8) };
        var add = new Button { Content = "Add" };
        var edit = new Button { Content = "Edit", Margin = new Thickness(6, 0, 0, 0) };
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        var appendCitation = new Button { Content = "Append Citation", Margin = new Thickness(18, 0, 0, 0) };
        var goToUse = new Button { Content = "Go to First Use", Margin = new Thickness(6, 0, 0, 0) };
        var bibtex = new Button { Content = "BibTeX Import / Export…", Margin = new Thickness(18, 0, 0, 0) };
        var close = new Button { Content = "Close", Margin = new Thickness(12, 0, 0, 0) };

        var searchBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10) };
        searchBar.Children.Add(search);
        Grid.SetColumn(filter, 1);
        filter.Margin = new Thickness(8, 0, 0, 0);
        searchBar.Children.Add(filter);

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*"), ColumnSpacing = 8 };
        split.Children.Add(list);
        var right = new StackPanel { Children = { details, status } };
        Grid.SetColumn(right, 1);
        split.Children.Add(right);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10),
            Children = { add, edit, delete, appendCitation, goToUse, bibtex, close }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(searchBar);
        Grid.SetRow(split, 1);
        root.Children.Add(split);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Citation Manager",
            Width = 1080,
            Height = 650,
            MinWidth = 820,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        IReadOnlyList<CitationRow> rows = [];

        async Task<IReadOnlyDictionary<string, List<CitationUse>>> CollectUsesAsync()
        {
            var uses = new Dictionary<string, List<CitationUse>>(StringComparer.OrdinalIgnoreCase);
            await foreach (var (node, content) in repository.EnumerateDocumentsAsync(project))
            {
                var ast = parser.Parse(content);
                foreach (var block in ast.Blocks)
                {
                    foreach (var citation in Citations(block))
                    {
                        if (!uses.TryGetValue(citation.Key, out var locations))
                        {
                            locations = [];
                            uses[citation.Key] = locations;
                        }
                        locations.Add(new CitationUse(node.Title, block.SourceLine, citation.Locator));
                    }
                }
            }
            return uses;
        }

        async Task RefreshAsync(string? selectKey = null)
        {
            var entries = await database.LoadAsync(project.RootPath);
            var uses = await CollectUsesAsync();
            rows = entries
                .Select(entry => new CitationRow(entry, uses.TryGetValue(entry.CitationKey, out var locations) ? locations : []))
                .OrderBy(static row => row.Entry.Author, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.Entry.Year, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.Entry.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ApplyFilter(selectKey);
        }

        void ApplyFilter(string? selectKey = null)
        {
            var query = search.Text?.Trim() ?? string.Empty;
            var mode = filter.SelectedIndex;
            var visible = rows.Where(row =>
                    (query.Length == 0 || row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                    (mode == 0 || (mode == 1 && row.Uses.Count > 0) || (mode == 2 && row.Uses.Count == 0)))
                .ToArray();
            list.ItemsSource = visible;
            list.SelectedItem = selectKey is null
                ? visible.FirstOrDefault()
                : visible.FirstOrDefault(row => string.Equals(row.Entry.CitationKey, selectKey, StringComparison.OrdinalIgnoreCase)) ?? visible.FirstOrDefault();
            status.Text = $"{visible.Length} shown • {rows.Count} total • {rows.Count(row => row.Uses.Count == 0)} unused";
        }

        void RefreshDetails()
        {
            if (list.SelectedItem is not CitationRow row)
            {
                details.Text = "No reference selected.";
                return;
            }
            var entry = row.Entry;
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(entry.Author)) warnings.Add("missing author");
            if (string.IsNullOrWhiteSpace(entry.Title)) warnings.Add("missing title");
            if (string.IsNullOrWhiteSpace(entry.Year)) warnings.Add("missing year");
            var usage = row.Uses.Count == 0
                ? "Unused in manuscript."
                : string.Join("\n", row.Uses.Select(static use => $"• {use.Document}:{use.Line}{(string.IsNullOrWhiteSpace(use.Locator) ? string.Empty : $" — {use.Locator}")}"));
            details.Text =
                $"{entry.CitationKey}\n{entry.Author}\n{entry.Title}\n{entry.Year}\n{entry.Journal}\n{entry.Publisher}\n{entry.Doi}\n{entry.Url}\n\nUses: {row.Uses.Count}\n{usage}" +
                (warnings.Count == 0 ? string.Empty : "\n\nMetadata warnings: " + string.Join(", ", warnings));
        }

        search.TextChanged += (_, _) => ApplyFilter();
        filter.SelectionChanged += (_, _) => ApplyFilter();
        list.SelectionChanged += (_, _) => RefreshDetails();
        add.Click += async (_, _) =>
        {
            var entry = await BibliographyManagerWindow.EditEntryAsync(dialog, null);
            if (entry is null) return;
            await database.UpsertAsync(project.RootPath, entry);
            await RefreshAsync(entry.CitationKey);
        };
        edit.Click += async (_, _) =>
        {
            if (list.SelectedItem is not CitationRow selected) return;
            var entry = await BibliographyManagerWindow.EditEntryAsync(dialog, selected.Entry);
            if (entry is null) return;
            if (!string.Equals(entry.CitationKey, selected.Entry.CitationKey, StringComparison.OrdinalIgnoreCase))
                await database.DeleteAsync(project.RootPath, selected.Entry.CitationKey);
            await database.UpsertAsync(project.RootPath, entry);
            await RefreshAsync(entry.CitationKey);
        };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not CitationRow selected) return;
            await database.DeleteAsync(project.RootPath, selected.Entry.CitationKey);
            await RefreshAsync();
        };
        appendCitation.Click += (_, _) =>
        {
            if (!viewModel.HasDocument || list.SelectedItem is not CitationRow selected) return;
            var markup = $"[@{selected.Entry.CitationKey}]";
            var existing = viewModel.EditorText;
            var separator = existing.Length == 0 || char.IsWhiteSpace(existing[^1]) ? string.Empty : " ";
            viewModel.UpdateEditorText(existing + separator + markup);
        };
        goToUse.Click += async (_, _) =>
        {
            if (list.SelectedItem is not CitationRow selected || selected.Uses.Count == 0) return;
            await viewModel.SearchAsync($"[@{selected.Entry.CitationKey}");
            var hit = viewModel.SearchResults.FirstOrDefault();
            if (hit is not null) await viewModel.GoToSearchHitAsync(hit);
        };
        bibtex.Click += async (_, _) =>
        {
            await BibliographyManagerWindow.ShowAsync(dialog, project, database);
            await RefreshAsync();
        };
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is not CitationRow selected) return;
            var entry = await BibliographyManagerWindow.EditEntryAsync(dialog, selected.Entry);
            if (entry is null) return;
            await database.UpsertAsync(project.RootPath, entry);
            await RefreshAsync(entry.CitationKey);
        };
        close.Click += (_, _) => dialog.Close();

        await RefreshAsync();
        await dialog.ShowDialog(owner);
    }

    private static IEnumerable<CitationInline> Citations(AstBlock block)
        => block switch
        {
            HeadingBlock heading => Walk(heading.Inlines),
            ParagraphBlock paragraph => Walk(paragraph.Inlines),
            QuoteBlock quote => Walk(quote.Inlines),
            ListItemBlock item => Walk(item.Inlines),
            FootnoteDefinitionBlock footnote => Walk(footnote.Inlines),
            TableBlock table => table.Header.SelectMany(static cell => Walk(cell.Inlines))
                .Concat(table.Rows.SelectMany(static row => row).SelectMany(static cell => Walk(cell.Inlines))),
            _ => []
        };

    private static IEnumerable<CitationInline> Walk(IEnumerable<AstInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is CitationInline citation) yield return citation;
            var children = inline switch
            {
                StrongInline strong => strong.Children,
                EmphasisInline emphasis => emphasis.Children,
                LinkInline link => link.Label,
                _ => null
            };
            if (children is null) continue;
            foreach (var child in Walk(children)) yield return child;
        }
    }

    private sealed record CitationUse(string Document, int Line, string? Locator);
    private sealed record CitationRow(BibliographyEntry Entry, IReadOnlyList<CitationUse> Uses)
    {
        public string SearchText => $"{Entry.CitationKey} {Entry.Author} {Entry.Title} {Entry.Year} {Entry.Journal} {Entry.Publisher} {Entry.Doi} {Entry.Url}";
        public override string ToString()
            => $"{(Uses.Count == 0 ? "○" : "●")} {Entry.CitationKey} — {Entry.Author} — {Entry.Title}  ({Uses.Count})";
    }
}
