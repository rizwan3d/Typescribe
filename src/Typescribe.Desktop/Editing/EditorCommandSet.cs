using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Input;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Shared editor commands used by the command bar, menus, context menus and keyboard shortcuts.
/// The command objects are created before an editor is attached and are bound to editing behavior
/// by <see cref="LongFormEditorFeature"/> once the live manuscript editor is available.
/// </summary>
public sealed class EditorCommandSet
{
    private readonly EditorCommand[] _all;
    private readonly EditorCommand[] _shortcutCommands;

    public EditorCommandSet()
    {
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        Bold = new EditorCommand("bold", "Bold", "Bold", "Bold selected text", new EditorShortcut(Key.B, primary), requiresEditorFocusForShortcut: true);
        Italic = new EditorCommand("italic", "Italic", "Italic", "Italicize selected text", new EditorShortcut(Key.I, primary), requiresEditorFocusForShortcut: true);
        InlineCode = new EditorCommand("inline-code", "Inline Code", "Code", "Format selection as inline code");
        Heading1 = new EditorCommand("heading-1", "Heading 1", "Heading 1", "Toggle Heading 1 for the current paragraph");
        Heading2 = new EditorCommand("heading-2", "Heading 2", "Heading 2", "Toggle Heading 2 for the current paragraph");
        Heading3 = new EditorCommand("heading-3", "Heading 3", "Heading 3", "Toggle Heading 3 for the current paragraph");
        BlockQuote = new EditorCommand("block-quote", "Block Quote", "Quote", "Toggle block quote for the selected paragraph(s)");
        BulletList = new EditorCommand("bullet-list", "Bullet List", "Bullets", "Toggle a bulleted list for the selected paragraph(s)");
        NumberedList = new EditorCommand("numbered-list", "Numbered List", "Numbered", "Toggle a numbered list for the selected paragraph(s)");

        FindReplace = new EditorCommand("find-replace", "Find / Replace in Document", "Find / Replace", "Find and replace in this document", new EditorShortcut(Key.H, primary));
        FindPrevious = new EditorCommand("find-previous", "Find Previous in Document", "Previous", "Go to the previous document match", new EditorShortcut(Key.F3, KeyModifiers.Shift));
        FindNext = new EditorCommand("find-next", "Find Next in Document", "Next", "Go to the next document match", new EditorShortcut(Key.F3));
        ReplaceCurrent = new EditorCommand("replace-current", "Replace Current Match", "Replace", "Replace the current document match");
        ReplaceAll = new EditorCommand("replace-all", "Replace All Matches", "Replace All", "Replace all document matches");
        CloseFind = new EditorCommand("close-find", "Close Find / Replace", "Close", "Close find and replace");

        FocusMode = new EditorToggleCommand("focus-mode", "Focus Mode", "Focus", "Dim text outside the current paragraph");
        TypewriterMode = new EditorToggleCommand("typewriter-mode", "Typewriter Mode", "Typewriter", "Keep the caret vertically centered while typing");
        MarkdownMarks = new EditorToggleCommand("markdown-marks", "Markdown Marks", "Markdown", "Show Markdown punctuation", new EditorShortcut(Key.M, primary | KeyModifiers.Shift), requiresEditorFocusForShortcut: true);
        WordWrap = new EditorToggleCommand("word-wrap", "Word Wrap", "Wrap", "Toggle soft word wrapping", new EditorShortcut(Key.Z, KeyModifiers.Alt), requiresEditorFocusForShortcut: true);
        LineNumbers = new EditorToggleCommand("line-numbers", "Line Numbers", "Lines", "Show manuscript line numbers");
        PageWidth = new EditorToggleCommand("page-width", "Page Width", "Page Width", "Use a comfortable centered manuscript width");

        ZoomOut = new EditorCommand("zoom-out", "Zoom Out", "Zoom Out", "Decrease editor font size");
        ZoomIn = new EditorCommand("zoom-in", "Zoom In", "Zoom In", "Increase editor font size");
        ZoomReset = new EditorCommand("zoom-reset", "Reset Zoom", "Reset", "Reset editor font size");

        _all =
        [
            Bold, Italic, InlineCode, Heading1, Heading2, Heading3, BlockQuote, BulletList, NumberedList,
            FindReplace, FindPrevious, FindNext, ReplaceCurrent, ReplaceAll, CloseFind,
            FocusMode, TypewriterMode, MarkdownMarks, WordWrap, LineNumbers, PageWidth,
            ZoomOut, ZoomIn, ZoomReset
        ];
        _shortcutCommands = _all.Where(static command => command.Shortcut is not null).ToArray();
    }

