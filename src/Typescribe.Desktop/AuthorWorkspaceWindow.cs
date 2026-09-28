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

public sealed class AuthorWorkspaceWindow : Window
{
    private static readonly DataFormat<BinderRowViewModel> BinderRowFormat =
        DataFormat.CreateInProcessFormat<BinderRowViewModel>("typescribe-binder-row");

    private readonly WorkspaceViewModel _viewModel;
    private readonly PdfPreviewRenderer _pdfPreviewRenderer = new();

    private readonly ListBox _binder = new();
    private readonly TextBox _searchBox = new();
    private readonly ListBox _searchResults = new();
    private readonly CheckBox _regexSearch = new() { Content = "Regex" };
    private readonly CheckBox _caseSearch = new() { Content = "Case" };
    private readonly CheckBox _wholeWordSearch = new() { Content = "Whole word" };
    private readonly TabControl _leftTabs = new();

    private readonly TextBox _editor = new();
    private readonly TextBlock _documentTitle = new();
    private readonly TextBlock _documentMeta = new();
    private readonly TabControl _centerTabs = new();
    private readonly WrapPanel _corkboardPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _corkboardTitle = new();

    private readonly TabControl _inspectorTabs = new();
    private readonly TextBox _synopsisBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 82 };
    private readonly TextBox _notesBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };
    private readonly TextBox _statusBox = new() { Watermark = "Draft / Revised / Final" };
    private readonly TextBox _labelBox = new() { Watermark = "Storyline, POV, research…" };
    private readonly TextBox _keywordsBox = new() { Watermark = "comma-separated keywords" };
    private readonly TextBox _targetBox = new() { Watermark = "0" };
    private readonly ProgressBar _targetProgress = new() { Minimum = 0, Maximum = 1, Height = 7 };
    private readonly TextBlock _targetText = new();
    private readonly ListBox _outline = new();
    private readonly ListBox _snapshots = new();

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
    private readonly TextBlock _previewScope = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly CheckBox _wholeBookPreview = new() { Content = "Whole book", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _previousPageButton = new() { Content = "‹", MinWidth = 34 };
    private readonly Button _nextPageButton = new() { Content = "›", MinWidth = 34 };
    private readonly Button _zoomOutButton = new() { Content = "−", MinWidth = 34, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _zoomInButton = new() { Content = "+", MinWidth = 34 };

    private readonly TextBlock _status = new();
    private readonly TextBlock _wordCount = new();
    private readonly TextBlock _engine = new();

    private Menu? _menuBar;
    private Control? _toolbar;
    private Control? _statusBar;
    private Grid? _workspaceGrid;
    private Control? _binderPane;
    private Control? _inspectorPane;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;
    private MenuItem? _showBinderMenuItem;
    private MenuItem? _showInspectorMenuItem;
    private MenuItem? _wholeBookPreviewMenuItem;
    private MenuItem? _compositionMenuItem;

    private CancellationTokenSource? _previewRenderCts;
    private CancellationTokenSource? _metadataSaveCts;
    private PdfPreviewPage? _renderedPreviewPage;
    private BinderRowViewModel? _binderDragCandidate;
    private PointerPressedEventArgs? _binderDragTrigger;
    private Point _binderDragStart;
    private long _loadedPreviewVersion = -1;
    private long _loadedCorkboardVersion = -1;
    private long _appliedEditorNavigationVersion = -1;
    private string? _lastSelectedPersistentId;
    private int _previewPageIndex;
    private double _previewZoom = 1.0;
    private bool _showBinder = true;
    private bool _showInspector = true;
    private bool _compositionMode;
    private bool _savedShowBinder = true;
    private bool _savedShowInspector = true;
    private WindowState _savedWindowState = WindowState.Normal;
    private bool _updatingUi;

    public AuthorWorkspaceWindow(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Typescribe";
        Width = 1620;
        Height = 980;
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

        foreach (var box in new[] { _synopsisBox, _notesBox, _statusBox, _labelBox, _keywordsBox, _targetBox })
            box.TextChanged += MetadataTextChanged;

        KeyDown += WindowKeyDown;
        Closed += WindowClosed;
        UpdateFromState();
        _ = RefreshPreviewIfNeededAsync();
    }

    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };

        _menuBar = BuildMenu();
        Grid.SetRow(_menuBar, 0);
        root.Children.Add(_menuBar);

        _toolbar = BuildToolbar();
        Grid.SetRow(_toolbar, 1);
        root.Children.Add(_toolbar);

        _workspaceGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,5,*,5,440"),
            Margin = new Thickness(6, 0, 6, 0)
        };
        _workspaceGrid.ColumnDefinitions[0].MinWidth = 220;
        _workspaceGrid.ColumnDefinitions[2].MinWidth = 420;
        _workspaceGrid.ColumnDefinitions[4].MinWidth = 320;
        Grid.SetRow(_workspaceGrid, 2);
        root.Children.Add(_workspaceGrid);

        _binderPane = BuildLeftPane();
        Grid.SetColumn(_binderPane, 0);
        _workspaceGrid.Children.Add(_binderPane);

        _leftSplitter = BuildSplitter();
        Grid.SetColumn(_leftSplitter, 1);
        _workspaceGrid.Children.Add(_leftSplitter);

        var center = BuildCenterPane();
        Grid.SetColumn(center, 2);
        _workspaceGrid.Children.Add(center);

        _rightSplitter = BuildSplitter();
        Grid.SetColumn(_rightSplitter, 3);
        _workspaceGrid.Children.Add(_rightSplitter);

        _inspectorPane = BuildInspectorPane();
        Grid.SetColumn(_inspectorPane, 4);
        _workspaceGrid.Children.Add(_inspectorPane);

        _statusBar = BuildStatusBar();
        Grid.SetRow(_statusBar, 3);
        root.Children.Add(_statusBar);
        return root;
    }

    private Menu BuildMenu()
    {
        _showBinderMenuItem = ToggleMenuItem("_Binder", true, () => SetBinderVisible(!_showBinder));
        _showInspectorMenuItem = ToggleMenuItem("_Inspector", true, () => SetInspectorVisible(!_showInspector));
        _wholeBookPreviewMenuItem = ToggleMenuItem("Preview _Whole Book", false, () =>
        {
            _viewModel.SetPreviewWholeBook(_wholeBookPreviewMenuItem?.IsChecked == true);
        });
        _compositionMenuItem = ToggleMenuItem("_Composition Mode", false, ToggleCompositionMode);

        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_New Project…", CreateProjectAsync, new KeyGesture(Key.N, KeyModifiers.Control)),
                MenuAction("_Open Project…", OpenProjectAsync, new KeyGesture(Key.O, KeyModifiers.Control)),
                new Separator(),
                MenuAction("_Save", SaveAllAsync, new KeyGesture(Key.S, KeyModifiers.Control)),
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
                MenuAction("_Find in Project", FocusSearchAsync, new KeyGesture(Key.F, KeyModifiers.Control)),
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
                MenuAction("_Editor", () => { _centerTabs.SelectedIndex = 0; return Task.CompletedTask; }),
                MenuAction("_Corkboard", () => { _centerTabs.SelectedIndex = 1; return Task.CompletedTask; }),
                new Separator(),
                _wholeBookPreviewMenuItem,
                MenuAction("_Refresh PDF Preview", () => _viewModel.RefreshLivePdfPreviewAsync(), new KeyGesture(Key.R, KeyModifiers.Control | KeyModifiers.Shift)),
                new Separator(),
                _compositionMenuItem
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

        var document = new MenuItem
        {
            Header = "_Document",
            ItemsSource = new object[]
            {
                MenuAction("Take _Snapshot…", TakeSnapshotAsync),
                MenuAction("Open _Inspector", () => { SetInspectorVisible(true); _inspectorTabs.SelectedIndex = 0; return Task.CompletedTask; }),
                new Separator(),
                MenuAction("Include / _Exclude", () => _viewModel.ToggleSelectedCompilationAsync())
            }
        };

        var project = new MenuItem
        {
            Header = "_Project",
            ItemsSource = new object[]
            {
                MenuAction("_Book Style…", EditStyleAsync),
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
            ItemsSource = new object[] { MenuAction("_About Typescribe", ShowAboutAsync) }
        };

        return new Menu { ItemsSource = new object[] { file, edit, view, insert, format, document, project, publish, help } };
    }

    private Control BuildToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(8, 6), Orientation = Orientation.Horizontal };
        var newButton = ToolbarButton("New");
        var openButton = ToolbarButton("Open");
        var saveButton = ToolbarButton("Save");
        var chapterButton = ToolbarButton("+ Chapter");
        var editorButton = ToolbarButton("Editor");
        var corkboardButton = ToolbarButton("Corkboard");
        var inspectorButton = ToolbarButton("Inspector");
        var snapshotButton = ToolbarButton("Snapshot");
        var compositionButton = ToolbarButton("Composition");
        var previewButton = ToolbarButton("Refresh PDF");
        var publishButton = ToolbarButton("Publish PDF");

        newButton.Click += async (_, _) => await RunUiTaskAsync(CreateProjectAsync);
        openButton.Click += async (_, _) => await RunUiTaskAsync(OpenProjectAsync);
        saveButton.Click += async (_, _) => await RunUiTaskAsync(SaveAllAsync);
        chapterButton.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter"));
        editorButton.Click += (_, _) => _centerTabs.SelectedIndex = 0;
        corkboardButton.Click += (_, _) => _centerTabs.SelectedIndex = 1;
        inspectorButton.Click += (_, _) => { SetInspectorVisible(true); _inspectorTabs.SelectedIndex = 0; };
        snapshotButton.Click += async (_, _) => await RunUiTaskAsync(TakeSnapshotAsync);
        compositionButton.Click += (_, _) => ToggleCompositionMode();
        previewButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.RefreshLivePdfPreviewAsync());
        publishButton.Click += async (_, _) => await RunUiTaskAsync(ExportPdfAsync);

        foreach (var button in new[]
                 {
                     newButton, openButton, saveButton, chapterButton, editorButton, corkboardButton,
                     inspectorButton, snapshotButton, compositionButton, previewButton, publishButton
                 })
            bar.Children.Add(button);

        return bar;
    }

    private Control BuildLeftPane()
    {
        _binder.HorizontalAlignment = HorizontalAlignment.Stretch;
        _binder.VerticalAlignment = VerticalAlignment.Stretch;
        _binder.ContextMenu = BuildBinderContextMenu();

        var binderActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        var chapter = SmallButton("+ Chapter");
        var folder = SmallButton("+ Folder");
        var part = SmallButton("+ Part");
        chapter.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter"));
        folder.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Folder, "New Folder"));
        part.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Part, "New Part"));
        binderActions.Children.Add(chapter);
        binderActions.Children.Add(folder);
        binderActions.Children.Add(part);

        var binderPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        binderPanel.Children.Add(binderActions);
        Grid.SetRow(_binder, 1);
        binderPanel.Children.Add(_binder);

        var searchButton = new Button { Content = "Search" };
        searchButton.Click += async (_, _) => await RunUiTaskAsync(SearchAsync);
        _searchBox.Watermark = "Search project";
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6) };
        searchRow.Children.Add(_searchBox);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        var options = new WrapPanel { Margin = new Thickness(6, 0, 6, 6) };
        _regexSearch.Margin = new Thickness(0, 0, 8, 0);
        _caseSearch.Margin = new Thickness(0, 0, 8, 0);
        options.Children.Add(_regexSearch);
        options.Children.Add(_caseSearch);
        options.Children.Add(_wholeWordSearch);

        var searchPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        searchPanel.Children.Add(searchRow);
        Grid.SetRow(options, 1);
        searchPanel.Children.Add(options);
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

    private Control BuildCenterPane()
    {
        _documentTitle.FontSize = 18;
        _documentTitle.FontWeight = FontWeight.SemiBold;
        _documentMeta.Opacity = 0.72;
        _documentMeta.VerticalAlignment = VerticalAlignment.Center;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 8) };
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
        _editor.Margin = new Thickness(12, 0, 12, 12);

        var editorPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        editorPanel.Children.Add(header);
        Grid.SetRow(_editor, 1);
        editorPanel.Children.Add(_editor);

        _corkboardTitle.FontSize = 18;
        _corkboardTitle.FontWeight = FontWeight.SemiBold;
        _corkboardTitle.Margin = new Thickness(12, 8);
        _corkboardPanel.Margin = new Thickness(8);
        var corkboardScroll = new ScrollViewer
        {
            Content = _corkboardPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var corkboard = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        corkboard.Children.Add(_corkboardTitle);
        Grid.SetRow(corkboardScroll, 1);
        corkboard.Children.Add(corkboardScroll);

        _centerTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "Editor", Content = editorPanel },
            new TabItem { Header = "Corkboard", Content = corkboard }
        };
        _centerTabs.SelectedIndex = 0;
        return _centerTabs;
    }

    private Control BuildInspectorPane()
    {
        _outline.HorizontalAlignment = HorizontalAlignment.Stretch;
        _outline.VerticalAlignment = VerticalAlignment.Stretch;

        var inspector = BuildDocumentInspector();
        var pdf = BuildPreviewPanel();
        var outline = BuildOutlinePanel();
        var snapshots = BuildSnapshotsPanel();

        _inspectorTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "Inspector", Content = inspector },
            new TabItem { Header = "PDF", Content = pdf },
            new TabItem { Header = "Outline", Content = outline },
            new TabItem { Header = "Snapshots", Content = snapshots }
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

    private Control BuildDocumentInspector()
    {
        var panel = new StackPanel { Spacing = 7, Margin = new Thickness(10) };
        panel.Children.Add(InspectorLabel("Synopsis"));
        panel.Children.Add(_synopsisBox);
        panel.Children.Add(InspectorLabel("Notes"));
        panel.Children.Add(_notesBox);
        panel.Children.Add(InspectorLabel("Status"));
        panel.Children.Add(_statusBox);
        panel.Children.Add(InspectorLabel("Label"));
        panel.Children.Add(_labelBox);
        panel.Children.Add(InspectorLabel("Keywords"));
        panel.Children.Add(_keywordsBox);
        panel.Children.Add(InspectorLabel("Word target"));
        panel.Children.Add(_targetBox);
        panel.Children.Add(_targetProgress);
        panel.Children.Add(_targetText);

        var save = new Button { Content = "Save Inspector", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        save.Click += async (_, _) => await RunUiTaskAsync(SaveInspectorAsync);
        panel.Children.Add(save);

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private Control BuildOutlinePanel()
    {
        var clear = new Button { Content = "Show Full Document", Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) =>
        {
            _previewPageIndex = 0;
            _loadedPreviewVersion = -1;
            _viewModel.SelectOutline(null);
        };
        var help = new TextBlock
        {
            Text = "Choose a heading to move the editor caret and focus the live PDF preview on that section.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 8),
            Opacity = 0.72
        };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        panel.Children.Add(clear);
        Grid.SetRow(help, 1);
        panel.Children.Add(help);
        Grid.SetRow(_outline, 2);
        panel.Children.Add(_outline);
        return panel;
    }

    private Control BuildSnapshotsPanel()
    {
        var create = new Button { Content = "Take Snapshot", Margin = new Thickness(0, 0, 6, 0) };
        var restore = new Button { Content = "Restore" };
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        create.Click += async (_, _) => await RunUiTaskAsync(TakeSnapshotAsync);
        restore.Click += async (_, _) => await RunUiTaskAsync(RestoreSelectedSnapshotAsync);
        delete.Click += async (_, _) => await RunUiTaskAsync(DeleteSelectedSnapshotAsync);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8),
            Children = { create, restore, delete }
        };
        var help = new TextBlock
        {
            Text = "Snapshots preserve the current document text. Restoring automatically creates a safety snapshot first.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 8),
            Opacity = 0.72
        };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        panel.Children.Add(buttons);
        Grid.SetRow(help, 1);
        panel.Children.Add(help);
        Grid.SetRow(_snapshots, 2);
        panel.Children.Add(_snapshots);
        return panel;
    }

    private Control BuildPreviewPanel()
    {
        _previousPageButton.Click += async (_, _) => await ChangePreviewPageAsync(-1);
        _nextPageButton.Click += async (_, _) => await ChangePreviewPageAsync(1);
        _zoomOutButton.Click += (_, _) => ChangeZoom(-0.1);
        _zoomInButton.Click += (_, _) => ChangeZoom(0.1);

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
        _previewScope.Margin = new Thickness(8, 0);
        controls.Children.Add(_previewScope);
        Grid.SetColumn(_previewStatus, 7);
        controls.Children.Add(_previewStatus);

        var surface = new Grid { Background = new SolidColorBrush(Color.Parse("#E8E8E8")) };
        surface.Children.Add(_pdfPreviewImage);
        surface.Children.Add(_previewMessage);

        var scroll = new ScrollViewer
        {
            Content = surface,
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

    private ContextMenu BuildBinderContextMenu() => new()
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
            MenuAction("Include / Exclude", () => _viewModel.ToggleSelectedCompilationAsync()),
            new Separator(),
            MenuAction("Take Snapshot…", TakeSnapshotAsync)
        }
    };

    private async Task CreateProjectAsync()
    {
        await FlushMetadataAsync();
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
        await FlushMetadataAsync();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Typescribe project",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) await _viewModel.OpenProjectAsync(folder.Path.LocalPath);
    }

    private async Task SaveAllAsync()
    {
        await FlushMetadataAsync();
        await _viewModel.SaveNowAsync();
    }

    private async Task AddBinderNodeAsync(NodeKind kind, string initialTitle)
    {
        await FlushMetadataAsync();
        var title = await DesktopDialogService.PromptAsync(this, $"Add {kind}", "Title", initialTitle);
        if (title is not null) await _viewModel.AddNodeAsync(kind, title);
    }

    private async Task RenameBinderNodeAsync()
    {
        if (!_viewModel.HasSelection) return;
        await FlushMetadataAsync();
        var title = await DesktopDialogService.PromptAsync(this, "Rename Binder Item", "Title", _viewModel.SelectedTitle);
        if (title is not null) await _viewModel.RenameSelectedAsync(title);
    }

    private async Task DeleteBinderNodeAsync()
    {
        if (!_viewModel.HasSelection) return;
        await FlushMetadataAsync();
        var confirmed = await DesktopDialogService.ConfirmAsync(
            this,
            "Delete Binder Item",
            $"Delete '{_viewModel.SelectedTitle}' and its on-disk content? This cannot be undone.");
        if (confirmed) await _viewModel.DeleteSelectedAsync();
    }

    private async Task SaveInspectorAsync()
    {
        if (!_viewModel.HasSelection) return;
        if (!TryGetTarget(out var target))
            throw new InvalidOperationException("Word target must be a whole number of zero or more.");
        await _viewModel.SaveSelectedMetadataAsync(
            _synopsisBox.Text ?? string.Empty,
            _notesBox.Text ?? string.Empty,
            _statusBox.Text ?? string.Empty,
            _labelBox.Text ?? string.Empty,
            _keywordsBox.Text ?? string.Empty,
            target);
    }

    private void MetadataTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingUi || !_viewModel.HasSelection) return;
        ScheduleMetadataSave();
    }

    private void ScheduleMetadataSave()
    {
        _metadataSaveCts?.Cancel();
        _metadataSaveCts?.Dispose();
        _metadataSaveCts = new CancellationTokenSource();
        var token = _metadataSaveCts.Token;
        _ = SaveMetadataAfterDelayAsync(token);
    }

    private async Task SaveMetadataAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken);
            if (!TryGetTarget(out var target)) return;
            await _viewModel.SaveSelectedMetadataAsync(
                _synopsisBox.Text ?? string.Empty,
                _notesBox.Text ?? string.Empty,
                _statusBox.Text ?? string.Empty,
                _labelBox.Text ?? string.Empty,
                _keywordsBox.Text ?? string.Empty,
                target,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = $"Inspector autosave failed: {ex.Message}");
        }
    }

    private async Task FlushMetadataAsync()
    {
        _metadataSaveCts?.Cancel();
        _metadataSaveCts?.Dispose();
        _metadataSaveCts = null;
        if (!_viewModel.HasSelection || !TryGetTarget(out var target)) return;
        await _viewModel.SaveSelectedMetadataAsync(
            _synopsisBox.Text ?? string.Empty,
            _notesBox.Text ?? string.Empty,
            _statusBox.Text ?? string.Empty,
            _labelBox.Text ?? string.Empty,
            _keywordsBox.Text ?? string.Empty,
            target,
            CancellationToken.None);
    }

    private bool TryGetTarget(out int target)
    {
        var text = _targetBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            target = 0;
            return true;
        }
        return int.TryParse(text, out target) && target >= 0;
    }

    private async Task TakeSnapshotAsync()
    {
        if (!_viewModel.HasDocument) return;
        var label = await DesktopDialogService.PromptAsync(this, "Take Snapshot", "Snapshot label", $"Snapshot {DateTime.Now:g}");
        if (label is null) return;
        await _viewModel.CreateSnapshotAsync(label);
        SetInspectorVisible(true);
        _inspectorTabs.SelectedIndex = 3;
    }

    private async Task RestoreSelectedSnapshotAsync()
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(
            this,
            "Restore Snapshot",
            $"Restore snapshot from {snapshot.CreatedAt.LocalDateTime:g}? Typescribe will save the current text as a safety snapshot first.",
            "Restore");
        if (!confirmed) return;
        await _viewModel.RestoreSnapshotAsync(snapshot);
        _centerTabs.SelectedIndex = 0;
    }

    private async Task DeleteSelectedSnapshotAsync()
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(
            this,
            "Delete Snapshot",
            $"Delete snapshot from {snapshot.CreatedAt.LocalDateTime:g}?",
            "Delete");
        if (confirmed) await _viewModel.DeleteSnapshotAsync(snapshot);
    }

    private SnapshotInfo? SelectedSnapshot()
    {
        var index = _snapshots.SelectedIndex;
        return index >= 0 && index < _viewModel.Snapshots.Count ? _viewModel.Snapshots[index] : null;
    }

    private async Task EditStyleAsync()
    {
        if (!_viewModel.HasProject) return;
        var style = await DesktopDialogService.EditStyleAsync(this, _viewModel.CurrentStyle);
        if (style is not null) await _viewModel.UpdateStyleAsync(style);
    }

    private async Task ExportPdfAsync()
    {
        await FlushMetadataAsync();
        var path = await PickSavePathAsync("Publish Book PDF", "PDF document", "pdf");
        if (path is not null) await _viewModel.ExportPdfAsync(path);
    }

    private async Task ExportLatexAsync()
    {
        await FlushMetadataAsync();
        var path = await PickSavePathAsync("Export Book LaTeX source", "LaTeX source", "tex");
        if (path is not null) await _viewModel.ExportLatexAsync(path);
    }

    private Task FocusSearchAsync()
    {
        SetBinderVisible(true);
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
            Width = 470,
            Height = 270,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Typescribe", FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Long-form authoring, Corkboard planning, snapshots, and LuaLaTeX publishing.", TextWrapping = TextWrapping.Wrap },
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
        if (_updatingUi || _binder.SelectedItem is not BinderRowViewModel row) return;
        await RunUiTaskAsync(async () =>
        {
            await FlushMetadataAsync();
            _previewPageIndex = 0;
            _loadedPreviewVersion = -1;
            await _viewModel.SelectAsync(row);
        });
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
        e.DragEffects = _viewModel.CanDropBinderItem(source, target) ? DragDropEffects.Move : DragDropEffects.None;
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

        var placement = GetBinderDropPlacement(e, FindBinderListItem(e.Source), target);
        e.DragEffects = DragDropEffects.Move;
        await RunUiTaskAsync(() => _viewModel.MoveBinderItemAsync(
            source,
            target,
            placement == BinderDropPlacement.Inside,
            placement == BinderDropPlacement.After));
    }

    private async void OutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _outline.SelectedItem is not OutlineItemViewModel item) return;
        _previewPageIndex = 0;
        _loadedPreviewVersion = -1;
        _viewModel.SelectOutline(item);
        _inspectorTabs.SelectedIndex = 1;
        await RefreshPreviewIfNeededAsync();
    }

    private void EditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingUi) return;
        _viewModel.UpdateEditorText(_editor.Text ?? string.Empty);
    }

    private async void SearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunUiTaskAsync(SearchAsync);
    }

    private async void SearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        var index = _searchResults.SelectedIndex;
        if (index < 0 || index >= _viewModel.SearchResults.Count) return;
        await RunUiTaskAsync(async () =>
        {
            await FlushMetadataAsync();
            await _viewModel.GoToSearchHitAsync(_viewModel.SearchResults[index]);
            _leftTabs.SelectedIndex = 0;
            _centerTabs.SelectedIndex = 0;
        });
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleCompositionMode();
            return;
        }

        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!primary && e.Key != Key.F2) return;

        if (primary && e.Key == Key.S)
        {
            e.Handled = true;
            await RunUiTaskAsync(SaveAllAsync);
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
            await RunUiTaskAsync(FocusSearchAsync);
        }
        else if (e.Key == Key.F2)
        {
            e.Handled = true;
            await RunUiTaskAsync(RenameBinderNodeAsync);
        }
    }

    private void ToggleCompositionMode()
    {
        if (_workspaceGrid is null || _menuBar is null || _toolbar is null || _statusBar is null) return;
        _compositionMode = !_compositionMode;

        if (_compositionMode)
        {
            _savedShowBinder = _showBinder;
            _savedShowInspector = _showInspector;
            _savedWindowState = WindowState;
            SetBinderVisible(false);
            SetInspectorVisible(false);
            _menuBar.IsVisible = false;
            _toolbar.IsVisible = false;
            _statusBar.IsVisible = false;
            _centerTabs.SelectedIndex = 0;
            _editor.Margin = new Thickness(90, 24, 90, 36);
            WindowState = WindowState.FullScreen;
            _editor.Focus();
        }
        else
        {
            WindowState = _savedWindowState;
            _menuBar.IsVisible = true;
            _toolbar.IsVisible = true;
            _statusBar.IsVisible = true;
            SetBinderVisible(_savedShowBinder);
            SetInspectorVisible(_savedShowInspector);
            _editor.Margin = new Thickness(12, 0, 12, 12);
        }

        if (_compositionMenuItem is not null) _compositionMenuItem.IsChecked = _compositionMode;
    }

    private void SetBinderVisible(bool visible)
    {
        if (_workspaceGrid is null || _binderPane is null || _leftSplitter is null) return;
        _showBinder = visible;
        _binderPane.IsVisible = visible;
        _leftSplitter.IsVisible = visible;
        _workspaceGrid.ColumnDefinitions[0].Width = new GridLength(visible ? 300 : 0);
        _workspaceGrid.ColumnDefinitions[1].Width = new GridLength(visible ? 5 : 0);
        if (_showBinderMenuItem is not null) _showBinderMenuItem.IsChecked = visible;
    }

    private void SetInspectorVisible(bool visible)
    {
        if (_workspaceGrid is null || _inspectorPane is null || _rightSplitter is null) return;
        _showInspector = visible;
        _inspectorPane.IsVisible = visible;
        _rightSplitter.IsVisible = visible;
        _workspaceGrid.ColumnDefinitions[3].Width = new GridLength(visible ? 5 : 0);
        _workspaceGrid.ColumnDefinitions[4].Width = new GridLength(visible ? 440 : 0);
        if (_showInspectorMenuItem is not null) _showInspectorMenuItem.IsChecked = visible;
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

    private void ApplyPreviewZoom() => _pdfPreviewImage.Width = 760 * _previewZoom;

    private void RebuildCorkboardIfNeeded()
    {
        if (_loadedCorkboardVersion == _viewModel.CorkboardVersion) return;
        _loadedCorkboardVersion = _viewModel.CorkboardVersion;
        _corkboardPanel.Children.Clear();
        _corkboardTitle.Text = $"Corkboard — {_viewModel.CorkboardTitle}";

        foreach (var card in _viewModel.CorkboardCards)
            _corkboardPanel.Children.Add(BuildCorkboardCard(card));

        if (_viewModel.CorkboardCards.Count == 0)
        {
            _corkboardPanel.Children.Add(new TextBlock
            {
                Text = "This binder group has no cards yet.",
                Margin = new Thickness(18),
                Opacity = 0.7
            });
        }
    }

    private Control BuildCorkboardCard(CorkboardCardViewModel card)
    {
        var title = new TextBlock
        {
            Text = card.Title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var synopsis = new TextBlock
        {
            Text = card.Synopsis,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 78,
            Opacity = string.IsNullOrWhiteSpace(card.Node.Synopsis) ? 0.55 : 0.9
        };
        var footer = new TextBlock { Text = card.Footer, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7 };
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { title, synopsis } };
        if (!string.IsNullOrWhiteSpace(card.Label))
        {
            stack.Children.Add(new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = card.Label, FontSize = 11 }
            });
        }
        if (card.TargetWords > 0)
            stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = card.Progress, Height = 5 });
        stack.Children.Add(footer);

        var border = new Border
        {
            Width = 245,
            MinHeight = 170,
            Margin = new Thickness(7),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.White,
            Child = stack
        };
        border.DoubleTapped += async (_, _) => await RunUiTaskAsync(async () =>
        {
            await FlushMetadataAsync();
            await _viewModel.SelectCorkboardCardAsync(card);
            _centerTabs.SelectedIndex = card.Node.IsDocument ? 0 : 1;
        });
        return border;
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
        var hashes = 0;
        while (hashes < content.Length && content[hashes] == '#') hashes++;
        if (hashes > 0 && hashes < content.Length && content[hashes] == ' ') content = content[(hashes + 1)..];
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
            if (next < 0) { index = text.Length; break; }
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
            _snapshots.ItemsSource = _viewModel.Snapshots
                .Select(static snapshot => $"{snapshot.CreatedAt.LocalDateTime:g}  •  {snapshot.WordCount:N0} words  •  {snapshot.Label}")
                .ToArray();

            if (!string.Equals(_editor.Text, _viewModel.EditorText, StringComparison.Ordinal))
                _editor.Text = _viewModel.EditorText;
            _editor.IsEnabled = _viewModel.HasDocument;

            _documentTitle.Text = _viewModel.HasSelection ? _viewModel.SelectedTitle : "No document selected";
            _documentMeta.Text = _viewModel.HasDocument
                ? $"{_viewModel.SelectedStatus} • {(_viewModel.SelectedIncluded ? "Included" : "Excluded")}"
                : _viewModel.HasSelection ? _viewModel.SelectedStatus : string.Empty;

            if (!string.Equals(_synopsisBox.Text, _viewModel.SelectedSynopsis, StringComparison.Ordinal)) _synopsisBox.Text = _viewModel.SelectedSynopsis;
            if (!string.Equals(_notesBox.Text, _viewModel.SelectedNotes, StringComparison.Ordinal)) _notesBox.Text = _viewModel.SelectedNotes;
            if (!string.Equals(_statusBox.Text, _viewModel.SelectedStatus, StringComparison.Ordinal)) _statusBox.Text = _viewModel.SelectedStatus;
            if (!string.Equals(_labelBox.Text, _viewModel.SelectedLabel, StringComparison.Ordinal)) _labelBox.Text = _viewModel.SelectedLabel;
            if (!string.Equals(_keywordsBox.Text, _viewModel.SelectedKeywords, StringComparison.Ordinal)) _keywordsBox.Text = _viewModel.SelectedKeywords;
            var targetText = _viewModel.SelectedTargetWords.ToString();
            if (!string.Equals(_targetBox.Text, targetText, StringComparison.Ordinal)) _targetBox.Text = targetText;
            _targetProgress.Value = _viewModel.SelectedTargetProgress;
            _targetText.Text = _viewModel.SelectedTargetText;

            _status.Text = _viewModel.Status;
            _wordCount.Text = _viewModel.SelectedTargetWords > 0 ? _viewModel.SelectedTargetText : $"{_viewModel.WordCount:N0} words";
            _engine.Text = _viewModel.CanPublishPdf ? $"PDF: {_viewModel.PublishingEngineName}" : "PDF: LuaLaTeX not installed";
            _previewScope.Text = _viewModel.PreviewScopeLabel;
            _wholeBookPreview.IsChecked = _viewModel.PreviewWholeBook;
            if (_wholeBookPreviewMenuItem is not null) _wholeBookPreviewMenuItem.IsChecked = _viewModel.PreviewWholeBook;

            _previewStatus.Text = _viewModel.IsLivePreviewBuilding
                ? "Compiling…"
                : _viewModel.LivePreviewError is not null
                    ? "Preview error"
                    : _viewModel.LivePreviewPdfPath is not null ? "Live" : string.Empty;

            if (_renderedPreviewPage is null)
            {
                _previewMessage.IsVisible = true;
                _previewMessage.Text = _viewModel.LivePreviewError
                    ?? (_viewModel.CanPublishPdf ? _viewModel.PreviewText : "Install LuaLaTeX to enable live PDF preview.");
            }

            RebuildCorkboardIfNeeded();

            var persistentId = _viewModel.SelectedRow?.Node.PersistentId;
            if (!string.Equals(_lastSelectedPersistentId, persistentId, StringComparison.Ordinal))
            {
                _lastSelectedPersistentId = persistentId;
                if (!_compositionMode)
                    _centerTabs.SelectedIndex = _viewModel.SelectedIsContainer ? 1 : 0;
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
        try { await action(); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async Task ShowErrorAsync(string message)
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "Typescribe",
            Width = 650,
            Height = 330,
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
                        MaxHeight = 230,
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
        _metadataSaveCts?.Cancel();
        try { await FlushMetadataAsync(); } catch { }
        _previewRenderCts?.Dispose();
        _metadataSaveCts?.Dispose();
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

    private static BinderDropPlacement GetBinderDropPlacement(DragEventArgs e, ListBoxItem? item, BinderRowViewModel target)
    {
        if (item is null) return target.Node.IsContainer ? BinderDropPlacement.Inside : BinderDropPlacement.Before;
        var height = Math.Max(1, item.Bounds.Height);
        var y = e.GetPosition(item).Y;
        if (target.Node.IsContainer && y >= height * 0.25 && y <= height * 0.75) return BinderDropPlacement.Inside;
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
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private static GridSplitter BuildSplitter() => new()
    {
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ShowsPreview = true,
        Background = Brushes.Gray,
        Opacity = 0.35
    };

    private static TextBlock InspectorLabel(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };
    private static Button ToolbarButton(string content) => new() { Content = content, Margin = new Thickness(0, 0, 6, 0) };
    private static Button SmallButton(string content) => new() { Content = content, Margin = new Thickness(0, 0, 4, 4) };

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "typescribe-book" : cleaned;
    }

    private enum BinderDropPlacement
    {
        Before,
        Inside,
        After
    }
}
