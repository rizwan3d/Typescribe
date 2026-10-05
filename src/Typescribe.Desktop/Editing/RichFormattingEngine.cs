using System.Text.RegularExpressions;
using Avalonia.Input;
using AvaloniaEdit.Document;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Single source of truth for Markdown formatting commands used by every editor surface.
/// The document remains canonical Markdown; this class only applies reversible text edits.
/// </summary>
internal static class RichFormattingEngine
{
    private static readonly Regex LinkRegex = new(
        @"\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex PrefixRegex = new(
        @"^(?<indent>[ \t]*)(?<marker>>|[-*+]|\d+\.)(?<space>[ \t]+)",
        RegexOptions.CultureInvariant);

    public readonly record struct State(
        bool Bold,
        bool Italic,
        bool Code,
        int HeadingLevel,
        bool Quote,
        bool BulletList,
        bool NumberedList,
        bool Link);

    public readonly record struct LinkRange(int Offset, int Length, string Label, string Url);

    public static State ReadState(ManuscriptEditor editor)
    {
        if (editor.Document.TextLength == 0) return default;

        var caret = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var lookup = Math.Min(caret, Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        var text = editor.Document.GetText(line);
        var relative = Math.Clamp(caret - line.Offset, 0, text.Length);
        var trimmed = text.TrimStart();
        var prefix = PrefixRegex.Match(text);
        var marker = prefix.Success ? prefix.Groups["marker"].Value : string.Empty;

        return new State(
            Bold: IsInsideInline(text, relative, "**"),
            Italic: IsInsideInline(text, relative, "*") || IsInsideInline(text, relative, "_"),
            Code: IsInsideInline(text, relative, "`"),
            HeadingLevel: HeadingLevel(trimmed),
            Quote: marker == ">",
            BulletList: marker is "-" or "*" or "+",
            NumberedList: marker.Length > 1 && char.IsDigit(marker[0]),
            Link: TryGetLinkRange(editor, out _));
    }

    public static void ToggleBold(ManuscriptEditor editor) => ToggleInline(editor, "**");
    public static void ToggleItalic(ManuscriptEditor editor) => ToggleInline(editor, "*");
    public static void ToggleCode(ManuscriptEditor editor) => ToggleInline(editor, "`");
    public static void ToggleHeading(ManuscriptEditor editor, int level) => ToggleHeadingCore(editor, Math.Clamp(level, 1, 6));
    public static void ToggleQuote(ManuscriptEditor editor) => TogglePrefix(editor, PrefixKind.Quote);
    public static void ToggleBulletList(ManuscriptEditor editor) => TogglePrefix(editor, PrefixKind.Bullet);
    public static void ToggleNumberedList(ManuscriptEditor editor) => TogglePrefix(editor, PrefixKind.Numbered);

    public static bool TryHandleEditingKey(ManuscriptEditor editor, KeyEventArgs e)
    {
        if (editor.IsReadOnly || editor.SelectionLength > 0) return false;

        if (e.Key == Key.Enter && !HasPrimaryModifier(e.KeyModifiers))
            return ContinueOrExitList(editor);

        if (e.Key == Key.Tab && !HasPrimaryModifier(e.KeyModifiers))
            return IndentOrOutdent(editor, e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
            return RemoveEmptyPrefixOnBackspace(editor);

        return false;
    }

    public static void ClearFormatting(ManuscriptEditor editor)
    {
        if (editor.IsReadOnly) return;

        if (editor.SelectionLength > 0)
        {
            var start = editor.SelectionStart;
            var text = editor.Document.GetText(start, editor.SelectionLength);
            var cleaned = LinkRegex.Replace(text, "${label}");
            cleaned = Regex.Replace(cleaned, @"(?<!\*)\*\*(?<v>.+?)\*\*(?!\*)", "${v}");
            cleaned = Regex.Replace(cleaned, @"(?<!\*)\*(?<v>[^*\r\n]+?)\*(?!\*)", "${v}");
            cleaned = Regex.Replace(cleaned, @"_(?<v>[^_\r\n]+?)_", "${v}");
            cleaned = Regex.Replace(cleaned, @"`(?<v>[^`\r\n]+?)`", "${v}");
            cleaned = Regex.Replace(cleaned, @"(?m)^[ \t]*(?:#{1,6}[ \t]+|>[ \t]+|[-*+][ \t]+|\d+\.[ \t]+)", string.Empty);
            editor.Document.Replace(start, editor.SelectionLength, cleaned);
            editor.Select(start, cleaned.Length);
            editor.CaretOffset = start + cleaned.Length;
            editor.Focus();
            return;
        }

        var state = ReadState(editor);
        if (state.Link) Unlink(editor);
        if (state.Code) ToggleCode(editor);
        if (ReadState(editor).Bold) ToggleBold(editor);
        if (ReadState(editor).Italic) ToggleItalic(editor);
        var refreshed = ReadState(editor);
        if (refreshed.HeadingLevel > 0) ToggleHeading(editor, refreshed.HeadingLevel);
        refreshed = ReadState(editor);
        if (refreshed.Quote) ToggleQuote(editor);
        else if (refreshed.BulletList) ToggleBulletList(editor);
        else if (refreshed.NumberedList) ToggleNumberedList(editor);
        editor.Focus();
    }

    public static bool Unlink(ManuscriptEditor editor)
    {
        if (editor.IsReadOnly || !TryGetLinkRange(editor, out var range)) return false;
        editor.Document.Replace(range.Offset, range.Length, range.Label);
        editor.Select(range.Offset, range.Label.Length);
        editor.CaretOffset = range.Offset + range.Label.Length;
        editor.Focus();
        return true;
    }

    public static bool TryGetLinkRange(ManuscriptEditor editor, out LinkRange range)
    {
        range = default;
        if (editor.Document.TextLength == 0) return false;

        var selectionStart = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var selectionEnd = editor.SelectionLength > 0
            ? editor.SelectionStart + editor.SelectionLength
            : editor.CaretOffset;
        var lookup = Math.Min(Math.Clamp(selectionStart, 0, editor.Document.TextLength), Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        if (selectionEnd > line.EndOffset) return false;

        var text = editor.Document.GetText(line);
        var relativeStart = Math.Clamp(selectionStart - line.Offset, 0, text.Length);
        var relativeEnd = Math.Clamp(selectionEnd - line.Offset, relativeStart, text.Length);

        foreach (Match match in LinkRegex.Matches(text))
        {
            var start = match.Index;
            var end = match.Index + match.Length;
            var containsCaret = relativeStart >= start && relativeStart <= end;
            var containsSelection = relativeStart >= start && relativeEnd <= end;
            if (!containsCaret && !containsSelection) continue;
            range = new LinkRange(
                line.Offset + start,
                match.Length,
                match.Groups["label"].Value,
                match.Groups["url"].Value);
            return true;
        }
        return false;
    }

    private static void ToggleInline(ManuscriptEditor editor, string marker)
    {
        if (editor.IsReadOnly) return;
        var document = editor.Document;
        var markerLength = marker.Length;
        var selectionStart = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var selectionLength = Math.Max(0, editor.SelectionLength);
        var selectionEnd = selectionStart + selectionLength;

        if (selectionLength > 0)
        {
            var selected = document.GetText(selectionStart, selectionLength);
            if (selected.Length >= markerLength * 2 &&
                selected.StartsWith(marker, StringComparison.Ordinal) &&
                selected.EndsWith(marker, StringComparison.Ordinal))
            {
                var inner = selected[markerLength..^markerLength];
                document.Replace(selectionStart, selectionLength, inner);
                editor.Select(selectionStart, inner.Length);
                editor.CaretOffset = selectionStart + inner.Length;
                editor.Focus();
                return;
            }

            if (selectionStart >= markerLength &&
                selectionEnd + markerLength <= document.TextLength &&
                document.GetText(selectionStart - markerLength, markerLength) == marker &&
                document.GetText(selectionEnd, markerLength) == marker &&
                IsValidMarkerAt(document.Text, selectionStart - markerLength, marker) &&
                IsValidMarkerAt(document.Text, selectionEnd, marker))
            {
                document.Remove(selectionEnd, markerLength);
                document.Remove(selectionStart - markerLength, markerLength);
                editor.Select(selectionStart - markerLength, selectionLength);
                editor.CaretOffset = selectionStart - markerLength + selectionLength;
                editor.Focus();
                return;
            }

            if (TryFindContainingInlineRange(editor, marker, selectionStart, selectionEnd, out var open, out var close))
            {
                document.Remove(close, markerLength);
                document.Remove(open, markerLength);
                var adjustedStart = Math.Max(open, selectionStart - markerLength);
                editor.Select(adjustedStart, selectionLength);
                editor.CaretOffset = adjustedStart + selectionLength;
                editor.Focus();
                return;
            }

            document.Insert(selectionEnd, marker);
            document.Insert(selectionStart, marker);
            editor.Select(selectionStart + markerLength, selectionLength);
            editor.CaretOffset = selectionStart + markerLength + selectionLength;
            editor.Focus();
            return;
        }

        var caret = Math.Clamp(editor.CaretOffset, 0, document.TextLength);
        if (TryFindContainingInlineRange(editor, marker, caret, caret, out var containingOpen, out var containingClose))
        {
            document.Remove(containingClose, markerLength);
            document.Remove(containingOpen, markerLength);
            editor.CaretOffset = Math.Max(containingOpen, caret - markerLength);
            editor.Select(editor.CaretOffset, 0);
            editor.Focus();
            return;
        }

        document.Insert(caret, marker + marker);
        editor.CaretOffset = caret + markerLength;
        editor.Select(editor.CaretOffset, 0);
        editor.Focus();
    }

    private static void ToggleHeadingCore(ManuscriptEditor editor, int level)
    {
        if (editor.IsReadOnly || editor.Document.LineCount == 0) return;
        var lineNumbers = SelectedLineNumbers(editor);
        if (lineNumbers.Count == 0) return;
        var remove = lineNumbers.All(number =>
            HeadingLevel(editor.Document.GetText(editor.Document.GetLineByNumber(number)).TrimStart()) == level);

        editor.Document.BeginUpdate();
        try
        {
            for (var index = lineNumbers.Count - 1; index >= 0; index--)
            {
                var line = editor.Document.GetLineByNumber(lineNumbers[index]);
                var text = editor.Document.GetText(line);
                var indent = LeadingWhitespace(text);
                var existingLevel = HeadingLevel(text[indent..]);
                var existingLength = existingLevel > 0 ? existingLevel + 1 : 0;
                var marker = remove ? string.Empty : new string('#', level) + " ";
                editor.Document.Replace(line.Offset + indent, existingLength, marker);
            }
        }
        finally
        {
            editor.Document.EndUpdate();
        }
        editor.Focus();
    }

    private static void TogglePrefix(ManuscriptEditor editor, PrefixKind kind)
    {
        if (editor.IsReadOnly || editor.Document.LineCount == 0) return;
        var lineNumbers = SelectedLineNumbers(editor);
        if (lineNumbers.Count == 0) return;

        var allMatch = lineNumbers.All(number => PrefixMatches(editor.Document.GetText(editor.Document.GetLineByNumber(number)), kind));
        editor.Document.BeginUpdate();
        try
        {
            for (var index = lineNumbers.Count - 1; index >= 0; index--)
            {
                var number = lineNumbers[index];
                var line = editor.Document.GetLineByNumber(number);
                var text = editor.Document.GetText(line);
                var match = PrefixRegex.Match(text);
                var indent = match.Success ? match.Groups["indent"].Length : LeadingWhitespace(text);
                var existingLength = match.Success ? match.Length - indent : 0;

                if (allMatch)
                {
                    if (existingLength > 0) editor.Document.Remove(line.Offset + indent, existingLength);
                    continue;
                }

                var replacement = kind switch
                {
                    PrefixKind.Quote => "> ",
                    PrefixKind.Bullet => "- ",
                    PrefixKind.Numbered => $"{index + 1}. ",
                    _ => string.Empty
                };
                editor.Document.Replace(line.Offset + indent, existingLength, replacement);
            }
        }
        finally
        {
            editor.Document.EndUpdate();
        }
        editor.Focus();
    }

    private static bool ContinueOrExitList(ManuscriptEditor editor)
    {
        if (editor.Document.LineCount == 0) return false;
        var line = editor.Document.GetLineByOffset(Math.Min(editor.CaretOffset, Math.Max(0, editor.Document.TextLength)));
        var text = editor.Document.GetText(line);
        var match = PrefixRegex.Match(text);
        if (!match.Success) return false;

        var prefixEnd = match.Length;
        var caretInLine = editor.CaretOffset - line.Offset;
        if (caretInLine < prefixEnd) return false;

        var content = text[prefixEnd..];
        if (string.IsNullOrWhiteSpace(content))
        {
            editor.Document.Remove(line.Offset + match.Groups["indent"].Length, match.Length - match.Groups["indent"].Length);
            editor.CaretOffset = line.Offset + match.Groups["indent"].Length;
            return true;
        }

        var indent = match.Groups["indent"].Value;
        var marker = match.Groups["marker"].Value;
        var nextMarker = marker;
        if (marker.Length > 1 && char.IsDigit(marker[0]))
        {
            var numberText = marker[..^1];
            if (int.TryParse(numberText, out var number)) nextMarker = $"{number + 1}.";
        }

        var insertion = "\n" + indent + nextMarker + " ";
        editor.Document.Insert(editor.CaretOffset, insertion);
        editor.CaretOffset += insertion.Length;
        return true;
    }

    private static bool IndentOrOutdent(ManuscriptEditor editor, bool outdent)
    {
        if (editor.Document.LineCount == 0) return false;
        var line = editor.Document.GetLineByOffset(Math.Min(editor.CaretOffset, Math.Max(0, editor.Document.TextLength)));
        var text = editor.Document.GetText(line);
        var match = PrefixRegex.Match(text);
        if (!match.Success) return false;

        var indentLength = match.Groups["indent"].Length;
        if (outdent)
        {
            if (indentLength == 0) return true;
            var remove = Math.Min(2, indentLength);
            editor.Document.Remove(line.Offset, remove);
            editor.CaretOffset = Math.Max(line.Offset, editor.CaretOffset - remove);
        }
        else
        {
            editor.Document.Insert(line.Offset, "  ");
            editor.CaretOffset += 2;
        }
        return true;
    }

    private static bool RemoveEmptyPrefixOnBackspace(ManuscriptEditor editor)
    {
        if (editor.Document.LineCount == 0) return false;
        var line = editor.Document.GetLineByOffset(Math.Min(editor.CaretOffset, Math.Max(0, editor.Document.TextLength)));
        var text = editor.Document.GetText(line);
        var match = PrefixRegex.Match(text);
        if (!match.Success) return false;
        var caretInLine = editor.CaretOffset - line.Offset;
        if (caretInLine != match.Length || !string.IsNullOrWhiteSpace(text[match.Length..])) return false;

        var indentLength = match.Groups["indent"].Length;
        editor.Document.Remove(line.Offset + indentLength, match.Length - indentLength);
        editor.CaretOffset = line.Offset + indentLength;
        return true;
    }

    private static IReadOnlyList<int> SelectedLineNumbers(ManuscriptEditor editor)
    {
        if (editor.Document.LineCount == 0) return [];
        var start = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var end = editor.SelectionLength > 0 ? editor.SelectionStart + editor.SelectionLength : editor.CaretOffset;
        var maxOffset = Math.Max(0, editor.Document.TextLength - 1);
        var startLine = editor.Document.GetLineByOffset(Math.Min(Math.Max(0, start), maxOffset));
        var endLookup = end > start ? end - 1 : end;
        var endLine = editor.Document.GetLineByOffset(Math.Min(Math.Max(0, endLookup), maxOffset));
        return Enumerable.Range(startLine.LineNumber, endLine.LineNumber - startLine.LineNumber + 1).ToArray();
    }

    private static bool PrefixMatches(string text, PrefixKind kind)
    {
        var match = PrefixRegex.Match(text);
        if (!match.Success) return false;
        var marker = match.Groups["marker"].Value;
        return kind switch
        {
            PrefixKind.Quote => marker == ">",
            PrefixKind.Bullet => marker is "-" or "*" or "+",
            PrefixKind.Numbered => marker.Length > 1 && char.IsDigit(marker[0]),
            _ => false
        };
    }

    private static bool TryFindContainingInlineRange(
        ManuscriptEditor editor,
        string marker,
        int absoluteStart,
        int absoluteEnd,
        out int openOffset,
        out int closeOffset)
    {
        openOffset = -1;
        closeOffset = -1;
        if (editor.Document.TextLength == 0) return false;

        var lookup = Math.Min(Math.Max(0, absoluteStart), Math.Max(0, editor.Document.TextLength - 1));
        var line = editor.Document.GetLineByOffset(lookup);
        if (absoluteEnd > line.EndOffset) return false;

        var text = editor.Document.GetText(line);
        var relativeStart = Math.Clamp(absoluteStart - line.Offset, 0, text.Length);
        var relativeEnd = Math.Clamp(absoluteEnd - line.Offset, relativeStart, text.Length);
        var positions = MarkerPositions(text, marker).ToArray();
        for (var index = 0; index + 1 < positions.Length; index += 2)
        {
            var open = positions[index];
            var close = positions[index + 1];
            if (relativeStart < open + marker.Length || relativeEnd > close) continue;
            openOffset = line.Offset + open;
            closeOffset = line.Offset + close;
            return true;
        }
        return false;
    }

    private static bool IsInsideInline(string text, int relative, string marker)
    {
        var positions = MarkerPositions(text, marker).ToArray();
        for (var index = 0; index + 1 < positions.Length; index += 2)
        {
            if (relative >= positions[index] + marker.Length && relative <= positions[index + 1])
                return true;
        }
        return false;
    }

    private static IEnumerable<int> MarkerPositions(string text, string marker)
    {
        var search = 0;
        while (search <= text.Length - marker.Length)
        {
            var index = text.IndexOf(marker, search, StringComparison.Ordinal);
            if (index < 0) yield break;
            if (IsValidMarkerAt(text, index, marker)) yield return index;
            search = index + marker.Length;
        }
    }

    private static bool IsValidMarkerAt(string text, int index, string marker)
    {
        if (index < 0 || index + marker.Length > text.Length) return false;
        if (!text.AsSpan(index, marker.Length).SequenceEqual(marker.AsSpan())) return false;
        if (marker != "*") return true;
        var before = index > 0 && text[index - 1] == '*';
        var after = index + 1 < text.Length && text[index + 1] == '*';
        return !before && !after;
    }

    private static int HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
        return level > 0 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
    }

    private static int LeadingWhitespace(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]) && text[index] != '\n' && text[index] != '\r') index++;
        return index;
    }

    private static bool HasPrimaryModifier(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    private enum PrefixKind
    {
        Quote,
        Bullet,
        Numbered
    }
}
