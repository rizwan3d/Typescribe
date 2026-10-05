namespace Typescribe.Desktop.Editing;

/// <summary>
/// Compatibility entry point for editor-adjacent enhancements that historically lived beside
/// toolbar post-processing. The command bar itself is now declared by LongFormEditorChrome and
/// is no longer discovered or reconstructed from visual-tree child indexes.
/// </summary>
internal static class EditorToolbarPolishFeature
{
    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Canonical Markdown pipe tables are edited as inline AvaloniaEdit objects.
        InlineMarkdownTableEditorFeature.Apply(window);

        // AvaloniaEdit 12 hardcodes visual-line paragraph direction to LTR. RTL manuscript lines
        // therefore use a native in-flow Avalonia TextBox so Unicode BiDi, shaping and caret
        // navigation are handled by the platform text formatter without reordering source text.
        InlineBidiParagraphEditorFeature.Apply(window);

        // Character/paragraph typography uses hidden, Markdown-safe metadata rather than a
        // parallel binary document model. System/project fonts and multilingual intent share it.
        RichTypographyInspectorFeature.Apply(window);
        BidiInspectorStatusFeature.Apply(window);

        // Ctrl/Cmd+C/X/V exchanges TypeScribe rich Markdown together with HTML, RTF and plain
        // Unicode so Word, LibreOffice, browsers and other TypeScribe windows interoperate.
        RichClipboardFeature.Apply(window);
    }
}
