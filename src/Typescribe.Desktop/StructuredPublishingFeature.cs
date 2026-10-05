using System.Collections;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Models;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>Coordinates TS-213–TS-219 project UI and keeps structured publishing concerns together.</summary>
internal sealed class StructuredPublishingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IDocumentParser _parser;
    private readonly IDocumentRenderer _renderer;
    private readonly IPdfPublishingEngine _publishingEngine;
    private readonly BibTeXDatabase _bibliography = new();
    private readonly AssetManagerService _assets;
    private readonly ManuscriptPreflightService _preflight;
    private readonly DocxInterchangeService _docx;
    private readonly ListBox _problems = new();
    private readonly List<ProblemDisplay> _problemRows = [];

    private TabControl? _inspectorTabs;
    private TabItem? _problemsTab;
    private bool _menuInstalled;
    private bool _editorInstalled;
    private bool _disposed;

    private StructuredPublishingFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser,
        IDocumentRenderer renderer,
        IPdfPublishingEngine publishingEngine)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _parser = parser;
        _renderer = renderer;
        _publishingEngine = publishingEngine;
        _assets = new AssetManagerService(repository, parser);
        _preflight = new ManuscriptPreflightService(repository, parser, _bibliography, _assets);
        _docx = new DocxInterchangeService(parser, _bibliography);
        _problems.DoubleTapped += async (_, _) => await NavigateSelectedProblemAsync();
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser,
        IDocumentRenderer renderer,
        IPdfPublishingEngine publishingEngine)
    {
        var feature = new StructuredPublishingFeature(window, viewModel, repository, parser, renderer, publishingEngine);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.DiscoverAndInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => DiscoverAndInstall();
    private void OnLayoutUpdated(object? sender, EventArgs e) => DiscoverAndInstall();
    private void OnClosed(object? sender, EventArgs e) => _disposed = true;

    private void DiscoverAndInstall()
    {
        if (_disposed) return;
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        if (!_menuInstalled) InstallMenus(controls.OfType<Menu>().FirstOrDefault());
        if (_inspectorTabs is null)
        {
            _inspectorTabs = controls.OfType<TabControl>().FirstOrDefault(tab =>
                TabItems(tab).Any(item => HeaderEquals(item, "Inspector")));
        }
        InstallProblemsTab();

        if (!_editorInstalled)
        {
            var editor = controls.OfType<Editing.ManuscriptEditor>().FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
                         ?? controls.OfType<Editing.ManuscriptEditor>().FirstOrDefault();
            if (editor is not null)
            {
                StructuredEditorFeature.Install(_window, _viewModel, _repository, editor, _assets, _bibliography, _parser);
                _editorInstalled = true;
            }
        }

        if (_menuInstalled && _problemsTab is not null && _editorInstalled)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void InstallMenus(Menu? menu)
    {
        if (menu?.ItemsSource is not IEnumerable source) return;
        var top = source.Cast<object>().OfType<MenuItem>().ToArray();
        var file = top.FirstOrDefault(item => HeaderEquals(item, "File"));
        var project = top.FirstOrDefault(item => HeaderEquals(item, "Project"));
        var publish = top.FirstOrDefault(item => HeaderEquals(item, "Publish"));

        if (file is not null)
        {
            var items = MenuItems(file.ItemsSource);
            ReplaceCommand(items, "Export LaTeX…", ExportLatexAsync);
            ReplaceCommand(items, "Publish PDF…", PublishPdfAsync);
            items.Add(new Separator());
            items.Add(Command("Import DOCX…", ImportDocxAsync));
            items.Add(Command("Export DOCX…", ExportDocxAsync));
            file.ItemsSource = items.ToArray();
        }

        if (project is not null)
        {
            var items = MenuItems(project.ItemsSource);
            items.Insert(0, new Separator());
            items.Insert(0, Command("Preflight…", ShowPreflightAsync));
            items.Insert(0, Command("Bibliography…", OpenBibliographyAsync));
            items.Insert(0, Command("Assets…", OpenAssetsAsync));
            project.ItemsSource = items.ToArray();
        }

        if (publish is not null)
        {
            var items = MenuItems(publish.ItemsSource);
            ReplaceCommand(items, "Export LaTeX…", ExportLatexAsync);
            foreach (var existing in items.OfType<MenuItem>().Where(item => Normalize(item.Header?.ToString()).Contains("Publish PDF", StringComparison.OrdinalIgnoreCase)))
                existing.IsVisible = false;
            items.Insert(0, Command("Preflight & Publish PDF…", PreflightAndPublishAsync));
            publish.ItemsSource = items.ToArray();
        }
        _menuInstalled = file is not null || project is not null || publish is not null;
    }

    private void InstallProblemsTab()
    {
        if (_problemsTab is not null || _inspectorTabs is null) return;
        var items = TabItems(_inspectorTabs);
        var existing = items.FirstOrDefault(item => HeaderEquals(item, "Problems"));
        if (existing is not null)
        {
            _problemsTab = existing;
            return;
        }

        var refresh = new Button { Content = "Run Preflight", HorizontalAlignment = HorizontalAlignment.Right };
        refresh.Click += async (_, _) => await ShowPreflightAsync();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 8, 8, 4) };
        header.Children.Add(new TextBlock { Text = "Problems", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        panel.Children.Add(header);
        Grid.SetRow(_problems, 1);
        _problems.Margin = new Thickness(6);
        panel.Children.Add(_problems);
        _problemsTab = new TabItem { Header = "Problems", Content = panel };
        items.Add(_problemsTab);
        _inspectorTabs.ItemsSource = items.ToArray();
    }

    private async Task OpenAssetsAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        await AssetManagerWindow.ShowAsync(_window, project, _assets);
    }

    private async Task OpenBibliographyAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        await BibliographyManagerWindow.ShowAsync(_window, project, _bibliography);
    }

    private async Task ShowPreflightAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        var report = await _preflight.RunAsync(project);
        SetProblems(report.Issues.Select(static issue => new CompilerProblem(issue.Severity, issue.Message, issue.DocumentId, issue.DocumentTitle, issue.Line)).ToArray());
        SelectProblemsTab();
        await ShowReportDialogAsync(report);
    }

    private async Task PreflightAndPublishAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        await _viewModel.SaveNowAsync();
        var report = await _preflight.RunAsync(project);
        SetProblems(report.Issues.Select(static issue => new CompilerProblem(issue.Severity, issue.Message, issue.DocumentId, issue.DocumentTitle, issue.Line)).ToArray());
        SelectProblemsTab();
        if (report.HasErrors)
        {
            await ShowReportDialogAsync(report, "Publishing stopped because preflight found errors.");
            return;
        }

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Publish Book PDF",
            SuggestedFileName = SanitizeFileName(project.Title) + ".pdf",
            FileTypeChoices = [new FilePickerFileType("PDF document") { Patterns = ["*.pdf"] }]
        });
        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        var compilation = await BuildCompilationAsync(project);
        try
        {
            await PublishingProgressDialog.RunAsync(_window, "Publishing PDF", async progress =>
            {
                progress.Report(0.18, "Rendering manuscript to LaTeX…");
                var latex = _renderer.RenderLatex(_parser.Parse(compilation.Source), project.Title, project.Style);
                latex = AddProjectGraphicPath(latex, project.RootPath);

                progress.Report(0.42, _publishingEngine.IsAvailable
                    ? "Preparing PDF engine…"
                    : "Preparing LuaLaTeX…");
                if (!_publishingEngine.IsAvailable) await _publishingEngine.EnsureAvailableAsync();

                progress.Report(0.68, "Running LuaLaTeX (2 passes)…");
                await _publishingEngine.PublishAsync(latex, destination, passes: 2);
                progress.Report(0.98, "Finalizing PDF…");
            });
        }
        catch (PublishingDiagnosticException ex)
        {
            var mapped = MapSourceProblem(compilation.Segments, ex);
            SetProblems(_problemRows.Select(static row => row.Problem).Append(mapped).ToArray());
            SelectProblemsTab();
            await ShowMessageAsync("PDF build failed", BuildPublishingFailureMessage(mapped, ex));
        }
    }

    private async Task PublishPdfAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        await _viewModel.SaveNowAsync();
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Publish Book PDF",
            SuggestedFileName = SanitizeFileName(project.Title) + ".pdf",
            FileTypeChoices = [new FilePickerFileType("PDF document") { Patterns = ["*.pdf"] }]
        });
        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        await PublishingProgressDialog.RunAsync(_window, "Publishing PDF", async progress =>
        {
            progress.Report(0.18, "Preparing manuscript…");
            progress.Report(0.42, "Rendering and compiling PDF…");
            await _viewModel.ExportPdfAsync(destination);
            progress.Report(0.98, "Finalizing PDF…");
        });
    }

    private async Task ExportLatexAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        await _viewModel.SaveNowAsync();
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Book LaTeX source",
            SuggestedFileName = SanitizeFileName(project.Title) + ".tex",
            FileTypeChoices = [new FilePickerFileType("LaTeX source") { Patterns = ["*.tex"] }]
        });
        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        await PublishingProgressDialog.RunAsync(_window, "Exporting LaTeX", async progress =>
        {
            progress.Report(0.18, "Preparing manuscript…");
            progress.Report(0.52, "Rendering LaTeX source…");
            await _viewModel.ExportLatexAsync(destination);
            progress.Report(0.98, "Finalizing LaTeX export…");
        });
    }

    private async Task ExportDocxAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        await _viewModel.SaveNowAsync();
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DOCX",
            SuggestedFileName = SanitizeFileName(project.Title) + ".docx",
            FileTypeChoices = [new FilePickerFileType("Word document") { Patterns = ["*.docx"] }]
        });
        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        await PublishingProgressDialog.RunAsync(_window, "Exporting DOCX", async progress =>
        {
            progress.Report(0.14, "Collecting manuscript documents…");
            var documents = new List<(ProjectNode Node, string Content)>();
            await foreach (var item in _repository.EnumerateDocumentsAsync(project)) documents.Add(item);

            progress.Report(0.58, "Writing Word document…");
            await _docx.ExportAsync(project, documents, destination);
            progress.Report(0.98, "Finalizing DOCX export…");
        });
    }

    private async Task ImportDocxAsync()
    {
        var project = CurrentProject();
        if (project is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import DOCX",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Word document") { Patterns = ["*.docx"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        var markup = await _docx.ImportAsync(project, path);
        var title = Path.GetFileNameWithoutExtension(path);
        await _viewModel.AddNodeAsync(NodeKind.Chapter, string.IsNullOrWhiteSpace(title) ? "Imported DOCX" : title);
        _viewModel.UpdateEditorText(markup);
        await _viewModel.SaveNowAsync();
    }

    private async Task<Compilation> BuildCompilationAsync(BookProject project)
    {
        var source = new StringBuilder();
        var segments = new List<SourceSegment>();
        var line = 1;
        await foreach (var (node, content) in _repository.EnumerateDocumentsAsync(project))
        {
            if (source.Length > 0)
            {
                source.AppendLine().AppendLine();
                line += 2;
            }
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
            var lines = normalized.Length == 0 ? 1 : normalized.Count(static ch => ch == '\n') + 1;
            segments.Add(new SourceSegment(line, line + lines - 1, node.PersistentId, node.Title));
            source.Append(normalized).AppendLine();
            line += lines;
        }

        var entries = await _bibliography.LoadAsync(project.RootPath);
        foreach (var entry in entries)
        {
            source.AppendLine(AdvancedDocumentParser.CreateBibliographyDirective(entry));
            line++;
        }
        return new Compilation(source.ToString(), segments);
    }

    private static CompilerProblem MapSourceProblem(IReadOnlyList<SourceSegment> segments, PublishingDiagnosticException exception)
    {
        if (exception.SourceLine is not int sourceLine)
            return new CompilerProblem(PreflightSeverity.Error, exception.Message, null, null, null, exception.GeneratedLine);
        var segment = segments.FirstOrDefault(candidate => sourceLine >= candidate.StartLine && sourceLine <= candidate.EndLine);
        if (segment is null)
            return new CompilerProblem(PreflightSeverity.Error, exception.Message, null, null, sourceLine, exception.GeneratedLine);
        var local = sourceLine - segment.StartLine + 1;
        return new CompilerProblem(PreflightSeverity.Error, exception.Message, segment.DocumentId, segment.DocumentTitle, local, exception.GeneratedLine);
    }

    private static string BuildPublishingFailureMessage(CompilerProblem problem, PublishingDiagnosticException exception)
    {
        var builder = new StringBuilder(problem.Message);
        if (problem.DocumentTitle is not null)
            builder.AppendLine().AppendLine($"{problem.DocumentTitle}:{problem.SourceLine}");
        if (exception.GeneratedLine is int generatedLine)
            builder.AppendLine().Append($"Generated LaTeX line: {generatedLine}");

        var details = TrimFailureDetails(exception.Details);
        if (!string.IsNullOrWhiteSpace(details))
            builder.AppendLine().AppendLine().Append(details);

        return builder.ToString();
    }

    private static string TrimFailureDetails(string details)
    {
        var normalized = (details ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (normalized.Length == 0) return string.Empty;

        const int maxLength = 2_400;
        return normalized.Length <= maxLength ? normalized : normalized[^maxLength..];
    }

    private void SetProblems(IEnumerable<CompilerProblem> problems)
    {
        _problemRows.Clear();
        _problemRows.AddRange(problems.Select(static problem => new ProblemDisplay(problem)));
        _problems.ItemsSource = _problemRows.ToArray();
    }

    private async Task NavigateSelectedProblemAsync()
    {
        if (_problems.SelectedItem is not ProblemDisplay row || row.Problem.DocumentId is null) return;
        var binder = _viewModel.BinderRows.FirstOrDefault(candidate => string.Equals(candidate.Node.PersistentId, row.Problem.DocumentId, StringComparison.Ordinal));
        if (binder is null) return;
        var line = row.Problem.SourceLine ?? 1;
        await _viewModel.GoToSearchHitAsync(new SearchHit(binder.Node.Id, binder.Node.Title, binder.Node.RelativePath ?? string.Empty, line, row.Problem.Message));
    }

    private void SelectProblemsTab()
    {
        if (_inspectorTabs is null || _problemsTab is null) return;
        _inspectorTabs.SelectedItem = _problemsTab;
    }

    private async Task ShowReportDialogAsync(PreflightReport report, string? note = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"✓ {report.FiguresResolved} figures resolved");
        builder.AppendLine($"✓ {report.CitationsResolved} citations resolved");
        if (report.UnusedAssets > 0) builder.AppendLine($"⚠ {report.UnusedAssets} unused assets");
        foreach (var issue in report.Issues)
        {
            var icon = issue.Severity == PreflightSeverity.Error ? "✕" : issue.Severity == PreflightSeverity.Warning ? "⚠" : "•";
            var location = issue.DocumentTitle is null ? string.Empty : $"  {issue.DocumentTitle}:{issue.Line}";
            builder.Append(icon).Append(' ').Append(issue.Message).AppendLine(location);
        }
        if (!string.IsNullOrWhiteSpace(note)) builder.AppendLine().AppendLine(note);
        await ShowMessageAsync("Manuscript Preflight", builder.ToString());
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var close = new Button { Content = "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        var text = new TextBox { Text = message, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12) };
        grid.Children.Add(text);
        Grid.SetRow(close, 1);
        close.Margin = new Thickness(0, 10, 0, 0);
        grid.Children.Add(close);
        var dialog = new Window { Title = title, Width = 720, Height = 520, MinWidth = 480, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(_window);
    }

    private BookProject? CurrentProject() => _repository.CurrentProject;

    private static string AddProjectGraphicPath(string latex, string projectRoot)
    {
        var path = Path.GetFullPath(projectRoot).Replace('\\', '/').Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        var command = $"\\graphicspath{{{{{path.TrimEnd('/')}/}}}}" + Environment.NewLine;
        var marker = "\\begin{document}";
        var index = latex.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? command + latex : latex.Insert(index, command);
    }

    private static void ReplaceCommand(List<object> items, string header, Func<Task> action)
    {
        var index = items.FindIndex(item => item is MenuItem menuItem && HeaderEquals(menuItem, header));
        if (index >= 0) items[index] = Command(header, action);
    }

    private static MenuItem Command(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static List<object> MenuItems(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object>().ToList() : [];

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source) return source.Cast<object>().OfType<TabItem>().ToList();
        return tabs.Items.Cast<object>().OfType<TabItem>().ToList();
    }

    private static bool HeaderEquals(MenuItem item, string expected)
        => string.Equals(Normalize(item.Header?.ToString()), expected, StringComparison.OrdinalIgnoreCase);

    private static bool HeaderEquals(TabItem item, string expected)
        => string.Equals(Normalize(item.Header?.ToString()), expected, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => (value ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Trim();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        return result.Length == 0 ? "manuscript" : result;
    }

    private sealed record SourceSegment(int StartLine, int EndLine, string DocumentId, string DocumentTitle);
    private sealed record Compilation(string Source, IReadOnlyList<SourceSegment> Segments);

    private sealed record ProblemDisplay(CompilerProblem Problem)
    {
        public override string ToString()
        {
            var severity = Problem.Severity == PreflightSeverity.Error ? "Error" : Problem.Severity == PreflightSeverity.Warning ? "Warn" : "Info";
            var location = Problem.DocumentTitle is null ? string.Empty : $"  {Problem.DocumentTitle}:{Problem.SourceLine}";
            return $"{severity}{location}   {Problem.Message}";
        }
    }
}
