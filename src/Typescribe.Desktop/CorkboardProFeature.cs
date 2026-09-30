using System.Collections;
using System.Text.Json;
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
/// Professional Corkboard surface: grid/freeform/stack layouts, persistent card positions,
/// search/filter/grouping, adjustable card density, direct card actions, and keyboard navigation.
/// The binder/project model remains the source of truth for ordering and metadata.
/// </summary>
internal sealed class CorkboardProFeature
{
    private const string StateDirectory = ".typescribe";
    private const string StateFileName = "corkboard.json";
    private static readonly DataFormat<CorkboardCardViewModel> CardFormat =
        DataFormat.CreateInProcessFormat<CorkboardCardViewModel>("typescribe-pro-corkboard-card");

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;

    private readonly TextBox _searchBox = new()
    {
        Watermark = "Search corkboard",
        MinWidth = 190,
        Height = 28,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(7, 2)
    };

    private readonly ComboBox _layoutBox = new()
    {
        ItemsSource = new[] { "Cards", "Freeform", "Stacks" },
        SelectedIndex = 0,
        MinWidth = 105,
        Height = 28
    };

    private readonly ComboBox _groupBox = new()
    {
        ItemsSource = new[] { "None", "Status", "Label", "Type", "Compile" },
        SelectedIndex = 0,
        MinWidth = 100,
        Height = 28
    };

    private readonly ComboBox _filterBox = new()
    {
        ItemsSource = new[] { "All", "Included", "Excluded", "Draft", "Research / Notes" },
        SelectedIndex = 0,
        MinWidth = 120,
        Height = 28
    };

    private readonly ComboBox _sizeBox = new()
    {
        ItemsSource = new[] { "Small", "Medium", "Large" },
        SelectedIndex = 1,
        MinWidth = 92,
        Height = 28
    };

