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

    // Avalonia generates members for x:Name elements. These semantic aliases give the editor
    // feature a small stable API without any visual-tree queries or generated-name collisions.
    internal Border CommandBar => EditorCommandBar;
    internal Border FindPanel => FindPanelElement;
    internal Border StatusBar => EditorStatusBar;
    internal TextBox FindBox => FindBoxElement;
    internal TextBox ReplaceBox => ReplaceBoxElement;
    internal CheckBox MatchCase => MatchCaseElement;
    internal CheckBox WholeWord => WholeWordElement;
    internal TextBlock FindStatus => FindStatusElement;
    internal TextBlock ContextText => ContextTextElement;
    internal TextBlock StatsText => StatsTextElement;
    internal TextBlock ZoomText => ZoomTextElement;

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
