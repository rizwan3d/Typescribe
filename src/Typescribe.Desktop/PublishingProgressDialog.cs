using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Typescribe.Desktop;

/// <summary>
/// Small modal progress surface shared by book export and publishing commands.
/// Progress values are stage-based so long-running format writers can keep the UI responsive
/// even when their underlying libraries do not expose byte-level progress callbacks.
/// </summary>
internal sealed class PublishingProgressDialog : Window
{
    private readonly ProgressBar _progress = new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 8,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private readonly TextBlock _message = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _percent = new()
    {
        MinWidth = 48,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    private PublishingProgressDialog(string title)
    {
        Title = title;
        Width = 460;
        Height = 150;
        MinWidth = 380;
        MinHeight = 130;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        status.Children.Add(_message);
        Grid.SetColumn(_percent, 1);
        status.Children.Add(_percent);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(18),
            RowSpacing = 14,
            Children =
            {
                status,
                _progress
            }
        };
        Grid.SetRow(_progress, 1);
    }

    public static async Task RunAsync(
        Window owner,
        string title,
        Func<PublishingProgressDialog, Task> action)
    {
        var dialog = new PublishingProgressDialog(title);
        var shown = dialog.ShowDialog(owner);
        try
        {
            dialog.Report(0.02, "Starting…");
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await action(dialog);
            dialog.Report(1, "Complete");
            await Task.Delay(250);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown;
        }
    }

    public void Report(double value, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Report(value, message));
            return;
        }

        var normalized = Math.Clamp(value, 0, 1);
        _progress.Value = normalized;
        _message.Text = message;
        _percent.Text = $"{normalized:P0}";
    }
}
