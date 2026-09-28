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

public sealed class MainWindow : Window
{
    private readonly WorkspaceViewModel _viewModel;
    private readonly PdfPreviewRenderer _pdfPreviewRenderer = new();
    private readonly ListBox _binder = new();
    private readonly TextBox _editor = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _wordCount = new();
    private readonly TextBlock _engine = new();
    private readonly TextBox _searchBox = new();
    private readonly ListBox _searchResults = new();
    private readonly CheckBox _regexSearch = new() { Content = "Regex" };
    private readonly CheckBox _caseSearch = new() { Content = "Case" };
    private readonly CheckBox _wholeWordSearch = new() { Content = "Whole word" };

    private readonly Button _renameBinderButton = new() { Content = "Rename" };
    private readonly Button _deleteBinderButton = new() { Content = "Delete" };
    private readonly Button _moveUpButton = new() { Content = "↑" };
    private readonly Button _moveDownButton = new() { Content = "↓" };
    private readonly Button _includeButton = new() { Content = "Exclude" };
    private readonly Button _styleButton = new() { Content = "Book Style", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _refreshPreviewButton = new() { Content = "Refresh Preview", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _exportPdfButton = new() { Content = "Publish Book PDF", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _downloadPdfEngineButton = new() { Content = "Download LuaLaTeX", Margin = new Thickness(0, 0, 6, 0) };

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

    private CancellationTokenSource? _previewRenderCts;
    private PdfPreviewPage? _renderedPreviewPage;
    private long _loadedPreviewVersion = -1;
    private int _previewPageIndex;
    private double _previewZoom = 1.0;
    private bool _updatingUi;

    public MainWindow(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Typescribe";
        Width = 1500;
        Height = 920;
        MinWidth = 1040;
        MinHeight = 650;
        Content = BuildLayout();

        _viewModel.StateChanged += OnStateChanged;
        _binder.SelectionChanged += BinderSelectionChanged;
        _editor.TextChanged += EditorTextChanged;
        _searchBox.KeyDown += SearchBoxKeyDown;
        _searchResults.DoubleTapped += SearchResultDoubleTapped;
        Closed += WindowClosed;
        UpdateFromState();
        _ = RefreshPreviewIfNeededAsync();
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = null
        };

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,5*,4*"),
            Margin = new Thickness(8, 0, 8, 0)
        };
        Grid.SetRow(workspace, 1);
        root.Children.Add(workspace);

        var binderPanel = BuildBinderPanel();
        Grid.SetColumn(binderPanel, 0);
        workspace.Children.Add(binderPanel);

        var editorPanel = BuildEditorPanel();
        Grid.SetColumn(editorPanel, 1);
        workspace.Children.Add(editorPanel);

        var previewPanel = BuildPreviewPanel();
        Grid.SetColumn(previewPanel, 2);
        workspace.Children.Add(previewPanel);

        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);
        return root;
    }

