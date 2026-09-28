using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

public sealed class MainWindow : Window
{
    private readonly WorkspaceViewModel _viewModel;
    private readonly ListBox _binder = new();
    private readonly TextBox _editor = new();
    private readonly TextBlock _preview = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _wordCount = new();
    private readonly TextBlock _engine = new();
    private readonly TextBox _searchBox = new();
    private readonly ListBox _searchResults = new();
    private readonly Button _exportPdfButton = new() { Content = "Publish Book PDF" };
    private bool _updatingUi;

    public MainWindow(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Typescribe";
        Width = 1440;
        Height = 900;
        MinWidth = 980;
        MinHeight = 620;
        Content = BuildLayout();

        _viewModel.StateChanged += OnStateChanged;
        _binder.SelectionChanged += BinderSelectionChanged;
        _editor.TextChanged += EditorTextChanged;
        _searchBox.KeyDown += SearchBoxKeyDown;
        _searchResults.DoubleTapped += SearchResultDoubleTapped;
        Closed += WindowClosed;
        UpdateFromState();
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
            ColumnDefinitions = new ColumnDefinitions("250,5*,4*"),
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
        var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = false };
        var newButton = new Button { Content = "New Project", Margin = new Thickness(0, 0, 6, 0) };
        var openButton = new Button { Content = "Open Project", Margin = new Thickness(0, 0, 6, 0) };
        var addChapterButton = new Button { Content = "New Chapter", Margin = new Thickness(0, 0, 6, 0) };
        var saveButton = new Button { Content = "Save", Margin = new Thickness(0, 0, 6, 0) };
        var typstButton = new Button { Content = "Export Book .typ", Margin = new Thickness(0, 0, 6, 0) };
        var latexButton = new Button { Content = "Export Book .tex", Margin = new Thickness(0, 0, 6, 0) };

        newButton.Click += async (_, _) => await RunUiTaskAsync(CreateProjectAsync);
        openButton.Click += async (_, _) => await RunUiTaskAsync(OpenProjectAsync);
        addChapterButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.AddChapterAsync());
        saveButton.Click += async (_, _) => await RunUiTaskAsync(() => _viewModel.SaveNowAsync());
        _exportPdfButton.Click += async (_, _) => await RunUiTaskAsync(ExportPdfAsync);
        typstButton.Click += async (_, _) => await RunUiTaskAsync(ExportTypstAsync);
        latexButton.Click += async (_, _) => await RunUiTaskAsync(ExportLatexAsync);

        bar.Children.Add(newButton);
        bar.Children.Add(openButton);
        bar.Children.Add(addChapterButton);
        bar.Children.Add(saveButton);
        bar.Children.Add(_exportPdfButton);
        bar.Children.Add(typstButton);
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

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,3*,Auto,2*") };
        panel.Children.Add(searchRow);
        Grid.SetRow(_binder, 1);
        panel.Children.Add(_binder);
        var resultLabel = new TextBlock { Text = "Search results", Margin = new Thickness(0, 8, 0, 4) };
        Grid.SetRow(resultLabel, 2);
        panel.Children.Add(resultLabel);
        Grid.SetRow(_searchResults, 3);
        panel.Children.Add(_searchResults);
        return panel;
    }

    private Control BuildEditorPanel()
    {
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _editor.FontFamily = new Avalonia.Media.FontFamily("monospace");
        _editor.FontSize = 15;
        _editor.VerticalContentAlignment = VerticalAlignment.Top;
        _editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        _editor.VerticalAlignment = VerticalAlignment.Stretch;
        _editor.Margin = new Thickness(8, 0);
        return _editor;
    }

    private Control BuildPreviewPanel()
    {
        _preview.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _preview.FontSize = 16;
        _preview.LineHeight = 24;
        _preview.Margin = new Thickness(18);
        var scroll = new ScrollViewer
        {
            Content = _preview,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FAF8F4"))
        };
        return scroll;
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

    private async Task ExportPdfAsync()
    {
        var path = await PickSavePathAsync("Publish Book PDF", "PDF document", "pdf");
        if (path is not null) await _viewModel.ExportPdfAsync(path);
    }

    private async Task ExportTypstAsync()
    {
        var path = await PickSavePathAsync("Export Book Typst source", "Typst source", "typ");
        if (path is not null) await _viewModel.ExportTypstAsync(path);
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
        await _viewModel.SearchAsync(_searchBox.Text ?? string.Empty);
        _searchResults.ItemsSource = _viewModel.SearchResults.Select(static hit => $"{hit.Title}:{hit.Line}  {hit.Preview}").ToArray();
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

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateFromState);

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
            _preview.Text = _viewModel.PreviewText;
            _status.Text = _viewModel.Status;
            _wordCount.Text = $"{_viewModel.WordCount:N0} words";
            _engine.Text = _viewModel.CanPublishPdf ? $"PDF: {_viewModel.PublishingEngineName}" : "PDF engine not bundled in dev build";
            _exportPdfButton.IsEnabled = _viewModel.HasDocument && _viewModel.CanPublishPdf;
        }
        finally
        {
            _updatingUi = false;
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
            Width = 520,
            Height = 220,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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
        await _viewModel.DisposeAsync();
    }
}
