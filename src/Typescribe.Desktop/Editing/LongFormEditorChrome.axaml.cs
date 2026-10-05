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

    // Avalonia's name generator creates members for every x:Name. Expose stable aliases with
    // distinct names so LongFormEditorFeature never needs to search the visual tree.
    internal Border CommandBarControl => EditorCommandBar;
    internal Border FindPanelControl => FindPanel;
    internal Border StatusBarControl => EditorStatusBar;
    internal TextBox FindBoxControl => FindBox;
    internal TextBox ReplaceBoxControl => ReplaceBox;
    internal CheckBox MatchCaseControl => MatchCase;
    internal CheckBox WholeWordControl => WholeWord;
    internal TextBlock FindStatusText => FindStatus;
    internal TextBlock ContextStatusText => ContextText;
    internal TextBlock StatisticsText => StatsText;
    internal TextBlock ZoomStatusText => ZoomText;

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
