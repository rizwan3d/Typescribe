using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Models;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

public sealed class MainWindow : Window
{
    private static readonly DataFormat<BinderRowViewModel> BinderRowFormat =
        DataFormat.CreateInProcessFormat<BinderRowViewModel>("typescribe-binder-row");

    private readonly WorkspaceViewModel _viewModel;
    private readonly PdfPreviewRenderer _pdfPreviewRenderer = new();
    private readonly ListBox _binder = new();
    private readonly ListBox _outline = new();
    private readonly TextBox _editor = new();
    private readonly TextBlock _documentTitle = new();
    private readonly TextBlock _documentMeta = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _wordCount = new();
    private readonly TextBlock _engine = new();
    private readonly TextBlock _previewScope = new();
    private readonly TextBox _searchBox = new();
    private readonly ListBox _searchResults = new();
    private readonly CheckBox _regexSearch = new() { Content = "Regex" };
    private readonly CheckBox _caseSearch = new() { Content = "Case" };
    private readonly CheckBox _wholeWordSearch = new() { Content = "Whole word" };
    private readonly CheckBox _wholeBookPreview = new() { Content = "Whole book" };
    private readonly TabControl _leftTabs = new();
    private readonly TabControl _inspectorTabs = new();

    private readonly Button _renameBinderButton = new() { Content = "Rename" };
    private readonly Button _deleteBinderButton = new() { Content = "Delete" };
    private readonly Button _moveUpButton = new() { Content = "↑" };
    private readonly Button _moveDownButton = new() { Content = "↓" };
    private readonly Button _includeButton = new() { Content = "Exclude" };
    private readonly Button _styleButton = new() { Content = "Book Style" };
    private readonly Button _refreshPreviewButton = new() { Content = "Refresh Preview" };
    private readonly Button _exportPdfButton = new() { Content = "Publish PDF" };
    private readonly Button _downloadPdfEngineButton = new() { Content = "Download LuaLaTeX" };

