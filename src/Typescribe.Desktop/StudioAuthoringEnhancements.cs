using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Durable author-focused workspace conveniences. Navigation is driven directly by the
/// WorkspaceViewModel so Favorites/Recent/Quick Reference do not depend on any Binder control.
/// </summary>
internal sealed class StudioAuthoringEnhancements
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly string _workspaceDirectory;
    private readonly string _favoritesPath;
    private readonly List<FavoriteEntry> _favorites = [];
    private readonly List<NavigationEntry> _history = [];
    private readonly ListBox _favoritesList = new();
    private readonly ListBox _recentList = new();
    private readonly TextBox _scratchpad = new()
    {
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        MinHeight = 260,
        PlaceholderText = "Project notes, loose ideas, reminders, research leads…"
    };
    private readonly DispatcherTimer _scratchpadTimer;

    private TextBox? _editor;
    private TabControl? _leftTabs;
    private TabControl? _inspectorTabs;
    private string? _scratchpadKey;
    private string? _lastSelectionId;
    private int _historyIndex = -1;
    private bool _navigatingHistory;
    private bool _favoritesInjected;
    private bool _scratchpadInjected;
    private bool _menuInjected;
    private bool _applyScheduled;
    private bool _disposed;
    private bool _loadingScratchpad;

    private StudioAuthoringEnhancements(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _workspaceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Typescribe",
            "workspace");
        Directory.CreateDirectory(_workspaceDirectory);
        _favoritesPath = Path.Combine(_workspaceDirectory, "favorites.tsv");
        LoadFavorites();

        _scratchpadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _scratchpadTimer.Tick += (_, _) =>
        {
            _scratchpadTimer.Stop();
            SaveScratchpad();
        };
        _scratchpad.TextChanged += (_, _) =>
        {
            if (_loadingScratchpad) return;
            _scratchpadTimer.Stop();
            _scratchpadTimer.Start();
        };
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        var host = new StudioAuthoringEnhancements(window, viewModel);
        window.Opened += host.OnOpened;
        window.LayoutUpdated += host.OnLayoutUpdated;
        window.KeyDown += host.OnWindowKeyDown;
        window.Closed += host.OnClosed;
        viewModel.StateChanged += host.OnViewModelStateChanged;
        host.ScheduleApply();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleApply();
    private void OnLayoutUpdated(object? sender, EventArgs e) => ScheduleApply();

    private void OnViewModelStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            CaptureCurrentSelection();
            RefreshFavorites();
            RefreshScratchpadProject();
        }, DispatcherPriority.Background);

    private void ScheduleApply()
    {
        if (_disposed || _applyScheduled) return;
        _applyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applyScheduled = false;
            if (!_disposed) DiscoverAndApply();
        }, DispatcherPriority.Background);
    }

    private void DiscoverAndApply()
    {
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        HideLegacyToolbar();

        _editor ??= controls.OfType<TextBox>().FirstOrDefault(static box => box.AcceptsReturn && box.AcceptsTab);

        foreach (var tabControl in controls.OfType<TabControl>())
        {
            var headers = GetTabHeaders(tabControl);
            if (_leftTabs is null && headers.Contains("Binder", StringComparer.Ordinal)) _leftTabs = tabControl;
            if (_inspectorTabs is null && headers.Contains("Inspector", StringComparer.Ordinal)) _inspectorTabs = tabControl;
        }

        InjectFavoritesTab();
        InjectScratchpadTab();
        InjectMenuCommands(controls.OfType<Menu>().FirstOrDefault());
        CaptureCurrentSelection();
        RefreshFavorites();
        RefreshRecent();
        RefreshScratchpadProject();

        if (_editor is not null && _leftTabs is not null && _inspectorTabs is not null && _menuInjected)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void HideLegacyToolbar()
    {
        if (_window.Content is not Grid root || root.RowDefinitions.Count < 4) return;
        var toolbar = root.Children.OfType<Control>().FirstOrDefault(control => Grid.GetRow(control) == 1);
        if (toolbar is not null) toolbar.IsVisible = false;
        root.RowDefinitions[1].Height = new GridLength(0);
    }

    private void InjectFavoritesTab()
    {
        if (_favoritesInjected || _leftTabs is null) return;
        var items = GetTabItems(_leftTabs);
        if (items.Any(static item => string.Equals(item.Header?.ToString(), "Favorites", StringComparison.Ordinal)))
        {
            _favoritesInjected = true;
            return;
        }

        _favoritesList.DoubleTapped += async (_, _) => await NavigateFavoriteAsync();
        _recentList.DoubleTapped += async (_, _) => await NavigateRecentAsync();

        var back = new Button { Content = "← Back" };
        var forward = new Button { Content = "Forward →", Margin = new Thickness(6, 0, 0, 0) };
        var add = new Button { Content = "★ Favorite", Margin = new Thickness(6, 0, 0, 0) };
        back.Click += async (_, _) => await NavigateHistoryAsync(-1);
        forward.Click += async (_, _) => await NavigateHistoryAsync(1);
        add.Click += (_, _) => AddSelectedFavorite();

        var remove = new Button { Content = "Remove" };
        var quick = new Button { Content = "Quick Reference", Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => RemoveSelectedFavorite();
        quick.Click += (_, _) => OpenQuickReference();

        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,*"),
            Margin = new Thickness(6)
        };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Children = { back, forward, add } };
        panel.Children.Add(nav);

        var favoritesTitle = SectionLabel("Favorites");
        Grid.SetRow(favoritesTitle, 1);
        panel.Children.Add(favoritesTitle);
        Grid.SetRow(_favoritesList, 2);
        panel.Children.Add(_favoritesList);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 8),
            Children = { remove, quick }
        };
        Grid.SetRow(actions, 3);
        panel.Children.Add(actions);

        var recentTitle = SectionLabel("Recent documents");
        Grid.SetRow(recentTitle, 4);
        panel.Children.Add(recentTitle);
        Grid.SetRow(_recentList, 5);
        panel.Children.Add(_recentList);

        items.Add(new TabItem { Header = "Favorites", Content = panel });
        _leftTabs.ItemsSource = items.ToArray();
        _favoritesInjected = true;
    }

    private void InjectScratchpadTab()
    {
        if (_scratchpadInjected || _inspectorTabs is null) return;
        _scratchpadInjected = true;
    }

    private void InjectMenuCommands(Menu? menu)
    {
        if (_menuInjected || menu?.ItemsSource is not IEnumerable<object> topItems) return;
        var items = topItems.OfType<MenuItem>().ToArray();
        var view = items.FirstOrDefault(item => HeaderEquals(item, "View"));
        var document = items.FirstOrDefault(item => HeaderEquals(item, "Document"));

        if (view is not null)
        {
            var existing = ToMenuItems(view.ItemsSource);
            existing.Add(new Separator());
            existing.Add(MenuCommand("Back", () => NavigateHistoryAsync(-1), new KeyGesture(Key.Left, KeyModifiers.Alt)));
            existing.Add(MenuCommand("Forward", () => NavigateHistoryAsync(1), new KeyGesture(Key.Right, KeyModifiers.Alt)));
            existing.Add(MenuCommand("Quick Reference", () => { OpenQuickReference(); return Task.CompletedTask; }, new KeyGesture(Key.Q, PrimaryModifier() | KeyModifiers.Shift)));
            view.ItemsSource = existing.ToArray();
        }

        if (document is not null)
        {
            var existing = ToMenuItems(document.ItemsSource);
            existing.Add(new Separator());
            existing.Add(MenuCommand("Add to Favorites", () => { AddSelectedFavorite(); return Task.CompletedTask; }, new KeyGesture(Key.D, PrimaryModifier() | KeyModifiers.Shift)));
            document.ItemsSource = existing.ToArray();
        }
        _menuInjected = true;
    }

    private void CaptureCurrentSelection()
    {
        if (_navigatingHistory || _viewModel.SelectedRow is not { } row) return;
        var id = row.Node.PersistentId;
        if (string.Equals(id, _lastSelectionId, StringComparison.Ordinal)) return;
        _lastSelectionId = id;

        if (_historyIndex >= 0 && _historyIndex < _history.Count &&
            string.Equals(_history[_historyIndex].PersistentId, id, StringComparison.Ordinal))
            return;

        if (_historyIndex + 1 < _history.Count)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(new NavigationEntry(id, row.Node.Title));
        if (_history.Count > 80) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        RefreshRecent();
    }

    private async Task NavigateHistoryAsync(int offset)
    {
        if (_history.Count == 0) return;
        var target = Math.Clamp(_historyIndex + offset, 0, _history.Count - 1);
        if (target == _historyIndex) return;
        if (await SelectNodeAsync(_history[target].PersistentId)) _historyIndex = target;
        RefreshRecent();
    }

    private async Task<bool> SelectNodeAsync(string persistentId)
    {
        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, persistentId, StringComparison.Ordinal));
        if (row is null) return false;

        _navigatingHistory = true;
        try
        {
            await _viewModel.SelectAsync(row);
            _lastSelectionId = row.Node.PersistentId;
        }
        finally
        {
            _navigatingHistory = false;
        }
        return true;
    }

    private void AddSelectedFavorite()
    {
        if (_viewModel.SelectedRow is not { } row) return;
        var existing = _favorites.FindIndex(item =>
            string.Equals(item.PersistentId, row.Node.PersistentId, StringComparison.Ordinal));
        var entry = new FavoriteEntry(row.Node.PersistentId, row.Node.Title);
        if (existing >= 0) _favorites[existing] = entry;
        else _favorites.Add(entry);
        SaveFavorites();
        RefreshFavorites();
    }

    private void RemoveSelectedFavorite()
    {
        if (_favoritesList.SelectedItem is not FavoriteEntry selected) return;
        _favorites.RemoveAll(item => string.Equals(item.PersistentId, selected.PersistentId, StringComparison.Ordinal));
        SaveFavorites();
        RefreshFavorites();
    }

    private async Task NavigateFavoriteAsync()
    {
        if (_favoritesList.SelectedItem is FavoriteEntry selected)
            await SelectNodeAsync(selected.PersistentId);
    }

    private async Task NavigateRecentAsync()
    {
        if (_recentList.SelectedItem is NavigationEntry selected)
            await SelectNodeAsync(selected.PersistentId);
    }

    private void RefreshFavorites()
    {
        var available = _viewModel.BinderRows.ToDictionary(static row => row.Node.PersistentId, StringComparer.Ordinal);
        var visible = new List<FavoriteEntry>();
        foreach (var favorite in _favorites)
        {
            if (!available.TryGetValue(favorite.PersistentId, out var row)) continue;
            visible.Add(favorite with { Title = row.Node.Title });
        }
        _favoritesList.ItemsSource = visible;
    }

    private void RefreshRecent()
    {
        _recentList.ItemsSource = _history
            .TakeLast(12)
            .Reverse()
            .ToArray();
    }

    private void OpenQuickReference()
    {
        if (_viewModel.SelectedRow is not { } row || !row.Node.IsDocument || _editor is null) return;
        var reference = new Window
        {
            Title = $"{row.Node.Title} — Quick Reference",
            Width = 720,
            Height = 780,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Children =
                {
                    new TextBlock
                    {
                        Text = row.Node.Title,
                        FontSize = 18,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        Margin = new Thickness(18, 14, 18, 10)
                    },
                    new TextBox
                    {
                        Text = _editor.Text ?? string.Empty,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Thickness(18, 0, 18, 18),
                        Padding = new Thickness(18),
                        FontSize = Math.Max(14, _editor.FontSize)
                    }
                }
            }
        };
        if (reference.Content is Grid grid && grid.Children.Count > 1) Grid.SetRow(grid.Children[1], 1);
        reference.Show(_window);
    }

    private void RefreshScratchpadProject()
    {
        var title = _window.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) || string.Equals(title, "Typescribe", StringComparison.Ordinal)) return;
        var key = ScratchpadKey(title);
        if (string.Equals(key, _scratchpadKey, StringComparison.Ordinal)) return;

        SaveScratchpad();
        _scratchpadKey = key;
        var path = ScratchpadPath(key);
        _loadingScratchpad = true;
        try { _scratchpad.Text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty; }
        finally { _loadingScratchpad = false; }
    }

    private void SaveScratchpad()
    {
        if (string.IsNullOrWhiteSpace(_scratchpadKey)) return;
        try { File.WriteAllText(ScratchpadPath(_scratchpadKey), _scratchpad.Text ?? string.Empty, new UTF8Encoding(false)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ScratchpadPath(string key) => Path.Combine(_workspaceDirectory, $"scratchpad-{key}.md");

    private static string ScratchpadKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..20];

    private void LoadFavorites()
    {
        if (!File.Exists(_favoritesPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_favoritesPath, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
                try
                {
                    var title = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                    _favorites.Add(new FavoriteEntry(parts[0], title));
                }
                catch (FormatException) { }
            }
        }
        catch (IOException) { }
    }

    private void SaveFavorites()
    {
        try
        {
            var lines = _favorites.Select(item =>
                $"{item.PersistentId}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Title))}");
            File.WriteAllLines(_favoritesPath, lines, new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left)
        {
            e.Handled = true;
            await NavigateHistoryAsync(-1);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Right)
        {
            e.Handled = true;
            await NavigateHistoryAsync(1);
        }
        else if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Q)
        {
            e.Handled = true;
            OpenQuickReference();
        }
        else if (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.D)
        {
            e.Handled = true;
            AddSelectedFavorite();
        }
        else if (primary && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            e.Handled = true;
            ChangeEditorZoom(1);
        }
        else if (primary && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            e.Handled = true;
            ChangeEditorZoom(-1);
        }
        else if (primary && e.Key == Key.D0)
        {
            e.Handled = true;
            if (_editor is not null) _editor.FontSize = 16;
        }
    }

    private void ChangeEditorZoom(double delta)
    {
        if (_editor is null) return;
        _editor.FontSize = Math.Clamp(_editor.FontSize + delta, 12, 26);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _scratchpadTimer.Stop();
        SaveScratchpad();
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.KeyDown -= OnWindowKeyDown;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnViewModelStateChanged;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Margin = new Thickness(2, 8, 2, 5)
    };

    private static List<TabItem> GetTabItems(TabControl control)
        => control.ItemsSource is IEnumerable<object> items ? items.OfType<TabItem>().ToList() : [];

    private static string[] GetTabHeaders(TabControl control)
        => GetTabItems(control).Select(item => item.Header?.ToString() ?? string.Empty).ToArray();

    private static List<object> ToMenuItems(object? source)
        => source is IEnumerable<object> items ? items.ToList() : [];

    private static bool HeaderEquals(MenuItem item, string expected)
        => string.Equals(
            (item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static MenuItem MenuCommand(string header, Func<Task> action, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return item;
    }

    private static KeyModifiers PrimaryModifier()
        => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private sealed record FavoriteEntry(string PersistentId, string Title)
    {
        public override string ToString() => $"★  {Title}";
    }

    private sealed record NavigationEntry(string PersistentId, string Title)
    {
        public override string ToString() => Title;
    }
}
