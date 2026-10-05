using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaTextBlock = Avalonia.Controls.TextBlock;
using AvaloniaTextBox = Avalonia.Controls.TextBox;

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

    // These are explicit named-component lookups, not visual-tree discovery. Keeping the small
    // semantic API here avoids coupling LongFormEditorFeature to generated XAML member details.
    internal Border CommandBar => Require<Border>("EditorCommandBar");
    internal Border FindPanel => Require<Border>("FindPanelElement");
    internal Border StatusBar => Require<Border>("EditorStatusBar");
    internal AvaloniaTextBox FindBox => Require<AvaloniaTextBox>("FindBoxElement");
    internal AvaloniaTextBox ReplaceBox => Require<AvaloniaTextBox>("ReplaceBoxElement");
    internal CheckBox MatchCase => Require<CheckBox>("MatchCaseElement");
    internal CheckBox WholeWord => Require<CheckBox>("WholeWordElement");
    internal AvaloniaTextBlock FindStatus => Require<AvaloniaTextBlock>("FindStatusElement");
    internal AvaloniaTextBlock ContextText => Require<AvaloniaTextBlock>("ContextTextElement");
    internal AvaloniaTextBlock StatsText => Require<AvaloniaTextBlock>("StatsTextElement");
    internal AvaloniaTextBlock ZoomText => Require<AvaloniaTextBlock>("ZoomTextElement");

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

    private T Require<T>(string name) where T : Control
        => this.FindControl<T>(name)
           ?? throw new InvalidOperationException($"Editor chrome element '{name}' is unavailable.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
