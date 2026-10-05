using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal static class NamedStyleManagerWindow
{
    private static readonly string[] Categories = ["Paragraph", "Character", "Figure", "Table", "Cell", "Page"];

    public static async Task ShowAsync(Window owner, WorkspaceViewModel viewModel)
    {
        if (!viewModel.HasProject) return;

        var category = new ComboBox { ItemsSource = Categories, SelectedIndex = 0, MinWidth = 160 };
        var styles = new ListBox { MinWidth = 260 };
        var id = new TextBox { Watermark = "style.id" };
        var name = new TextBox { Watermark = "Display name" };
        var basedOn = new ComboBox { MinWidth = 220 };
        var property1Label = new TextBlock();
        var property1 = new TextBox();
        var property2Label = new TextBlock();
        var property2 = new TextBox();
        var property3Label = new TextBlock();
        var property3 = new TextBox();
        var enumLabel = new TextBlock();
        var enumValue = new ComboBox { MinWidth = 180 };
        var flag = new CheckBox();
        var hint = new TextBlock { Opacity = .72, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var add = new Button { Content = "New Derived" };
        var duplicate = new Button { Content = "Duplicate", Margin = new Thickness(6, 0, 0, 0) };
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        var saveStyle = new Button { Content = "Apply Style", MinWidth = 100 };
        var saveProject = new Button { Content = "Save Project Styles", MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Close", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        var left = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Style type", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                category,
                styles,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { add, duplicate, delete } }
            }
        };

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 7
        };
        AddField(form, 0, "Style ID", id);
        AddField(form, 1, "Name", name);
        AddField(form, 2, "Based on", basedOn);
        AddField(form, 3, property1Label, property1);
        AddField(form, 4, property2Label, property2);
        AddField(form, 5, property3Label, property3);
        AddField(form, 6, enumLabel, enumValue);
        Grid.SetRow(flag, 7);
        Grid.SetColumn(flag, 1);
        form.Children.Add(flag);

        var right = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Reusable named style", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                hint,
                form,
                saveStyle,
                error
            }
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*"), ColumnSpacing = 8 };
        body.Children.Add(left);
        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(rightScroll, 1);
        body.Children.Add(rightScroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { saveProject, close }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            Text = "Named Styles — define reusable paragraph, character, figure, table, cell and page styles. Based-on relationships inherit unset properties.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Named Styles",
            Width = 920,
            Height = 650,
            MinWidth = 760,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        var catalog = Clone(viewModel.CurrentStyle.NamedStyles);
        INamedStyleDefinition? selected = null;

        void ConfigureFields()
        {
            var type = category.SelectedIndex;
            property1.IsVisible = property1Label.IsVisible = true;
            property2.IsVisible = property2Label.IsVisible = true;
            property3.IsVisible = property3Label.IsVisible = true;
            enumValue.IsVisible = enumLabel.IsVisible = true;
            flag.IsVisible = true;
            switch (type)
            {
                case 0:
                    property1Label.Text = "Font family";
                    property2Label.Text = "Font size (pt)";
                    property3Label.Text = "Line spacing";
                    enumLabel.Text = "Alignment";
                    enumValue.ItemsSource = OptionalEnum<TextAlignmentMode>();
                    flag.Content = "Keep with next";
                    hint.Text = "Paragraph styles control reusable body/heading-like paragraph typography. Blank fields inherit from Based on.";
                    break;
                case 1:
                    property1Label.Text = "Font family";
                    property2Label.Text = "Font size (pt)";
                    property3Label.Text = "Color";
                    enumLabel.Text = "Emphasis";
                    enumValue.ItemsSource = new[] { "(inherit)", "Normal", "Italic" };
                    flag.Content = "Bold";
                    hint.Text = "Character styles layer inline typography over paragraph styles.";
                    break;
                case 2:
                    property1Label.Text = "Max width (%)";
                    property2Label.Text = "Border width (pt)";
                    property3Label.Text = "Padding (pt)";
                    enumLabel.Text = "Alignment";
                    enumValue.ItemsSource = OptionalEnum<FigureAlignment>();
                    flag.Content = "Keep with caption";
                    hint.Text = "Figure styles provide reusable layout defaults. Individual figures may still override width, placement and other properties.";
                    break;
                case 3:
                    property1Label.Text = "Width (%)";
                    property2Label.Text = "Header cell style";
                    property3Label.Text = "Body cell style";
                    enumLabel.Text = "Layout";
                    enumValue.ItemsSource = OptionalEnum<TableLayoutMode>();
                    flag.Content = "Repeat header";
                    hint.Text = "Table styles define reusable table layout and link to named cell styles.";
                    break;
                case 4:
                    property1Label.Text = "Background color";
                    property2Label.Text = "Text color";
                    property3Label.Text = "Padding (pt)";
                    enumLabel.Text = "Horizontal align";
                    enumValue.ItemsSource = OptionalEnum<TableCellHorizontalAlignment>();
                    flag.Content = "Bold";
                    hint.Text = "Cell styles can be reused by table styles or assigned to individual cells.";
                    break;
                default:
                    property1Label.Text = "Width (in)";
                    property2Label.Text = "Height (in)";
                    property3Label.Text = "Inner margin (in)";
                    enumLabel.IsVisible = enumValue.IsVisible = false;
                    flag.Content = "Start on right";
                    hint.Text = "Page styles capture reusable trim and page-flow choices for future section-level page styling.";
                    break;
            }
        }

        void RefreshList(string? selectId = null)
        {
            var list = GetStyles(catalog, category.SelectedIndex).ToArray();
            styles.ItemsSource = list;
            styles.SelectedItem = selectId is null
                ? list.FirstOrDefault()
                : list.FirstOrDefault(item => string.Equals(item.Id, selectId, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
            RefreshBasedOn();
        }

        void RefreshBasedOn()
        {
            var ids = GetStyles(catalog, category.SelectedIndex)
                .Where(item => selected is null || !string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                .Select(static item => item.Id)
                .Prepend(string.Empty)
                .ToArray();
            basedOn.ItemsSource = ids;
        }

        void LoadStyle(INamedStyleDefinition? styleDef)
        {
            selected = styleDef;
            RefreshBasedOn();
            id.Text = styleDef?.Id ?? string.Empty;
            name.Text = styleDef?.Name ?? string.Empty;
            basedOn.SelectedItem = styleDef?.BasedOn ?? string.Empty;
            property1.Text = property2.Text = property3.Text = string.Empty;
            enumValue.SelectedIndex = 0;
            flag.IsChecked = false;
            if (styleDef is null) return;

            switch (styleDef)
            {
                case ParagraphStyleDefinition p:
                    property1.Text = p.FontFamily ?? string.Empty;
                    property2.Text = Number(p.FontSizePoints);
                    property3.Text = Number(p.LineSpacing);
                    SelectEnum(enumValue, p.Alignment);
                    flag.IsChecked = p.KeepWithNext;
                    break;
                case CharacterStyleDefinition c:
                    property1.Text = c.FontFamily ?? string.Empty;
                    property2.Text = Number(c.FontSizePoints);
                    property3.Text = c.ColorHex ?? string.Empty;
                    enumValue.SelectedItem = c.Italic switch { true => "Italic", false => "Normal", _ => "(inherit)" };
                    flag.IsChecked = c.Bold;
                    break;
                case FigureStyleDefinition f:
                    property1.Text = Number(f.MaxWidthPercent);
                    property2.Text = Number(f.BorderWidthPoints);
                    property3.Text = Number(f.PaddingPoints);
                    SelectEnum(enumValue, f.Alignment);
                    flag.IsChecked = f.KeepWithCaption;
                    break;
                case TableStyleDefinition t:
                    property1.Text = Number(t.WidthPercent);
                    property2.Text = t.HeaderCellStyleId ?? string.Empty;
                    property3.Text = t.BodyCellStyleId ?? string.Empty;
                    SelectEnum(enumValue, t.Layout);
                    flag.IsChecked = t.RepeatHeader;
                    break;
                case CellStyleDefinition c:
                    property1.Text = c.BackgroundColorHex ?? string.Empty;
                    property2.Text = c.TextColorHex ?? string.Empty;
                    property3.Text = Number(c.PaddingPoints);
                    SelectEnum(enumValue, c.HorizontalAlignment);
                    flag.IsChecked = c.Bold;
                    break;
                case PageStyleDefinition p:
                    property1.Text = Number(p.WidthInches);
                    property2.Text = Number(p.HeightInches);
                    property3.Text = Number(p.MarginInnerInches);
                    flag.IsChecked = p.StartOnRight;
                    break;
            }
        }

        INamedStyleDefinition BuildStyle()
        {
            var styleId = RequiredId(id.Text);
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? styleId : name.Text!.Trim();
            var parent = Clean(basedOn.SelectedItem?.ToString());
            return category.SelectedIndex switch
            {
                0 => new ParagraphStyleDefinition(styleId, displayName, parent, Clean(property1.Text), ParseDouble(property2.Text), ParseDouble(property3.Text), Alignment: ParseEnum<TextAlignmentMode>(enumValue.SelectedItem), KeepWithNext: flag.IsChecked),
                1 => new CharacterStyleDefinition(styleId, displayName, parent, Clean(property1.Text), ParseDouble(property2.Text), Bold: flag.IsChecked, Italic: enumValue.SelectedItem?.ToString() switch { "Italic" => true, "Normal" => false, _ => null }, ColorHex: Clean(property3.Text)),
                2 => new FigureStyleDefinition(styleId, displayName, parent, ParseDouble(property1.Text), ParseEnum<FigureAlignment>(enumValue.SelectedItem), BorderWidthPoints: ParseDouble(property2.Text), PaddingPoints: ParseDouble(property3.Text), KeepWithCaption: flag.IsChecked),
                3 => new TableStyleDefinition(styleId, displayName, parent, ParseDouble(property1.Text), ParseEnum<TableLayoutMode>(enumValue.SelectedItem), flag.IsChecked, Clean(property2.Text), Clean(property3.Text)),
                4 => new CellStyleDefinition(styleId, displayName, parent, ParseEnum<TableCellHorizontalAlignment>(enumValue.SelectedItem), BackgroundColorHex: Clean(property1.Text), TextColorHex: Clean(property2.Text), PaddingPoints: ParseDouble(property3.Text), Bold: flag.IsChecked),
                _ => new PageStyleDefinition(styleId, displayName, parent, ParseDouble(property1.Text), ParseDouble(property2.Text), MarginInnerInches: ParseDouble(property3.Text), StartOnRight: flag.IsChecked)
            };
        }

        category.SelectionChanged += (_, _) =>
        {
            ConfigureFields();
            selected = null;
            RefreshList();
        };
        styles.SelectionChanged += (_, _) => LoadStyle(styles.SelectedItem as INamedStyleDefinition);
        add.Click += (_, _) =>
        {
            var baseStyle = styles.SelectedItem as INamedStyleDefinition;
            var newId = UniqueId(catalog, category.SelectedIndex, baseStyle?.Id is { Length: > 0 } value ? value + ".derived" : CategoryPrefix(category.SelectedIndex) + ".new");
            selected = NewDerived(category.SelectedIndex, newId, baseStyle);
            catalog = Replace(catalog, category.SelectedIndex, null, selected);
            RefreshList(newId);
        };
        duplicate.Click += (_, _) =>
        {
            if (styles.SelectedItem is not INamedStyleDefinition existing) return;
            var newId = UniqueId(catalog, category.SelectedIndex, existing.Id + ".copy");
            var copy = WithIdentity(existing, newId, existing.Name + " Copy", existing.BasedOn);
            catalog = Replace(catalog, category.SelectedIndex, null, copy);
            RefreshList(newId);
        };
        delete.Click += (_, _) =>
        {
            if (styles.SelectedItem is not INamedStyleDefinition existing) return;
            if (IsBuiltInDefault(viewModel.CurrentStyle, existing.Id))
            {
                error.Text = "The active default style cannot be deleted. Choose another default first or create a derived style.";
                return;
            }
            catalog = Remove(catalog, category.SelectedIndex, existing.Id);
            RefreshList();
        };
        saveStyle.Click += (_, _) =>
        {
            try
            {
                error.Text = string.Empty;
                var built = BuildStyle();
                catalog = Replace(catalog, category.SelectedIndex, selected?.Id, built).Validate();
                selected = built;
                RefreshList(built.Id);
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        saveProject.Click += async (_, _) =>
        {
            try
            {
                error.Text = string.Empty;
                catalog.Validate();
                await viewModel.UpdateStyleAsync((viewModel.CurrentStyle with { NamedStyles = catalog }).Validate());
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        close.Click += (_, _) => dialog.Close();

        ConfigureFields();
        RefreshList();
        await dialog.ShowDialog(owner);
    }

    private static void AddField(Grid grid, int row, string label, Control control)
        => AddField(grid, row, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, control);

    private static void AddField(Grid grid, int row, TextBlock label, Control control)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(label, row);
        grid.Children.Add(label);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static IEnumerable<INamedStyleDefinition> GetStyles(NamedStyleCatalog catalog, int category)
        => category switch
        {
            0 => catalog.ParagraphStyles,
            1 => catalog.CharacterStyles,
            2 => catalog.FigureStyles,
            3 => catalog.TableStyles,
            4 => catalog.CellStyles,
            _ => catalog.PageStyles
        };

    private static NamedStyleCatalog Clone(NamedStyleCatalog source)
        => new(source.ParagraphStyles.ToArray(), source.CharacterStyles.ToArray(), source.FigureStyles.ToArray(), source.TableStyles.ToArray(), source.CellStyles.ToArray(), source.PageStyles.ToArray());

    private static NamedStyleCatalog Replace(NamedStyleCatalog catalog, int category, string? oldId, INamedStyleDefinition replacement)
    {
        bool Keep(INamedStyleDefinition candidate) => oldId is null || !string.Equals(candidate.Id, oldId, StringComparison.OrdinalIgnoreCase);
        return category switch
        {
            0 => catalog with { ParagraphStyles = catalog.ParagraphStyles.Where(item => Keep(item)).Append((ParagraphStyleDefinition)replacement).ToArray() },
            1 => catalog with { CharacterStyles = catalog.CharacterStyles.Where(item => Keep(item)).Append((CharacterStyleDefinition)replacement).ToArray() },
            2 => catalog with { FigureStyles = catalog.FigureStyles.Where(item => Keep(item)).Append((FigureStyleDefinition)replacement).ToArray() },
            3 => catalog with { TableStyles = catalog.TableStyles.Where(item => Keep(item)).Append((TableStyleDefinition)replacement).ToArray() },
            4 => catalog with { CellStyles = catalog.CellStyles.Where(item => Keep(item)).Append((CellStyleDefinition)replacement).ToArray() },
            _ => catalog with { PageStyles = catalog.PageStyles.Where(item => Keep(item)).Append((PageStyleDefinition)replacement).ToArray() }
        };
    }

    private static NamedStyleCatalog Remove(NamedStyleCatalog catalog, int category, string id)
        => category switch
        {
            0 => catalog with { ParagraphStyles = catalog.ParagraphStyles.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray() },
            1 => catalog with { CharacterStyles = catalog.CharacterStyles.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray() },
            2 => catalog with { FigureStyles = catalog.FigureStyles.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray() },
            3 => catalog with { TableStyles = catalog.TableStyles.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray() },
            4 => catalog with { CellStyles = catalog.CellStyles.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray() },
            _ => catalog with { PageStyles = catalog.PageStyles.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray() }
        };

    private static INamedStyleDefinition NewDerived(int category, string id, INamedStyleDefinition? parent)
    {
        var basedOn = parent?.Id;
        var name = parent is null ? "New Style" : parent.Name + " Derived";
        return category switch
        {
            0 => new ParagraphStyleDefinition(id, name, basedOn),
            1 => new CharacterStyleDefinition(id, name, basedOn),
            2 => new FigureStyleDefinition(id, name, basedOn),
            3 => new TableStyleDefinition(id, name, basedOn),
            4 => new CellStyleDefinition(id, name, basedOn),
            _ => new PageStyleDefinition(id, name, basedOn)
        };
    }

    private static INamedStyleDefinition WithIdentity(INamedStyleDefinition style, string id, string name, string? basedOn)
        => style switch
        {
            ParagraphStyleDefinition value => value with { Id = id, Name = name, BasedOn = basedOn },
            CharacterStyleDefinition value => value with { Id = id, Name = name, BasedOn = basedOn },
            FigureStyleDefinition value => value with { Id = id, Name = name, BasedOn = basedOn },
            TableStyleDefinition value => value with { Id = id, Name = name, BasedOn = basedOn },
            CellStyleDefinition value => value with { Id = id, Name = name, BasedOn = basedOn },
            PageStyleDefinition value => value with { Id = id, Name = name, BasedOn = basedOn },
            _ => throw new InvalidOperationException("Unsupported style type.")
        };

    private static bool IsBuiltInDefault(BookStyle style, string id)
        => new[] { style.DefaultParagraphStyleId, style.DefaultCharacterStyleId, style.DefaultFigureStyleId, style.DefaultTableStyleId, style.DefaultCellStyleId, style.DefaultPageStyleId }
            .Any(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase));

    private static string UniqueId(NamedStyleCatalog catalog, int category, string seed)
    {
        var existing = GetStyles(catalog, category).Select(static item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = seed;
        var number = 2;
        while (existing.Contains(candidate)) candidate = seed + number++;
        return candidate;
    }

    private static string CategoryPrefix(int category) => category switch { 0 => "paragraph", 1 => "character", 2 => "figure", 3 => "table", 4 => "cell", _ => "page" };
    private static string RequiredId(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new InvalidOperationException("Style ID is required.");
        if (text.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Style ID cannot contain spaces.");
        return text;
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Number(double? value) => value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var local)) return local;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var invariant)) return invariant;
        throw new InvalidOperationException($"'{value}' is not a valid number.");
    }
    private static object[] OptionalEnum<T>() where T : struct, Enum
        => new object[] { "(inherit)" }.Concat(Enum.GetValues<T>().Cast<object>()).ToArray();
    private static T? ParseEnum<T>(object? value) where T : struct, Enum
        => value is T typed ? typed : null;
    private static void SelectEnum<T>(ComboBox combo, T? value) where T : struct, Enum
        => combo.SelectedItem = value.HasValue ? value.Value : "(inherit)";
}
