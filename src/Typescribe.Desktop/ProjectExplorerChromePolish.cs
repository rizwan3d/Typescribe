using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Gives the native Project Explorer an IDE-style compact command bar, complete project-item
/// creation, expand/collapse controls, explorer-local filtering, and search. Filter/search results
/// temporarily replace the tree; clearing them restores the native hierarchy and its persisted
/// expansion state.
/// </summary>
internal sealed class ProjectExplorerChromePolish
{
    private static readonly NewNodeChoice[] NewNodeChoices =
    [
        new(NodeKind.Chapter, "Chapter", "New Chapter", "▤"),
        new(NodeKind.Scene, "Scene", "New Scene", "▪"),
        new(NodeKind.Section, "Section", "New Section", "§"),
        new(NodeKind.Part, "Part", "New Part", "◆"),
        new(NodeKind.Folder, "Folder", "New Folder", "▰"),
        new(NodeKind.Research, "Research", "New Research", "⌕"),
        new(NodeKind.Note, "Note", "New Note", "✎")
    ];

    private static readonly ExplorerFilterChoice[] FilterChoices =
    [
        new(ExplorerFilter.All, "All"),
        new(ExplorerFilter.Manuscript, "Manuscript"),
        new(ExplorerFilter.ResearchAndNotes, "Research/Notes"),
        new(ExplorerFilter.Included, "Included"),
        new(ExplorerFilter.Excluded, "Excluded")
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private readonly Avalonia.Controls.TextBox _searchBox = new()
    {
        Name = "ProjectExplorerSearchBox",
        Watermark = "Search Project Explorer",
        Height = 28,
        MinHeight = 28,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(8, 2),
        Margin = new Thickness(6, 3, 3, 5)
    };

    private readonly ComboBox _filterBox = new()
    {
        Name = "ProjectExplorerFilterBox",
        ItemsSource = FilterChoices,
        SelectedIndex = 0,
        Width = 104,
        Height = 28,
        MinHeight = 28,
        Margin = new Thickness(0, 3, 3, 5),
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly ListBox _results = new()
    {
        Name = "ProjectExplorerSearchResults",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Margin = new Thickness(2, 0, 2, 2),
        IsVisible = false
    };

    private readonly TextBlock _emptySearch = new()
    {
        Text = "No matching project items",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Opacity = 0.58,
        Margin = new Thickness(14),
        IsHitTestVisible = false,
        IsVisible = false
    };

    private TreeView? _tree;
    private Grid? _surface;
    private Button? _clearSearch;
    private bool _installed;
    private bool _disposed;

    private ProjectExplorerChromePolish(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var polish = new ProjectExplorerChromePolish(window, viewModel);
        window.Opened += polish.OnOpened;
        window.LayoutUpdated += polish.OnLayoutUpdated;
        window.Closed += polish.OnClosed;
        viewModel.BinderRows.CollectionChanged += polish.OnBinderRowsChanged;
        polish.TryInstall();
    }

    private ExplorerFilter ActiveFilter
        => _filterBox.SelectedItem is ExplorerFilterChoice choice ? choice.Filter : ExplorerFilter.All;

    private void OnOpened(object? sender, EventArgs e) => TryInstall();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void OnBinderRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_installed && (!string.IsNullOrWhiteSpace(_searchBox.Text) || ActiveFilter != ExplorerFilter.All))
            Dispatcher.UIThread.Post(RefreshSearchResults, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        var host = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(grid => string.Equals(grid.Name, "ProjectExplorerHost", StringComparison.Ordinal));
        if (host is null) return;

        _surface = host.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.Classes.Contains("project-explorer-surface"));
        if (_surface is null) return;

        _tree = _surface.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(tree => tree.Classes.Contains("project-explorer-tree"));
        if (_tree is null) return;

        HideLegacyCreateBar(host);
        BuildCompactToolbar();
        InstallExtendedContextMenu();
        InstallExplorerSearch();

        _searchBox.TextChanged += (_, _) => RefreshSearchResults();
        _searchBox.KeyDown += SearchBoxKeyDown;
        _filterBox.SelectionChanged += FilterSelectionChanged;
        _results.DoubleTapped += async (_, _) => await OpenSelectedSearchResultAsync();
        _results.KeyDown += ResultsKeyDown;

        _installed = true;
        UpdateFilterUi();
    }

    private static void HideLegacyCreateBar(Grid host)
    {
        if (host.Parent is not Grid parent) return;

        var legacyBar = parent.Children
            .OfType<WrapPanel>()
            .FirstOrDefault(panel => panel.Children.OfType<Button>().Any(button =>
                string.Equals(button.Content?.ToString(), "+ Chapter", StringComparison.Ordinal)));
        if (legacyBar is not null)
            legacyBar.IsVisible = false;
    }

