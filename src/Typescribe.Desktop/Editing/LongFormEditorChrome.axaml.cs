using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Stable editor chrome declared in XAML. The live ManuscriptEditor remains a direct child
/// of this Grid so existing editor extensions keep a stable host contract while toolbar,
/// find/replace and status UI are explicit named components.
/// </summary>
public sealed partial class LongFormEditorChrome : Grid
{
    public LongFormEditorChrome()
        => InitializeComponent();

    internal LongFormEditorChrome(EditorCommandSet commands)
        : this()
        => DataContext = commands ?? throw new ArgumentNullException(nameof(commands));

    internal Border CommandBar
        => this.FindControl<Border>("EditorCommandBar")
           ?? throw new InvalidOperationException("Editor command bar is unavailable.");

    internal Border FindPanel
        => this.FindControl<Border>("FindPanel")
           ?? throw new InvalidOperationException("Editor find panel is unavailable.");

    internal Border StatusBar
        => this.FindControl<Border>("EditorStatusBar")
           ?? throw new InvalidOperationException("Editor status bar is unavailable.");

    internal TextBox FindBox
        => this.FindControl<TextBox>("FindBox")
           ?? throw new InvalidOperationException("Editor find box is unavailable.");

    internal TextBox ReplaceBox
        => this.FindControl<TextBox>("ReplaceBox")
           ?? throw new InvalidOperationException("Editor replace box is unavailable.");

    internal CheckBox MatchCase
        => this.FindControl<CheckBox>("MatchCase")
           ?? throw new InvalidOperationException("Editor match-case option is unavailable.");

    internal CheckBox WholeWord
        => this.FindControl<CheckBox>("WholeWord")
           ?? throw new InvalidOperationException("Editor whole-word option is unavailable.");

    internal TextBlock FindStatus
        => this.FindControl<TextBlock>("FindStatus")
           ?? throw new InvalidOperationException("Editor find status is unavailable.");

    internal TextBlock ContextText
        => this.FindControl<TextBlock>("ContextText")
           ?? throw new InvalidOperationException("Editor context status is unavailable.");

    internal TextBlock StatsText
        => this.FindControl<TextBlock>("StatsText")
           ?? throw new InvalidOperationException("Editor statistics status is unavailable.");

    internal TextBlock ZoomText
        => this.FindControl<TextBlock>("ZoomText")
           ?? throw new InvalidOperationException("Editor zoom status is unavailable.");

    internal void SetEditor(ManuscriptEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.Parent is not null)
            throw new InvalidOperationException("The manuscript editor must be detached before it is hosted.");

        Grid.SetRow(editor, 2);
        Grid.SetColumn(editor, 0);
        Grid.SetRowSpan(editor, 1);
        Grid.SetColumnSpan(editor, 1);
        Children.Add(editor);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
