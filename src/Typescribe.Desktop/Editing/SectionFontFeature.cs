using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Adds a focused section-font inspector. Users can choose a system font, import font files into
/// the project's fonts directory, and apply different fonts to heading-defined manuscript sections.
/// Section formatting is stored with the existing Markdown-safe paragraph metadata, so exporters
/// and plain Markdown workflows keep using the same source of truth.
/// </summary>
internal sealed class SectionFontFeature
{
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".woff2"];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IDocumentParser _parser;
    private readonly ComboBox _systemFont = new();
    private readonly ComboBox _projectFont = new();
    private readonly TextBox _family = new() { Watermark = "Optional family name override" };
    private readonly TextBox _size = new() { Watermark = "Keep current size" };
    private readonly TextBlock _section = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .68, FontSize = 10.5 };

    private ManuscriptEditor? _editor;
    private TabControl? _inspectorTabs;
    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private SectionFontFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _parser = parser;
        ConfigureInputs();
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(parser);

        var feature = new SectionFontFeature(window, viewModel, repository, parser);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.QueueInstall();
    }

    private void ConfigureInputs()
    {
        _systemFont.ItemsSource = FontManager.Current.SystemFonts
            .OrderBy(static family => family.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _systemFont.SelectedItem = FontManager.Current.DefaultFontFamily;
        _systemFont.SelectionChanged += (_, _) =>
        {
            if (_systemFont.SelectedItem is not null) _projectFont.SelectedItem = null;
        };
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueInstall();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) QueueInstall();
    }

    private void QueueInstall()
    {
        if (_disposed || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (!_disposed) TryInstall();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        _editor ??= controls.OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.Parent is Grid grid && grid.Classes.Contains("long-form-editor-host"));

        foreach (var tabs in controls.OfType<TabControl>())
        {
            var items = GetTabItems(tabs);
            if (!items.Any(static item => string.Equals(item.Header?.ToString(), "Inspector", StringComparison.Ordinal)))
                continue;
            _inspectorTabs = tabs;
            break;
        }

        if (_editor is null || _inspectorTabs is null) return;

        var itemsNow = GetTabItems(_inspectorTabs);
        if (!itemsNow.Any(static item => string.Equals(item.Header?.ToString(), "Section Fonts", StringComparison.Ordinal)))
        {
            itemsNow.Add(new TabItem { Header = "Section Fonts", Content = BuildInspector() });
            _inspectorTabs.ItemsSource = itemsNow.ToArray();
        }

        _editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        _editor.TextChanged += EditorTextChanged;
        _installed = true;
        RefreshProjectFonts();
        RefreshSectionLabel();
    }

    private Control BuildInspector()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = "SECTION FONT",
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            Opacity = .62
        });
        panel.Children.Add(_section);
        panel.Children.Add(new TextBlock
        {
            Text = "A section runs from the current heading through the next heading at the same or a higher level.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Opacity = .58
        });

        panel.Children.Add(Labelled("System font", _systemFont));
        panel.Children.Add(Labelled("Project/custom font", _projectFont));
        panel.Children.Add(Labelled("Family name override", _family));
        panel.Children.Add(Labelled("Size (pt, optional)", _size));

        var addFont = new Button { Content = "Add font to project…" };
        addFont.Click += async (_, _) => await AddFontAsync();
        var useSystem = new Button { Content = "Use system font" };
        useSystem.Click += (_, _) =>
        {
            _projectFont.SelectedItem = null;
            _status.Text = "System font selected for the next section-font operation.";
        };
        panel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { addFont, useSystem }
        });

        var load = new Button { Content = "Read current" };
        var apply = new Button { Content = "Apply to section" };
        var clear = new Button { Content = "Clear section font" };
        load.Click += (_, _) => LoadCurrent();
        apply.Click += (_, _) => ApplyCurrentSection();
        clear.Click += (_, _) => ClearCurrentSection();
        panel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { load, apply, clear }
        });
        panel.Children.Add(_status);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private static Control Labelled(string label, Control control)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 3 };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Opacity = .7 });
        Grid.SetRow(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private async Task AddFontAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null)
        {
            _status.Text = "Open a project before adding a custom font.";
            return;
        }

        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add font to project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Font files") { Patterns = ["*.ttf", "*.otf", "*.ttc", "*.woff2"] }
            ]
        });
        var source = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(source)) return;

        try
        {
            var fontsDirectory = Path.Combine(project.RootPath, "fonts");
            Directory.CreateDirectory(fontsDirectory);
            var destination = UniqueDestination(fontsDirectory, Path.GetFileName(source));
            if (!SamePath(source, destination)) File.Copy(source, destination, overwrite: false);

            RefreshProjectFonts();
            var choice = (_projectFont.ItemsSource as IEnumerable)?.Cast<object>()
                .OfType<ProjectFontChoice>()
                .FirstOrDefault(item => SamePath(item.FullPath, destination));
            if (choice is not null) _projectFont.SelectedItem = choice;
            if (string.IsNullOrWhiteSpace(_family.Text))
                _family.Text = Path.GetFileNameWithoutExtension(destination);

            _status.Text = $"Added {Path.GetFileName(destination)} to the project fonts folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = $"Could not add font: {ex.Message}";
        }
    }

    private void RefreshProjectFonts()
    {
        var project = _repository.CurrentProject;
        if (project is null)
        {
            _projectFont.ItemsSource = Array.Empty<ProjectFontChoice>();
            return;
        }

        var directory = Path.Combine(project.RootPath, "fonts");
        if (!Directory.Exists(directory))
        {
            _projectFont.ItemsSource = Array.Empty<ProjectFontChoice>();
            return;
        }

        var fonts = Directory.EnumerateFiles(directory)
            .Where(path => FontExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static path => new ProjectFontChoice(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path)))
            .ToArray();
        _projectFont.ItemsSource = fonts;
    }

    private void ApplyCurrentSection()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        var font = ReadFont();
        if (font is null)
        {
            _status.Text = "Choose a system font or add/select a project font first.";
            return;
        }

        try
        {
            var line = CurrentLine();
            var range = SectionFontFormatter.ResolveSection(_parser, _viewModel.EditorText, line);
            var size = ParseSize(_size.Text);
            var updated = SectionFontFormatter.Apply(_parser, _viewModel.EditorText, line, font, size);
            _viewModel.UpdateEditorText(updated);
            _status.Text = $"Applied {font.Family} to “{range.Label}” ({range.TextBlockCount} text blocks).";
            RefreshSectionLabel();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not apply section font: {ex.Message}";
        }
    }

    private void ClearCurrentSection()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        try
        {
            var line = CurrentLine();
            var range = SectionFontFormatter.ResolveSection(_parser, _viewModel.EditorText, line);
            _viewModel.UpdateEditorText(SectionFontFormatter.Clear(_parser, _viewModel.EditorText, line));
            _status.Text = $"Cleared the section font override for “{range.Label}”.";
            RefreshSectionLabel();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not clear section font: {ex.Message}";
        }
    }

    private void LoadCurrent()
    {
        if (_editor is null || !_viewModel.HasDocument) return;
        try
        {
            var line = CurrentLine();
            var document = _parser.Parse(_viewModel.EditorText);
            var block = document.Blocks
                .Where(block => block.SourceLine <= line)
                .OrderBy(block => Math.Abs(block.SourceLine - line))
                .FirstOrDefault();
            var font = block?.Formatting?.Paragraph?.CharacterDefaults?.Font;
            var size = block?.Formatting?.Paragraph?.CharacterDefaults?.FontSizePoints;
            if (font is null)
            {
                _status.Text = "This block has no section/paragraph font override.";
                return;
            }

            _family.Text = font.Source == FontSourceKind.System ? string.Empty : font.Family;
            if (size is { } points) _size.Text = points.ToString("0.##", CultureInfo.InvariantCulture);

            if (font.Source == FontSourceKind.Project && !string.IsNullOrWhiteSpace(font.ProjectPath))
            {
                RefreshProjectFonts();
                var choice = (_projectFont.ItemsSource as IEnumerable)?.Cast<object>()
                    .OfType<ProjectFontChoice>()
                    .FirstOrDefault(item => SamePath(item.FullPath, font.ProjectPath!));
                if (choice is not null) _projectFont.SelectedItem = choice;
            }
            else
            {
                _projectFont.SelectedItem = null;
                var system = FontManager.Current.SystemFonts.FirstOrDefault(item =>
                    string.Equals(item.Name, font.Family, StringComparison.CurrentCultureIgnoreCase));
                if (system is not null) _systemFont.SelectedItem = system;
            }

            _status.Text = $"Loaded {font.Family} from the current block.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read current font: {ex.Message}";
        }
    }

    private FontReference? ReadFont()
    {
        if (_projectFont.SelectedItem is ProjectFontChoice projectFont)
        {
            var family = string.IsNullOrWhiteSpace(_family.Text) ? projectFont.Name : _family.Text!.Trim();
            // Store the copied file's absolute path so LuaLaTeX can load it from its isolated build
            // directory. The font itself still lives inside the project for simple backup/packaging.
            return new FontReference(family, FontSourceKind.Project, projectFont.FullPath);
        }

        if (_systemFont.SelectedItem is FontFamily systemFont)
        {
            var family = string.IsNullOrWhiteSpace(_family.Text) ? systemFont.Name : _family.Text!.Trim();
            return new FontReference(family, FontSourceKind.System);
        }

        if (!string.IsNullOrWhiteSpace(_family.Text))
            return new FontReference(_family.Text.Trim(), FontSourceKind.System);

        return null;
    }

    private void RefreshSectionLabel()
    {
        if (_editor is null || !_viewModel.HasDocument)
        {
            _section.Text = "No manuscript section selected.";
            return;
        }

        try
        {
            var range = SectionFontFormatter.ResolveSection(_parser, _viewModel.EditorText, CurrentLine());
            _section.Text = $"{range.Label}  ·  lines {range.StartLine}–{range.EndLine}  ·  {range.TextBlockCount} text blocks";
        }
        catch
        {
            _section.Text = "Section could not be resolved.";
        }
    }

    private int CurrentLine()
    {
        if (_editor is null || _editor.Document.LineCount == 0) return 1;
        var offset = Math.Clamp(_editor.CaretOffset, 0, Math.Max(0, _editor.Document.TextLength));
        if (offset == _editor.Document.TextLength && offset > 0) offset--;
        return _editor.Document.GetLineByOffset(offset).LineNumber;
    }

    private void CaretPositionChanged(object? sender, EventArgs e) => RefreshSectionLabel();

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        RefreshProjectFonts();
        RefreshSectionLabel();
    }

    private static double? ParseSize(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? Math.Clamp(value, 5, 240)
            : null;

    private static string UniqueDestination(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 10000; index++)
        {
            candidate = Path.Combine(directory, $"{stem}-{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not allocate a unique project font filename.");
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static List<TabItem> GetTabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable source
            ? source.Cast<object>().OfType<TabItem>().ToList()
            : [];

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_editor is not null)
        {
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextChanged -= EditorTextChanged;
        }
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private sealed record ProjectFontChoice(string Name, string FullPath)
    {
        public override string ToString() => Path.GetFileName(FullPath);
    }
}