    private void BuildCompactToolbar()
    {
        if (_surface is null) return;

        var header = _surface.Children
            .OfType<Grid>()
            .FirstOrDefault(control => Grid.GetRow(control) == 0);
        if (header is null) return;

        var existingTools = header.Children.OfType<Button>().ToArray();
        var caption = header.Children.OfType<TextBlock>().FirstOrDefault();

        header.Children.Clear();
        header.ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto");
        header.Margin = new Thickness(6, 3, 4, 2);
        header.MinHeight = 28;

        if (caption is not null)
        {
            caption.Text = "PROJECT EXPLORER";
            caption.FontSize = 10.5;
            caption.Opacity = 0.68;
            caption.Margin = new Thickness(2, 0, 6, 0);
            header.Children.Add(caption);
        }

        var create = ExplorerCommandButton("＋", "New project item", ShowNewItemDialogAsync);
        var expand = ExplorerCommandButton("⊞", "Expand all", ExpandAllAsync);
        Grid.SetColumn(create, 1);
        Grid.SetColumn(expand, 2);
        header.Children.Add(create);
        header.Children.Add(expand);

        // Reuse ProjectExplorerFeature-owned Collapse All / Reveal Active buttons so their
        // existing state handling and expansion persistence remain the single source of truth.
        for (var index = 0; index < Math.Min(existingTools.Length, 2); index++)
        {
            var button = existingTools[index];
            button.Width = 27;
            button.Height = 25;
            button.MinWidth = 27;
            button.MinHeight = 25;
            button.Padding = new Thickness(2, 0);
            button.Margin = new Thickness(1, 0);
            Grid.SetColumn(button, 3 + index);
            header.Children.Add(button);
        }
    }