    private readonly CheckBox _detailsBox = new()
    {
        Content = "Details",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _title = new()
    {
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _status = new()
    {
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Grid _contentHost = new();

    private TabControl? _centerTabs;
    private TabItem? _corkboardTab;
    private Canvas? _freeformCanvas;
    private CorkboardCardViewModel? _cardDragCandidate;
    private PointerPressedEventArgs? _cardDragTrigger;
    private Point _cardDragStart;
    private CorkboardCardViewModel? _freeformDragCard;
    private Border? _freeformDragBorder;
    private Point _freeformPointerOffset;
    private BoardState _state = new();
    private string? _loadedProjectRoot;
    private long _loadedVersion = -1;
    private bool _installed;
    private bool _syncingControls;
    private bool _disposed;

    private CorkboardProFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);

        var feature = new CorkboardProFeature(window, viewModel, repository);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnViewModelStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        RefreshBoard(force: true);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() => RefreshBoard(force: false), DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs).ToArray();
            if (!items.Any(item => HeaderEquals(item, "Editor")) ||
                !items.Any(item => HeaderEquals(item, "Corkboard")))
                continue;

            var corkboard = items.FirstOrDefault(item => HeaderEquals(item, "Corkboard"));
            if (corkboard is null) continue;

            _centerTabs = tabs;
            _corkboardTab = corkboard;
            corkboard.Content = BuildSurface();
            _installed = true;
            EnsureProjectState();
            RefreshBoard(force: true);
            return;
        }
    }

    private Control BuildSurface()
    {
        var reset = SmallButton("Reset", "Reset freeform card positions", () =>
        {
            _state.Positions.Clear();
            SaveState();
            RefreshBoard(force: true);
        });

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(10, 7)
        };
        toolbar.Children.Add(_title);
        Grid.SetColumn(_searchBox, 1);
        toolbar.Children.Add(_searchBox);
        Grid.SetColumn(_layoutBox, 2);
        toolbar.Children.Add(_layoutBox);
        Grid.SetColumn(_groupBox, 3);
        toolbar.Children.Add(_groupBox);
        Grid.SetColumn(_filterBox, 4);
        toolbar.Children.Add(_filterBox);
        Grid.SetColumn(_sizeBox, 5);
        toolbar.Children.Add(_sizeBox);
        Grid.SetColumn(_detailsBox, 6);
        toolbar.Children.Add(_detailsBox);
        Grid.SetColumn(reset, 7);
        toolbar.Children.Add(reset);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 4, 10, 5),
            MinHeight = 24
        };
        footer.Children.Add(new TextBlock
        {
            Text = "Double-click a card to open it. Drag cards to reorder; in Freeform mode drag to position.",
            Opacity = 0.58,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(_status, 1);
        footer.Children.Add(_status);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(toolbar);
        Grid.SetRow(_contentHost, 1);
        root.Children.Add(_contentHost);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _searchBox.TextChanged += (_, _) => RefreshBoard(force: true);
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_searchBox.Text))
            {
                e.Handled = true;
                _searchBox.Text = string.Empty;
            }
        };

        _layoutBox.SelectionChanged += (_, _) => PreferenceChanged();
        _groupBox.SelectionChanged += (_, _) => PreferenceChanged();
        _filterBox.SelectionChanged += (_, _) => PreferenceChanged();
        _sizeBox.SelectionChanged += (_, _) => PreferenceChanged();
        _detailsBox.Click += (_, _) => PreferenceChanged();

        return root;
    }

    private void PreferenceChanged()
    {
        if (_syncingControls) return;
        _state.Layout = Selected(_layoutBox, "Cards");
        _state.GroupBy = Selected(_groupBox, "None");
        _state.Filter = Selected(_filterBox, "All");
        _state.CardSize = Selected(_sizeBox, "Medium");
        _state.ShowDetails = _detailsBox.IsChecked == true;

        if (string.Equals(_state.Layout, "Stacks", StringComparison.Ordinal) &&
            string.Equals(_state.GroupBy, "None", StringComparison.Ordinal))
        {
            _state.GroupBy = "Status";
            _syncingControls = true;
            _groupBox.SelectedItem = "Status";
            _syncingControls = false;
        }

        SaveState();
        RefreshBoard(force: true);
    }

    private void EnsureProjectState()
    {
        var root = _repository.CurrentProject?.RootPath;
        if (string.Equals(root, _loadedProjectRoot, StringComparison.Ordinal)) return;
        _loadedProjectRoot = root;
        _loadedVersion = -1;
        _state = LoadState(root);
        ApplyStateToControls();
    }

    private void ApplyStateToControls()
    {
        _syncingControls = true;
        try
        {
            _layoutBox.SelectedItem = NormalizeChoice(_state.Layout, "Cards", "Freeform", "Stacks") ?? "Cards";
            _groupBox.SelectedItem = NormalizeChoice(_state.GroupBy, "None", "Status", "Label", "Type", "Compile") ?? "None";
            _filterBox.SelectedItem = NormalizeChoice(_state.Filter, "All", "Included", "Excluded", "Draft", "Research / Notes") ?? "All";
            _sizeBox.SelectedItem = NormalizeChoice(_state.CardSize, "Small", "Medium", "Large") ?? "Medium";
            _detailsBox.IsChecked = _state.ShowDetails;
        }
        finally
        {
            _syncingControls = false;
        }
    }

    private void RefreshBoard(bool force)
    {
        if (!_installed || _disposed) return;
        EnsureProjectState();

        if (!force && _loadedVersion == _viewModel.CorkboardVersion) return;
        _loadedVersion = _viewModel.CorkboardVersion;

        var all = _viewModel.CorkboardCards.ToArray();
        var cards = ApplySearchAndFilter(all).ToArray();
        _title.Text = $"Corkboard — {_viewModel.CorkboardTitle}";
        _status.Text = cards.Length == all.Length
            ? $"{all.Length:N0} card{(all.Length == 1 ? string.Empty : "s")}"
            : $"{cards.Length:N0} of {all.Length:N0} cards";

        _contentHost.Children.Clear();
        _freeformCanvas = null;

        if (cards.Length == 0)
        {
            _contentHost.Children.Add(new TextBlock
            {
                Text = all.Length == 0
                    ? "This project group has no cards yet."
                    : "No cards match the current Corkboard search or filter.",
                Margin = new Thickness(22),
                Opacity = 0.68,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var layout = _state.Layout;
        if (string.Equals(layout, "Freeform", StringComparison.Ordinal))
            _contentHost.Children.Add(BuildFreeform(cards));
        else if (string.Equals(layout, "Stacks", StringComparison.Ordinal))
            _contentHost.Children.Add(BuildStacks(cards));
        else
            _contentHost.Children.Add(BuildGrid(cards));
    }

    private Control BuildGrid(IReadOnlyList<CorkboardCardViewModel> cards)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8)
        };
        foreach (var card in cards)
            panel.Children.Add(BuildCard(card, freeform: false));

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    private Control BuildFreeform(IReadOnlyList<CorkboardCardViewModel> cards)
    {
        var canvas = new Canvas
        {
            Width = 1800,
            Height = 1200,
            MinWidth = 900,
            MinHeight = 700
        };
        _freeformCanvas = canvas;

        var width = CardWidth();
        var maxX = 0d;
        var maxY = 0d;
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var border = BuildCard(card, freeform: true);
            var position = PositionFor(card, index, width);
            Canvas.SetLeft(border, position.X);
            Canvas.SetTop(border, position.Y);
            canvas.Children.Add(border);
            maxX = Math.Max(maxX, position.X + width + 80);
            maxY = Math.Max(maxY, position.Y + CardMinimumHeight() + 120);
        }
        canvas.Width = Math.Max(canvas.Width, maxX);
        canvas.Height = Math.Max(canvas.Height, maxY);

        return new ScrollViewer
        {
            Content = canvas,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private Control BuildStacks(IReadOnlyList<CorkboardCardViewModel> cards)
    {
        var groupBy = string.Equals(_state.GroupBy, "None", StringComparison.Ordinal) ? "Status" : _state.GroupBy;
        var groups = cards
            .GroupBy(card => GroupKey(card, groupBy), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var columns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(10)
        };

        foreach (var group in groups)
        {
            var list = new StackPanel { Spacing = 4 };
            list.Children.Add(new TextBlock
            {
                Text = $"{group.Key}  ·  {group.Count()}",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(6, 5, 6, 8),
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var card in group)
                list.Children.Add(BuildCard(card, freeform: false, stacked: true));

            columns.Children.Add(new Border
            {
                Width = CardWidth() + 24,
                Padding = new Thickness(5),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                CornerRadius = new CornerRadius(6),
                Child = list
            });
        }

        return new ScrollViewer
        {
            Content = columns,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private Border BuildCard(CorkboardCardViewModel card, bool freeform, bool stacked = false)
    {
        var node = card.Node;
        var width = CardWidth();
        var body = new StackPanel { Spacing = 7, Margin = new Thickness(11, 9, 11, 10) };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = card.Title,
            FontSize = _state.CardSize == "Small" ? 14 : 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        });
        var compile = new TextBlock
        {
            Text = card.IsIncluded ? "●" : "○",
            ToolTip = card.IsIncluded ? "Included in compilation" : "Excluded from compilation",
            Opacity = card.IsIncluded ? 0.85 : 0.45,
            Margin = new Thickness(6, 1, 0, 0)
        };
        Grid.SetColumn(compile, 1);
        header.Children.Add(compile);
        body.Children.Add(header);

        body.Children.Add(new TextBlock
        {
            Text = card.Synopsis,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = _state.CardSize == "Small" ? 58 : _state.CardSize == "Large" ? 116 : 88,
            Opacity = 0.86
        });

        if (_state.ShowDetails)
        {
            var tags = new WrapPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrWhiteSpace(card.Status))
                tags.Children.Add(Tag(card.Status));
            if (!string.IsNullOrWhiteSpace(card.Label))
                tags.Children.Add(Tag(card.Label));
            tags.Children.Add(Tag(card.Kind));
            body.Children.Add(tags);

            if (card.TargetWords > 0)
                body.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = card.Progress, Height = 5 });

            body.Children.Add(new TextBlock
            {
                Text = card.Footer,
                FontSize = 11,
                Opacity = 0.67,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var stripe = new Border
        {
            Width = 5,
            Background = LabelBrush(card.Label),
            CornerRadius = new CornerRadius(5, 0, 0, 5)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("5,*") };
        grid.Children.Add(stripe);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var border = new Border
        {
            Width = width,
            MinHeight = CardMinimumHeight(),
            Margin = freeform ? new Thickness(0) : new Thickness(stacked ? 3 : 6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
            Child = grid,
            Focusable = true,
            ContextMenu = BuildCardMenu(card)
        };

        border.DoubleTapped += async (_, _) => await RunSafeAsync(() => OpenCardAsync(card));
        border.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RunSafeAsync(() => OpenCardAsync(card));
            }
            else if (e.Key == Key.F2)
            {
                e.Handled = true;
                await RunSafeAsync(() => EditCardAsync(card));
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                await RunSafeAsync(() => DeleteCardAsync(card));
            }
        };

        border.PointerPressed += (_, e) => CardPointerPressed(card, border, e, freeform);
        border.PointerMoved += async (_, e) => await CardPointerMovedAsync(border, e, freeform);
        border.PointerReleased += (_, e) => CardPointerReleased(card, border, e, freeform);

        if (!freeform)
        {
            DragDrop.SetAllowDrop(border, true);
            DragDrop.AddDragOverHandler(border, (_, e) => CardDragOver(card, e));
            DragDrop.AddDropHandler(border, async (_, e) => await CardDropAsync(card, border, e));
        }

        return border;
    }

    private ContextMenu BuildCardMenu(CorkboardCardViewModel card)
    {
        var open = new MenuItem { Header = "Open" };
        var edit = new MenuItem { Header = "Edit Card…" };
        var include = new MenuItem { Header = card.IsIncluded ? "Exclude from Compilation" : "Include in Compilation" };
        var up = new MenuItem { Header = "Move Earlier" };
        var down = new MenuItem { Header = "Move Later" };
        var delete = new MenuItem { Header = "Delete…" };

        open.Click += async (_, _) => await RunSafeAsync(() => OpenCardAsync(card));
        edit.Click += async (_, _) => await RunSafeAsync(() => EditCardAsync(card));
        include.Click += async (_, _) => await RunSafeAsync(() => ToggleCompilationAsync(card));
        up.Click += async (_, _) => await RunSafeAsync(() => MoveCardAsync(card, -1));
        down.Click += async (_, _) => await RunSafeAsync(() => MoveCardAsync(card, 1));
        delete.Click += async (_, _) => await RunSafeAsync(() => DeleteCardAsync(card));

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                open,
                edit,
                new Separator(),
                include,
                up,
                down,
                new Separator(),
                delete
            }
        };
    }

    private async Task OpenCardAsync(CorkboardCardViewModel card)
    {
        await _viewModel.SelectCorkboardCardAsync(card);
        if (_centerTabs is not null) _centerTabs.SelectedIndex = 0;
    }

    private async Task EditCardAsync(CorkboardCardViewModel card)
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        var node = card.Node;

        var title = new TextBox { Text = node.Title, Watermark = "Title" };
        var synopsis = new TextBox
        {
            Text = node.Synopsis,
            Watermark = "Synopsis",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110
        };
        var status = new TextBox { Text = node.Status, Watermark = "Draft / Revised / Final" };
        var label = new TextBox { Text = node.Label, Watermark = "Label / storyline / POV" };
        var keywords = new TextBox { Text = node.Keywords, Watermark = "comma-separated keywords" };
        var target = new TextBox { Text = node.TargetWords.ToString(), Watermark = "Word target" };
        var save = new Button { Content = "Save", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, cancel }
        };
        var form = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Field("Title", title),
                Field("Synopsis", synopsis),
                Field("Status", status),
                Field("Label", label),
                Field("Keywords", keywords),
                Field("Word target", target),
                buttons
            }
        };
        var dialog = new Window
        {
            Title = $"Edit Corkboard Card — {node.Title}",
            Width = 560,
            Height = 610,
            MinWidth = 440,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = form, Margin = new Thickness(14) }
        };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        title.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                dialog.Close(true);
            }
        };

        if (!await dialog.ShowDialog<bool>(_window)) return;
        var newTitle = title.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        if (!int.TryParse(target.Text, out var targetWords) || targetWords < 0) targetWords = 0;

        if (!string.Equals(newTitle, node.Title, StringComparison.Ordinal))
            await _repository.RenameNodeAsync(project, node, newTitle);
        await _repository.SaveNodeMetadataAsync(
            project,
            node,
            synopsis.Text ?? string.Empty,
            node.Notes,
            status.Text ?? string.Empty,
            label.Text ?? string.Empty,
            keywords.Text ?? string.Empty,
            targetWords);

        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Node, node) || candidate.Node.PersistentId == node.PersistentId);
        if (row is not null) await _viewModel.SelectAsync(row);
        RefreshBoard(force: true);
    }

    private async Task ToggleCompilationAsync(CorkboardCardViewModel card)
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        await _repository.SetCompilationIncludedAsync(project, card.Node, !card.Node.IncludeInCompilation);
        RefreshBoard(force: true);
    }

    private async Task MoveCardAsync(CorkboardCardViewModel card, int offset)
    {
        var row = _viewModel.BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, card.Node));
        if (row is null) return;
        await _viewModel.SelectAsync(row);
        await _viewModel.MoveSelectedAsync(offset);
        RefreshBoard(force: true);
    }

    private async Task DeleteCardAsync(CorkboardCardViewModel card)
    {
        var confirmed = await DesktopDialogService.ConfirmAsync(
            _window,
            "Delete Corkboard Card",
            $"Delete '{card.Title}' and its on-disk content?",
            "Delete");
        if (!confirmed) return;
        await _viewModel.SelectCorkboardCardAsync(card);
        await _viewModel.DeleteSelectedAsync();
        _state.Positions.Remove(card.Node.PersistentId);
        SaveState();
        RefreshBoard(force: true);
    }

    private void CardPointerPressed(CorkboardCardViewModel card, Border border, PointerPressedEventArgs e, bool freeform)
    {
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        border.Focus();

        if (freeform)
        {
            _freeformDragCard = card;
            _freeformDragBorder = border;
            _freeformPointerOffset = e.GetPosition(border);
            e.Pointer.Capture(border);
            e.Handled = true;
            return;
        }

        _cardDragCandidate = card;
        _cardDragTrigger = e;
        _cardDragStart = e.GetPosition(border);
    }

    private async Task CardPointerMovedAsync(Border border, PointerEventArgs e, bool freeform)
    {
        if (freeform)
        {
            if (_freeformCanvas is null || _freeformDragCard is null || !ReferenceEquals(_freeformDragBorder, border)) return;
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            var position = e.GetPosition(_freeformCanvas);
            var left = Math.Max(6, position.X - _freeformPointerOffset.X);
            var top = Math.Max(6, position.Y - _freeformPointerOffset.Y);
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            _freeformCanvas.Width = Math.Max(_freeformCanvas.Width, left + CardWidth() + 80);
            _freeformCanvas.Height = Math.Max(_freeformCanvas.Height, top + CardMinimumHeight() + 120);
            e.Handled = true;
            return;
        }

        if (_cardDragCandidate is null || _cardDragTrigger is null) return;
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            ClearCardDragCandidate();
            return;
        }
        var current = e.GetPosition(border);
        var dx = current.X - _cardDragStart.X;
        var dy = current.Y - _cardDragStart.Y;
        if ((dx * dx) + (dy * dy) < 64) return;
        var candidate = _cardDragCandidate;
        var trigger = _cardDragTrigger;
        ClearCardDragCandidate();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(CardFormat, candidate));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    private void CardPointerReleased(CorkboardCardViewModel card, Border border, PointerReleasedEventArgs e, bool freeform)
    {
        if (!freeform || _freeformDragCard is null || !ReferenceEquals(_freeformDragBorder, border)) return;
        var left = Canvas.GetLeft(border);
        var top = Canvas.GetTop(border);
        _state.Positions[card.Node.PersistentId] = new CardPosition(
            double.IsNaN(left) ? 6 : Math.Max(0, left),
            double.IsNaN(top) ? 6 : Math.Max(0, top));
        e.Pointer.Capture(null);
        _freeformDragCard = null;
        _freeformDragBorder = null;
        SaveState();
        e.Handled = true;
    }

    private void CardDragOver(CorkboardCardViewModel target, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(CardFormat);
        e.DragEffects = source is not null && !ReferenceEquals(source.Node, target.Node)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private async Task CardDropAsync(CorkboardCardViewModel target, Control targetControl, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(CardFormat);
        if (source is null || ReferenceEquals(source.Node, target.Node)) return;
        var sourceRow = _viewModel.BinderRows.FirstOrDefault(row => ReferenceEquals(row.Node, source.Node));
        var targetRow = _viewModel.BinderRows.FirstOrDefault(row => ReferenceEquals(row.Node, target.Node));
        if (sourceRow is null || targetRow is null) return;
        var after = e.GetPosition(targetControl).Y > Math.Max(1, targetControl.Bounds.Height) / 2;
        await _viewModel.MoveBinderItemAsync(sourceRow, targetRow, dropIntoTarget: false, insertAfterTarget: after);
        e.DragEffects = DragDropEffects.Move;
        RefreshBoard(force: true);
    }

    private void ClearCardDragCandidate()
    {
        _cardDragCandidate = null;
        _cardDragTrigger = null;
    }

    private IEnumerable<CorkboardCardViewModel> ApplySearchAndFilter(IEnumerable<CorkboardCardViewModel> cards)
    {
        var query = _searchBox.Text?.Trim();
        foreach (var card in cards)
        {
            if (!MatchesFilter(card)) continue;
            if (!string.IsNullOrWhiteSpace(query) && !MatchesQuery(card, query)) continue;
            yield return card;
        }
    }

    private bool MatchesFilter(CorkboardCardViewModel card)
        => _state.Filter switch
        {
            "Included" => card.IsIncluded,
            "Excluded" => !card.IsIncluded,
            "Draft" => string.Equals(card.Status, "Draft", StringComparison.OrdinalIgnoreCase),
            "Research / Notes" => card.Node.Kind is NodeKind.Research or NodeKind.Note,
            _ => true
        };

    private static bool MatchesQuery(CorkboardCardViewModel card, string query)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (card.Title.Contains(query, comparison) ||
            card.Synopsis.Contains(query, comparison) ||
            card.Status.Contains(query, comparison) ||
            card.Label.Contains(query, comparison) ||
            card.Kind.Contains(query, comparison) ||
            card.Node.Keywords.Contains(query, comparison))
            return true;

        return card.Node.CustomMetadata.Any(pair =>
            pair.Key.Contains(query, comparison) || pair.Value.Contains(query, comparison));
    }

    private string GroupKey(CorkboardCardViewModel card, string groupBy)
        => groupBy switch
        {
            "Label" => string.IsNullOrWhiteSpace(card.Label) ? "No label" : card.Label,
            "Type" => card.Kind,
            "Compile" => card.IsIncluded ? "Included" : "Excluded",
            _ => string.IsNullOrWhiteSpace(card.Status) ? "No status" : card.Status
        };

    private Point PositionFor(CorkboardCardViewModel card, int index, double width)
    {
        if (_state.Positions.TryGetValue(card.Node.PersistentId, out var saved))
            return new Point(Math.Max(6, saved.X), Math.Max(6, saved.Y));

        var columns = 4;
        var x = 18 + (index % columns) * (width + 28);
        var y = 18 + (index / columns) * (CardMinimumHeight() + 34);
        return new Point(x, y);
    }

    private double CardWidth()
        => _state.CardSize switch
        {
            "Small" => 210,
            "Large" => 320,
            _ => 260
        };

    private double CardMinimumHeight()
        => _state.ShowDetails
            ? _state.CardSize switch
            {
                "Small" => 150,
                "Large" => 220,
                _ => 180
            }
            : _state.CardSize == "Large" ? 155 : 130;

    private static Border Tag(string text)
        => new()
        {
            Padding = new Thickness(5, 1),
            Margin = new Thickness(0, 0, 5, 3),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(75, 128, 128, 128)),
            Child = new TextBlock { Text = text, FontSize = 10, Opacity = 0.78 }
        };

    private static IBrush LabelBrush(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return new SolidColorBrush(Color.FromArgb(85, 128, 128, 128));

        uint hash = 2166136261;
        foreach (var ch in label)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= 16777619;
        }
        var hue = hash % 6;
        var color = hue switch
        {
            0 => Color.Parse("#4F8EF7"),
            1 => Color.Parse("#8B6CF2"),
            2 => Color.Parse("#D65A8A"),
            3 => Color.Parse("#D9823B"),
            4 => Color.Parse("#39A878"),
            _ => Color.Parse("#4AA3B8")
        };
        return new SolidColorBrush(color);
    }

    private static Control Field(string label, Control editor)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 11, Opacity = 0.75 });
        panel.Children.Add(editor);
        return panel;
    }

    private static Button SmallButton(string text, string tip, Action action)
    {
        var button = new Button
        {
            Content = text,
            Height = 28,
            MinHeight = 28,
            MinWidth = 52,
            Padding = new Thickness(7, 1)
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static string Selected(ComboBox combo, string fallback)
        => combo.SelectedItem?.ToString() ?? fallback;

    private static string? NormalizeChoice(string? value, params string[] allowed)
        => allowed.FirstOrDefault(item => string.Equals(item, value, StringComparison.Ordinal));

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is not IEnumerable items) yield break;
        foreach (var item in items)
            if (item is TabItem tab) yield return tab;
    }

    private static bool HeaderEquals(TabItem item, string value)
        => string.Equals(item.Header?.ToString(), value, StringComparison.OrdinalIgnoreCase);

    private BoardState LoadState(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return new BoardState();
        try
        {
            var path = StatePath(root);
            if (!File.Exists(path)) return new BoardState();
            return JsonSerializer.Deserialize<BoardState>(File.ReadAllText(path)) ?? new BoardState();
        }
        catch
        {
            return new BoardState();
        }
    }

    private void SaveState()
    {
        var root = _loadedProjectRoot;
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var path = StatePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        catch
        {
            // Corkboard layout preferences must never interfere with manuscript editing.
        }
    }

    private static string StatePath(string root)
        => Path.Combine(root, StateDirectory, StateFileName);

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _status.Text = $"Corkboard error: {ex.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        SaveState();
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private sealed class BoardState
    {
        public string Layout { get; set; } = "Cards";
        public string GroupBy { get; set; } = "None";
        public string Filter { get; set; } = "All";
        public string CardSize { get; set; } = "Medium";
        public bool ShowDetails { get; set; } = true;
        public Dictionary<string, CardPosition> Positions { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record CardPosition(double X, double Y);
}
