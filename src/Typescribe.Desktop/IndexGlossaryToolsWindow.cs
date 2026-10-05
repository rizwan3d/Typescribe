using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal static class IndexGlossaryToolsWindow
{
    public static async Task ShowAsync(Window owner, WorkspaceViewModel viewModel)
    {
        if (!viewModel.HasProject) return;

        var glossaryList = new ListBox { MinHeight = 260 };
        var glossaryTerm = new TextBox { Watermark = "Term" };
        var glossaryDefinition = new TextBox { Watermark = "Definition", AcceptsReturn = true, MinHeight = 70, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var glossaryAdd = new Button { Content = "Add / Update" };
        var glossaryDelete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        var glossarySort = new Button { Content = "Sort A–Z", Margin = new Thickness(6, 0, 0, 0) };

        var indexList = new ListBox { MinHeight = 300 };
        var indexEntry = new TextBox { Watermark = "Index entry (for example: Typesetting!widows and orphans)" };
        var indexAdd = new Button { Content = "Add / Update" };
        var indexDelete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        var indexSort = new Button { Content = "Sort A–Z", Margin = new Thickness(6, 0, 0, 0) };
        var extract = new Button { Content = "Suggest from headings", Margin = new Thickness(18, 0, 0, 0) };

        var includeGlossary = new CheckBox { Content = "Include Glossary in generated book" };
        var includeIndex = new CheckBox { Content = "Include Index in generated book" };
        var save = new Button { Content = "Save", MinWidth = 90 };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var glossaryPanel = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Glossary entries", FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = "Terms are stored as structured one-line entries in the existing Glossary content, so older projects remain compatible.", Opacity = .72, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                glossaryList,
                glossaryTerm,
                glossaryDefinition,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { glossaryAdd, glossaryDelete, glossarySort } },
                includeGlossary
            }
        };

        var indexPanel = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Index entries", FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = "One semantic entry per line. Use ! for subentries (for example: Typography!tracking). Entries remain editable as plain book-structure text.", Opacity = .72, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                indexList,
                indexEntry,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { indexAdd, indexDelete, indexSort, extract } },
                includeIndex
            }
        };

        var tabs = new TabControl
        {
            ItemsSource = new object[]
            {
                new TabItem { Header = "Glossary", Content = glossaryPanel },
                new TabItem { Header = "Index", Content = indexPanel }
            }
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, close }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            Text = "Index & Glossary Tools — manage generated back matter without editing long raw text blocks.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        Grid.SetRow(error, 2);
        error.Margin = new Thickness(2, 8, 2, 4);
        root.Children.Add(error);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Index & Glossary",
            Width = 900,
            Height = 680,
            MinWidth = 700,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        var glossary = ParseGlossary(viewModel.CurrentStyle.GlossaryText);
        var index = ParseIndex(viewModel.CurrentStyle.IndexText);
        includeGlossary.IsChecked = viewModel.CurrentStyle.IncludeGlossary;
        includeIndex.IsChecked = viewModel.CurrentStyle.IncludeIndex;

        void RefreshGlossary(string? selectTerm = null)
        {
            var rows = glossary.Select(static item => new GlossaryRow(item.Key, item.Value)).ToArray();
            glossaryList.ItemsSource = rows;
            glossaryList.SelectedItem = selectTerm is null
                ? rows.FirstOrDefault()
                : rows.FirstOrDefault(row => string.Equals(row.Term, selectTerm, StringComparison.OrdinalIgnoreCase));
        }

        void RefreshIndex(string? select = null)
        {
            var rows = index.Select(static item => new IndexRow(item)).ToArray();
            indexList.ItemsSource = rows;
            indexList.SelectedItem = select is null
                ? rows.FirstOrDefault()
                : rows.FirstOrDefault(row => string.Equals(row.Entry, select, StringComparison.OrdinalIgnoreCase));
        }

        glossaryList.SelectionChanged += (_, _) =>
        {
            if (glossaryList.SelectedItem is not GlossaryRow row) return;
            glossaryTerm.Text = row.Term;
            glossaryDefinition.Text = row.Definition;
        };
        indexList.SelectionChanged += (_, _) =>
        {
            if (indexList.SelectedItem is IndexRow row) indexEntry.Text = row.Entry;
        };
        glossaryAdd.Click += (_, _) =>
        {
            var term = glossaryTerm.Text?.Trim() ?? string.Empty;
            if (term.Length == 0) return;
            glossary[term] = glossaryDefinition.Text?.Trim() ?? string.Empty;
            RefreshGlossary(term);
        };
        glossaryDelete.Click += (_, _) =>
        {
            if (glossaryList.SelectedItem is not GlossaryRow row) return;
            glossary.Remove(row.Term);
            glossaryTerm.Text = string.Empty;
            glossaryDefinition.Text = string.Empty;
            RefreshGlossary();
        };
        glossarySort.Click += (_, _) =>
        {
            glossary = new SortedDictionary<string, string>(glossary, StringComparer.OrdinalIgnoreCase);
            RefreshGlossary();
        };
        indexAdd.Click += (_, _) =>
        {
            var entry = indexEntry.Text?.Trim() ?? string.Empty;
            if (entry.Length == 0) return;
            if (!index.Contains(entry, StringComparer.OrdinalIgnoreCase)) index.Add(entry);
            RefreshIndex(entry);
        };
        indexDelete.Click += (_, _) =>
        {
            if (indexList.SelectedItem is not IndexRow row) return;
            index.RemoveAll(item => string.Equals(item, row.Entry, StringComparison.OrdinalIgnoreCase));
            indexEntry.Text = string.Empty;
            RefreshIndex();
        };
        indexSort.Click += (_, _) =>
        {
            index = index.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToList();
            RefreshIndex();
        };
        extract.Click += (_, _) =>
        {
            foreach (var heading in viewModel.OutlineItems.Select(static item => item.Title))
            {
                var clean = heading?.Trim();
                if (!string.IsNullOrWhiteSpace(clean) && !index.Contains(clean, StringComparer.OrdinalIgnoreCase)) index.Add(clean);
            }
            index = index.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToList();
            RefreshIndex();
        };
        save.Click += async (_, _) =>
        {
            try
            {
                error.Text = string.Empty;
                var current = viewModel.CurrentStyle;
                var updated = current with
                {
                    GlossaryText = SerializeGlossary(glossary),
                    IndexText = string.Join(Environment.NewLine, index.Where(static item => !string.IsNullOrWhiteSpace(item))),
                    IncludeGlossary = includeGlossary.IsChecked == true,
                    IncludeIndex = includeIndex.IsChecked == true
                };
                await viewModel.UpdateStyleAsync(updated.Validate());
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        close.Click += (_, _) => dialog.Close();

        RefreshGlossary();
        RefreshIndex();
        await dialog.ShowDialog(owner);
    }

    private static SortedDictionary<string, string> ParseGlossary(string? text)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf('\t');
            if (separator < 0) separator = line.IndexOf(" — ", StringComparison.Ordinal);
            if (separator < 0) separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                result[line] = string.Empty;
                continue;
            }
            var skip = line[separator] == '\t' ? 1 : 3;
            var term = line[..separator].Trim();
            if (term.Length > 0) result[term] = line[(separator + skip)..].Trim();
        }
        return result;
    }

    private static List<string> ParseIndex(string? text)
        => SplitLines(text).Select(static line => line.Trim()).Where(static line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string SerializeGlossary(IEnumerable<KeyValuePair<string, string>> entries)
        => string.Join(Environment.NewLine, entries.Select(static pair => string.IsNullOrWhiteSpace(pair.Value) ? pair.Key : $"{pair.Key} — {pair.Value}"));

    private static IEnumerable<string> SplitLines(string? value)
        => (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private sealed record GlossaryRow(string Term, string Definition)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Definition) ? Term : $"{Term} — {Definition}";
    }
    private sealed record IndexRow(string Entry)
    {
        public override string ToString() => Entry;
    }
}