    private Button ExplorerCommandButton(string glyph, string toolTip, Func<Task> action)
    {
        var content = new TextBlock
        {
            Text = glyph,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Button
        {
            Content = content,
            Width = 29,
            Height = 25,
            MinWidth = 29,
            MinHeight = 25,
            Padding = new Thickness(2, 0),
            Margin = new Thickness(1, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (!button.Classes.Contains("explorer-tool-button"))
            button.Classes.Add("explorer-tool-button");

        ToolTip.SetTip(button, toolTip);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return button;
    }

    private async Task ShowNewItemDialogAsync()
    {
        var type = new ComboBox
        {
            ItemsSource = NewNodeChoices,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var title = new Avalonia.Controls.TextBox
        {
            Text = NewNodeChoices[0].DefaultTitle,
            Watermark = "Title",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var create = new Button { Content = "Create", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        create.Classes.Add("primary");

        var previousDefault = NewNodeChoices[0].DefaultTitle;
        type.SelectionChanged += (_, _) =>
        {
            if (type.SelectedItem is not NewNodeChoice choice) return;
            if (string.IsNullOrWhiteSpace(title.Text) || string.Equals(title.Text, previousDefault, StringComparison.Ordinal))
                title.Text = choice.DefaultTitle;
            previousDefault = choice.DefaultTitle;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { create, cancel }
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(16)
        };
        content.Children.Add(new TextBlock { Text = "New Project Item", FontSize = 18, FontWeight = FontWeight.SemiBold });
        var typeLabel = new TextBlock { Text = "Type", Margin = new Thickness(0, 12, 0, 4), Opacity = 0.72 };
        Grid.SetRow(typeLabel, 1);
        content.Children.Add(typeLabel);
        Grid.SetRow(type, 2);
        content.Children.Add(type);
        var titleLabel = new TextBlock { Text = "Title", Margin = new Thickness(0, 10, 0, 4), Opacity = 0.72 };
        Grid.SetRow(titleLabel, 3);
        content.Children.Add(titleLabel);

        var footer = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        footer.Children.Add(title);
        Grid.SetRow(buttons, 1);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 4);
        content.Children.Add(footer);

        var dialog = new Window
        {
            Title = "New Project Item",
            Width = 420,
            Height = 285,
            MinWidth = 360,
            MinHeight = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };

        void Complete()
        {
            if (type.SelectedItem is not NewNodeChoice choice) return;
            var text = title.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            dialog.Close(new NewNodeRequest(choice.Kind, text));
        }

        create.Click += (_, _) => Complete();
        cancel.Click += (_, _) => dialog.Close(null);
        title.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Complete();
        };

        var result = await dialog.ShowDialog<NewNodeRequest?>(_window);
        if (result is not null)
            await _viewModel.AddNodeAsync(result.Kind, result.Title);
    }

    private void InstallExtendedContextMenu()
    {
        if (_tree?.ContextMenu is not { } contextMenu) return;

        var items = MenuItems(contextMenu.ItemsSource);
        var firstSeparator = items.FindIndex(static item => item is Separator);
        if (firstSeparator >= 0)
            items.RemoveRange(0, firstSeparator + 1);

        items.Insert(0, BuildNewItemMenu());
        items.Insert(1, new Separator());

        var collapseIndex = items.FindIndex(item => item is MenuItem menu && HeaderEquals(menu, "Collapse All"));
        if (collapseIndex >= 0 && !items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Expand All")))
            items.Insert(collapseIndex, ContextAction("Expand All", ExpandAllAsync));

        contextMenu.ItemsSource = items.ToArray();
    }

    private MenuItem BuildNewItemMenu()
    {
        var menu = new MenuItem { Header = "New" };
        menu.ItemsSource = NewNodeChoices
            .Select(choice => (object)ContextAction(
                $"{choice.Icon}  {choice.Label}…",
                () => AddNodeAsync(choice.Kind, choice.DefaultTitle)))
            .ToArray();
        return menu;
    }

    private static List<object> MenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) return [];
        return enumerable.Cast<object>().ToList();
    }

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals((item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);

    private static MenuItem ContextAction(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return item;
    }

    private async Task ExpandAllAsync()
    {
        if (_tree is null) return;

        // Expansion is applied through the realized TreeViewItems so ProjectExplorerFeature's
        // existing ExpandedEvent handler records each key in project-explorer.tsv. Multiple UI
        // passes allow newly materialized descendants to participate without private-model access.
        var previousCount = -1;
        for (var pass = 0; pass < 48; pass++)
        {
            var items = _tree.GetVisualDescendants().OfType<TreeViewItem>().ToArray();
            var changed = false;
            foreach (var item in items)
            {
                if (item.IsExpanded) continue;
                item.IsExpanded = true;
                changed = true;
            }

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            var currentCount = _tree.GetVisualDescendants().OfType<TreeViewItem>().Count();
            if (!changed || (currentCount == previousCount && items.All(static item => item.IsExpanded)))
                break;
            previousCount = currentCount;
        }
    }

    private void InstallExplorerSearch()
    {
        if (_surface is null || _tree is null) return;

        var separator = _surface.Children
            .OfType<Border>()
            .FirstOrDefault(control => Grid.GetRow(control) == 1);

        _surface.RowDefinitions = new RowDefinitions("Auto,Auto,1,*");

        _clearSearch = new Button
        {
            Content = "×",
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 5, 5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        ToolTip.SetTip(_clearSearch, "Clear Project Explorer search");
        _clearSearch.Click += (_, _) =>
        {
            _searchBox.Text = string.Empty;
            _searchBox.Focus();
        };
        ToolTip.SetTip(_filterBox, "Filter Project Explorer");

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        searchRow.Children.Add(_searchBox);
        Grid.SetColumn(_filterBox, 1);
        searchRow.Children.Add(_filterBox);
        Grid.SetColumn(_clearSearch, 2);
        searchRow.Children.Add(_clearSearch);
        Grid.SetRow(searchRow, 1);
        _surface.Children.Add(searchRow);

        if (separator is not null)
            Grid.SetRow(separator, 2);

        Grid.SetRow(_tree, 3);

        Grid.SetRow(_results, 3);
        _surface.Children.Add(_results);
        Grid.SetRow(_emptySearch, 3);
        _surface.Children.Add(_emptySearch);
    }

    private async Task AddNodeAsync(NodeKind kind, string initialTitle)
    {
        var title = await DesktopDialogService.PromptAsync(
            _window,
            $"Add {kind}",
            "Title",
            initialTitle);
        if (title is null) return;

        await _viewModel.AddNodeAsync(kind, title);
    }

    private void FilterSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateFilterUi();
        RefreshSearchResults();
    }

    private void UpdateFilterUi()
    {
        var filter = ActiveFilter;
        _searchBox.Watermark = filter switch
        {
            ExplorerFilter.Manuscript => "Search manuscript items",
            ExplorerFilter.ResearchAndNotes => "Search research and notes",
            ExplorerFilter.Included => "Search included documents",
            ExplorerFilter.Excluded => "Search excluded documents",
            _ => "Search Project Explorer"
        };
    }

    private void RefreshSearchResults()
    {
        if (!_installed || _tree is null) return;

        var query = _searchBox.Text?.Trim() ?? string.Empty;
        var filtering = ActiveFilter != ExplorerFilter.All;
        var searching = query.Length > 0;
        if (_clearSearch is not null)
            _clearSearch.IsVisible = searching;

        if (!searching && !filtering)
        {
            _results.ItemsSource = null;
            _results.IsVisible = false;
            _emptySearch.IsVisible = false;
            _tree.IsVisible = true;
            return;
        }

        var matches = _viewModel.BinderRows
            .Where(MatchesFilter)
            .Where(row => query.Length == 0 || MatchesText(row, query))
            .Take(300)
            .Select(static row => new ExplorerSearchEntry(row))
            .ToArray();

        _results.ItemsSource = matches;
        _results.SelectedIndex = matches.Length > 0 ? 0 : -1;
        _results.IsVisible = matches.Length > 0;
        _emptySearch.Text = filtering
            ? $"No matching {FilterLabel(ActiveFilter).ToLowerInvariant()} items"
            : "No matching project items";
        _emptySearch.IsVisible = matches.Length == 0;
        _tree.IsVisible = false;
    }

    private bool MatchesFilter(BinderRowViewModel row)
    {
        var node = row.Node;
        return ActiveFilter switch
        {
            ExplorerFilter.Manuscript => node.Kind is NodeKind.Chapter or NodeKind.Section or NodeKind.Scene,
            ExplorerFilter.ResearchAndNotes => node.Kind is NodeKind.Research or NodeKind.Note,
            ExplorerFilter.Included => node.IsDocument && node.IncludeInCompilation,
            ExplorerFilter.Excluded => node.IsDocument && !node.IncludeInCompilation,
            _ => true
        };
    }

    private static bool MatchesText(BinderRowViewModel row, string query)
    {
        var node = row.Node;
        return node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (node.RelativePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               node.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Status.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string FilterLabel(ExplorerFilter filter) => filter switch
    {
        ExplorerFilter.Manuscript => "manuscript",
        ExplorerFilter.ResearchAndNotes => "research/note",
        ExplorerFilter.Included => "included",
        ExplorerFilter.Excluded => "excluded",
        _ => "project"
    };

    private async void SearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(_searchBox.Text))
            {
                _searchBox.Text = string.Empty;
                return;
            }
            if (ActiveFilter != ExplorerFilter.All)
            {
                _filterBox.SelectedIndex = 0;
                return;
            }
            _tree?.Focus();
            return;
        }

        if (e.Key == Key.Down && _results.IsVisible)
        {
            e.Handled = true;
            _results.Focus();
            if (_results.SelectedIndex < 0 && _results.ItemCount > 0)
                _results.SelectedIndex = 0;
            return;
        }

        if (e.Key == Key.Enter && _results.IsVisible)
        {
            e.Handled = true;
            await OpenSelectedSearchResultAsync();
        }
    }

    private async void ResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await OpenSelectedSearchResultAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(_searchBox.Text))
                _searchBox.Text = string.Empty;
            else if (ActiveFilter != ExplorerFilter.All)
                _filterBox.SelectedIndex = 0;
            _searchBox.Focus();
        }
    }

