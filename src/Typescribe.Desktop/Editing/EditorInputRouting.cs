using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Typescribe.Desktop.Editing;

internal static class EditorInputRouting
{
    public static bool IsFromNativeInlineEditor(KeyEventArgs e, ManuscriptEditor? editor)
    {
        if (editor is null || e.Source is not TextBox source) return false;
        return ReferenceEquals(source, editor) ||
               source.GetVisualAncestors().OfType<ManuscriptEditor>().Contains(editor);
    }
}
