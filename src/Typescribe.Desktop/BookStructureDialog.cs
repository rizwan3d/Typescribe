using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed class BookStructureDialog : Window
{
    private static readonly string[] RecommendedFrontOrder =
    [
        "front-cover", "spine", "front-blanks", "title-page", "copyright",
        "dedication", "toc", "foreword", "preface", "introduction"
    ];

    private static readonly string[] RecommendedBackOrder =
    [
        "conclusion", "epilogue", "acknowledgments", "appendix", "glossary",
        "references", "index", "about-author", "end-blanks", "back-cover"
    ];

    private readonly WorkspaceViewModel _viewModel;

    // Identity / publishing metadata.
    private readonly TextBox _bookName = SingleLine("Workspace/project name");
    private readonly TextBox _publishedTitle = SingleLine("Printed title; leave blank to use Book Name");
    private readonly TextBox _subtitle = SingleLine("Optional subtitle");
    private readonly TextBox _displayAuthor = SingleLine("Author name as readers should see it");
    private readonly TextBox _seriesTitle = SingleLine("Optional series name");
    private readonly TextBox _volumeLabel = SingleLine("Optional volume, e.g. Book 2");
    private readonly TextBox _edition = SingleLine("Optional edition, e.g. Second Edition");
    private readonly TextBox _publisher = SingleLine("Publisher / imprint");
    private readonly TextBox _isbn = SingleLine("ISBN or other edition identifier");

    // Front matter.
    private readonly CheckBox _frontCover = new() { Content = "Front Cover" };
    private readonly TextBox _frontCoverText = MultiLine("Optional front-cover text; leave blank to use Published Title.");
    private readonly CheckBox _spine = new() { Content = "Spine" };
    private readonly TextBox _spineText = MultiLine("Optional spine text; leave blank to use Published Title.");
    private readonly TextBox _frontBlankPages = NumberBox();
    private readonly CheckBox _titlePage = new() { Content = "Title Page" };
    private readonly CheckBox _copyrightPage = new() { Content = "Copyright Page" };
    private readonly TextBox _copyrightText = MultiLine("Copyright notice, edition, ISBN, publisher, rights statement…");
    private readonly CheckBox _dedication = new() { Content = "Dedication" };
    private readonly TextBox _dedicationText = MultiLine("Dedication text");
    private readonly CheckBox _toc = new() { Content = "Table of Contents" };
    private readonly CheckBox _preface = new() { Content = "Preface" };
    private readonly TextBox _prefaceText = MultiLine("Preface text");
    private readonly CheckBox _foreword = new() { Content = "Foreword" };
    private readonly TextBox _forewordText = MultiLine("Foreword text");
    private readonly CheckBox _introduction = new() { Content = "Introduction" };
    private readonly TextBox _introductionText = MultiLine("Introduction text");

    // Running elements and pagination.
    private readonly CheckBox _pageNumbers = new() { Content = "Page Numbers" };
    private readonly CheckBox _headersFooters = new() { Content = "Headers / Footers" };
    private readonly CheckBox _romanFrontMatter = new() { Content = "Use Roman numerals for front matter (i, ii, iii…)" };
    private readonly CheckBox _resetBodyPageNumbers = new() { Content = "Restart body page numbering at 1" };
    private readonly CheckBox _generatedMatterInToc = new() { Content = "List generated sections in the Table of Contents" };
    private readonly CheckBox _generatedMatterOpenRight = new() { Content = "Start generated sections on a right-hand page" };

    // Back matter.
    private readonly CheckBox _conclusion = new() { Content = "Conclusion" };
    private readonly TextBox _conclusionText = MultiLine("Conclusion text");
    private readonly CheckBox _epilogue = new() { Content = "Epilogue" };
    private readonly TextBox _epilogueText = MultiLine("Epilogue text");
    private readonly CheckBox _acknowledgments = new() { Content = "Acknowledgments" };
    private readonly TextBox _acknowledgmentsText = MultiLine("Acknowledgments text");
    private readonly CheckBox _appendix = new() { Content = "Appendix" };
    private readonly TextBox _appendixText = MultiLine("Appendix text");
    private readonly CheckBox _glossary = new() { Content = "Glossary" };
    private readonly TextBox _glossaryText = MultiLine("Glossary text");
    private readonly CheckBox _references = new() { Content = "References / Bibliography" };
    private readonly CheckBox _index = new() { Content = "Index" };
    private readonly TextBox _indexText = MultiLine("Manual index text");
    private readonly CheckBox _aboutAuthor = new() { Content = "About the Author" };
    private readonly TextBox _aboutAuthorText = MultiLine("About-the-author text");
    private readonly TextBox _endBlankPages = NumberBox();
    private readonly CheckBox _backCover = new() { Content = "Back Cover" };
    private readonly TextBox _backCoverText = MultiLine("Optional back-cover text; leave blank to use Published Title.");

    // Flexible order and labels.
    private readonly ListBox _frontOrderList = new() { MinHeight = 250 };
    private readonly ListBox _backOrderList = new() { MinHeight = 250 };
    private readonly List<string> _frontOrder = [];
    private readonly List<string> _backOrder = [];
    private readonly TextBox _prefaceHeading = SingleLine("Preface");
    private readonly TextBox _forewordHeading = SingleLine("Foreword");
    private readonly TextBox _introductionHeading = SingleLine("Introduction");
    private readonly TextBox _conclusionHeading = SingleLine("Conclusion");
    private readonly TextBox _epilogueHeading = SingleLine("Epilogue");
    private readonly TextBox _acknowledgmentsHeading = SingleLine("Acknowledgments");
    private readonly TextBox _appendixHeading = SingleLine("Appendix");
    private readonly TextBox _glossaryHeading = SingleLine("Glossary");
    private readonly TextBox _referencesHeading = SingleLine("Bibliography");
    private readonly TextBox _indexHeading = SingleLine("Index");
    private readonly TextBox _aboutAuthorHeading = SingleLine("About the Author");

    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    public BookStructureDialog(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = "Book Structure";
        Width = 1040;
        Height = 800;
        MinWidth = 780;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Load(_viewModel.CurrentStyle);
        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var tabs = new TabControl
        {
            ItemsSource = new object[]
            {
                new TabItem { Header = "Identity", Content = BuildIdentity() },
                new TabItem { Header = "Front Matter", Content = BuildFrontMatter() },
                new TabItem { Header = "Page Elements", Content = BuildPageElements() },
                new TabItem { Header = "Back Matter", Content = BuildBackMatter() },
                new TabItem { Header = "Order & Labels", Content = BuildAdvancedStructure() }
            }
        };
        tabs.SelectedIndex = 0;

        var save = new Button { Content = "Save & Close", MinWidth = 110 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, cancel }
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(18)
        };
        root.Children.Add(new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new TextBlock { Text = "Book Structure", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Separate the workspace name from the reader-facing title, choose optional matter, reorder it freely, customize labels, and control publishing pagination. Cover and spine choices remain proof pages; printer-specific wrap-cover geometry is a production step.",
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        Grid.SetRow(_error, 2);
        _error.Margin = new Thickness(4, 8, 4, 4);
        root.Children.Add(_error);
        Grid.SetRow(buttons, 3);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(buttons);
        return root;
    }

    private Control BuildIdentity()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Best practice: use Book Name for the project/workspace identity and Published Title for the exact title readers see. They may be different.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(Field("Book Name", _bookName, "Renames the project in typescribe.yaml and the workspace. It does not rename the project folder."));
        panel.Children.Add(Field("Published Title", _publishedTitle, "Leave blank to use Book Name. Used by the title page, cover fallback and PDF metadata."));
        panel.Children.Add(Field("Subtitle", _subtitle));
        panel.Children.Add(Field("Display Author", _displayAuthor));
        panel.Children.Add(Field("Series", _seriesTitle));
        panel.Children.Add(Field("Volume", _volumeLabel));
        panel.Children.Add(Field("Edition", _edition));
        panel.Children.Add(Field("Publisher / Imprint", _publisher));
        panel.Children.Add(Field("ISBN / Edition ID", _isbn));
        return Scroll(panel);
    }

    private Control BuildFrontMatter()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        panel.Children.Add(Option(_frontCover, _frontCoverText));
        panel.Children.Add(Option(_spine, _spineText));
        panel.Children.Add(NumberRow("Blank Pages before title/front matter", _frontBlankPages));
        panel.Children.Add(SimpleOption(_titlePage));
        panel.Children.Add(Option(_copyrightPage, _copyrightText));
        panel.Children.Add(Option(_dedication, _dedicationText));
        panel.Children.Add(SimpleOption(_toc));
        panel.Children.Add(Option(_foreword, _forewordText));
        panel.Children.Add(Option(_preface, _prefaceText));
        panel.Children.Add(Option(_introduction, _introductionText));
        return Scroll(panel);
    }

    private Control BuildPageElements()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Running elements", FontWeight = FontWeight.SemiBold, FontSize = 16 });
        panel.Children.Add(SimpleOption(_pageNumbers));
        panel.Children.Add(SimpleOption(_headersFooters));
        panel.Children.Add(new TextBlock
        {
            Text = "Header/footer text, alignment and typography remain editable under Project → Book Design & LaTeX → Headers & Footer.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 2, 4, 8)
        });
        panel.Children.Add(new TextBlock { Text = "Publishing pagination", FontWeight = FontWeight.SemiBold, FontSize = 16 });
        panel.Children.Add(SimpleOption(_romanFrontMatter));
        panel.Children.Add(SimpleOption(_resetBodyPageNumbers));
        panel.Children.Add(SimpleOption(_generatedMatterInToc));
        panel.Children.Add(SimpleOption(_generatedMatterOpenRight));
        panel.Children.Add(new TextBlock
        {
            Text = "Recommended for print books: Roman-numbered front matter, body restarted at page 1, generated sections listed in the TOC, and major sections opened on right-hand pages.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 4, 4, 0)
        });
        return Scroll(panel);
    }

    private Control BuildBackMatter()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        panel.Children.Add(Option(_conclusion, _conclusionText));
        panel.Children.Add(Option(_epilogue, _epilogueText));
        panel.Children.Add(Option(_acknowledgments, _acknowledgmentsText));
        panel.Children.Add(Option(_appendix, _appendixText));
        panel.Children.Add(Option(_glossary, _glossaryText));
        panel.Children.Add(SimpleOption(_references));
        panel.Children.Add(new TextBlock
        {
            Text = "References / Bibliography uses the project's structured citation entries. Its position is configurable under Order & Labels.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, -6, 4, 0)
        });
        panel.Children.Add(Option(_index, _indexText));
        panel.Children.Add(Option(_aboutAuthor, _aboutAuthorText));
        panel.Children.Add(NumberRow("End Pages / Blank Pages", _endBlankPages));
        panel.Children.Add(Option(_backCover, _backCoverText));
        return Scroll(panel);
    }

    private Control BuildAdvancedStructure()
    {
        var root = new StackPanel { Margin = new Thickness(12), Spacing = 16 };
        root.Children.Add(new TextBlock
        {
            Text = "Enabled items can be placed in any order. Up/Down changes only position; the Front/Back Matter checkboxes still decide whether an item is included. Reset restores a conventional publishing order.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });

        var orders = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 18 };
        orders.Children.Add(OrderEditor("Front matter order", _frontOrderList, _frontOrder, RecommendedFrontOrder));
        var back = OrderEditor("Back matter order", _backOrderList, _backOrder, RecommendedBackOrder);
        Grid.SetColumn(back, 1);
        orders.Children.Add(back);
        root.Children.Add(orders);

        root.Children.Add(new TextBlock
        {
            Text = "Custom section labels",
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 8, 0, 0)
        });
        root.Children.Add(Field("Foreword", _forewordHeading));
        root.Children.Add(Field("Preface", _prefaceHeading));
        root.Children.Add(Field("Introduction", _introductionHeading));
        root.Children.Add(Field("Conclusion", _conclusionHeading));
        root.Children.Add(Field("Epilogue", _epilogueHeading));
        root.Children.Add(Field("Acknowledgments", _acknowledgmentsHeading));
        root.Children.Add(Field("Appendix", _appendixHeading));
        root.Children.Add(Field("Glossary", _glossaryHeading));
        root.Children.Add(Field("References / Bibliography", _referencesHeading));
        root.Children.Add(Field("Index", _indexHeading));
        root.Children.Add(Field("About the Author", _aboutAuthorHeading));
        return Scroll(root);
    }

    private Control OrderEditor(string title, ListBox list, List<string> order, IReadOnlyList<string> recommended)
    {
        var up = new Button { Content = "↑ Up", MinWidth = 72 };
        var down = new Button { Content = "↓ Down", MinWidth = 72, Margin = new Thickness(6, 0, 0, 0) };
        var reset = new Button { Content = "Recommended", MinWidth = 110, Margin = new Thickness(6, 0, 0, 0) };
        up.Click += (_, _) => MoveOrder(list, order, -1);
        down.Click += (_, _) => MoveOrder(list, order, 1);
        reset.Click += (_, _) =>
        {
            order.Clear();
            order.AddRange(recommended);
            RefreshOrderList(list, order, 0);
        };

        return new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { up, down, reset }
                }
            }
        };
    }

    private static Control Option(CheckBox toggle, TextBox text)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(toggle);
        text.Margin = new Thickness(24, 0, 0, 0);
        panel.Children.Add(text);
        return panel;
    }

    private static Control SimpleOption(CheckBox toggle)
    {
        toggle.Margin = new Thickness(0, 2);
        return toggle;
    }

    private static Control Field(string label, Control input, string? help = null)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(input);
        if (!string.IsNullOrWhiteSpace(help))
        {
            panel.Children.Add(new TextBlock
            {
                Text = help,
                Opacity = 0.68,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, -1, 2, 0)
            });
        }
        return panel;
    }

    private static Control NumberRow(string label, TextBox box)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("300,100,*") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    private static TextBox SingleLine(string watermark)
        => new() { Watermark = watermark, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static TextBox MultiLine(string watermark)
        => new()
        {
            Watermark = watermark,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 62
        };

    private static TextBox NumberBox()
        => new() { Text = "0", HorizontalAlignment = HorizontalAlignment.Stretch };

    private static ScrollViewer Scroll(Control content)
        => new()
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

    private void Load(BookStyle style)
    {
        _bookName.Text = _viewModel.ProjectTitle;
        _publishedTitle.Text = style.PublishedTitle;
        _subtitle.Text = style.Subtitle;
        _displayAuthor.Text = style.DisplayAuthor;
        _seriesTitle.Text = style.SeriesTitle;
        _volumeLabel.Text = style.VolumeLabel;
        _edition.Text = style.Edition;
        _publisher.Text = style.Publisher;
        _isbn.Text = style.Isbn;

        _frontCover.IsChecked = style.IncludeFrontCover;
        _frontCoverText.Text = style.FrontCoverText;
        _spine.IsChecked = style.IncludeSpine;
        _spineText.Text = style.SpineText;
        _frontBlankPages.Text = style.FrontBlankPages.ToString();
        _titlePage.IsChecked = style.IncludeTitlePage;
        _copyrightPage.IsChecked = style.IncludeCopyrightPage;
        _copyrightText.Text = style.CopyrightText;
        _dedication.IsChecked = style.IncludeDedication;
        _dedicationText.Text = style.DedicationText;
        _toc.IsChecked = style.IncludeTableOfContents;
        _preface.IsChecked = style.IncludePreface;
        _prefaceText.Text = style.PrefaceText;
        _foreword.IsChecked = style.IncludeForeword;
        _forewordText.Text = style.ForewordText;
        _introduction.IsChecked = style.IncludeIntroduction;
        _introductionText.Text = style.IntroductionText;

        _pageNumbers.IsChecked = style.ShowPageNumbers;
        _headersFooters.IsChecked = style.ShowHeadersAndFooters;
        _romanFrontMatter.IsChecked = style.UseRomanFrontMatterPageNumbers;
        _resetBodyPageNumbers.IsChecked = style.ResetBodyPageNumbers;
        _generatedMatterInToc.IsChecked = style.GeneratedMatterInTableOfContents;
        _generatedMatterOpenRight.IsChecked = style.StartGeneratedMatterOnRight;

        _conclusion.IsChecked = style.IncludeConclusion;
        _conclusionText.Text = style.ConclusionText;
        _epilogue.IsChecked = style.IncludeEpilogue;
        _epilogueText.Text = style.EpilogueText;
        _acknowledgments.IsChecked = style.IncludeAcknowledgments;
        _acknowledgmentsText.Text = style.AcknowledgmentsText;
        _appendix.IsChecked = style.IncludeAppendix;
        _appendixText.Text = style.AppendixText;
        _glossary.IsChecked = style.IncludeGlossary;
        _glossaryText.Text = style.GlossaryText;
        _references.IsChecked = style.IncludeReferencesBibliography;
        _index.IsChecked = style.IncludeIndex;
        _indexText.Text = style.IndexText;
        _aboutAuthor.IsChecked = style.IncludeAboutAuthor;
        _aboutAuthorText.Text = style.AboutAuthorText;
        _endBlankPages.Text = style.EndBlankPages.ToString();
        _backCover.IsChecked = style.IncludeBackCover;
        _backCoverText.Text = style.BackCoverText;

        _frontOrder.Clear();
        _frontOrder.AddRange(NormalizeOrder(style.FrontMatterOrder, RecommendedFrontOrder));
        _backOrder.Clear();
        _backOrder.AddRange(NormalizeOrder(style.BackMatterOrder, RecommendedBackOrder));
        RefreshOrderList(_frontOrderList, _frontOrder, 0);
        RefreshOrderList(_backOrderList, _backOrder, 0);

        _prefaceHeading.Text = style.PrefaceHeading;
        _forewordHeading.Text = style.ForewordHeading;
        _introductionHeading.Text = style.IntroductionHeading;
        _conclusionHeading.Text = style.ConclusionHeading;
        _epilogueHeading.Text = style.EpilogueHeading;
        _acknowledgmentsHeading.Text = style.AcknowledgmentsHeading;
        _appendixHeading.Text = style.AppendixHeading;
        _glossaryHeading.Text = style.GlossaryHeading;
        _referencesHeading.Text = style.ReferencesHeading;
        _indexHeading.Text = style.IndexHeading;
        _aboutAuthorHeading.Text = style.AboutAuthorHeading;
    }

    private async Task SaveAsync()
    {
        try
        {
            _error.Text = string.Empty;
            var newBookName = Text(_bookName);
            if (string.IsNullOrWhiteSpace(newBookName))
                throw new InvalidOperationException("Book Name is required.");
            if (newBookName.Length > 500)
                throw new InvalidOperationException("Book Name must be 500 characters or fewer.");

            var frontBlanks = ParsePageCount(_frontBlankPages.Text, "Front blank pages");
            var endBlanks = ParsePageCount(_endBlankPages.Text, "End blank pages");
            var current = _viewModel.CurrentStyle;
            var updated = current with
            {
                PublishedTitle = Text(_publishedTitle),
                Subtitle = Text(_subtitle),
                DisplayAuthor = Text(_displayAuthor),
                SeriesTitle = Text(_seriesTitle),
                VolumeLabel = Text(_volumeLabel),
                Edition = Text(_edition),
                Publisher = Text(_publisher),
                Isbn = Text(_isbn),
                FrontMatterOrder = string.Join(',', _frontOrder),
                BackMatterOrder = string.Join(',', _backOrder),
                UseRomanFrontMatterPageNumbers = _romanFrontMatter.IsChecked == true,
                ResetBodyPageNumbers = _resetBodyPageNumbers.IsChecked == true,
                GeneratedMatterInTableOfContents = _generatedMatterInToc.IsChecked == true,
                StartGeneratedMatterOnRight = _generatedMatterOpenRight.IsChecked == true,
                PrefaceHeading = Heading(_prefaceHeading, "Preface"),
                ForewordHeading = Heading(_forewordHeading, "Foreword"),
                IntroductionHeading = Heading(_introductionHeading, "Introduction"),
                ConclusionHeading = Heading(_conclusionHeading, "Conclusion"),
                EpilogueHeading = Heading(_epilogueHeading, "Epilogue"),
                AcknowledgmentsHeading = Heading(_acknowledgmentsHeading, "Acknowledgments"),
                AppendixHeading = Heading(_appendixHeading, "Appendix"),
                GlossaryHeading = Heading(_glossaryHeading, "Glossary"),
                ReferencesHeading = Heading(_referencesHeading, "Bibliography"),
                IndexHeading = Heading(_indexHeading, "Index"),
                AboutAuthorHeading = Heading(_aboutAuthorHeading, "About the Author"),
                IncludeFrontCover = _frontCover.IsChecked == true,
                FrontCoverText = Text(_frontCoverText),
                IncludeSpine = _spine.IsChecked == true,
                SpineText = Text(_spineText),
                FrontBlankPages = frontBlanks,
                IncludeTitlePage = _titlePage.IsChecked == true,
                IncludeCopyrightPage = _copyrightPage.IsChecked == true,
                CopyrightText = Text(_copyrightText),
                IncludeDedication = _dedication.IsChecked == true,
                DedicationText = Text(_dedicationText),
                IncludeTableOfContents = _toc.IsChecked == true,
                IncludePreface = _preface.IsChecked == true,
                PrefaceText = Text(_prefaceText),
                IncludeForeword = _foreword.IsChecked == true,
                ForewordText = Text(_forewordText),
                IncludeIntroduction = _introduction.IsChecked == true,
                IntroductionText = Text(_introductionText),
                ShowPageNumbers = _pageNumbers.IsChecked == true,
                ShowHeadersAndFooters = _headersFooters.IsChecked == true,
                IncludeConclusion = _conclusion.IsChecked == true,
                ConclusionText = Text(_conclusionText),
                IncludeEpilogue = _epilogue.IsChecked == true,
                EpilogueText = Text(_epilogueText),
                IncludeAcknowledgments = _acknowledgments.IsChecked == true,
                AcknowledgmentsText = Text(_acknowledgmentsText),
                IncludeAppendix = _appendix.IsChecked == true,
                AppendixText = Text(_appendixText),
                IncludeGlossary = _glossary.IsChecked == true,
                GlossaryText = Text(_glossaryText),
                IncludeReferencesBibliography = _references.IsChecked == true,
                IncludeIndex = _index.IsChecked == true,
                IndexText = Text(_indexText),
                IncludeAboutAuthor = _aboutAuthor.IsChecked == true,
                AboutAuthorText = Text(_aboutAuthorText),
                EndBlankPages = endBlanks,
                IncludeBackCover = _backCover.IsChecked == true,
                BackCoverText = Text(_backCoverText)
            }.Validate();

            await RenameBookAsync(newBookName);
            await _viewModel.UpdateStyleAsync(updated);
            Close(true);
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
    }

    private async Task RenameBookAsync(string newName)
    {
        var project = TrackingProjectRepository.ActiveInstance?.CurrentProject
            ?? throw new InvalidOperationException("The active project is not available.");
        if (string.Equals(project.Title, newName, StringComparison.Ordinal)) return;

        var manifestPath = Path.Combine(project.RootPath, "typescribe.yaml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The project manifest could not be found.", manifestPath);

        var lines = (await File.ReadAllLinesAsync(manifestPath)).ToList();
        var projectIndex = lines.FindIndex(static line => string.Equals(line.Trim(), "project:", StringComparison.OrdinalIgnoreCase));
        if (projectIndex < 0)
            throw new InvalidOperationException("The project manifest has no project section.");

        var replaced = false;
        for (var index = projectIndex + 1; index < lines.Count; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0])) break;
            if (!trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) continue;

            var indentLength = raw.Length - raw.TrimStart().Length;
            lines[index] = raw[..indentLength] + "title: " + QuoteYaml(newName);
            replaced = true;
            break;
        }

        if (!replaced)
            lines.Insert(projectIndex + 1, "  title: " + QuoteYaml(newName));

        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        var temporary = manifestPath + ".rename-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        project.Title = newName;
        project.Root.Rename(newName);
    }

    private static void MoveOrder(ListBox list, List<string> order, int offset)
    {
        var index = list.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        RefreshOrderList(list, order, target);
    }

    private static void RefreshOrderList(ListBox list, IReadOnlyList<string> order, int selectedIndex)
    {
        list.ItemsSource = order.Select(DisplayToken).ToArray();
        list.SelectedIndex = order.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, order.Count - 1);
    }

    private static IReadOnlyList<string> NormalizeOrder(string configured, IReadOnlyList<string> known)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (configured ?? string.Empty).Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
            if (known.Contains(token, StringComparer.OrdinalIgnoreCase) && seen.Add(token))
                output.Add(token);
        }
        foreach (var token in known)
            if (seen.Add(token)) output.Add(token);
        return output;
    }

    private static string DisplayToken(string token) => token switch
    {
        "front-cover" => "Front Cover",
        "spine" => "Spine proof",
        "front-blanks" => "Front Blank Pages",
        "title-page" => "Title Page",
        "copyright" => "Copyright Page",
        "dedication" => "Dedication",
        "toc" => "Table of Contents",
        "foreword" => "Foreword",
        "preface" => "Preface",
        "introduction" => "Introduction",
        "conclusion" => "Conclusion",
        "epilogue" => "Epilogue",
        "acknowledgments" => "Acknowledgments",
        "appendix" => "Appendix",
        "glossary" => "Glossary",
        "references" => "References / Bibliography",
        "index" => "Index",
        "about-author" => "About the Author",
        "end-blanks" => "End Blank Pages",
        "back-cover" => "Back Cover",
        _ => token
    };

    private static int ParsePageCount(string? value, string label)
    {
        if (!int.TryParse(value?.Trim(), out var count) || count is < 0 or > 20)
            throw new InvalidOperationException($"{label} must be a whole number from 0 to 20.");
        return count;
    }

    private static string Text(TextBox box) => (box.Text ?? string.Empty).Trim();

    private static string Heading(TextBox box, string fallback)
    {
        var text = Text(box);
        return text.Length == 0 ? fallback : text;
    }

    private static string QuoteYaml(string value)
        => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