    private Control BuildToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(8), Orientation = Orientation.Horizontal };
        var newButton = ToolbarButton("New Project");
        var openButton = ToolbarButton("Open Project");
        var saveButton = ToolbarButton("Save");
        var latexButton = ToolbarButton("Export .tex");

        newButton.Click += async (_, _) => await RunUiTaskAsync(CreateProjectAsync);
        openButton.Click += async (_, _) => await RunUiTaskAsync(OpenProjectAsync);
        saveButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.SaveNowAsync());
        _styleButton.Click += async (_, _) => await RunUiTaskAsync(EditStyleAsync);
        _refreshPreviewButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.RefreshLivePdfPreviewAsync());
        _exportPdfButton.Click += async (_, _) => await RunUiTaskAsync(ExportPdfAsync);
        _downloadPdfEngineButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.EnsurePdfEngineAsync());
        latexButton.Click += async (_, _) => await RunUiTaskAsync(ExportLatexAsync);

        bar.Children.Add(newButton);
        bar.Children.Add(openButton);
        bar.Children.Add(saveButton);
        bar.Children.Add(_styleButton);
        bar.Children.Add(_refreshPreviewButton);
        bar.Children.Add(_exportPdfButton);
        bar.Children.Add(_downloadPdfEngineButton);
        bar.Children.Add(latexButton);
        return bar;
    }

    private Control BuildBinderPanel()
    {
        _binder.HorizontalAlignment = HorizontalAlignment.Stretch;
        _binder.VerticalAlignment = VerticalAlignment.Stretch;

        var searchButton = new Button { Content = "Search", HorizontalAlignment = HorizontalAlignment.Right };
        searchButton.Click += async (_, _) => await RunUiTaskAsync(SearchAsync);
        _searchBox.Watermark = "Search project";

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        searchRow.Children.Add(_searchBox);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        var searchOptions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _regexSearch.Margin = new Thickness(0, 0, 8, 0);
        _caseSearch.Margin = new Thickness(0, 0, 8, 0);
        searchOptions.Children.Add(_regexSearch);
        searchOptions.Children.Add(_caseSearch);
        searchOptions.Children.Add(_wholeWordSearch);

        var binderActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var addChapter = SmallButton("+ Chapter");
        var addFolder = SmallButton("+ Folder");
        var addPart = SmallButton("+ Part");
        _renameBinderButton.Margin = new Thickness(0, 0, 4, 4);
        _deleteBinderButton.Margin = new Thickness(0, 0, 4, 4);
        _moveUpButton.Margin = new Thickness(0, 0, 4, 4);
        _moveDownButton.Margin = new Thickness(0, 0, 4, 4);
        _includeButton.Margin = new Thickness(0, 0, 4, 4);

        addChapter.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Chapter, "New Chapter"));
        addFolder.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Folder, "New Folder"));
        addPart.Click += async (_, _) => await RunUiTaskAsync(() => AddBinderNodeAsync(NodeKind.Part, "New Part"));
        _renameBinderButton.Click += async (_, _) => await RunUiTaskAsync(RenameBinderNodeAsync);
        _deleteBinderButton.Click += async (_, _) => await RunUiTaskAsync(DeleteBinderNodeAsync);
        _moveUpButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.MoveSelectedAsync(-1));
        _moveDownButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.MoveSelectedAsync(1));
        _includeButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.ToggleSelectedCompilationAsync());

        binderActions.Children.Add(addChapter);
        binderActions.Children.Add(addFolder);
        binderActions.Children.Add(addPart);
        binderActions.Children.Add(_renameBinderButton);
        binderActions.Children.Add(_deleteBinderButton);
        binderActions.Children.Add(_moveUpButton);
        binderActions.Children.Add(_moveDownButton);
        binderActions.Children.Add(_includeButton);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,3*,Auto,2*") };
        panel.Children.Add(searchRow);
        Grid.SetRow(searchOptions, 1);
        panel.Children.Add(searchOptions);
        Grid.SetRow(binderActions, 2);
        panel.Children.Add(binderActions);
        Grid.SetRow(_binder, 3);
        panel.Children.Add(_binder);
        var resultLabel = new TextBlock { Text = "Search results", Margin = new Thickness(0, 8, 0, 4) };
        Grid.SetRow(resultLabel, 4);
        panel.Children.Add(resultLabel);
        Grid.SetRow(_searchResults, 5);
        panel.Children.Add(_searchResults);
        return panel;
    }

    private Control BuildEditorPanel()
    {
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.TextWrapping = TextWrapping.Wrap;
        _editor.FontFamily = new FontFamily("monospace");
        _editor.FontSize = 15;
        _editor.VerticalContentAlignment = VerticalAlignment.Top;
        _editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        _editor.VerticalAlignment = VerticalAlignment.Stretch;
        _editor.Margin = new Thickness(8, 0);
        return _editor;
    }

    private Control BuildPreviewPanel()
    {
        _previousPageButton.Click += async (_, _) => await ChangePreviewPageAsync(-1);
        _nextPageButton.Click += async (_, _) => await ChangePreviewPageAsync(1);
        _zoomOutButton.Click += (_, _) => ChangeZoom(-0.1);
        _zoomInButton.Click += (_, _) => ChangeZoom(0.1);

        var controls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*"),
            Margin = new Thickness(8, 0, 8, 6)
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
        Grid.SetColumn(_previewStatus, 5);
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
            await RunUiTaskAsync(() => _viewModel.SelectAsync(row));
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
            await RunUiTaskAsync(() => _viewModel.GoToSearchHitAsync(_viewModel.SearchResults[index]));
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
            if (!string.Equals(_editor.Text, _viewModel.EditorText, StringComparison.Ordinal))
                _editor.Text = _viewModel.EditorText;
            _editor.IsEnabled = _viewModel.HasDocument;
            _status.Text = _viewModel.Status;
            _wordCount.Text = $"{_viewModel.WordCount:N0} words";
            _engine.Text = _viewModel.CanPublishPdf
                ? $"PDF: {_viewModel.PublishingEngineName}"
                : "PDF: LuaLaTeX not installed";

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
            Width = 600,
            Height = 280,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new ScrollViewer
                    {
                        MaxHeight = 180,
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

    private static Button ToolbarButton(string content)
        => new() { Content = content, Margin = new Thickness(0, 0, 6, 0) };

    private static Button SmallButton(string content)
        => new() { Content = content, Margin = new Thickness(0, 0, 4, 4) };
}
