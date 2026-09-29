using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Typescribe.Application.Models;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

public sealed class StudioWorkspaceWindow : Window
{
    private static readonly DataFormat<CorkboardCardViewModel> CorkboardCardFormat =
        DataFormat.CreateInProcessFormat<CorkboardCardViewModel>("typescribe-corkboard-card");

    private readonly WorkspaceViewModel _viewModel;
    private readonly AuthoringFeatureCoordinator _features = new();
    private readonly PdfPreviewRenderer _pdfPreviewRenderer = new();

    private readonly Grid _projectExplorerHost = new()
    {
        Name = "ProjectExplorerHost",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly TextBox _searchBox = new();
    private readonly ListBox _searchResults = new();
    private readonly CheckBox _regexSearch = new() { Content = "Regex" };
    private readonly CheckBox _caseSearch = new() { Content = "Case" };
    private readonly CheckBox _wholeWordSearch = new() { Content = "Whole word" };
    private readonly ListBox _collections = new();
    private readonly TabControl _leftTabs = new();

    private readonly TextBox _editor = new();
    private readonly TextBlock _documentTitle = new();
    private readonly TextBlock _documentMeta = new();
    private readonly TabControl _centerTabs = new();
    private readonly WrapPanel _corkboardPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _corkboardTitle = new();
    private readonly StackPanel _outlinerRows = new() { Spacing = 1 };
    private readonly TextBlock _outlinerTitle = new();

    private readonly TabControl _inspectorTabs = new();
    private readonly TextBox _synopsisBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
    private readonly TextBox _notesBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110 };
    private readonly TextBox _statusBox = new() { Watermark = "Draft / Revised / Final" };
    private readonly TextBox _labelBox = new() { Watermark = "Storyline, POV, research…" };
    private readonly TextBox _keywordsBox = new() { Watermark = "comma-separated keywords" };
    private readonly TextBox _targetBox = new() { Watermark = "0" };
    private readonly ProgressBar _targetProgress = new() { Minimum = 0, Maximum = 1, Height = 6 };
    private readonly TextBlock _targetText = new();
    private readonly StackPanel _customFieldsPanel = new() { Spacing = 6 };

    private readonly ListBox _comments = new();
    private readonly ListBox _outline = new();
    private readonly ListBox _snapshots = new();

    private readonly TextBox _projectTargetBox = new() { Watermark = "0" };
    private readonly TextBox _dailyTargetBox = new() { Watermark = "0" };
    private readonly TextBox _sessionTargetBox = new() { Watermark = "0" };
    private readonly ProgressBar _projectProgress = new() { Minimum = 0, Maximum = 1, Height = 7 };
    private readonly ProgressBar _dailyProgress = new() { Minimum = 0, Maximum = 1, Height = 7 };
    private readonly ProgressBar _sessionProgress = new() { Minimum = 0, Maximum = 1, Height = 7 };
    private readonly TextBlock _projectProgressText = new();
    private readonly TextBlock _dailyProgressText = new();
    private readonly TextBlock _sessionProgressText = new();

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
    private CorkboardCardViewModel? _cardDragCandidate;
    private PointerPressedEventArgs? _cardDragTrigger;
    private Point _cardDragStart;
    private ProjectCollection? _activeCollection;
    private string? _projectRoot;
    private string? _lastSelectedPersistentId;
    private long _loadedPreviewVersion = -1;
    private long _loadedCorkboardVersion = -1;
    private long _appliedEditorNavigationVersion = -1;
    private int _previewPageIndex;
    private double _previewZoom = 1.0;
    private bool _showBinder = true;
    private bool _showInspector = true;
    private bool _compositionMode;
    private bool _savedShowBinder = true;
    private bool _savedShowInspector = true;
    private WindowState _savedWindowState = WindowState.Normal;
    private bool _updatingUi;

