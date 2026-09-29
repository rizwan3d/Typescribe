using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

public sealed partial class BookDesignDialog : Window
{
    private readonly WorkspaceViewModel? _workspace;

    public BookDesignDialog()
    {
        InitializeComponent();
        DataContext = BookDesignEditorViewModel.FromStyle(BookStyle.Default);
    }

    public BookDesignDialog(WorkspaceViewModel workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        InitializeComponent();
        DataContext = BookDesignEditorViewModel.FromStyle(workspace.CurrentStyle);
    }

    private BookDesignEditorViewModel Editor
        => DataContext as BookDesignEditorViewModel
           ?? throw new InvalidOperationException("Book Design editor model is unavailable.");

    private TextBlock ErrorText
        => this.FindControl<TextBlock>("ErrorText")
           ?? throw new InvalidOperationException("Book Design error surface is unavailable.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
        => await ApplyAsync(closeWhenDone: false);

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
        => await ApplyAsync(closeWhenDone: true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        DataContext = BookDesignEditorViewModel.FromStyle(BookStyle.Default);
        ErrorText.Text = string.Empty;
    }

    private async Task ApplyAsync(bool closeWhenDone)
    {
        if (_workspace is null) return;
        try
        {
            ErrorText.Text = string.Empty;
            var style = Editor.ToStyle();
            await _workspace.UpdateStyleAsync(style);
            if (closeWhenDone) Close(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
