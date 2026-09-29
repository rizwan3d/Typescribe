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
/// Gives the native Project Explorer an IDE-style compact command bar and an explorer-local
/// search surface. Search results temporarily replace the tree, then return to the tree after
/// a result is opened or the query is cleared.
/// </summary>
internal sealed class ProjectExplorerChromePolish
{
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
        Margin = new Thickness(6, 3, 4, 5)
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

    private void OnOpened(object? sender, EventArgs e) => TryInstall();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void OnBinderRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_installed && !string.IsNullOrWhiteSpace(_searchBox.Text))
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
        InstallExplorerSearch();

        _searchBox.TextChanged += (_, _) => RefreshSearchResults();
        _searchBox.KeyDown += SearchBoxKeyDown;
        _results.DoubleTapped += async (_, _) => await OpenSelectedSearchResultAsync();
        _results.KeyDown += ResultsKeyDown;

        _installed = true;
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
        header.ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto");
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

        var chapter = ExplorerCommandButton("▤+", "New Chapter", () => AddNodeAsync(NodeKind.Chapter, "New Chapter"));
        var folder = ExplorerCommandButton("▰+", "New Folder", () => AddNodeAsync(NodeKind.Folder, "New Folder"));
        var part = ExplorerCommandButton("◆+", "New Part", () => AddNodeAsync(NodeKind.Part, "New Part"));

        Grid.SetColumn(chapter, 1);
        Grid.SetColumn(folder, 2);
        Grid.SetColumn(part, 3);
        header.Children.Add(chapter);
        header.Children.Add(folder);
        header.Children.Add(part);

        // Reuse the ProjectExplorerFeature-owned Collapse All / Reveal Active buttons so their
        // existing behavior remains intact while their appearance matches the compact toolbar.
        for (var index = 0; index < Math.Min(existingTools.Length, 2); index++)
        {
            var button = existingTools[index];
            button.Width = 27;
            button.Height = 25;
            button.MinWidth = 27;
            button.MinHeight = 25;
            button.Padding = new Thickness(2, 0);
            button.Margin = new Thickness(1, 0);
            Grid.SetColumn(button, 4 + index);
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

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        searchRow.Children.Add(_searchBox);
        Grid.SetColumn(_clearSearch, 1);
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

    private void RefreshSearchResults()
    {
        if (!_installed || _tree is null) return;

        var query = _searchBox.Text?.Trim() ?? string.Empty;
        var searching = query.Length > 0;
        if (_clearSearch is not null)
            _clearSearch.IsVisible = searching;

        if (!searching)
        {
            _results.ItemsSource = null;
            _results.IsVisible = false;
            _emptySearch.IsVisible = false;
            _tree.IsVisible = true;
            return;
        }

        var matches = _viewModel.BinderRows
            .Where(row => Matches(row, query))
            .Take(200)
            .Select(static row => new ExplorerSearchEntry(row))
            .ToArray();

        _results.ItemsSource = matches;
        _results.SelectedIndex = matches.Length > 0 ? 0 : -1;
        _results.IsVisible = matches.Length > 0;
        _emptySearch.IsVisible = matches.Length == 0;
        _tree.IsVisible = false;
    }

    private static bool Matches(BinderRowViewModel row, string query)
    {
        var node = row.Node;
        return node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (node.RelativePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               node.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Status.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async void SearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _searchBox.Text = string.Empty;
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
            _searchBox.Text = string.Empty;
            _searchBox.Focus();
        }
    }

    private async Task OpenSelectedSearchResultAsync()
    {
        if (_results.SelectedItem is not ExplorerSearchEntry entry) return;

        try
        {
            await _viewModel.SelectAsync(entry.Row);
            _searchBox.Text = string.Empty;
            Dispatcher.UIThread.Post(() => _tree?.Focus(), DispatcherPriority.Background);
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
        _results.KeyDown -= ResultsKeyDown;
    }

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
            return $"{icon}  {node.Title}";
        }
    }
}