    private async Task OpenSelectedSearchResultAsync()
    {
        if (_results.SelectedItem is not ExplorerSearchEntry entry) return;

        try
        {
            await _viewModel.SelectAsync(entry.Row);
            if (!string.IsNullOrWhiteSpace(_searchBox.Text))
                _searchBox.Text = string.Empty;

            Dispatcher.UIThread.Post(() =>
            {
                if (ActiveFilter == ExplorerFilter.All)
                    _tree?.Focus();
                else
                    _results.Focus();
            }, DispatcherPriority.Background);
        }
        catch
        {
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.BinderRows.CollectionChanged -= OnBinderRowsChanged;
        _searchBox.KeyDown -= SearchBoxKeyDown;
        _filterBox.SelectionChanged -= FilterSelectionChanged;
        _results.KeyDown -= ResultsKeyDown;
    }

    private enum ExplorerFilter
    {
        All,
        Manuscript,
        ResearchAndNotes,
        Included,
        Excluded
    }

    private sealed record ExplorerFilterChoice(ExplorerFilter Filter, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record NewNodeChoice(NodeKind Kind, string Label, string DefaultTitle, string Icon)
    {
        public override string ToString() => $"{Icon}  {Label}";
    }

    private sealed record NewNodeRequest(NodeKind Kind, string Title);

    private sealed record ExplorerSearchEntry(BinderRowViewModel Row)
    {
        public override string ToString()
        {
            var node = Row.Node;
            var icon = node.Kind switch
            {
                NodeKind.Book => "▦",
                NodeKind.Part => "◆",
                NodeKind.Folder => "▰",
                NodeKind.Chapter => "▤",
                NodeKind.Section => "§",
                NodeKind.Scene => "▪",
                NodeKind.Research => "⌕",
                NodeKind.Note => "✎",
                _ => "·"
            };
            var suffix = string.IsNullOrWhiteSpace(node.RelativePath)
                ? node.Kind.ToString()
                : $"{node.Kind} · {node.RelativePath}";
            return $"{icon}  {node.Title}   —   {suffix}";
        }
    }
}
