using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed class BookStructureDialog : Window
{
    private readonly WorkspaceViewModel _viewModel;

    private readonly CheckBox _frontCover = new() { Content = "Front Cover" };
    private readonly TextBox _frontCoverText = MultiLine("Optional front-cover text; leave blank to use the project title.");
    private readonly CheckBox _spine = new() { Content = "Spine" };
    private readonly TextBox _spineText = MultiLine("Optional spine text; leave blank to use the project title.");
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

    private readonly CheckBox _pageNumbers = new() { Content = "Page Numbers" };
    private readonly CheckBox _headersFooters = new() { Content = "Headers / Footers" };

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
    private readonly TextBox _backCoverText = MultiLine("Optional back-cover text; leave blank to use the project title.");
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    public BookStructureDialog(WorkspaceViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = "Book Structure";
        Width = 940;
        Height = 760;
        MinWidth = 720;
        MinHeight = 560;
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
                new TabItem { Header = "Front Matter", Content = BuildFrontMatter() },
                new TabItem { Header = "Page Elements", Content = BuildPageElements() },
                new TabItem { Header = "Back Matter", Content = BuildBackMatter() }
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
                    Text = "Choose generated front matter, running page elements, and back matter for PDF/LaTeX publishing. Cover and spine choices are emitted as separate proof pages; printer-specific wrap-cover geometry remains a production step.",
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
        panel.Children.Add(Option(_preface, _prefaceText));
        panel.Children.Add(Option(_foreword, _forewordText));
        panel.Children.Add(Option(_introduction, _introductionText));
        return Scroll(panel);
    }

    private Control BuildPageElements()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        panel.Children.Add(SimpleOption(_pageNumbers));
        panel.Children.Add(SimpleOption(_headersFooters));
        panel.Children.Add(new TextBlock
        {
            Text = "Header/footer text, alignment and typography remain editable under Project → Book Design & LaTeX → Headers & Footer.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 2, 4, 0)
        });
        return panel;
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
            Text = "References / Bibliography uses the project's structured citation entries and is placed before Index/About the Author.",
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

    private static Control NumberRow(string label, TextBox box)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,100,*") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

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
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

    private void Load(BookStyle style)
    {
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
    }

    private async Task SaveAsync()
    {
        try
        {
            _error.Text = string.Empty;
            var frontBlanks = ParsePageCount(_frontBlankPages.Text, "Front blank pages");
            var endBlanks = ParsePageCount(_endBlankPages.Text, "End blank pages");
            var current = _viewModel.CurrentStyle;
            var updated = current with
            {
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
            };

            await _viewModel.UpdateStyleAsync(updated.Validate());
            Close(true);
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
    }

    private static int ParsePageCount(string? value, string label)
    {
        if (!int.TryParse(value?.Trim(), out var count) || count is < 0 or > 20)
            throw new InvalidOperationException($"{label} must be a whole number from 0 to 20.");
        return count;
    }

    private static string Text(TextBox box) => (box.Text ?? string.Empty).Trim();
}
