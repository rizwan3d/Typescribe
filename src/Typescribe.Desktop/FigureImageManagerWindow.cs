using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

internal static class FigureImageManagerWindow
{
    public static async Task ShowAsync(
        Window owner,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser,
        AssetManagerService assets)
    {
        var project = repository.CurrentProject;
        if (project is null) return;

        var assetList = new ListBox { MinWidth = 250 };
        var figureList = new ListBox { MinWidth = 260 };
        var source = new ComboBox { MinWidth = 260 };
        var caption = new TextBox { Watermark = "Caption" };
        var identifier = new TextBox { Watermark = "figure-id" };
        var altText = new TextBox { Watermark = "Accessible alt text" };
        var credit = new TextBox { Watermark = "Credit / source note" };
        var style = new ComboBox { MinWidth = 200 };
        var width = new TextBox { Watermark = "Width % (e.g. 90)" };
        var height = new TextBox { Watermark = "Optional height (in)" };
        var alignment = new ComboBox { ItemsSource = Enum.GetValues<FigureAlignment>(), SelectedItem = FigureAlignment.Center };
        var placement = new ComboBox { ItemsSource = Enum.GetValues<FigurePlacement>(), SelectedItem = FigurePlacement.HereOrTop };
        var fit = new ComboBox { ItemsSource = Enum.GetValues<FigureFitMode>(), SelectedItem = FigureFitMode.Contain };
        var keepCaption = new CheckBox { Content = "Keep figure with caption", IsChecked = true };
        var allowBreak = new CheckBox { Content = "Allow page break around figure", IsChecked = true };
        var rotation = new TextBox { Text = "0", Watermark = "Rotation °" };
        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            MinHeight = 95,
            FontFamily = new Avalonia.Media.FontFamily("monospace"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var import = new Button { Content = "Import Image…" };
        var assetLibrary = new Button { Content = "Asset Library…", Margin = new Thickness(6, 0, 0, 0) };
        var insert = new Button { Content = "Insert Figure", Margin = new Thickness(6, 0, 0, 0) };
        var save = new Button { Content = "Save Figure", Margin = new Thickness(6, 0, 0, 0) };
        var close = new Button { Content = "Close", Margin = new Thickness(12, 0, 0, 0) };

        var details = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 6
        };
        AddField(details, 0, "Image source", source);
        AddField(details, 1, "Caption", caption);
        AddField(details, 2, "Identifier", identifier);
        AddField(details, 3, "Alt text", altText);
        AddField(details, 4, "Credit", credit);
        AddField(details, 5, "Figure style", style);
        AddField(details, 6, "Width (%)", width);
        AddField(details, 7, "Height (in)", height);
        AddField(details, 8, "Alignment", alignment);
        AddField(details, 9, "Placement", placement);
        AddField(details, 10, "Fit", fit);
        AddField(details, 11, "Rotation (°)", rotation);

        var editor = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Semantic figure", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                details,
                keepCaption,
                allowBreak,
                new TextBlock { Text = "Canonical Markdown", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                preview,
                error
            }
        };

