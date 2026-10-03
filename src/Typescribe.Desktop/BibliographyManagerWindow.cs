using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

internal static class BibliographyManagerWindow
{
    public static async Task ShowAsync(Window owner, BookProject project, BibTeXDatabase database)
    {
        var search = new TextBox { Watermark = "Search references…", Margin = new Thickness(10) };
        var list = new ListBox { Margin = new Thickness(10, 0, 10, 0) };
        var add = new Button { Content = "Add" };
        var edit = new Button { Content = "Edit", Margin = new Thickness(6, 0, 0, 0) };
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        var import = new Button { Content = "Import BibTeX…", Margin = new Thickness(18, 0, 0, 0) };
        var export = new Button { Content = "Export BibTeX…", Margin = new Thickness(6, 0, 0, 0) };
        var close = new Button { Content = "Close", Margin = new Thickness(18, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), Children = { add, edit, delete, import, export, close } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(search);
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        var dialog = new Window { Title = "Bibliography", Width = 900, Height = 620, MinWidth = 640, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        IReadOnlyList<BibliographyEntry> entries = [];

        void ApplyFilter()
        {
            var query = search.Text?.Trim() ?? string.Empty;
            list.ItemsSource = entries
                .Where(entry => query.Length == 0 || SearchText(entry).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => new EntryRow(entry))
                .ToArray();
        }

        async Task RefreshAsync(string? selectKey = null)
        {
            entries = await database.LoadAsync(project.RootPath);
            ApplyFilter();
            if (selectKey is not null)
                list.SelectedItem = (list.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(row => string.Equals(row.Entry.CitationKey, selectKey, StringComparison.OrdinalIgnoreCase));
        }

        search.TextChanged += (_, _) => ApplyFilter();
        add.Click += async (_, _) =>
        {
            var entry = await EditEntryAsync(dialog, null);
            if (entry is null) return;
            await database.UpsertAsync(project.RootPath, entry);
            await RefreshAsync(entry.CitationKey);
        };
        edit.Click += async (_, _) =>
        {
            if (list.SelectedItem is not EntryRow selected) return;
            var entry = await EditEntryAsync(dialog, selected.Entry);
            if (entry is null) return;
            if (!string.Equals(entry.CitationKey, selected.Entry.CitationKey, StringComparison.OrdinalIgnoreCase))
                await database.DeleteAsync(project.RootPath, selected.Entry.CitationKey);
            await database.UpsertAsync(project.RootPath, entry);
            await RefreshAsync(entry.CitationKey);
        };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not EntryRow selected) return;
            await database.DeleteAsync(project.RootPath, selected.Entry.CitationKey);
            await RefreshAsync();
        };
        import.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import BibTeX",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("BibTeX database") { Patterns = ["*.bib"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            await database.ImportAsync(project.RootPath, path);
            await RefreshAsync();
        };
        export.Click += async (_, _) =>
        {
            var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export BibTeX",
                SuggestedFileName = "bibliography.bib",
                FileTypeChoices = [new FilePickerFileType("BibTeX database") { Patterns = ["*.bib"] }]
            });
            var path = file?.TryGetLocalPath();
            if (path is not null) await database.ExportAsync(project.RootPath, path);
        };
        close.Click += (_, _) => dialog.Close();
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is not EntryRow selected) return;
            var entry = await EditEntryAsync(dialog, selected.Entry);
            if (entry is null) return;
            await database.UpsertAsync(project.RootPath, entry);
            await RefreshAsync(entry.CitationKey);
        };

        await RefreshAsync();
        await dialog.ShowDialog(owner);
    }

    internal static async Task<BibliographyEntry?> EditEntryAsync(Window owner, BibliographyEntry? existing)
    {
        var key = Box(existing?.CitationKey, "smith2024");
        var author = Box(existing?.Author, "Author");
        var title = Box(existing?.Title, "Title");
        var year = Box(existing?.Year, "Year");
        var publisher = Box(existing?.Publisher, "Publisher");
        var doi = Box(existing?.Doi, "DOI");
        var url = Box(existing?.Url, "URL");
        var journal = Box(existing?.Journal, "Journal");
        var pages = Box(existing?.Pages, "Pages");
        var fields = new (string Label, TextBox Box)[]
        {
            ("Citation key", key), ("Author", author), ("Title", title), ("Year", year),
            ("Publisher", publisher), ("DOI", doi), ("URL", url), ("Journal", journal), ("Pages", pages)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", fields.Length + 1))), Margin = new Thickness(12) };
        for (var row = 0; row < fields.Length; row++)
        {
            var label = new TextBlock { Text = fields[row].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            Grid.SetRow(fields[row].Box, row);
            Grid.SetColumn(fields[row].Box, 1);
            fields[row].Box.Margin = new Thickness(0, 3);
            grid.Children.Add(fields[row].Box);
        }
        var ok = new Button { Content = "Save", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        Grid.SetRow(buttons, fields.Length);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        var dialog = new Window { Title = existing is null ? "Add Reference" : "Edit Reference", Width = 640, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid };
        ok.Click += (_, _) =>
        {
            var citationKey = key.Text?.Trim() ?? string.Empty;
            if (citationKey.Length == 0) return;
            dialog.Close(new BibliographyEntry(citationKey, author.Text ?? string.Empty, title.Text ?? string.Empty, year.Text ?? string.Empty, publisher.Text ?? string.Empty, doi.Text ?? string.Empty, url.Text ?? string.Empty, journal.Text ?? string.Empty, pages.Text ?? string.Empty).Normalize());
        };
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<BibliographyEntry?>(owner);
    }

    private static TextBox Box(string? text, string watermark) => new() { Text = text ?? string.Empty, Watermark = watermark };
    private static string SearchText(BibliographyEntry entry) => $"{entry.CitationKey} {entry.Author} {entry.Title} {entry.Year} {entry.Journal} {entry.Publisher} {entry.Doi}";

    private sealed record EntryRow(BibliographyEntry Entry)
    {
        public override string ToString()
            => $"{Entry.CitationKey}   {Entry.Author} — {Entry.Title}{(string.IsNullOrWhiteSpace(Entry.Year) ? string.Empty : $" ({Entry.Year})")}";
    }
}