    public StudioWorkspaceWindow(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Typescribe";
        Width = 1680;
        Height = 1000;
        MinWidth = 1120;
        MinHeight = 700;
        Content = BuildLayout();

        _viewModel.StateChanged += OnStateChanged;
        _outline.SelectionChanged += OutlineSelectionChanged;
        _editor.TextChanged += EditorTextChanged;
        _searchBox.KeyDown += SearchBoxKeyDown;
        _searchResults.DoubleTapped += SearchResultDoubleTapped;
        _collections.DoubleTapped += CollectionDoubleTapped;
        _comments.DoubleTapped += CommentDoubleTapped;
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
            ColumnDefinitions = new ColumnDefinitions("310,5,*,5,470"),
            Margin = new Thickness(6, 0, 6, 0)
        };
        _workspaceGrid.ColumnDefinitions[0].MinWidth = 230;
        _workspaceGrid.ColumnDefinitions[2].MinWidth = 430;
        _workspaceGrid.ColumnDefinitions[4].MinWidth = 340;
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
            _viewModel.SetPreviewWholeBook(_wholeBookPreviewMenuItem?.IsChecked == true));
        _compositionMenuItem = ToggleMenuItem("_Composition Mode", false, ToggleCompositionMode);

        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_New Project…", CreateProjectAsync, new KeyGesture(Key.N, KeyModifiers.Control)),
                MenuAction("New from _Template…", NewFromTemplateAsync),
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
                MenuAction("_Rename Project Item", RenameBinderNodeAsync, new KeyGesture(Key.F2)),
                MenuAction("_Delete Project Item", DeleteBinderNodeAsync),
                new Separator(),
                MenuAction("Move Project Item _Up", () => MoveSelectedAsync(-1)),
                MenuAction("Move Project Item _Down", () => MoveSelectedAsync(1))
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
                MenuAction("_Editor", () => SelectCenterTabAsync(0)),
                MenuAction("_Corkboard", () => SelectCenterTabAsync(1)),
                MenuAction("_Outliner", () => SelectCenterTabAsync(2)),
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
                MenuAction("Add _Comment at Caret…", AddCommentAsync),
                new Separator(),
                MenuAction("Include / _Exclude", ToggleCompilationAsync)
            }
        };

        var project = new MenuItem
        {
            Header = "_Project",
            ItemsSource = new object[]
            {
                MenuAction("_Book Style…", EditStyleAsync),
                MenuAction("Writing _Targets", () => { SetInspectorVisible(true); _inspectorTabs.SelectedIndex = 5; return Task.CompletedTask; }),
                new Separator(),
                MenuAction("Add Custom Metadata _Field…", AddCustomFieldAsync),
                MenuAction("Remove Custom Metadata Field…", RemoveCustomFieldAsync),
                new Separator(),
                MenuAction("New Manual _Collection from Selection…", CreateManualCollectionAsync),
                MenuAction("Save Current Search as Collection…", SaveCurrentSearchAsync),
                new Separator(),
                MenuAction("Save Project as _Template…", SaveAsTemplateAsync),
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
        foreach (var pair in new (string Text, Func<Task> Action)[]
                 {
                     ("New", CreateProjectAsync),
                     ("Open", OpenProjectAsync),
                     ("Save", SaveAllAsync),
                     ("+ Chapter", () => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter")),
                     ("Editor", () => SelectCenterTabAsync(0)),
                     ("Corkboard", () => SelectCenterTabAsync(1)),
                     ("Outliner", () => SelectCenterTabAsync(2)),
                     ("Inspector", () => { SetInspectorVisible(true); _inspectorTabs.SelectedIndex = 0; return Task.CompletedTask; }),
                     ("Snapshot", TakeSnapshotAsync),
                     ("Composition", () => { ToggleCompositionMode(); return Task.CompletedTask; }),
                     ("Refresh PDF", () => _viewModel.RefreshLivePdfPreviewAsync()),
                     ("Publish PDF", ExportPdfAsync)
                 })
        {
            var button = ToolbarButton(pair.Text);
            button.Click += async (_, _) => await RunUiTaskAsync(pair.Action);
            bar.Children.Add(button);
        }
        return bar;
    }

    private Control BuildLeftPane()
    {
        var binderActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var pair in new (string Text, NodeKind Kind)[]
                 {
                     ("+ Chapter", NodeKind.Chapter), ("+ Folder", NodeKind.Folder), ("+ Part", NodeKind.Part)
                 })
        {
            var button = SmallButton(pair.Text);
            button.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(pair.Kind, $"New {pair.Kind}"));
            binderActions.Children.Add(button);
        }

        var explorerPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        explorerPanel.Children.Add(binderActions);
        Grid.SetRow(_projectExplorerHost, 1);
        explorerPanel.Children.Add(_projectExplorerHost);

        var searchButton = new Button { Content = "Search" };
        var saveSearch = new Button { Content = "Save Search", Margin = new Thickness(6, 0, 0, 0) };
        searchButton.Click += async (_, _) => await RunUiTaskAsync(SearchAsync);
        saveSearch.Click += async (_, _) => await RunUiTaskAsync(SaveCurrentSearchAsync);
        _searchBox.Watermark = "Search project";
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(6) };
        searchRow.Children.Add(_searchBox);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);
        Grid.SetColumn(saveSearch, 2);
        searchRow.Children.Add(saveSearch);

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

        var newCollection = new Button { Content = "New from Selection" };
        var addToCollection = new Button { Content = "Add Selection", Margin = new Thickness(6, 0, 0, 0) };
        var deleteCollection = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        newCollection.Click += async (_, _) => await RunUiTaskAsync(CreateManualCollectionAsync);
        addToCollection.Click += async (_, _) => await RunUiTaskAsync(AddSelectionToCollectionAsync);
        deleteCollection.Click += async (_, _) => await RunUiTaskAsync(DeleteSelectedCollectionAsync);
        var collectionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6),
            Children = { newCollection, addToCollection, deleteCollection }
        };
        var collectionsPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        collectionsPanel.Children.Add(collectionButtons);
        Grid.SetRow(_collections, 1);
        collectionsPanel.Children.Add(_collections);

        _leftTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "Binder", Content = explorerPanel },
            new TabItem { Header = "Search", Content = searchPanel },
            new TabItem { Header = "Collections", Content = collectionsPanel }
        };
        _leftTabs.SelectedIndex = 0;

        return PanelBorder(_leftTabs);
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

        _outlinerTitle.FontSize = 18;
        _outlinerTitle.FontWeight = FontWeight.SemiBold;
        _outlinerTitle.Margin = new Thickness(12, 8);
        var outlinerScroll = new ScrollViewer
        {
            Content = _outlinerRows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var outliner = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        outliner.Children.Add(_outlinerTitle);
        Grid.SetRow(outlinerScroll, 1);
        outliner.Children.Add(outlinerScroll);

        _centerTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "Editor", Content = editorPanel },
            new TabItem { Header = "Corkboard", Content = corkboard },
            new TabItem { Header = "Outliner", Content = outliner }
        };
        _centerTabs.SelectedIndex = 0;
        return _centerTabs;
    }

    private Control BuildInspectorPane()
    {
        _inspectorTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "Inspector", Content = BuildDocumentInspector() },
            new TabItem { Header = "Comments", Content = BuildCommentsPanel() },
            new TabItem { Header = "PDF", Content = BuildPreviewPanel() },
            new TabItem { Header = "Outline", Content = BuildOutlinePanel() },
            new TabItem { Header = "Snapshots", Content = BuildSnapshotsPanel() },
            new TabItem { Header = "Project", Content = BuildProjectPanel() }
        };
        _inspectorTabs.SelectedIndex = 0;
        return PanelBorder(_inspectorTabs);
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
        panel.Children.Add(InspectorLabel("Document word target"));
        panel.Children.Add(_targetBox);
        panel.Children.Add(_targetProgress);
        panel.Children.Add(_targetText);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 8) });
        panel.Children.Add(InspectorLabel("Custom metadata"));
        panel.Children.Add(_customFieldsPanel);

        var save = new Button { Content = "Save Inspector", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        save.Click += async (_, _) => await RunUiTaskAsync(SaveInspectorAsync);
        panel.Children.Add(save);
        return Scroll(panel);
    }

    private Control BuildCommentsPanel()
    {
        var add = new Button { Content = "Add at Caret" };
        var resolve = new Button { Content = "Resolve / Reopen", Margin = new Thickness(6, 0, 0, 0) };
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        add.Click += async (_, _) => await RunUiTaskAsync(AddCommentAsync);
        resolve.Click += async (_, _) => await RunUiTaskAsync(ToggleSelectedCommentResolvedAsync);
        delete.Click += async (_, _) => await RunUiTaskAsync(DeleteSelectedCommentAsync);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), Children = { add, resolve, delete } };
        var help = new TextBlock
        {
            Text = "Comments are stored outside manuscript text. Double-click a comment to jump to its source line.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 8),
            Opacity = 0.72
        };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        panel.Children.Add(buttons);
        Grid.SetRow(help, 1);
        panel.Children.Add(help);
        Grid.SetRow(_comments, 2);
        panel.Children.Add(_comments);
        return panel;
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
        var create = new Button { Content = "Take Snapshot" };
        var compare = new Button { Content = "Compare", Margin = new Thickness(6, 0, 0, 0) };
        var restore = new Button { Content = "Restore", Margin = new Thickness(6, 0, 0, 0) };
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        create.Click += async (_, _) => await RunUiTaskAsync(TakeSnapshotAsync);
        compare.Click += async (_, _) => await RunUiTaskAsync(CompareSelectedSnapshotAsync);
        restore.Click += async (_, _) => await RunUiTaskAsync(RestoreSelectedSnapshotAsync);
        delete.Click += async (_, _) => await RunUiTaskAsync(DeleteSelectedSnapshotAsync);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), Children = { create, compare, restore, delete } };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        panel.Children.Add(buttons);
        Grid.SetRow(_snapshots, 1);
        panel.Children.Add(_snapshots);
        return panel;
    }

    private Control BuildProjectPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(10), Spacing = 8 };
        panel.Children.Add(InspectorLabel("Project target"));
        panel.Children.Add(_projectTargetBox);
        panel.Children.Add(_projectProgress);
        panel.Children.Add(_projectProgressText);
        panel.Children.Add(InspectorLabel("Daily target"));
        panel.Children.Add(_dailyTargetBox);
        panel.Children.Add(_dailyProgress);
        panel.Children.Add(_dailyProgressText);
        panel.Children.Add(InspectorLabel("Session target"));
        panel.Children.Add(_sessionTargetBox);
        panel.Children.Add(_sessionProgress);
        panel.Children.Add(_sessionProgressText);
        var save = new Button { Content = "Save Writing Targets", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        save.Click += async (_, _) => await RunUiTaskAsync(SaveWritingTargetsAsync);
        panel.Children.Add(save);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 10) });
        panel.Children.Add(new TextBlock
        {
            Text = "Project target tracks the manuscript total. Daily progress survives restarts and resets on a new local day. Session progress starts when the project is opened.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        return Scroll(panel);
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
        Grid.SetColumn(_pageLabel, 1); controls.Children.Add(_pageLabel);
        Grid.SetColumn(_nextPageButton, 2); controls.Children.Add(_nextPageButton);
        Grid.SetColumn(_zoomOutButton, 3); controls.Children.Add(_zoomOutButton);
        Grid.SetColumn(_zoomInButton, 4); controls.Children.Add(_zoomInButton);
        Grid.SetColumn(_wholeBookPreview, 5); controls.Children.Add(_wholeBookPreview);
        Grid.SetColumn(_previewScope, 6); _previewScope.Margin = new Thickness(8, 0); controls.Children.Add(_previewScope);
        Grid.SetColumn(_previewStatus, 7); controls.Children.Add(_previewStatus);

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
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        return panel;
    }

    private Control BuildStatusBar()
    {
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(8, 5) };
        bar.Children.Add(_status);
        Grid.SetColumn(_wordCount, 1); _wordCount.Margin = new Thickness(12, 0); bar.Children.Add(_wordCount);
        Grid.SetColumn(_engine, 2); bar.Children.Add(_engine);
        return bar;
    }

    private async Task CreateProjectAsync()
    {
        await FlushMetadataAsync();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the new Typescribe project",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        _projectRoot = folder.Path.LocalPath;
        await _viewModel.CreateProjectAsync(_projectRoot);
        await _features.OpenAsync(_projectRoot, _viewModel.BinderRows);
        RefreshAdvancedViews();
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
        if (folder is null) return;
        await OpenProjectPathAsync(folder.Path.LocalPath);
    }

    private async Task OpenProjectPathAsync(string path)
    {
        _projectRoot = path;
        await _viewModel.OpenProjectAsync(path);
        await _features.OpenAsync(path, _viewModel.BinderRows);
        RefreshAdvancedViews();
    }

    private async Task SaveAllAsync()
    {
        await FlushMetadataAsync();
        await _viewModel.SaveNowAsync();
        if (_features.HasProject) await _features.RefreshStatisticsAsync();
        RefreshAdvancedViews();
    }

    private async Task AddBinderNodeAsync(NodeKind kind, string initialTitle)
    {
        await FlushMetadataAsync();
        var title = await DesktopDialogService.PromptAsync(this, $"Add {kind}", "Title", initialTitle);
        if (title is null) return;
        await _viewModel.AddNodeAsync(kind, title);
        await ReloadAdvancedStructureAsync();
    }

    private async Task RenameBinderNodeAsync()
    {
        if (!_viewModel.HasSelection) return;
        await FlushMetadataAsync();
        var title = await DesktopDialogService.PromptAsync(this, "Rename Project Item", "Title", _viewModel.SelectedTitle);
        if (title is null) return;
        await _viewModel.RenameSelectedAsync(title);
        await ReloadAdvancedStructureAsync();
    }

    private async Task DeleteBinderNodeAsync()
    {
        if (!_viewModel.HasSelection) return;
        await FlushMetadataAsync();
        var confirmed = await DesktopDialogService.ConfirmAsync(this, "Delete Project Item", $"Delete '{_viewModel.SelectedTitle}' and its on-disk content?", "Delete");
        if (!confirmed) return;
        await _viewModel.DeleteSelectedAsync();
        await ReloadAdvancedStructureAsync();
    }

    private async Task MoveSelectedAsync(int offset)
    {
        await _viewModel.MoveSelectedAsync(offset);
        await ReloadAdvancedStructureAsync();
    }

    private async Task ToggleCompilationAsync()
    {
        await _viewModel.ToggleSelectedCompilationAsync();
        await ReloadAdvancedStructureAsync();
    }

    private async Task ReloadAdvancedStructureAsync()
    {
        if (!_features.HasProject) return;
        await _features.ReloadStructureAsync(_viewModel.BinderRows);
        RefreshAdvancedViews();
    }

    private async Task SaveInspectorAsync()
    {
        if (!_viewModel.HasSelection) return;
        if (!TryNonNegative(_targetBox.Text, out var target)) throw new InvalidOperationException("Document target must be a whole number of zero or more.");
        await _viewModel.SaveSelectedMetadataAsync(
            _synopsisBox.Text ?? string.Empty,
            _notesBox.Text ?? string.Empty,
            _statusBox.Text ?? string.Empty,
            _labelBox.Text ?? string.Empty,
            _keywordsBox.Text ?? string.Empty,
            target);
        RebuildOutliner();
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
        _ = SaveMetadataAfterDelayAsync(_metadataSaveCts.Token);
    }

    private async Task SaveMetadataAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken);
            if (!TryNonNegative(_targetBox.Text, out var target)) return;
            await _viewModel.SaveSelectedMetadataAsync(
                _synopsisBox.Text ?? string.Empty,
                _notesBox.Text ?? string.Empty,
                _statusBox.Text ?? string.Empty,
                _labelBox.Text ?? string.Empty,
                _keywordsBox.Text ?? string.Empty,
                target,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.UIThread.InvokeAsync(() => _status.Text = $"Inspector autosave failed: {ex.Message}"); }
    }

    private async Task FlushMetadataAsync()
    {
        _metadataSaveCts?.Cancel();
        _metadataSaveCts?.Dispose();
        _metadataSaveCts = null;
        if (!_viewModel.HasSelection || !TryNonNegative(_targetBox.Text, out var target)) return;
        await _viewModel.SaveSelectedMetadataAsync(
            _synopsisBox.Text ?? string.Empty,
            _notesBox.Text ?? string.Empty,
            _statusBox.Text ?? string.Empty,
            _labelBox.Text ?? string.Empty,
            _keywordsBox.Text ?? string.Empty,
            target,
            CancellationToken.None);
    }

    private async Task SearchAsync()
    {
        var options = CurrentSearchOptions();
        await _viewModel.SearchAsync(_searchBox.Text ?? string.Empty, options);
        RefreshSearchResults();
    }

    private SearchOptions CurrentSearchOptions() => new(
        MatchCase: _caseSearch.IsChecked == true,
        UseRegex: _regexSearch.IsChecked == true,
        WholeWord: _wholeWordSearch.IsChecked == true);

    private void RefreshSearchResults()
    {
        _searchResults.ItemsSource = _viewModel.SearchResults
            .Select(static hit => hit.Line == 0 ? $"{hit.Title} [title]  {hit.Preview}" : $"{hit.Title}:{hit.Line}  {hit.Preview}")
            .ToArray();
    }

    private async Task SaveCurrentSearchAsync()
    {
        var query = _searchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Enter a search query first.");
        var name = await DesktopDialogService.PromptAsync(this, "Save Search", "Collection name", query);
        if (name is null) return;
        await _features.SaveSearchCollectionAsync(name, query, CurrentSearchOptions());
        RefreshCollections();
        _leftTabs.SelectedIndex = 2;
    }

    private async Task CreateManualCollectionAsync()
    {
        var node = _viewModel.SelectedRow?.Node ?? throw new InvalidOperationException("Select a project item first.");
        var name = await DesktopDialogService.PromptAsync(this, "New Collection", "Collection name", "New Collection");
        if (name is null) return;
        await _features.CreateManualCollectionAsync(name, [node]);
        RefreshCollections();
        _leftTabs.SelectedIndex = 2;
    }

    private async Task AddSelectionToCollectionAsync()
    {
        var collection = SelectedCollection();
        var node = _viewModel.SelectedRow?.Node;
        if (collection is null || node is null) return;
        if (collection.Kind != ProjectCollectionKind.Manual) throw new InvalidOperationException("Only manual collections can accept project items.");
        await _features.AddToManualCollectionAsync(collection, node);
        RefreshCollections();
    }

    private async Task DeleteSelectedCollectionAsync()
    {
        var collection = SelectedCollection();
        if (collection is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(this, "Delete Collection", $"Delete collection '{collection.Name}'?", "Delete");
        if (!confirmed) return;
        await _features.DeleteCollectionAsync(collection);
        if (_activeCollection?.Id == collection.Id) _activeCollection = null;
        RefreshCollections();
        RebuildOutliner();
    }

    private ProjectCollection? SelectedCollection()
    {
        var index = _collections.SelectedIndex;
        return index >= 0 && index < _features.Collections.Count ? _features.Collections[index] : null;
    }

    private async void CollectionDoubleTapped(object? sender, TappedEventArgs e)
    {
        var collection = SelectedCollection();
        if (collection is null) return;
        await RunUiTaskAsync(async () =>
        {
            if (collection.Kind == ProjectCollectionKind.Search)
            {
                _searchBox.Text = collection.Query;
                _caseSearch.IsChecked = collection.MatchCase;
                _regexSearch.IsChecked = collection.UseRegex;
                _wholeWordSearch.IsChecked = collection.WholeWord;
                await _viewModel.SearchAsync(collection.Query, new SearchOptions(collection.MatchCase, collection.MatchCase, collection.WholeWord));
                RefreshSearchResults();
                _leftTabs.SelectedIndex = 1;
            }
            else
            {
                _activeCollection = collection;
                _centerTabs.SelectedIndex = 2;
                RebuildOutliner();
            }
        });
    }

    private async Task AddCustomFieldAsync()
    {
        var name = await DesktopDialogService.PromptAsync(this, "Custom Metadata", "Field name", "POV");
        if (name is null) return;
        await _features.AddCustomFieldAsync(name);
        RebuildCustomFields();
        RebuildOutliner();
    }

    private async Task RemoveCustomFieldAsync()
    {
        if (_features.CustomFields.Count == 0) return;
        var name = await DesktopDialogService.PromptAsync(this, "Remove Custom Metadata", "Field name or key", _features.CustomFields[^1].Name);
        if (name is null) return;
        await _features.RemoveCustomFieldAsync(name, _viewModel.BinderRows);
        RebuildCustomFields();
        RebuildOutliner();
    }

    private async Task AddCommentAsync()
    {
        var node = _viewModel.SelectedRow?.Node;
        if (node?.IsDocument != true) throw new InvalidOperationException("Select a manuscript document first.");
        var text = await DesktopDialogService.PromptAsync(this, "Add Comment", $"Comment at line {CurrentEditorLine()}", string.Empty);
        if (text is null) return;
        await _features.AddCommentAsync(node, CurrentEditorLine(), text);
        RefreshComments();
        SetInspectorVisible(true);
        _inspectorTabs.SelectedIndex = 1;
    }

    private async Task ToggleSelectedCommentResolvedAsync()
    {
        var node = _viewModel.SelectedRow?.Node;
        var comment = SelectedComment();
        if (node is null || comment is null) return;
        await _features.SetCommentResolvedAsync(node, comment, !comment.Resolved);
        RefreshComments();
    }

    private async Task DeleteSelectedCommentAsync()
    {
        var node = _viewModel.SelectedRow?.Node;
        var comment = SelectedComment();
        if (node is null || comment is null) return;
        await _features.DeleteCommentAsync(node, comment);
        RefreshComments();
    }

    private DocumentComment? SelectedComment()
    {
        var node = _viewModel.SelectedRow?.Node;
        var index = _comments.SelectedIndex;
        return node is not null && index >= 0 && index < node.Comments.Count ? node.Comments[index] : null;
    }

    private void CommentDoubleTapped(object? sender, TappedEventArgs e)
    {
        var comment = SelectedComment();
        if (comment is not null)
        {
            _centerTabs.SelectedIndex = 0;
            NavigateEditorToLine(comment.Line);
        }
    }

    private async Task SaveWritingTargetsAsync()
    {
        if (!TryNonNegative(_projectTargetBox.Text, out var projectTarget) ||
            !TryNonNegative(_dailyTargetBox.Text, out var dailyTarget) ||
            !TryNonNegative(_sessionTargetBox.Text, out var sessionTarget))
            throw new InvalidOperationException("Writing targets must be whole numbers of zero or more.");
        await _features.SaveTargetsAsync(projectTarget, dailyTarget, sessionTarget);
        UpdateWritingTargetUi();
    }

    private async Task TakeSnapshotAsync()
    {
        if (!_viewModel.HasDocument) return;
        var label = await DesktopDialogService.PromptAsync(this, "Take Snapshot", "Snapshot label", $"Snapshot {DateTime.Now:g}");
        if (label is null) return;
        await _viewModel.CreateSnapshotAsync(label);
        SetInspectorVisible(true);
        _inspectorTabs.SelectedIndex = 4;
    }

    private SnapshotInfo? SelectedSnapshot()
    {
        var index = _snapshots.SelectedIndex;
        return index >= 0 && index < _viewModel.Snapshots.Count ? _viewModel.Snapshots[index] : null;
    }

    private async Task CompareSelectedSnapshotAsync()
    {
        var snapshot = SelectedSnapshot();
        var node = _viewModel.SelectedRow?.Node;
        if (snapshot is null || node?.IsDocument != true) return;
        var lines = await _features.CompareSnapshotAsync(node, snapshot, _viewModel.EditorText);
        await ShowDiffAsync(snapshot, lines);
    }

    private async Task RestoreSelectedSnapshotAsync()
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(this, "Restore Snapshot", $"Restore snapshot from {snapshot.CreatedAt.LocalDateTime:g}? Current text is snapshotted first.", "Restore");
        if (!confirmed) return;
        await _viewModel.RestoreSnapshotAsync(snapshot);
        _centerTabs.SelectedIndex = 0;
    }

    private async Task DeleteSelectedSnapshotAsync()
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(this, "Delete Snapshot", $"Delete snapshot from {snapshot.CreatedAt.LocalDateTime:g}?", "Delete");
        if (confirmed) await _viewModel.DeleteSnapshotAsync(snapshot);
    }

    private async Task ShowDiffAsync(SnapshotInfo snapshot, IReadOnlyList<DiffLine> lines)
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var changed = lines.Count(line => line.Kind != DiffLineKind.Unchanged);
        var list = new ListBox
        {
            ItemsSource = lines.Select(static line => line.ToString()).ToArray(),
            FontFamily = new FontFamily("monospace")
        };
        var dialog = new Window
        {
            Title = $"Snapshot Diff — {snapshot.Label}",
            Width = 980,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = $"{changed:N0} changed diff line(s) • snapshot {snapshot.CreatedAt.LocalDateTime:g}", Margin = new Thickness(0, 0, 0, 8) },
                    list
                }
            }
        };
        Grid.SetRow(list, 1);
        Grid.SetRow(close, 2);
        ((Grid)dialog.Content!).Children.Add(close);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async Task SaveAsTemplateAsync()
    {
        if (!_features.HasProject) return;
        await SaveAllAsync();
        var name = await DesktopDialogService.PromptAsync(this, "Save Project as Template", "Template name", _viewModel.ProjectTitle);
        if (name is null) return;
        await _features.SaveAsTemplateAsync(name);
        _status.Text = $"Template saved: {name}";
    }

    private async Task NewFromTemplateAsync()
    {
        var templates = await _features.ListTemplatesAsync();
        if (templates.Count == 0) throw new InvalidOperationException("No project templates have been saved yet.");
        var template = await ChooseTemplateAsync(templates);
        if (template is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty folder for the new project",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        var destination = folder.Path.LocalPath;
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Choose an empty destination folder for a template project.");
        var title = await DesktopDialogService.PromptAsync(this, "New from Template", "Project title", template.Name);
        if (title is null) return;
        await _features.MaterializeTemplateAsync(template, destination, title);
        await OpenProjectPathAsync(destination);
    }

    private async Task<ProjectTemplateInfo?> ChooseTemplateAsync(IReadOnlyList<ProjectTemplateInfo> templates)
    {
        var list = new ListBox { ItemsSource = templates.Select(static template => template.Name).ToArray(), SelectedIndex = 0 };
        var create = new Button { Content = "Create", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { create, cancel } };
        var dialog = new Window
        {
            Title = "Choose Project Template",
            Width = 520,
            Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Margin = new Thickness(14),
                Children = { list }
            }
        };
        Grid.SetRow(buttons, 1);
        ((Grid)dialog.Content!).Children.Add(buttons);
        create.Click += (_, _) => dialog.Close(list.SelectedIndex >= 0 ? list.SelectedIndex : -1);
        cancel.Click += (_, _) => dialog.Close(-1);
        var index = await dialog.ShowDialog<int>(this);
        return index >= 0 && index < templates.Count ? templates[index] : null;
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

    private Task SelectCenterTabAsync(int index)
    {
        _centerTabs.SelectedIndex = index;
        if (index == 2) RebuildOutliner();
        return Task.CompletedTask;
    }

    private async Task ShowAboutAsync()
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "About Typescribe",
            Width = 490,
            Height = 285,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Typescribe", FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Long-form writing studio with Corkboard, Outliner, collections, comments, snapshots, realtime LuaLaTeX preview, and professional publishing.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = ".NET 10 • Avalonia • Native AOT • LuaLaTeX", Opacity = 0.7 },
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

    private async void OutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _outline.SelectedItem is not OutlineItemViewModel item) return;
        _previewPageIndex = 0;
        _loadedPreviewVersion = -1;
        _viewModel.SelectOutline(item);
        _inspectorTabs.SelectedIndex = 2;
        await RefreshPreviewIfNeededAsync();
    }

    private void EditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingUi) return;
        _viewModel.UpdateEditorText(_editor.Text ?? string.Empty);
        _features.UpdateSelectedWords(_viewModel.SelectedRow?.Node.PersistentId, _viewModel.WordCount);
        UpdateWritingTargetUi();
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
        if (primary && e.Key == Key.S) { e.Handled = true; await RunUiTaskAsync(SaveAllAsync); }
        else if (primary && e.Key == Key.O) { e.Handled = true; await RunUiTaskAsync(OpenProjectAsync); }
        else if (primary && e.Key == Key.N) { e.Handled = true; await RunUiTaskAsync(CreateProjectAsync); }
        else if (primary && e.Key == Key.F) { e.Handled = true; await RunUiTaskAsync(FocusSearchAsync); }
        else if (e.Key == Key.F2) { e.Handled = true; await RunUiTaskAsync(RenameBinderNodeAsync); }
    }

    private void RefreshAdvancedViews()
    {
        RefreshCollections();
        RefreshComments();
        RebuildCustomFields();
        RebuildOutliner();
        UpdateWritingTargetUi();
        _loadedCorkboardVersion = -1;
        RebuildCorkboardIfNeeded();
    }

    private void RefreshCollections()
    {
        _collections.ItemsSource = _features.Collections
            .Select(static collection => collection.Kind == ProjectCollectionKind.Search ? $"⌕ {collection.Name}" : $"◆ {collection.Name}")
            .ToArray();
    }

    private void RefreshComments()
    {
        var node = _viewModel.SelectedRow?.Node;
        _comments.ItemsSource = node?.Comments
            .Select(static comment => $"{(comment.Resolved ? "✓" : "○")} L{comment.Line}  {comment.Text}")
            .ToArray() ?? [];
    }

    private void RebuildCustomFields()
    {
        _customFieldsPanel.Children.Clear();
        var node = _viewModel.SelectedRow?.Node;
        if (node is null || _features.CustomFields.Count == 0)
        {
            _customFieldsPanel.Children.Add(new TextBlock { Text = "No custom fields.", Opacity = 0.6 });
            return;
        }

        foreach (var field in _features.CustomFields)
        {
            var input = new TextBox { Text = node.CustomMetadata.GetValueOrDefault(field.Key), Watermark = field.Name };
            var currentField = field;
            input.LostFocus += async (_, _) => await RunUiTaskAsync(async () =>
            {
                var liveNode = _viewModel.SelectedRow?.Node;
                if (liveNode is not null) await _features.SetCustomValueAsync(liveNode, currentField.Key, input.Text ?? string.Empty);
                RebuildOutliner();
            });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
            row.Children.Add(new TextBlock { Text = field.Name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(input, 1); row.Children.Add(input);
            _customFieldsPanel.Children.Add(row);
        }
    }

    private void RebuildOutliner()
    {
        _outlinerRows.Children.Clear();
        var fields = _features.CustomFields.ToArray();
        var nodes = GetOutlinerNodes().ToArray();
        _outlinerTitle.Text = _activeCollection is null ? "Outliner — Manuscript" : $"Outliner — {_activeCollection.Name}";
        var columns = "240,90,110,110,90,110,80" + string.Concat(fields.Select(static _ => ",150"));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(4, 2) };
        string[] fixedHeaders = ["Title", "Type", "Status", "Label", "Words", "Target", "Compile"];
        for (var index = 0; index < fixedHeaders.Length; index++) AddCell(header, index, fixedHeaders[index], true);
        for (var index = 0; index < fields.Length; index++) AddCell(header, fixedHeaders.Length + index, fields[index].Name, true);
        _outlinerRows.Children.Add(header);

        foreach (var node in nodes)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(4, 1) };
            var title = new Button { Content = node.Title, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 2) };
            var currentNode = node;
            title.Click += async (_, _) => await RunUiTaskAsync(() => SelectNodeAsync(currentNode));
            row.Children.Add(title);
            AddCell(row, 1, node.Kind.ToString(), false);
            AddCell(row, 2, node.Status, false);
            AddCell(row, 3, node.Label, false);
            var words = _features.GetIndexedWords(node, _viewModel.SelectedRow?.Node.PersistentId, _viewModel.WordCount);
            AddCell(row, 4, words.ToString("N0"), false);
            AddCell(row, 5, node.TargetWords > 0 ? node.TargetWords.ToString("N0") : "—", false);
            AddCell(row, 6, node.IncludeInCompilation ? "Yes" : "No", false);
            for (var index = 0; index < fields.Length; index++)
                AddCell(row, fixedHeaders.Length + index, node.CustomMetadata.GetValueOrDefault(fields[index].Key), false);
            _outlinerRows.Children.Add(row);
        }
    }

    private IEnumerable<ProjectNode> GetOutlinerNodes()
    {
        var documents = _viewModel.BinderRows.Select(static row => row.Node).Where(static node => node.IsDocument);
        if (_activeCollection?.Kind != ProjectCollectionKind.Manual) return documents;
        var wanted = _activeCollection.NodePersistentIds.ToHashSet(StringComparer.Ordinal);
        return documents.Where(node => wanted.Contains(node.PersistentId));
    }

    private async Task SelectNodeAsync(ProjectNode node)
    {
        var row = _viewModel.BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, node) || candidate.Node.PersistentId == node.PersistentId);
        if (row is null) return;
        await _viewModel.SelectAsync(row);
        _centerTabs.SelectedIndex = 0;
    }

    private void UpdateWritingTargetUi()
    {
        if (!_features.HasProject) return;
        _projectProgress.Value = _features.ProjectProgress;
        _dailyProgress.Value = _features.DailyProgress;
        _sessionProgress.Value = _features.SessionProgress;
        _projectProgressText.Text = TargetLabel(_features.ProjectWords, _features.State.ProjectTargetWords);
        _dailyProgressText.Text = TargetLabel(_features.DailyWords, _features.State.DailyTargetWords);
        _sessionProgressText.Text = TargetLabel(_features.SessionWords, _features.State.SessionTargetWords);
        if (!_projectTargetBox.IsKeyboardFocusWithin) _projectTargetBox.Text = _features.State.ProjectTargetWords.ToString();
        if (!_dailyTargetBox.IsKeyboardFocusWithin) _dailyTargetBox.Text = _features.State.DailyTargetWords.ToString();
        if (!_sessionTargetBox.IsKeyboardFocusWithin) _sessionTargetBox.Text = _features.State.SessionTargetWords.ToString();
    }

    private static string TargetLabel(int value, int target)
        => target <= 0 ? $"{value:N0} words • no target" : $"{value:N0} / {target:N0} words ({Math.Clamp(value / (double)target, 0, 1):P0})";

    private void RebuildCorkboardIfNeeded()
    {
        if (_loadedCorkboardVersion == _viewModel.CorkboardVersion) return;
        _loadedCorkboardVersion = _viewModel.CorkboardVersion;
        _corkboardPanel.Children.Clear();
        _corkboardTitle.Text = $"Corkboard — {_viewModel.CorkboardTitle}";
        foreach (var card in _viewModel.CorkboardCards) _corkboardPanel.Children.Add(BuildCorkboardCard(card));
        if (_viewModel.CorkboardCards.Count == 0)
            _corkboardPanel.Children.Add(new TextBlock { Text = "This project group has no cards yet.", Margin = new Thickness(18), Opacity = 0.7 });
    }

    private Control BuildCorkboardCard(CorkboardCardViewModel card)
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock { Text = card.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = card.Synopsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 78, Opacity = 0.85 });
        if (!string.IsNullOrWhiteSpace(card.Label)) stack.Children.Add(new TextBlock { Text = card.Label, FontSize = 11, FontWeight = FontWeight.SemiBold });
        if (card.TargetWords > 0) stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = card.Progress, Height = 5 });
        stack.Children.Add(new TextBlock { Text = card.Footer, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

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
        border.DoubleTapped += async (_, _) => await RunUiTaskAsync(() => _viewModel.SelectCorkboardCardAsync(card));
        border.PointerPressed += (_, e) => CorkboardPointerPressed(card, e, border);
        border.PointerMoved += async (_, e) => await CorkboardPointerMovedAsync(e, border);
        DragDrop.SetAllowDrop(border, true);
        DragDrop.AddDragOverHandler(border, (_, e) => CorkboardDragOver(card, e));
        DragDrop.AddDropHandler(border, async (_, e) => await CorkboardDropAsync(card, border, e));
        return border;
    }

    private void CorkboardPointerPressed(CorkboardCardViewModel card, PointerPressedEventArgs e, Control control)
    {
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        _cardDragCandidate = card;
        _cardDragTrigger = e;
        _cardDragStart = e.GetPosition(control);
    }

    private async Task CorkboardPointerMovedAsync(PointerEventArgs e, Control control)
    {
        if (_cardDragCandidate is null || _cardDragTrigger is null) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            ClearCardDragCandidate();
            return;
        }
        var current = e.GetPosition(control);
        var dx = current.X - _cardDragStart.X;
        var dy = current.Y - _cardDragStart.Y;
        if ((dx * dx) + (dy * dy) < 64) return;
        var candidate = _cardDragCandidate;
        var trigger = _cardDragTrigger;
        ClearCardDragCandidate();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(CorkboardCardFormat, candidate));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    private void CorkboardDragOver(CorkboardCardViewModel target, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(CorkboardCardFormat);
        e.DragEffects = source is not null && !ReferenceEquals(source.Node, target.Node) ? DragDropEffects.Move : DragDropEffects.None;
    }

    private async Task CorkboardDropAsync(CorkboardCardViewModel target, Control targetControl, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(CorkboardCardFormat);
        if (source is null || ReferenceEquals(source.Node, target.Node)) return;
        var sourceRow = _viewModel.BinderRows.FirstOrDefault(row => ReferenceEquals(row.Node, source.Node));
        var targetRow = _viewModel.BinderRows.FirstOrDefault(row => ReferenceEquals(row.Node, target.Node));
        if (sourceRow is null || targetRow is null) return;
        var after = e.GetPosition(targetControl).Y > Math.Max(1, targetControl.Bounds.Height) / 2;
        await _viewModel.MoveBinderItemAsync(sourceRow, targetRow, dropIntoTarget: false, insertAfterTarget: after);
        await ReloadAdvancedStructureAsync();
        e.DragEffects = DragDropEffects.Move;
    }

    private void ClearCardDragCandidate()
    {
        _cardDragCandidate = null;
        _cardDragTrigger = null;
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
        _workspaceGrid.ColumnDefinitions[0].Width = new GridLength(visible ? 310 : 0);
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
        _workspaceGrid.ColumnDefinitions[4].Width = new GridLength(visible ? 470 : 0);
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
        _pdfPreviewImage.Width = 760 * _previewZoom;
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
        var content = text[lineStart..lineEnd].TrimStart();
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

    private int CurrentEditorLine()
    {
        var text = _editor.Text ?? string.Empty;
        var caret = Math.Clamp(_editor.CaretIndex, 0, text.Length);
        var line = 1;
        for (var index = 0; index < caret; index++) if (text[index] == '\n') line++;
        return line;
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
            _outline.ItemsSource = _viewModel.OutlineItems;
            _outline.SelectedItem = _viewModel.SelectedOutline;
            _snapshots.ItemsSource = _viewModel.Snapshots
                .Select(static snapshot => $"{snapshot.CreatedAt.LocalDateTime:g}  •  {snapshot.WordCount:N0} words  •  {snapshot.Label}")
                .ToArray();

            if (!string.Equals(_editor.Text, _viewModel.EditorText, StringComparison.Ordinal)) _editor.Text = _viewModel.EditorText;
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
            _previewStatus.Text = _viewModel.IsLivePreviewBuilding ? "Compiling…" : _viewModel.LivePreviewError is not null ? "Preview error" : _viewModel.LivePreviewPdfPath is not null ? "Live" : string.Empty;

            if (_renderedPreviewPage is null)
            {
                _previewMessage.IsVisible = true;
                _previewMessage.Text = _viewModel.LivePreviewError ?? (_viewModel.CanPublishPdf ? _viewModel.PreviewText : "Install LuaLaTeX to enable live PDF preview.");
            }

            if (_features.HasProject)
            {
                _features.UpdateSelectedWords(_viewModel.SelectedRow?.Node.PersistentId, _viewModel.WordCount);
                UpdateWritingTargetUi();
                RefreshComments();
            }
            RebuildCorkboardIfNeeded();

            var persistentId = _viewModel.SelectedRow?.Node.PersistentId;
            if (!string.Equals(_lastSelectedPersistentId, persistentId, StringComparison.Ordinal))
            {
                _lastSelectedPersistentId = persistentId;
                RebuildCustomFields();
                if (!_compositionMode) _centerTabs.SelectedIndex = _viewModel.SelectedIsContainer ? 1 : 0;
            }

            if (_centerTabs.SelectedIndex == 2) RebuildOutliner();

            if (_appliedEditorNavigationVersion != _viewModel.EditorNavigationVersion)
            {
                _appliedEditorNavigationVersion = _viewModel.EditorNavigationVersion;
                var line = _viewModel.EditorNavigationLine;
                Dispatcher.UIThread.Post(() => NavigateEditorToLine(line), DispatcherPriority.Background);
            }
        }
        finally { _updatingUi = false; }
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
            if (token.IsCancellationRequested) { page.Dispose(); return; }
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
                _pdfPreviewImage.Width = 760 * _previewZoom;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new ScrollViewer { MaxHeight = 230, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } },
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

    private static Border PanelBorder(Control child) => new()
    {
        BorderThickness = new Thickness(1),
        BorderBrush = Brushes.Gray,
        CornerRadius = new CornerRadius(4),
        Child = child
    };

    private static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    private static void AddCell(Grid grid, int column, string? text, bool header)
    {
        var block = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(text) ? "—" : text,
            Margin = new Thickness(6, 5),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static TextBlock InspectorLabel(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };
    private static Button ToolbarButton(string content) => new() { Content = content, Margin = new Thickness(0, 0, 6, 0) };
    private static Button SmallButton(string content) => new() { Content = content, Margin = new Thickness(0, 0, 4, 4) };

    private static bool TryNonNegative(string? text, out int value)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrEmpty(normalized)) { value = 0; return true; }
        return int.TryParse(normalized, out value) && value >= 0;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "typescribe-book" : cleaned;
    }
}
