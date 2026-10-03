using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Adds manuscript structure conversion and configurable DOCX heading import without changing
/// the editor's core parsing/rendering path. Project-item conversions use the existing
/// transactional mutation service so they remain undoable and restart-safe.
/// </summary>
internal sealed class DocumentStructureFeature
{
    private static readonly NodeKind[] ConvertibleKinds =
    [
        NodeKind.Heading,
        NodeKind.Chapter,
        NodeKind.Section,
        NodeKind.Scene,
        NodeKind.Research,
        NodeKind.Note
    ];

    private static readonly FieldInfo? ViewModelField = typeof(StudioWorkspaceWindow)
        .GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ProjectField = typeof(WorkspaceViewModel)
        .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? PreviewCtsField = typeof(WorkspaceViewModel)
        .GetField("_previewCts", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? RefreshBinderMethod = typeof(WorkspaceViewModel)
        .GetMethod("RefreshBinder", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? SchedulePreviewMethod = typeof(WorkspaceViewModel)
        .GetMethod("ScheduleLivePdfPreview", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IProjectMutationService _mutations = new FileSystemProjectMutationService();
    private readonly IProjectRepository _importRepository = new FileSystemProjectRepository();
    private readonly DocxInterchangeService _docx = new(new AdvancedDocumentParser(), new BibTeXDatabase());
    private bool _projectMenuInstalled;
    private bool _treeMenuInstalled;
    private bool _docxImportInstalled;
    private bool _disposed;

    private DocumentStructureFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (ViewModelField?.GetValue(window) is not WorkspaceViewModel viewModel) return;

        var feature = new DocumentStructureFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.DiscoverAndInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => DiscoverAndInstall();
    private void OnLayoutUpdated(object? sender, EventArgs e) => DiscoverAndInstall();

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private void DiscoverAndInstall()
    {
        if (_disposed || (_projectMenuInstalled && _treeMenuInstalled && _docxImportInstalled)) return;

        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        var menu = controls.OfType<Menu>().FirstOrDefault();
        var topLevel = TopLevelMenuItems(menu?.ItemsSource).ToArray();

        if (!_projectMenuInstalled)
        {
            var project = topLevel.FirstOrDefault(item => HeaderEquals(item, "Project"));
            if (project is not null)
            {
                var items = Items(project.ItemsSource);
                if (!items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Convert To")))
                {
                    items.Add(new Separator());
                    items.Add(BuildConvertMenu());
                    project.ItemsSource = items.ToArray();
                }
                _projectMenuInstalled = true;
            }
        }

        if (!_treeMenuInstalled)
        {
            var tree = controls.OfType<TreeView>()
                .FirstOrDefault(candidate => candidate.Classes.Contains("project-explorer-tree"));
            if (tree?.ContextMenu is { } contextMenu)
            {
                var items = Items(contextMenu.ItemsSource);
                if (!items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Convert To")))
                {
                    items.Add(new Separator());
                    items.Add(BuildConvertMenu());
                    contextMenu.ItemsSource = items.ToArray();
                }
                _treeMenuInstalled = true;
            }
        }

        if (!_docxImportInstalled)
        {
            var file = topLevel.FirstOrDefault(item => HeaderEquals(item, "File"));
            if (file is not null)
            {
                var items = Items(file.ItemsSource);
                var existingIndex = items.FindIndex(item =>
                    item is MenuItem menuItem && HeaderEquals(menuItem, "Import DOCX"));
                if (existingIndex >= 0)
                {
                    if (items[existingIndex] is MenuItem existing)
                        existing.IsVisible = false;

                    var enhanced = new MenuItem { Header = "Import DOCX…" };
                    enhanced.Click += async (_, _) => await ImportDocxAsync();
                    items.Insert(existingIndex + 1, enhanced);
                    file.ItemsSource = items.ToArray();
                    _docxImportInstalled = true;
                }
            }
        }

        if (_projectMenuInstalled && _treeMenuInstalled && _docxImportInstalled)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private MenuItem BuildConvertMenu()
    {
        var parent = new MenuItem { Header = "Convert To" };
        parent.ItemsSource = ConvertibleKinds
            .Select(kind => (object)CreateKindItem(kind))
            .ToArray();
        return parent;
    }

    private MenuItem CreateKindItem(NodeKind kind)
    {
        var item = new MenuItem { Header = kind.ToString() };
        item.Click += async (_, _) => await ConvertSelectedAsync(kind);
        return item;
    }

    private async Task ConvertSelectedAsync(NodeKind targetKind)
    {
        var project = CurrentProject();
        var row = _viewModel.SelectedRow;
        var node = row?.Node;
        if (project is null || row is null || node is null || !node.IsDocument || node.Kind == targetKind) return;

        await _viewModel.SaveNowAsync();
        var result = await _mutations.ExecuteAsync(project, ProjectMutationRequest.Convert(node, targetKind));
        if (!result.Applied) return;

        var index = _viewModel.BinderRows.IndexOf(row);
        var refreshed = new BinderRowViewModel(node, row.Depth);
        if (index >= 0)
            _viewModel.BinderRows[index] = refreshed;
        await _viewModel.SelectAsync(refreshed);
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

        var mappings = await ShowMappingDialogAsync();
        if (mappings is null) return;

        await _viewModel.SaveNowAsync();

        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var status = new TextBlock
        {
            Text = "Preparing DOCX import…",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var detail = new TextBlock
        {
            Text = Path.GetFileName(path),
            Opacity = 0.68,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(18)
        };
        content.Children.Add(status);
        Grid.SetRow(detail, 1);
        detail.Margin = new Thickness(0, 6, 0, 12);
        content.Children.Add(detail);
        Grid.SetRow(progress, 2);
        content.Children.Add(progress);
        Grid.SetRow(cancel, 3);
        cancel.Margin = new Thickness(0, 16, 0, 0);
        content.Children.Add(cancel);

        var dialog = new Window
        {
            Title = "Importing DOCX",
            Width = 520,
            Height = 210,
            MinWidth = 440,
            MinHeight = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };

        var finished = false;
        void RequestCancel()
        {
            if (finished || cancellation.IsCancellationRequested) return;
            cancel.IsEnabled = false;
            cancel.Content = "Cancelling…";
            status.Text = "Cancelling import…";
            progress.IsIndeterminate = true;
            cancellation.Cancel();
        }

        cancel.Click += (_, _) => RequestCancel();
        dialog.Closing += (_, e) =>
        {
            if (finished) return;
            e.Cancel = true;
            RequestCancel();
        };

        var previousSelectionId = _viewModel.SelectedRow?.Node.PersistentId;
        var importParent = _viewModel.SelectedRow?.Node;
        var createdNodes = new List<ProjectNode>();
        string? completedSelectionId = null;
        Exception? failure = null;
        var wasCancelled = false;

        PauseLivePreview();
        var dialogTask = dialog.ShowDialog(_window);

        try
        {
            ReportProgress(progress, status, 5, "Preparing DOCX import…");
            cancellation.Token.ThrowIfCancellationRequested();

            progress.IsIndeterminate = true;
            status.Text = "Reading and converting DOCX content…";
            var markup = await _docx.ImportAsync(project, path, cancellation.Token);
            progress.IsIndeterminate = false;
            ReportProgress(progress, status, 35, "DOCX content converted. Building project structure…");

            var fallbackTitle = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fallbackTitle)) fallbackTitle = "Imported DOCX";
            var segments = SplitMarkup(markup, mappings, fallbackTitle);
            cancellation.Token.ThrowIfCancellationRequested();
            ReportProgress(progress, status, 42, $"Importing {segments.Count} project item(s)…");

            for (var index = 0; index < segments.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var segment = segments[index];
                var start = 42 + (index * 53.0 / Math.Max(1, segments.Count));
                ReportProgress(progress, status, start,
                    $"Importing {index + 1} of {segments.Count}: {segment.Title}");

                var node = await _importRepository.AddNodeAsync(
                    project,
                    importParent,
                    segment.Kind,
                    segment.Title,
                    cancellation.Token);
                createdNodes.Add(node);

                await _importRepository.SaveDocumentAsync(
                    project,
                    node,
                    segment.Content,
                    cancellation.Token);
                completedSelectionId = node.PersistentId;

                var end = 42 + ((index + 1) * 53.0 / Math.Max(1, segments.Count));
                ReportProgress(progress, status, end,
                    $"Imported {index + 1} of {segments.Count}: {segment.Title}");
            }

            ReportProgress(progress, status, 100, "DOCX import complete.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            wasCancelled = true;
            progress.IsIndeterminate = true;
            status.Text = "Cancelling import and removing partial items…";
            await RollBackImportedNodesAsync(project, createdNodes);
        }
        catch (Exception ex)
        {
            failure = ex;
            progress.IsIndeterminate = true;
            status.Text = "Import failed. Removing partial items…";
            try
            {
                await RollBackImportedNodesAsync(project, createdNodes);
            }
            catch (Exception cleanupEx)
            {
                failure = new AggregateException(ex, cleanupEx);
            }
        }
        finally
        {
            finished = true;
            dialog.Close();
            try { await dialogTask; } catch { }

            RefreshBinderMethod?.Invoke(_viewModel, null);
            var targetId = failure is null && !wasCancelled
                ? completedSelectionId ?? previousSelectionId
                : previousSelectionId;
            var targetRow = string.IsNullOrWhiteSpace(targetId)
                ? null
                : _viewModel.BinderRows.FirstOrDefault(row =>
                    string.Equals(row.Node.PersistentId, targetId, StringComparison.Ordinal));

            try
            {
                if (targetRow is not null)
                    await _viewModel.SelectAsync(targetRow);
            }
            finally
            {
                ResumeLivePreview();
            }
        }

        if (failure is not null)
            await ShowMessageAsync("DOCX import failed", failure.GetBaseException().Message);
        else if (wasCancelled)
            await ShowMessageAsync("DOCX import cancelled", "The import was cancelled and partial imported items were removed.");
    }

    private static void ReportProgress(ProgressBar progress, TextBlock status, double value, string message)
    {
        progress.IsIndeterminate = false;
        progress.Value = Math.Clamp(value, progress.Minimum, progress.Maximum);
        status.Text = message;
    }

    private async Task RollBackImportedNodesAsync(BookProject project, IReadOnlyList<ProjectNode> createdNodes)
    {
        for (var index = createdNodes.Count - 1; index >= 0; index--)
        {
            var node = createdNodes[index];
            try
            {
                await _importRepository.DeleteNodeAsync(project, node, CancellationToken.None);
            }
            catch
            {
                // Continue cleanup so one damaged partial item does not leave all remaining imports behind.
            }
        }
    }

    private void PauseLivePreview()
    {
        if (PreviewCtsField?.GetValue(_viewModel) is not CancellationTokenSource previewCts) return;
        try { previewCts.Cancel(); } catch { }
        try { previewCts.Dispose(); } catch { }
        PreviewCtsField.SetValue(_viewModel, null);
    }

    private void ResumeLivePreview()
    {
        try
        {
            SchedulePreviewMethod?.Invoke(_viewModel, [TimeSpan.Zero]);
        }
        catch
        {
            // Preview recovery should never hide an otherwise successful import result.
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var close = new Button
        {
            Content = "Close",
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(16)
        };
        panel.Children.Add(text);
        Grid.SetRow(close, 1);
        close.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(close);
        var dialog = new Window
        {
            Title = title,
            Width = 540,
            Height = 230,
            MinWidth = 440,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(_window);
    }

    private async Task<HeadingMapping[]?> ShowMappingDialogAsync()
    {
        var choices = new[]
        {
            new MappingChoice("Keep inside document", null),
            new MappingChoice("Heading", NodeKind.Heading),
            new MappingChoice("Chapter", NodeKind.Chapter),
            new MappingChoice("Section", NodeKind.Section),
            new MappingChoice("Scene", NodeKind.Scene),
            new MappingChoice("Research", NodeKind.Research),
            new MappingChoice("Note", NodeKind.Note)
        };

        var defaults = new NodeKind?[]
        {
            NodeKind.Chapter,
            NodeKind.Section,
            NodeKind.Scene,
            NodeKind.Research,
            NodeKind.Note,
            null
        };

        var selectors = new ComboBox[6];
        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(new TextBlock
        {
            Text = "Choose what each Word heading level becomes. Mapped levels start new binder items; levels set to Keep stay inside the current document.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        for (var level = 1; level <= 6; level++)
        {
            var selector = new ComboBox
            {
                ItemsSource = choices,
                MinWidth = 220,
                SelectedItem = choices.First(choice => choice.Kind == defaults[level - 1])
            };
            selectors[level - 1] = selector;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
            row.Children.Add(new TextBlock
            {
                Text = $"Heading {level}",
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(selector, 1);
            row.Children.Add(selector);
            rows.Children.Add(row);
        }

        var import = new Button { Content = "Import", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { import, cancel }
        };
        rows.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "DOCX Import Mapping",
            Width = 520,
            Height = 470,
            MinWidth = 460,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = rows,
                Margin = new Thickness(18)
            }
        };

        import.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var accepted = await dialog.ShowDialog<bool>(_window);
        if (!accepted) return null;

        return selectors
            .Select((selector, index) => new HeadingMapping(index + 1, (selector.SelectedItem as MappingChoice)?.Kind))
            .ToArray();
    }

    private static IReadOnlyList<ImportSegment> SplitMarkup(
        string markup,
        IReadOnlyList<HeadingMapping> mappings,
        string fallbackTitle)
    {
        var byLevel = mappings.ToDictionary(static mapping => mapping.Level);
        var result = new List<ImportSegment>();
        var body = new StringBuilder();
        NodeKind? currentKind = null;
        string? currentTitle = null;

        void Flush()
        {
            var text = body.ToString().Trim();
            if (currentKind is null)
            {
                if (text.Length == 0) return;
                result.Add(new ImportSegment(NodeKind.Chapter, fallbackTitle, BuildDocument(fallbackTitle, text)));
            }
            else
            {
                var title = string.IsNullOrWhiteSpace(currentTitle) ? fallbackTitle : currentTitle!;
                result.Add(new ImportSegment(currentKind.Value, title, BuildDocument(title, text)));
            }
            body.Clear();
        }

        var lines = (markup ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        foreach (var line in lines)
        {
            if (TryReadHeading(line, out var level, out var title) &&
                byLevel.TryGetValue(level, out var mapping) &&
                mapping.Kind is NodeKind mappedKind)
            {
                Flush();
                currentKind = mappedKind;
                currentTitle = title;
                continue;
            }

            body.AppendLine(line);
        }
        Flush();

        if (result.Count == 0)
            result.Add(new ImportSegment(NodeKind.Chapter, fallbackTitle, BuildDocument(fallbackTitle, markup)));
        return result;
    }

    private static bool TryReadHeading(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        var span = line.AsSpan();
        while (level < span.Length && level < 6 && span[level] == '#') level++;
        if (level == 0 || level >= span.Length || span[level] != ' ') return false;
        title = span[(level + 1)..].ToString().Trim();
        return title.Length > 0;
    }

    private static string BuildDocument(string title, string body)
    {
        var normalizedBody = (body ?? string.Empty).Trim();
        return normalizedBody.Length == 0
            ? $"# {title.Trim()}\n"
            : $"# {title.Trim()}\n\n{normalizedBody}\n";
    }

    private BookProject? CurrentProject()
        => ProjectField?.GetValue(_viewModel) as BookProject;

    private static List<object> Items(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object>().ToList() : [];

    private static IEnumerable<MenuItem> TopLevelMenuItems(object? source)
        => Items(source).OfType<MenuItem>();

    private static bool HeaderEquals(MenuItem item, string expected)
        => string.Equals(Normalize(item.Header?.ToString()), expected, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('…', '.');

    private sealed record MappingChoice(string Label, NodeKind? Kind)
    {
        public override string ToString() => Label;
    }

    private sealed record HeadingMapping(int Level, NodeKind? Kind);
    private sealed record ImportSegment(NodeKind Kind, string Title, string Content);
}