    private readonly Image _pdfPreviewImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock _previewMessage = new()
    {
        Text = "Install LuaLaTeX to enable live PDF preview.",
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24)
    };
    private readonly TextBlock _previewStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly Button _previousPageButton = new() { Content = "‹", MinWidth = 34 };
    private readonly Button _nextPageButton = new() { Content = "›", MinWidth = 34 };
    private readonly Button _zoomOutButton = new() { Content = "−", MinWidth = 34, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _zoomInButton = new() { Content = "+", MinWidth = 34 };

    private Grid? _workspaceGrid;
    private Control? _binderPane;
    private Control? _inspectorPane;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;
    private MenuItem? _showBinderMenuItem;
    private MenuItem? _showInspectorMenuItem;
    private MenuItem? _wholeBookPreviewMenuItem;

    private CancellationTokenSource? _previewRenderCts;
    private PdfPreviewPage? _renderedPreviewPage;
    private BinderRowViewModel? _binderDragCandidate;
    private PointerPressedEventArgs? _binderDragTrigger;
    private Point _binderDragStart;
    private long _loadedPreviewVersion = -1;
    private long _appliedEditorNavigationVersion = -1;
    private int _previewPageIndex;
    private double _previewZoom = 1.0;
    private bool _showBinder = true;
    private bool _showInspector = true;
    private bool _updatingUi;

    public MainWindow(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Typescribe";
        Width = 1580;
        Height = 960;
        MinWidth = 1080;
        MinHeight = 680;
        Content = BuildLayout();

        _viewModel.StateChanged += OnStateChanged;
        _binder.SelectionChanged += BinderSelectionChanged;
        _binder.PointerPressed += BinderPointerPressed;
        _binder.PointerMoved += BinderPointerMoved;
        DragDrop.SetAllowDrop(_binder, true);
        DragDrop.AddDragOverHandler(_binder, BinderDragOver);
        DragDrop.AddDropHandler(_binder, BinderDrop);
        _outline.SelectionChanged += OutlineSelectionChanged;
        _editor.TextChanged += EditorTextChanged;
        _searchBox.KeyDown += SearchBoxKeyDown;
        _searchResults.DoubleTapped += SearchResultDoubleTapped;
        _wholeBookPreview.Click += (_, _) =>
        {
            if (!_updatingUi) _viewModel.SetPreviewWholeBook(_wholeBookPreview.IsChecked == true);
        };
        KeyDown += MainWindowKeyDown;
        Closed += WindowClosed;

        UpdateFromState();
        _ = RefreshPreviewIfNeededAsync();
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var menu = BuildMenu();
        Grid.SetRow(menu, 0);
        root.Children.Add(menu);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        _workspaceGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,5,*,5,430"),
            Margin = new Thickness(6, 0, 6, 0)
        };
        _workspaceGrid.ColumnDefinitions[0].MinWidth = 220;
        _workspaceGrid.ColumnDefinitions[2].MinWidth = 360;
        _workspaceGrid.ColumnDefinitions[4].MinWidth = 300;
        Grid.SetRow(_workspaceGrid, 2);
        root.Children.Add(_workspaceGrid);

        _binderPane = BuildLeftPane();
        Grid.SetColumn(_binderPane, 0);
        _workspaceGrid.Children.Add(_binderPane);

        _leftSplitter = BuildSplitter();
        Grid.SetColumn(_leftSplitter, 1);
        _workspaceGrid.Children.Add(_leftSplitter);

        var editorPanel = BuildEditorPanel();
        Grid.SetColumn(editorPanel, 2);
        _workspaceGrid.Children.Add(editorPanel);

        _rightSplitter = BuildSplitter();
        Grid.SetColumn(_rightSplitter, 3);
        _workspaceGrid.Children.Add(_rightSplitter);

        _inspectorPane = BuildInspectorPane();
        Grid.SetColumn(_inspectorPane, 4);
        _workspaceGrid.Children.Add(_inspectorPane);

        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 3);
        root.Children.Add(statusBar);
        return root;
    }

    private Menu BuildMenu()
    {
        var saveGesture = new KeyGesture(Key.S, KeyModifiers.Control);
        var openGesture = new KeyGesture(Key.O, KeyModifiers.Control);
        var newGesture = new KeyGesture(Key.N, KeyModifiers.Control);
        var findGesture = new KeyGesture(Key.F, KeyModifiers.Control);

        _showBinderMenuItem = ToggleMenuItem("_Binder", true, ToggleBinder);
        _showInspectorMenuItem = ToggleMenuItem("_Inspector", true, ToggleInspector);
        _wholeBookPreviewMenuItem = ToggleMenuItem("Preview _Whole Book", false, () =>
        {
            var enabled = _wholeBookPreviewMenuItem?.IsChecked == true;
            _viewModel.SetPreviewWholeBook(enabled);
        });

        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_New Project…", CreateProjectAsync, newGesture),
                MenuAction("_Open Project…", OpenProjectAsync, openGesture),
                new Separator(),
                MenuAction("_Save", () => _viewModel.SaveNowAsync(), saveGesture),
                new Separator(),
                MenuAction("Export _LaTeX…", ExportLatexAsync),
                MenuAction("_Publish PDF…", ExportPdfAsync),
                new Separator(),
                MenuAction("E_xit", () => { Close(); return Task.CompletedTask; })
            }
        };

        var edit = new MenuItem
        {
            Header = "_Edit",
            ItemsSource = new object[]
            {
                MenuAction("_Find in Project", FocusSearchAsync, findGesture),
                new Separator(),
                MenuAction("_Rename Binder Item", RenameBinderNodeAsync, new KeyGesture(Key.F2)),
                MenuAction("_Delete Binder Item", DeleteBinderNodeAsync),
                new Separator(),
                MenuAction("Move Binder Item _Up", () => _viewModel.MoveSelectedAsync(-1)),
                MenuAction("Move Binder Item _Down", () => _viewModel.MoveSelectedAsync(1))
            }
        };

        var view = new MenuItem
        {
            Header = "_View",
            ItemsSource = new object[]
            {
                _showBinderMenuItem,
                _showInspectorMenuItem,
                new Separator(),
                _wholeBookPreviewMenuItem,
                MenuAction("_Refresh PDF Preview", () => _viewModel.RefreshLivePdfPreviewAsync(), new KeyGesture(Key.R, KeyModifiers.Control | KeyModifiers.Shift)),
                new Separator(),
                MenuAction("Zoom _In", () => { ChangeZoom(0.1); return Task.CompletedTask; }, new KeyGesture(Key.OemPlus, KeyModifiers.Control)),
                MenuAction("Zoom _Out", () => { ChangeZoom(-0.1); return Task.CompletedTask; }, new KeyGesture(Key.OemMinus, KeyModifiers.Control))
            }
        };

        var insert = new MenuItem
        {
            Header = "_Insert",
            ItemsSource = new object[]
            {
                MenuAction("New _Chapter…", () => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter")),
                MenuAction("New _Part…", () => AddBinderNodeAsync(NodeKind.Part, "New Part")),
                MenuAction("New _Folder…", () => AddBinderNodeAsync(NodeKind.Folder, "New Folder")),
                new Separator(),
                MenuAction("_Heading 1", () => { ApplyHeading(1); return Task.CompletedTask; }),
                MenuAction("Heading _2", () => { ApplyHeading(2); return Task.CompletedTask; }),
                MenuAction("Heading _3", () => { ApplyHeading(3); return Task.CompletedTask; })
            }
        };

        var format = new MenuItem
        {
            Header = "F_ormat",
            ItemsSource = new object[]
            {
                MenuAction("_Bold", () => { WrapSelection("**", "**"); return Task.CompletedTask; }, new KeyGesture(Key.B, KeyModifiers.Control)),
                MenuAction("_Italic", () => { WrapSelection("*", "*"); return Task.CompletedTask; }, new KeyGesture(Key.I, KeyModifiers.Control)),
                MenuAction("Inline _Code", () => { WrapSelection("`", "`"); return Task.CompletedTask; }),
                new Separator(),
                MenuAction("Block _Quote", () => { PrefixSelectedLines("> "); return Task.CompletedTask; }),
                MenuAction("_Bullet List", () => { PrefixSelectedLines("- "); return Task.CompletedTask; })
            }
        };

        var project = new MenuItem
        {
            Header = "_Project",
            ItemsSource = new object[]
            {
                MenuAction("_Book Style…", EditStyleAsync),
                MenuAction("_Include / Exclude Selection", () => _viewModel.ToggleSelectedCompilationAsync()),
                new Separator(),
                MenuAction("Download _LuaLaTeX", () => _viewModel.EnsurePdfEngineAsync())
            }
        };

        var publish = new MenuItem
        {
            Header = "_Publish",
            ItemsSource = new object[]
            {
                MenuAction("Refresh _Preview", () => _viewModel.RefreshLivePdfPreviewAsync()),
                MenuAction("Publish _PDF…", ExportPdfAsync),
                MenuAction("Export _LaTeX…", ExportLatexAsync)
            }
        };

        var help = new MenuItem
        {
            Header = "_Help",
            ItemsSource = new object[]
            {
                MenuAction("_About Typescribe", ShowAboutAsync)
            }
        };

        return new Menu { ItemsSource = new object[] { file, edit, view, insert, format, project, publish, help } };
    }

    private Control BuildToolbar()
    {
        var bar = new WrapPanel
        {
            Margin = new Thickness(8, 6),
            Orientation = Orientation.Horizontal
        };

        var newButton = ToolbarButton("New");
        var openButton = ToolbarButton("Open");
        var saveButton = ToolbarButton("Save");
        var addChapter = ToolbarButton("+ Chapter");
        var searchButton = ToolbarButton("Search");
        var binderButton = ToolbarButton("Binder");
        var inspectorButton = ToolbarButton("Inspector");

        newButton.Click += async (_, _) => await RunUiTaskAsync(CreateProjectAsync);
        openButton.Click += async (_, _) => await RunUiTaskAsync(OpenProjectAsync);
        saveButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.SaveNowAsync());
        addChapter.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter"));
        searchButton.Click += async (_, _) => await RunUiTaskAsync(FocusSearchAsync);
        binderButton.Click += (_, _) => ToggleBinder();
        inspectorButton.Click += (_, _) => ToggleInspector();
        _styleButton.Click += async (_, _) => await RunUiTaskAsync(EditStyleAsync);
        _refreshPreviewButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.RefreshLivePdfPreviewAsync());
        _exportPdfButton.Click += async (_, _) => await RunUiTaskAsync(ExportPdfAsync);
        _downloadPdfEngineButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.EnsurePdfEngineAsync());

        foreach (var button in new[]
                 {
                     newButton, openButton, saveButton, addChapter, searchButton, binderButton,
                     inspectorButton, _styleButton, _refreshPreviewButton, _exportPdfButton, _downloadPdfEngineButton
                 })
        {
            bar.Children.Add(button);
        }
        return bar;
    }

    private Control BuildLeftPane()
    {
        _binder.HorizontalAlignment = HorizontalAlignment.Stretch;
        _binder.VerticalAlignment = VerticalAlignment.Stretch;
        _binder.ContextMenu = BuildBinderContextMenu();

        var binderActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        var addChapter = SmallButton("+ Chapter");
        var addFolder = SmallButton("+ Folder");
        var addPart = SmallButton("+ Part");

        addChapter.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter"));
        addFolder.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Folder, "New Folder"));
        addPart.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Part, "New Part"));
        _renameBinderButton.Click += async (_, _) => await RunUiTaskAsync(RenameBinderNodeAsync);
        _deleteBinderButton.Click += async (_, _) => await RunUiTaskAsync(DeleteBinderNodeAsync);
        _moveUpButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.MoveSelectedAsync(-1));
        _moveDownButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.MoveSelectedAsync(1));
        _includeButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.ToggleSelectedCompilationAsync());

        foreach (var button in new[]
                 {
                     addChapter, addFolder, addPart, _renameBinderButton, _deleteBinderButton,
                     _moveUpButton, _moveDownButton, _includeButton
                 })
        {
            button.Margin = new Thickness(0, 0, 4, 4);
            binderActions.Children.Add(button);
        }

        var binderPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        binderPanel.Children.Add(binderActions);
        Grid.SetRow(_binder, 1);
        binderPanel.Children.Add(_binder);

        var searchButton = new Button { Content = "Search", HorizontalAlignment = HorizontalAlignment.Right };
        searchButton.Click += async (_, _) => await RunUiTaskAsync(SearchAsync);
        _searchBox.Watermark = "Search manuscript";

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6) };
        searchRow.Children.Add(_searchBox);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        var searchOptions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 6) };
        _regexSearch.Margin = new Thickness(0, 0, 8, 0);
        _caseSearch.Margin = new Thickness(0, 0, 8, 0);
        searchOptions.Children.Add(_regexSearch);
        searchOptions.Children.Add(_caseSearch);
        searchOptions.Children.Add(_wholeWordSearch);

        var searchPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        searchPanel.Children.Add(searchRow);
        Grid.SetRow(searchOptions, 1);
        searchPanel.Children.Add(searchOptions);
        Grid.SetRow(_searchResults, 2);
        searchPanel.Children.Add(_searchResults);

        _leftTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "Binder", Content = binderPanel },
            new TabItem { Header = "Search", Content = searchPanel }
        };
        _leftTabs.SelectedIndex = 0;

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Child = _leftTabs
        };
    }

    private Control BuildEditorPanel()
    {
        _documentTitle.FontSize = 18;
        _documentTitle.FontWeight = FontWeight.SemiBold;
        _documentMeta.Opacity = 0.7;
        _documentMeta.Margin = new Thickness(12, 0, 0, 0);
        _documentMeta.VerticalAlignment = VerticalAlignment.Center;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 8)
        };
        header.Children.Add(_documentTitle);
        Grid.SetColumn(_documentMeta, 1);
        header.Children.Add(_documentMeta);

        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.TextWrapping = TextWrapping.Wrap;
        _editor.FontFamily = new FontFamily("monospace");
        _editor.FontSize = 15;
        _editor.VerticalContentAlignment = VerticalAlignment.Top;
        _editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        _editor.VerticalAlignment = VerticalAlignment.Stretch;
        _editor.Margin = new Thickness(10, 0, 10, 10);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        panel.Children.Add(header);
        Grid.SetRow(_editor, 1);
        panel.Children.Add(_editor);
        return panel;
    }

    private Control BuildInspectorPane()
    {
        _outline.HorizontalAlignment = HorizontalAlignment.Stretch;
        _outline.VerticalAlignment = VerticalAlignment.Stretch;

        var clearFocus = new Button
        {
            Content = "Show Full Document",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8)
        };
        clearFocus.Click += (_, _) =>
        {
            _previewPageIndex = 0;
            _loadedPreviewVersion = -1;
            _viewModel.SelectOutline(null);
        };

        var outlineHelp = new TextBlock
        {
            Text = "Select a heading to move the editor there and focus the live PDF preview on that section.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 8),
            Opacity = 0.75
        };

        var outlinePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        outlinePanel.Children.Add(clearFocus);
        Grid.SetRow(outlineHelp, 1);
        outlinePanel.Children.Add(outlineHelp);
        Grid.SetRow(_outline, 2);
        outlinePanel.Children.Add(_outline);

        _inspectorTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "PDF Preview", Content = BuildPreviewPanel() },
            new TabItem { Header = "Outline", Content = outlinePanel }
        };
        _inspectorTabs.SelectedIndex = 0;

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Child = _inspectorTabs
        };
    }

    private Control BuildPreviewPanel()
    {
        _previousPageButton.Click += async (_, _) => await ChangePreviewPageAsync(-1);
        _nextPageButton.Click += async (_, _) => await ChangePreviewPageAsync(1);
        _zoomOutButton.Click += (_, _) => ChangeZoom(-0.1);
        _zoomInButton.Click += (_, _) => ChangeZoom(0.1);

        _wholeBookPreview.Margin = new Thickness(8, 0, 0, 0);
        _previewScope.VerticalAlignment = VerticalAlignment.Center;
        _previewScope.Margin = new Thickness(8, 0);
        _previewScope.FontWeight = FontWeight.SemiBold;

        var controls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(8, 6)
        };
        controls.Children.Add(_previousPageButton);
        Grid.SetColumn(_pageLabel, 1);
        controls.Children.Add(_pageLabel);
        Grid.SetColumn(_nextPageButton, 2);
        controls.Children.Add(_nextPageButton);
        Grid.SetColumn(_zoomOutButton, 3);
        controls.Children.Add(_zoomOutButton);
        Grid.SetColumn(_zoomInButton, 4);
        controls.Children.Add(_zoomInButton);
        Grid.SetColumn(_wholeBookPreview, 5);
        controls.Children.Add(_wholeBookPreview);
        Grid.SetColumn(_previewScope, 6);
        controls.Children.Add(_previewScope);
        Grid.SetColumn(_previewStatus, 7);
        _previewStatus.HorizontalAlignment = HorizontalAlignment.Right;
        controls.Children.Add(_previewStatus);

        var previewSurface = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#E8E8E8"))
        };
        previewSurface.Children.Add(_pdfPreviewImage);
        previewSurface.Children.Add(_previewMessage);

        var scroll = new ScrollViewer
        {
            Content = previewSurface,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        panel.Children.Add(controls);
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        return panel;
    }

    private Control BuildStatusBar()
    {
        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(8, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        bar.Children.Add(_status);
        Grid.SetColumn(_wordCount, 1);
        _wordCount.Margin = new Thickness(12, 0);
        bar.Children.Add(_wordCount);
        Grid.SetColumn(_engine, 2);
        bar.Children.Add(_engine);
        return bar;
    }

    private static GridSplitter BuildSplitter() => new()
    {
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ShowsPreview = true,
        Background = Brushes.Gray,
        Opacity = 0.35
    };

    private ContextMenu BuildBinderContextMenu()
    {
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                MenuAction("New Chapter…", () => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter")),
                MenuAction("New Part…", () => AddBinderNodeAsync(NodeKind.Part, "New Part")),
                MenuAction("New Folder…", () => AddBinderNodeAsync(NodeKind.Folder, "New Folder")),
                new Separator(),
                MenuAction("Rename", RenameBinderNodeAsync),
                MenuAction("Delete", DeleteBinderNodeAsync),
                new Separator(),
                MenuAction("Move Up", () => _viewModel.MoveSelectedAsync(-1)),
                MenuAction("Move Down", () => _viewModel.MoveSelectedAsync(1)),
                MenuAction("Include / Exclude", () => _viewModel.ToggleSelectedCompilationAsync())
            }
        };
    }

    private async Task CreateProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the new Typescribe project",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) await _viewModel.CreateProjectAsync(folder.Path.LocalPath);
    }

    private async Task OpenProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Typescribe project",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) await _viewModel.OpenProjectAsync(folder.Path.LocalPath);
    }

    private async Task AddBinderNodeAsync(NodeKind kind, string initialTitle)
    {
        var title = await DesktopDialogService.PromptAsync(this, $"Add {kind}", "Title", initialTitle);
        if (title is not null) await _viewModel.AddNodeAsync(kind, title);
    }

    private async Task RenameBinderNodeAsync()
    {
        if (!_viewModel.HasSelection) return;
        var title = await DesktopDialogService.PromptAsync(this, "Rename Binder Item", "Title", _viewModel.SelectedTitle);
        if (title is not null) await _viewModel.RenameSelectedAsync(title);
    }

    private async Task DeleteBinderNodeAsync()
    {
        if (!_viewModel.HasSelection) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(
            this,
            "Delete Binder Item",
            $"Delete '{_viewModel.SelectedTitle}' and its on-disk content? This cannot be undone.");
        if (confirmed) await _viewModel.DeleteSelectedAsync();
    }

    private async Task EditStyleAsync()
    {
        if (!_viewModel.HasProject) return;
        var style = await DesktopDialogService.EditStyleAsync(this, _viewModel.CurrentStyle);
        if (style is not null) await _viewModel.UpdateStyleAsync(style);
    }

    private async Task ExportPdfAsync()
    {
        var path = await PickSavePathAsync("Publish Book PDF", "PDF document", "pdf");
        if (path is not null) await _viewModel.ExportPdfAsync(path);
    }

    private async Task ExportLatexAsync()
    {
        var path = await PickSavePathAsync("Export Book LaTeX source", "LaTeX source", "tex");
        if (path is not null) await _viewModel.ExportLatexAsync(path);
    }

    private Task FocusSearchAsync()
    {
        if (!_showBinder) ToggleBinder();
        _leftTabs.SelectedIndex = 1;
        _searchBox.Focus();
        return Task.CompletedTask;
    }

    private async Task ShowAboutAsync()
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "About Typescribe",
            Width = 460,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Typescribe", FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Cross-platform long-form authoring and LuaLaTeX publishing.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = ".NET 10 • Avalonia • Native AOT", Opacity = 0.7 },
                    close
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async Task<string?> PickSavePathAsync(string title, string typeName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = $"{SanitizeFileName(_viewModel.ProjectTitle)}.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }]
        });
        return file?.Path.LocalPath;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "typescribe-book" : cleaned;
    }

    private async Task SearchAsync()
    {
        var options = new SearchOptions(
            MatchCase: _caseSearch.IsChecked == true,
            UseRegex: _regexSearch.IsChecked == true,
            WholeWord: _wholeWordSearch.IsChecked == true);
        await _viewModel.SearchAsync(_searchBox.Text ?? string.Empty, options);
        _searchResults.ItemsSource = _viewModel.SearchResults
            .Select(static hit => hit.Line == 0
                ? $"{hit.Title} [title]  {hit.Preview}"
                : $"{hit.Title}:{hit.Line}  {hit.Preview}")
            .ToArray();
    }

    private async void BinderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        if (_binder.SelectedItem is BinderRowViewModel row)
        {
            _previewPageIndex = 0;
            _loadedPreviewVersion = -1;
            await RunUiTaskAsync(() => _viewModel.SelectAsync(row));
        }
    }

    private void BinderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_binder);
        if (!point.Properties.IsLeftButtonPressed) return;
        var row = FindBinderRow(e.Source);
        if (row is null) return;
        _binderDragCandidate = row;
        _binderDragTrigger = e;
        _binderDragStart = e.GetPosition(_binder);
    }

    private async void BinderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_binderDragCandidate is null || _binderDragTrigger is null) return;
        if (!e.GetCurrentPoint(_binder).Properties.IsLeftButtonPressed)
        {
            ClearBinderDragCandidate();
            return;
        }

        var current = e.GetPosition(_binder);
        var dx = current.X - _binderDragStart.X;
        var dy = current.Y - _binderDragStart.Y;
        if ((dx * dx) + (dy * dy) < 64) return;

        var candidate = _binderDragCandidate;
        var trigger = _binderDragTrigger;
        ClearBinderDragCandidate();

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(BinderRowFormat, candidate));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    private void BinderDragOver(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(BinderRowFormat);
        var target = FindBinderRow(e.Source);
        e.DragEffects = _viewModel.CanDropBinderItem(source, target)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private async void BinderDrop(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(BinderRowFormat);
        var target = FindBinderRow(e.Source);
        if (!_viewModel.CanDropBinderItem(source, target) || source is null || target is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var item = FindBinderListItem(e.Source);
        var placement = GetBinderDropPlacement(e, item, target);
        e.DragEffects = DragDropEffects.Move;
        await RunUiTaskAsync(() => _viewModel.MoveBinderItemAsync(
            source,
            target,
            placement == BinderDropPlacement.Inside,
            placement == BinderDropPlacement.After));
    }

    private async void OutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        if (_outline.SelectedItem is OutlineItemViewModel item)
        {
            _previewPageIndex = 0;
            _loadedPreviewVersion = -1;
            _viewModel.SelectOutline(item);
            _inspectorTabs.SelectedIndex = 0;
            await RefreshPreviewIfNeededAsync();
        }
    }

    private void EditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingUi) return;
        _viewModel.UpdateEditorText(_editor.Text ?? string.Empty);
    }

    private async void SearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunUiTaskAsync(SearchAsync);
        }
    }

    private async void SearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        var index = _searchResults.SelectedIndex;
        if (index >= 0 && index < _viewModel.SearchResults.Count)
        {
            await RunUiTaskAsync(() => _viewModel.GoToSearchHitAsync(_viewModel.SearchResults[index]));
            _leftTabs.SelectedIndex = 0;
        }
    }

    private async void MainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!primary && e.Key != Key.F2) return;

        if (primary && e.Key == Key.S)
        {
            e.Handled = true;
            await RunUiTaskAsync(() => _viewModel.SaveNowAsync());
        }
        else if (primary && e.Key == Key.O)
        {
            e.Handled = true;
            await RunUiTaskAsync(OpenProjectAsync);
        }
        else if (primary && e.Key == Key.N)
        {
            e.Handled = true;
            await RunUiTaskAsync(CreateProjectAsync);
        }
        else if (primary && e.Key == Key.F)
        {
            e.Handled = true;
            await FocusSearchAsync();
        }
        else if (primary && e.Key == Key.B)
        {
            e.Handled = true;
            WrapSelection("**", "**");
        }
        else if (primary && e.Key == Key.I)
        {
            e.Handled = true;
            WrapSelection("*", "*");
        }
        else if (e.Key == Key.F2)
        {
            e.Handled = true;
            await RunUiTaskAsync(RenameBinderNodeAsync);
        }
    }

    private async Task ChangePreviewPageAsync(int offset)
    {
        if (_renderedPreviewPage is null) return;
        var next = Math.Clamp(_previewPageIndex + offset, 0, _renderedPreviewPage.PageCount - 1);
        if (next == _previewPageIndex) return;
        _previewPageIndex = next;
        await RefreshPreviewIfNeededAsync(force: true);
    }

    private void ChangeZoom(double delta)
    {
        _previewZoom = Math.Clamp(_previewZoom + delta, 0.5, 2.5);
        ApplyPreviewZoom();
    }

    private void ApplyPreviewZoom()
    {
        _pdfPreviewImage.Width = 760 * _previewZoom;
    }

    private void ToggleBinder()
    {
        if (_workspaceGrid is null || _binderPane is null || _leftSplitter is null) return;
        _showBinder = !_showBinder;
        _binderPane.IsVisible = _showBinder;
        _leftSplitter.IsVisible = _showBinder;
        _workspaceGrid.ColumnDefinitions[0].Width = new GridLength(_showBinder ? 300 : 0);
        _workspaceGrid.ColumnDefinitions[1].Width = new GridLength(_showBinder ? 5 : 0);
        if (_showBinderMenuItem is not null) _showBinderMenuItem.IsChecked = _showBinder;
    }

    private void ToggleInspector()
    {
        if (_workspaceGrid is null || _inspectorPane is null || _rightSplitter is null) return;
        _showInspector = !_showInspector;
        _inspectorPane.IsVisible = _showInspector;
        _rightSplitter.IsVisible = _showInspector;
        _workspaceGrid.ColumnDefinitions[3].Width = new GridLength(_showInspector ? 5 : 0);
        _workspaceGrid.ColumnDefinitions[4].Width = new GridLength(_showInspector ? 430 : 0);
        if (_showInspectorMenuItem is not null) _showInspectorMenuItem.IsChecked = _showInspector;
    }

    private void WrapSelection(string prefix, string suffix)
    {
        if (!_viewModel.HasDocument) return;
        var text = _editor.Text ?? string.Empty;
        var start = Math.Min(_editor.SelectionStart, _editor.SelectionEnd);
        var end = Math.Max(_editor.SelectionStart, _editor.SelectionEnd);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        var selected = text[start..end];
        _editor.Text = text[..start] + prefix + selected + suffix + text[end..];
        _editor.SelectionStart = start + prefix.Length;
        _editor.SelectionEnd = start + prefix.Length + selected.Length;
        _editor.Focus();
    }

    private void ApplyHeading(int level)
    {
        if (!_viewModel.HasDocument) return;
        var text = _editor.Text ?? string.Empty;
        var caret = Math.Clamp(_editor.CaretIndex, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[lineStart..lineEnd];
        var content = line.TrimStart();
        var hashCount = 0;
        while (hashCount < content.Length && content[hashCount] == '#') hashCount++;
        if (hashCount > 0 && hashCount < content.Length && content[hashCount] == ' ')
            content = content[(hashCount + 1)..];
        var replacement = new string('#', Math.Clamp(level, 1, 6)) + " " + content;
        _editor.Text = text[..lineStart] + replacement + text[lineEnd..];
        _editor.CaretIndex = lineStart + replacement.Length;
        _editor.Focus();
    }

    private void PrefixSelectedLines(string prefix)
    {
        if (!_viewModel.HasDocument) return;
        var text = _editor.Text ?? string.Empty;
        var start = Math.Min(_editor.SelectionStart, _editor.SelectionEnd);
        var end = Math.Max(_editor.SelectionStart, _editor.SelectionEnd);
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = text.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = text.Length;
        var block = text[lineStart..lineEnd];
        var replacement = prefix + block.Replace("\n", "\n" + prefix, StringComparison.Ordinal);
        _editor.Text = text[..lineStart] + replacement + text[lineEnd..];
        _editor.SelectionStart = lineStart;
        _editor.SelectionEnd = lineStart + replacement.Length;
        _editor.Focus();
    }

    private void NavigateEditorToLine(int line)
    {
        var text = _editor.Text ?? string.Empty;
        var targetLine = Math.Max(1, line);
        var index = 0;
        for (var currentLine = 1; currentLine < targetLine && index < text.Length; currentLine++)
        {
            var next = text.IndexOf('\n', index);
            if (next < 0)
            {
                index = text.Length;
                break;
            }
            index = next + 1;
        }
        _editor.CaretIndex = index;
        _editor.SelectionStart = index;
        _editor.SelectionEnd = index;
        _editor.Focus();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        UpdateFromState();
        _ = RefreshPreviewIfNeededAsync();
    });

    private void UpdateFromState()
    {
        _updatingUi = true;
        try
        {
            Title = $"{_viewModel.ProjectTitle} — Typescribe";
            _binder.ItemsSource = _viewModel.BinderRows;
            _binder.SelectedItem = _viewModel.SelectedRow;
            _outline.ItemsSource = _viewModel.OutlineItems;
            _outline.SelectedItem = _viewModel.SelectedOutline;

            if (!string.Equals(_editor.Text, _viewModel.EditorText, StringComparison.Ordinal))
                _editor.Text = _viewModel.EditorText;
            _editor.IsEnabled = _viewModel.HasDocument;

            _documentTitle.Text = _viewModel.HasDocument
                ? _viewModel.SelectedTitle
                : _viewModel.HasSelection
                    ? _viewModel.SelectedTitle
                    : "No document selected";
            _documentMeta.Text = _viewModel.HasDocument
                ? (_viewModel.SelectedIncluded ? "Included in Compile" : "Excluded from Compile")
                : string.Empty;

            _status.Text = _viewModel.Status;
            _wordCount.Text = $"{_viewModel.WordCount:N0} words";
            _engine.Text = _viewModel.CanPublishPdf
                ? $"PDF: {_viewModel.PublishingEngineName}"
                : "PDF: LuaLaTeX not installed";
            _previewScope.Text = _viewModel.PreviewScopeLabel;

            _downloadPdfEngineButton.IsVisible = !_viewModel.CanPublishPdf;
            _downloadPdfEngineButton.IsEnabled = !_viewModel.CanPublishPdf;
            _refreshPreviewButton.IsEnabled = _viewModel.HasProject;
            _exportPdfButton.IsEnabled = _viewModel.HasProject;
            _styleButton.IsEnabled = _viewModel.HasProject;

            _renameBinderButton.IsEnabled = _viewModel.HasSelection;
            _deleteBinderButton.IsEnabled = _viewModel.HasSelection;
            _moveUpButton.IsEnabled = _viewModel.HasSelection;
            _moveDownButton.IsEnabled = _viewModel.HasSelection;
            _includeButton.IsEnabled = _viewModel.HasSelection;
            _includeButton.Content = _viewModel.SelectedIncluded ? "Exclude" : "Include";

            _wholeBookPreview.IsChecked = _viewModel.PreviewWholeBook;
            if (_wholeBookPreviewMenuItem is not null) _wholeBookPreviewMenuItem.IsChecked = _viewModel.PreviewWholeBook;

            _previewStatus.Text = _viewModel.IsLivePreviewBuilding
                ? "Compiling…"
                : _viewModel.LivePreviewError is not null
                    ? "Preview error"
                    : _viewModel.LivePreviewPdfPath is not null
                        ? "Live"
                        : string.Empty;

            if (_renderedPreviewPage is null)
            {
                _previewMessage.IsVisible = true;
                _previewMessage.Text = _viewModel.LivePreviewError
                    ?? (_viewModel.CanPublishPdf
                        ? _viewModel.PreviewText
                        : "Install LuaLaTeX to enable live PDF preview.");
            }

            if (_appliedEditorNavigationVersion != _viewModel.EditorNavigationVersion)
            {
                _appliedEditorNavigationVersion = _viewModel.EditorNavigationVersion;
                var line = _viewModel.EditorNavigationLine;
                Dispatcher.UIThread.Post(() => NavigateEditorToLine(line), DispatcherPriority.Background);
            }
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private async Task RefreshPreviewIfNeededAsync(bool force = false)
    {
        var path = _viewModel.LivePreviewPdfPath;
        var version = _viewModel.LivePreviewVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!force && version == _loadedPreviewVersion) return;

        _previewRenderCts?.Cancel();
        _previewRenderCts?.Dispose();
        _previewRenderCts = new CancellationTokenSource();
        var token = _previewRenderCts.Token;

        try
        {
            var page = await _pdfPreviewRenderer.RenderAsync(path, _previewPageIndex, token);
            if (token.IsCancellationRequested)
            {
                page.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _renderedPreviewPage?.Dispose();
                _renderedPreviewPage = page;
                _pdfPreviewImage.Source = page.Bitmap;
                _previewPageIndex = page.PageIndex;
                _loadedPreviewVersion = version;
                _pageLabel.Text = $"{page.PageIndex + 1} / {page.PageCount}";
                _previousPageButton.IsEnabled = page.PageIndex > 0;
                _nextPageButton.IsEnabled = page.PageIndex + 1 < page.PageCount;
                _previewMessage.IsVisible = false;
                ApplyPreviewZoom();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _previewMessage.Text = $"Could not render PDF preview: {ex.Message}";
                _previewMessage.IsVisible = true;
            });
        }
    }

    private async Task RunUiTaskAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "Typescribe",
            Width = 640,
            Height = 320,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new ScrollViewer
                    {
                        MaxHeight = 220,
                        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
                    },
                    close
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void WindowClosed(object? sender, EventArgs e)
    {
        _viewModel.StateChanged -= OnStateChanged;
        _previewRenderCts?.Cancel();
        _previewRenderCts?.Dispose();
        _renderedPreviewPage?.Dispose();
        await _viewModel.DisposeAsync();
    }

    private void ClearBinderDragCandidate()
    {
        _binderDragCandidate = null;
        _binderDragTrigger = null;
    }

    private static BinderRowViewModel? FindBinderRow(object? source)
        => FindBinderListItem(source)?.Content as BinderRowViewModel;

    private static ListBoxItem? FindBinderListItem(object? source)
        => source is Visual visual ? visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) : null;

    private static BinderDropPlacement GetBinderDropPlacement(
        DragEventArgs e,
        ListBoxItem? item,
        BinderRowViewModel target)
    {
        if (item is null) return target.Node.IsContainer ? BinderDropPlacement.Inside : BinderDropPlacement.Before;
        var height = Math.Max(1, item.Bounds.Height);
        var y = e.GetPosition(item).Y;
        if (target.Node.IsContainer && y >= height * 0.25 && y <= height * 0.75)
            return BinderDropPlacement.Inside;
        return y > height * 0.5 ? BinderDropPlacement.After : BinderDropPlacement.Before;
    }

    private MenuItem MenuAction(string header, Func<Task> action, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += async (_, _) => await RunUiTaskAsync(action);
        return item;
    }

    private static MenuItem ToggleMenuItem(string header, bool isChecked, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static Button ToolbarButton(string content)
        => new() { Content = content, Margin = new Thickness(0, 0, 6, 0) };

    private static Button SmallButton(string content)
        => new() { Content = content, Margin = new Thickness(0, 0, 4, 4) };

    private enum BinderDropPlacement
    {
        Before,
        Inside,
        After
    }
}