    public EditorCommand Bold { get; }
    public EditorCommand Italic { get; }
    public EditorCommand InlineCode { get; }
    public EditorCommand Heading1 { get; }
    public EditorCommand Heading2 { get; }
    public EditorCommand Heading3 { get; }
    public EditorCommand BlockQuote { get; }
    public EditorCommand BulletList { get; }
    public EditorCommand NumberedList { get; }

    public EditorCommand FindReplace { get; }
    public EditorCommand FindPrevious { get; }
    public EditorCommand FindNext { get; }
    public EditorCommand ReplaceCurrent { get; }
    public EditorCommand ReplaceAll { get; }
    public EditorCommand CloseFind { get; }

    public EditorToggleCommand FocusMode { get; }
    public EditorToggleCommand TypewriterMode { get; }
    public EditorToggleCommand MarkdownMarks { get; }
    public EditorToggleCommand WordWrap { get; }
    public EditorToggleCommand LineNumbers { get; }
    public EditorToggleCommand PageWidth { get; }

    public EditorCommand ZoomOut { get; }
    public EditorCommand ZoomIn { get; }
    public EditorCommand ZoomReset { get; }

    public bool TryExecuteShortcut(KeyEventArgs e, bool editorHasFocus)
    {
        foreach (var command in _shortcutCommands)
        {
            if (command.Shortcut?.Matches(e) != true) continue;
            if (command.RequiresEditorFocusForShortcut && !editorHasFocus) continue;
            if (!command.CanExecute(null)) return false;

            command.Execute(null);
            return true;
        }

        return false;
    }

    public void RefreshCanExecute()
    {
        foreach (var command in _all) command.RaiseCanExecuteChanged();
    }

    public void UnbindAll()
    {
        foreach (var command in _all) command.Unbind();
    }
}

public sealed record EditorShortcut(Key Key, KeyModifiers Modifiers)
{
    private const KeyModifiers RelevantModifiers =
        KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt;

    public KeyGesture Gesture => new(Key, Modifiers);

    public bool Matches(KeyEventArgs e)
        => e.Key == Key && (e.KeyModifiers & RelevantModifiers) == (Modifiers & RelevantModifiers);
}

public class EditorCommand : ICommand
{
    private Action? _execute;
    private Func<bool>? _canExecute;

    public EditorCommand(
        string id,
        string label,
        string toolbarLabel,
        string toolTip,
        EditorShortcut? shortcut = null,
        bool requiresEditorFocusForShortcut = false)
    {
        Id = id;
        Label = label;
        ToolbarLabel = toolbarLabel;
        ToolTip = toolTip;
        Shortcut = shortcut;
        RequiresEditorFocusForShortcut = requiresEditorFocusForShortcut;
    }

    public string Id { get; }
    public string Label { get; }
    public string ToolbarLabel { get; }
    public string ToolTip { get; }
    public EditorShortcut? Shortcut { get; }
    public KeyGesture? InputGesture => Shortcut?.Gesture;
    public bool RequiresEditorFocusForShortcut { get; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => _execute is not null && (_canExecute?.Invoke() ?? true);

    public virtual void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute!();
    }

    public void Bind(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        RaiseCanExecuteChanged();
    }

    public virtual void Unbind()
    {
        _execute = null;
        _canExecute = null;
        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class EditorToggleCommand : EditorCommand, INotifyPropertyChanged
{
    private Action<bool>? _apply;
    private bool _isChecked;

    public EditorToggleCommand(
        string id,
        string label,
        string toolbarLabel,
        string toolTip,
        EditorShortcut? shortcut = null,
        bool requiresEditorFocusForShortcut = false)
        : base(id, label, toolbarLabel, toolTip, shortcut, requiresEditorFocusForShortcut)
    {
    }

    public bool IsChecked
    {
        get => _isChecked;
        private set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void BindToggle(Action<bool> apply, Func<bool>? canExecute = null)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Bind(() => SetChecked(!IsChecked, applyValue: true), canExecute);
    }

    public void SetCheckedFromModel(bool value) => SetChecked(value, applyValue: false);

    public override void Unbind()
    {
        _apply = null;
        base.Unbind();
    }

    private void SetChecked(bool value, bool applyValue)
    {
        IsChecked = value;
        if (applyValue) _apply?.Invoke(value);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