        var leftTabs = new TabControl
        {
            ItemsSource = new object[]
            {
                new TabItem { Header = "Figures in document", Content = figureList },
                new TabItem { Header = "Project images", Content = assetList }
            }
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*"), ColumnSpacing = 8 };
        body.Children.Add(leftTabs);
        var editorScroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(editorScroll, 1);
        body.Children.Add(editorScroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10),
            Children = { import, assetLibrary, insert, save, close }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(new TextBlock
        {
            Text = "Figure / Image Manager — manage project images and semantic figure layout, accessibility and reusable styles from one place.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12)
        });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Figures & Images",
            Width = 1020,
            Height = 700,
            MinWidth = 800,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        FigureBlock? selectedFigure = null;
        IReadOnlyList<ProjectAsset> currentAssets = [];

        void LoadStyleChoices()
        {
            style.ItemsSource = new[] { string.Empty }.Concat(viewModel.CurrentStyle.NamedStyles.FigureStyles.Select(static item => item.Id)).ToArray();
        }

        async Task RefreshAssetsAsync(string? selectPath = null)
        {
            currentAssets = await assets.ListAsync(project);
            var rows = currentAssets.Select(static asset => new AssetChoice(asset)).ToArray();
            assetList.ItemsSource = rows;
            source.ItemsSource = currentAssets.Where(static asset => !asset.Missing).Select(static asset => asset.RelativePath).Prepend(string.Empty).ToArray();
            if (selectPath is not null)
            {
                assetList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.Asset.RelativePath, selectPath, StringComparison.OrdinalIgnoreCase));
                source.SelectedItem = selectPath;
            }
        }

        void RefreshFigures(int? selectLine = null)
        {
            var figures = parser.Parse(viewModel.EditorText).Blocks.OfType<FigureBlock>().Select(static figure => new FigureChoice(figure)).ToArray();
            figureList.ItemsSource = figures;
            figureList.SelectedItem = selectLine is int line
                ? figures.FirstOrDefault(choice => choice.Figure.SourceLine == line) ?? figures.FirstOrDefault()
                : figures.FirstOrDefault();
            if (figures.Length == 0) LoadFigure(null);
        }

        void LoadFigure(FigureBlock? figure)
        {
            selectedFigure = figure;
            if (figure is null)
            {
                source.SelectedItem = string.Empty;
                caption.Text = string.Empty;
                identifier.Text = string.Empty;
                altText.Text = string.Empty;
                credit.Text = string.Empty;
                style.SelectedItem = viewModel.CurrentStyle.DefaultFigureStyleId;
                width.Text = string.Empty;
                height.Text = string.Empty;
                alignment.SelectedItem = FigureAlignment.Center;
                placement.SelectedItem = FigurePlacement.HereOrTop;
                fit.SelectedItem = FigureFitMode.Contain;
                keepCaption.IsChecked = true;
                allowBreak.IsChecked = true;
                rotation.Text = "0";
                RefreshCanonicalPreview();
                return;
            }

            source.SelectedItem = figure.Source;
            source.Text = figure.Source;
            caption.Text = figure.Caption;
            identifier.Text = figure.Identifier ?? string.Empty;
            altText.Text = figure.AltText ?? string.Empty;
            credit.Text = figure.Credit ?? string.Empty;
            style.SelectedItem = figure.StyleId ?? string.Empty;
            width.Text = figure.Layout?.WidthPercent?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            height.Text = figure.Layout?.HeightInches?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            alignment.SelectedItem = figure.Layout?.Alignment ?? FigureAlignment.Center;
            placement.SelectedItem = figure.Layout?.Placement ?? FigurePlacement.HereOrTop;
            fit.SelectedItem = figure.Layout?.Fit ?? FigureFitMode.Contain;
            keepCaption.IsChecked = figure.Layout?.KeepWithCaption ?? true;
            allowBreak.IsChecked = figure.Layout?.AllowPageBreak ?? true;
            rotation.Text = (figure.Layout?.RotationDegrees ?? 0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            RefreshCanonicalPreview();
        }

        FigureBlock BuildFigure(int sourceLine)
        {
            var imageSource = Clean(source.Text) ?? throw new InvalidOperationException("Choose an image source.");
            var layoutValue = new FigureLayout(
                ParseOptionalDouble(width.Text),
                ParseOptionalDouble(height.Text),
                alignment.SelectedItem is FigureAlignment alignmentValue ? alignmentValue : null,
                placement.SelectedItem is FigurePlacement placementValue ? placementValue : null,
                fit.SelectedItem is FigureFitMode fitValue ? fitValue : FigureFitMode.Contain,
                keepCaption.IsChecked != false,
                allowBreak.IsChecked != false,
                ParseOptionalDouble(rotation.Text) ?? 0);
            var managed = currentAssets.Any(asset => string.Equals(asset.RelativePath, imageSource, StringComparison.OrdinalIgnoreCase));
            return new FigureBlock(
                sourceLine,
                imageSource,
                caption.Text?.Trim() ?? string.Empty,
                Clean(identifier.Text),
                Clean(altText.Text),
                Clean(credit.Text),
                Clean(style.SelectedItem?.ToString()),
                managed ? FigureSourceKind.ProjectAsset : FigureSourceKind.ProjectRelative,
                layoutValue);
        }

        void RefreshCanonicalPreview()
        {
            try { preview.Text = FigureMarkupCodec.Serialize(BuildFigure(selectedFigure?.SourceLine ?? 1), "\n"); }
            catch { preview.Text = "Choose an image source to preview canonical figure text."; }
        }

        figureList.SelectionChanged += (_, _) => LoadFigure((figureList.SelectedItem as FigureChoice)?.Figure);
        assetList.SelectionChanged += (_, _) =>
        {
            if (assetList.SelectedItem is AssetChoice selected)
            {
                source.SelectedItem = selected.Asset.RelativePath;
                source.Text = selected.Asset.RelativePath;
                RefreshCanonicalPreview();
            }
        };
        foreach (var box in new[] { caption, identifier, altText, credit, width, height, rotation })
            box.TextChanged += (_, _) => RefreshCanonicalPreview();
        source.SelectionChanged += (_, _) => RefreshCanonicalPreview();
        style.SelectionChanged += (_, _) => RefreshCanonicalPreview();
        alignment.SelectionChanged += (_, _) => RefreshCanonicalPreview();
        placement.SelectionChanged += (_, _) => RefreshCanonicalPreview();
        fit.SelectionChanged += (_, _) => RefreshCanonicalPreview();
        keepCaption.IsCheckedChanged += (_, _) => RefreshCanonicalPreview();
        allowBreak.IsCheckedChanged += (_, _) => RefreshCanonicalPreview();

        import.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Image",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.tif", "*.tiff"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            var relative = await assets.ImportAsync(project, path);
            await RefreshAssetsAsync(relative);
        };
        assetLibrary.Click += async (_, _) =>
        {
            await AssetManagerWindow.ShowAsync(dialog, project, assets);
            await RefreshAssetsAsync();
        };
        insert.Click += (_, _) =>
        {
            try
            {
                error.Text = string.Empty;
                var figure = BuildFigure(Math.Max(1, viewModel.EditorText.Count(static c => c == '\n') + 2));
                var separator = viewModel.EditorText.EndsWith('\n') ? string.Empty : DetectNewline(viewModel.EditorText);
                var text = viewModel.EditorText + separator + FigureMarkupCodec.Serialize(figure, DetectNewline(viewModel.EditorText)) + DetectNewline(viewModel.EditorText);
                viewModel.UpdateEditorText(text);
                RefreshFigures();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            try
            {
                if (selectedFigure is null) return;
                error.Text = string.Empty;
                var updated = BuildFigure(selectedFigure.SourceLine);
                var replacement = FigureMarkupCodec.Serialize(updated, DetectNewline(viewModel.EditorText));
                viewModel.UpdateEditorText(ReplaceFigure(viewModel.EditorText, selectedFigure, replacement));
                RefreshFigures(updated.SourceLine);
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        close.Click += (_, _) => dialog.Close();

        LoadStyleChoices();
        await RefreshAssetsAsync();
        RefreshFigures();
        await dialog.ShowDialog(owner);
    }

    private static void AddField(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static string ReplaceFigure(string source, FigureBlock figure, string replacement)
    {
        var newline = DetectNewline(source);
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var image = Math.Clamp(figure.SourceLine - 1, 0, Math.Max(0, lines.Count - 1));
        var start = image > 0 && lines[image - 1].TrimStart().StartsWith(FigureMarkupCodec.MetadataPrefix, StringComparison.Ordinal)
            ? image - 1
            : image;
        lines.RemoveRange(start, image - start + 1);
        lines.InsertRange(start, replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'));
        return string.Join(newline, lines);
    }

    private static string DetectNewline(string source) => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static double? ParseOptionalDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var local)) return local;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var invariant) ? invariant : null;
    }

    private sealed record AssetChoice(ProjectAsset Asset)
    {
        public override string ToString() => $"{(Asset.Missing ? "✕" : Asset.IsUnused ? "⚠" : "✓")} {Asset.FileName} — {Asset.Dimensions}";
    }
    private sealed record FigureChoice(FigureBlock Figure)
    {
        public override string ToString() => $"Line {Figure.SourceLine}: {(string.IsNullOrWhiteSpace(Figure.Caption) ? Figure.Source : Figure.Caption)}";
    }
}
