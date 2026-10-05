using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

internal static class RtfClipboardCodec
{
    private static readonly Regex FontRegex = new("\\\\f(?<id>[0-9]+)(?:[^;{}]*?)(?<name>[^;{}\\\\]+);", RegexOptions.CultureInvariant);

    public static byte[] Export(DocumentAst ast, ICollection<string> warnings)
    {
        var fonts = CollectFonts(ast);
        var fontIds = fonts.Select((font, index) => (font, index)).ToDictionary(static pair => pair.font, static pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder("{\\rtf1\\ansi\\deff0\\uc1");
        output.Append("{\\fonttbl");
        foreach (var pair in fontIds.OrderBy(static pair => pair.Value))
            output.Append("{\\f").Append(pair.Value).Append("\\fnil ").Append(EscapeAscii(pair.Key)).Append(";}");
        output.Append('}');

        foreach (var block in ast.Blocks)
        {
            var paragraph = block.Formatting?.Paragraph;
            output.Append('{');
            if (paragraph?.Direction == TextDirectionMode.RightToLeft) output.Append("\\rtlpar ");
            else if (paragraph?.Direction == TextDirectionMode.LeftToRight) output.Append("\\ltrpar ");
            AppendParagraphFormatting(output, paragraph, fontIds, warnings);

            switch (block)
            {
                case HeadingBlock heading:
                    output.Append("\\b\\fs").Append(Math.Max(24, 40 - heading.Level * 3)).Append(' ');
                    AppendInlines(output, heading.Inlines, fontIds, warnings);
                    break;
                case ParagraphBlock paragraphBlock:
                    AppendInlines(output, paragraphBlock.Inlines, fontIds, warnings);
                    break;
                case QuoteBlock quote:
                    output.Append("\\i ");
                    AppendInlines(output, quote.Inlines, fontIds, warnings);
                    break;
                case ListItemBlock item:
                    AppendUnicode(output, item.Ordered ? "1. " : "• ");
                    AppendInlines(output, item.Inlines, fontIds, warnings);
                    break;
                case CodeBlock code:
                    AppendUnicode(output, code.Text);
                    break;
                case DisplayMathBlock math:
                    AppendUnicode(output, math.Text);
                    break;
                case ThematicBreakBlock:
                    AppendUnicode(output, "———");
                    break;
                case TableBlock table:
                    AppendUnicode(output, string.Join("\t", table.Header.Select(static cell => cell.Inlines.ToPlainText())));
                    output.Append("\\par ");
                    foreach (var row in table.Rows)
                    {
                        AppendUnicode(output, string.Join("\t", row.Select(static cell => cell.Inlines.ToPlainText())));
                        output.Append("\\par ");
                    }
                    break;
                case FigureBlock figure:
                    AppendUnicode(output, figure.Caption);
                    break;
                case FootnoteDefinitionBlock note:
                    AppendUnicode(output, "[" + note.Identifier + "] ");
                    AppendInlines(output, note.Inlines, fontIds, warnings);
                    break;
            }
            output.Append("\\par}");
        }
        output.Append('}');
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    public static RichClipboardImport Import(byte[] bytes)
    {
        var rtf = Encoding.Latin1.GetString(bytes).TrimEnd('\0');
        if (!rtf.Contains("\\rtf", StringComparison.OrdinalIgnoreCase))
            return new RichClipboardImport(string.Empty, "RTF", ["Clipboard RTF payload was malformed; plain text fallback was used when available."]);

        var fonts = ParseFonts(rtf);
        var warnings = new List<string>();
        var output = new StringBuilder();
        var paragraph = new StringBuilder();
        var buffer = new StringBuilder();
        var stack = new Stack<RtfState>();
        var state = new RtfState();
        TextDirectionMode? paragraphDirection = null;
        var index = 0;

        void FlushRun()
        {
            if (buffer.Length == 0) return;
            var text = buffer.ToString();
            buffer.Clear();
            var markdown = EscapeMarkdown(text);
            if (state.Bold && state.Italic) markdown = "***" + markdown + "***";
            else if (state.Bold) markdown = "**" + markdown + "**";
            else if (state.Italic) markdown = "*" + markdown + "*";
            var formatting = StateFormatting(state, fonts);
            if (!IsEmpty(formatting)) markdown = RichMarkdownFormattingCodec.WrapInline(markdown, formatting);
            paragraph.Append(markdown);
        }

        void FlushParagraph()
        {
            FlushRun();
            if (paragraph.Length == 0) return;
            if (paragraphDirection is not null)
            {
                var metadata = RichMarkdownFormattingCodec.CreateBlockMetadata(new RichBlockFormatting(
                    Paragraph: new ParagraphFormatting(Direction: paragraphDirection)));
                output.AppendLine(metadata);
            }
            output.AppendLine(paragraph.ToString().TrimEnd());
            paragraph.Clear();
            paragraphDirection = null;
        }

        while (index < rtf.Length)
        {
            var ch = rtf[index];
            if (ch == '{')
            {
                if (TrySkipDestination(rtf, ref index)) continue;
                FlushRun();
                stack.Push(state);
                index++;
                continue;
            }
            if (ch == '}')
            {
                FlushRun();
                if (stack.Count > 0) state = stack.Pop();
                index++;
                continue;
            }
            if (ch != '\\')
            {
                if (ch is not '\r' and not '\n') buffer.Append(ch);
                index++;
                continue;
            }

            if (index + 1 >= rtf.Length) break;
            var next = rtf[index + 1];
            if (next is '\\' or '{' or '}')
            {
                buffer.Append(next);
                index += 2;
                continue;
            }
            if (next == '\'')
            {
                if (index + 3 < rtf.Length && byte.TryParse(rtf.AsSpan(index + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    buffer.Append(Encoding.GetEncoding(1252).GetString([value]));
                index = Math.Min(rtf.Length, index + 4);
                continue;
            }
            if (next == '~') { buffer.Append('\u00A0'); index += 2; continue; }
            if (next == '-') { buffer.Append('\u00AD'); index += 2; continue; }
            if (next == '_') { buffer.Append('\u2011'); index += 2; continue; }
            if (!char.IsLetter(next)) { index += 2; continue; }

            var wordStart = index + 1;
            var cursor = wordStart;
            while (cursor < rtf.Length && char.IsLetter(rtf[cursor])) cursor++;
            var word = rtf[wordStart..cursor].ToLowerInvariant();
            var negative = cursor < rtf.Length && rtf[cursor] == '-';
            if (negative) cursor++;
            var numberStart = cursor;
            while (cursor < rtf.Length && char.IsDigit(rtf[cursor])) cursor++;
            int? parameter = null;
            if (cursor > numberStart && int.TryParse(rtf[numberStart..cursor], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                parameter = negative ? -parsed : parsed;
            if (cursor < rtf.Length && rtf[cursor] == ' ') cursor++;
            index = cursor;

            switch (word)
            {
                case "b": FlushRun(); state = state with { Bold = parameter != 0 }; break;
                case "i": FlushRun(); state = state with { Italic = parameter != 0 }; break;
                case "ul": FlushRun(); state = state with { Underline = parameter != 0 }; break;
                case "ulnone": FlushRun(); state = state with { Underline = false }; break;
                case "scaps": FlushRun(); state = state with { SmallCaps = parameter != 0 }; break;
                case "rtlch": FlushRun(); state = state with { Rtl = true }; break;
                case "ltrch": FlushRun(); state = state with { Rtl = false }; break;
                case "rtlpar": FlushRun(); paragraphDirection = TextDirectionMode.RightToLeft; break;
                case "ltrpar": FlushRun(); paragraphDirection = TextDirectionMode.LeftToRight; break;
                case "f": FlushRun(); state = state with { FontId = parameter }; break;
                case "fs": FlushRun(); state = state with { FontSizeHalfPoints = parameter }; break;
                case "lang": FlushRun(); state = state with { LanguageId = parameter }; break;
                case "expndtw": FlushRun(); state = state with { TrackingTwips = parameter }; break;
                case "par": FlushParagraph(); break;
                case "line": FlushRun(); paragraph.Append("  \n"); break;
                case "tab": buffer.Append('\t'); break;
                case "emdash": buffer.Append('\u2014'); break;
                case "endash": buffer.Append('\u2013'); break;
                case "bullet": buffer.Append('•'); break;
                case "u" when parameter is not null:
                    var codeUnit = unchecked((char)(short)parameter.Value);
                    buffer.Append(codeUnit);
                    if (index < rtf.Length && rtf[index] == '?') index++;
                    break;
            }
        }

        FlushParagraph();
        if (output.Length == 0 && paragraph.Length > 0) FlushParagraph();
        return new RichClipboardImport(output.ToString().Trim(), "RTF", warnings);
    }

    private static void AppendParagraphFormatting(
        StringBuilder output,
        ParagraphFormatting? paragraph,
        IReadOnlyDictionary<string, int> fontIds,
        ICollection<string> warnings)
    {
        if (paragraph?.CharacterDefaults is { } defaults)
            AppendFormattingControls(output, defaults, fontIds, warnings);
        if (paragraph?.FirstLineIndentPoints is { } first) output.Append("\\fi").Append(ToTwips(first)).Append(' ');
        if (paragraph?.LeftIndentPoints is { } left) output.Append("\\li").Append(ToTwips(left)).Append(' ');
        if (paragraph?.RightIndentPoints is { } right) output.Append("\\ri").Append(ToTwips(right)).Append(' ');
        if (paragraph?.SpaceBeforePoints is { } before) output.Append("\\sb").Append(ToTwips(before)).Append(' ');
        if (paragraph?.SpaceAfterPoints is { } after) output.Append("\\sa").Append(ToTwips(after)).Append(' ');
    }

    private static void AppendInlines(
        StringBuilder output,
        IEnumerable<AstInline> inlines,
        IReadOnlyDictionary<string, int> fontIds,
        ICollection<string> warnings)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case RichSpanInline rich:
                    output.Append('{');
                    AppendFormattingControls(output, rich.Formatting, fontIds, warnings);
                    AppendInlines(output, rich.Children, fontIds, warnings);
                    output.Append('}');
                    break;
                case TextInline text:
                    AppendUnicode(output, text.Text);
                    break;
                case StrongInline strong:
                    output.Append("{\\b "); AppendInlines(output, strong.Children, fontIds, warnings); output.Append('}');
                    break;
                case EmphasisInline emphasis:
                    output.Append("{\\i "); AppendInlines(output, emphasis.Children, fontIds, warnings); output.Append('}');
                    break;
                case CodeInline code:
                    AppendUnicode(output, code.Text);
                    break;
                case LinkInline link:
                    AppendInlines(output, link.Label, fontIds, warnings);
                    AppendUnicode(output, " (" + link.Url + ")");
                    break;
                case MathInline math:
                    AppendUnicode(output, math.Text);
                    break;
                case FootnoteReferenceInline note:
                    AppendUnicode(output, "[^" + note.Identifier + "]");
                    break;
                case CitationInline citation:
                    AppendUnicode(output, "[" + citation.Key + (string.IsNullOrWhiteSpace(citation.Locator) ? string.Empty : ", " + citation.Locator) + "]");
                    break;
                case CrossReferenceInline reference:
                    AppendUnicode(output, "[" + reference.Identifier + "]");
                    break;
            }
        }
    }

    private static void AppendFormattingControls(
        StringBuilder output,
        CharacterFormatting formatting,
        IReadOnlyDictionary<string, int> fontIds,
        ICollection<string> warnings)
    {
        if (formatting.Font is { Family.Length: > 0 } font && fontIds.TryGetValue(font.Family, out var fontId)) output.Append("\\f").Append(fontId).Append(' ');
        if (formatting.FontSizePoints is { } size && size > 0) output.Append("\\fs").Append((int)Math.Round(size * 2)).Append(' ');
        if (formatting.Bold == true) output.Append("\\b ");
        if (formatting.Italic == true) output.Append("\\i ");
        if (formatting.Underline == true) output.Append("\\ul ");
        if (formatting.SmallCaps == true) output.Append("\\scaps ");
        if (formatting.Direction == TextDirectionMode.RightToLeft) output.Append("\\rtlch ");
        else if (formatting.Direction == TextDirectionMode.LeftToRight) output.Append("\\ltrch ");
        if (LanguageToLcid(formatting.Language) is { } lcid) output.Append("\\lang").Append(lcid).Append(' ');
        if (formatting.TrackingEm is { } tracking && formatting.FontSizePoints is { } points)
            output.Append("\\expndtw").Append((int)Math.Round(tracking * points * 20)).Append(' ');
        if (formatting.OpenTypeFeatures is { Count: > 0 }) warnings.Add("RTF clipboard output cannot preserve arbitrary OpenType feature tags; HTML and TypeScribe rich Markdown retain them.");
        if (formatting.VariableAxes is { Count: > 0 }) warnings.Add("RTF clipboard output cannot preserve variable-font axis values; HTML and TypeScribe rich Markdown retain them.");
    }

    private static HashSet<string> CollectFonts(DocumentAst ast)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Times New Roman" };
        foreach (var block in ast.Blocks)
        {
            if (block.Formatting?.Paragraph?.CharacterDefaults?.Font is { Family.Length: > 0 } font) result.Add(font.Family);
            foreach (var inline in BlockInlines(block)) CollectFonts(inline, result);
        }
        return result;
    }

    private static IEnumerable<AstInline> BlockInlines(AstBlock block)
        => block switch
        {
            HeadingBlock heading => heading.Inlines,
            ParagraphBlock paragraph => paragraph.Inlines,
            QuoteBlock quote => quote.Inlines,
            ListItemBlock item => item.Inlines,
            FootnoteDefinitionBlock note => note.Inlines,
            TableBlock table => table.Header.Concat(table.Rows.SelectMany(static row => row)).SelectMany(static cell => cell.Inlines),
            _ => []
        };

    private static void CollectFonts(AstInline inline, ISet<string> result)
    {
        switch (inline)
        {
            case RichSpanInline rich:
                if (rich.Formatting.Font is { Family.Length: > 0 } font) result.Add(font.Family);
                foreach (var child in rich.Children) CollectFonts(child, result);
                break;
            case StrongInline strong:
                foreach (var child in strong.Children) CollectFonts(child, result);
                break;
            case EmphasisInline emphasis:
                foreach (var child in emphasis.Children) CollectFonts(child, result);
                break;
            case LinkInline link:
                foreach (var child in link.Label) CollectFonts(child, result);
                break;
        }
    }

    private static Dictionary<int, string> ParseFonts(string rtf)
    {
        var result = new Dictionary<int, string>();
        var fontTable = rtf.IndexOf("{\\fonttbl", StringComparison.OrdinalIgnoreCase);
        if (fontTable < 0) return result;
        var end = FindGroupEnd(rtf, fontTable);
        if (end <= fontTable) return result;
        var table = rtf[fontTable..end];
        foreach (Match match in FontRegex.Matches(table))
        {
            if (int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                var name = match.Groups["name"].Value.Trim();
                if (name.Length > 0) result[id] = name;
            }
        }
        return result;
    }

    private static bool TrySkipDestination(string rtf, ref int index)
    {
        var probes = new[] { "{\\fonttbl", "{\\colortbl", "{\\stylesheet", "{\\info", "{\\pict", "{\\object", "{\\*" };
        foreach (var probe in probes)
        {
            if (!rtf.AsSpan(index).StartsWith(probe.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
            var end = FindGroupEnd(rtf, index);
            index = end > index ? end : index + 1;
            return true;
        }
        return false;
    }

    private static int FindGroupEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i + 1;
        }
        return text.Length;
    }

    private static CharacterFormatting StateFormatting(RtfState state, IReadOnlyDictionary<int, string> fonts)
    {
        FontReference? font = null;
        if (state.FontId is { } id && fonts.TryGetValue(id, out var family)) font = new FontReference(family);
        return new CharacterFormatting(
            Font: font,
            FontSizePoints: state.FontSizeHalfPoints is { } size ? size / 2d : null,
            Underline: state.Underline ? true : null,
            SmallCaps: state.SmallCaps ? true : null,
            TrackingEm: state.TrackingTwips is { } tracking && state.FontSizeHalfPoints is { } fs && fs > 0 ? tracking / (fs / 2d * 20d) : null,
            Language: LcidToLanguage(state.LanguageId),
            Direction: state.Rtl ? TextDirectionMode.RightToLeft : null);
    }

    private static bool IsEmpty(CharacterFormatting value)
        => value.Font is null && value.FontSizePoints is null && value.Underline is null && value.SmallCaps is null && value.TrackingEm is null && value.Language is null && value.Direction is null;

    private static int ToTwips(double points) => (int)Math.Round(points * 20);

    private static int? LanguageToLcid(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        try { return CultureInfo.GetCultureInfo(language).LCID; }
        catch (CultureNotFoundException) { return null; }
    }

    private static string? LcidToLanguage(int? lcid)
    {
        if (lcid is null or <= 0) return null;
        try { return CultureInfo.GetCultureInfo(lcid.Value).Name; }
        catch (CultureNotFoundException) { return null; }
    }

    private static void AppendUnicode(StringBuilder output, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': output.Append("\\\\"); break;
                case '{': output.Append("\\{"); break;
                case '}': output.Append("\\}"); break;
                case '\n': output.Append("\\line "); break;
                case '\t': output.Append("\\tab "); break;
                default:
                    if (ch is >= ' ' and <= '~') output.Append(ch);
                    else output.Append("\\u").Append(unchecked((short)ch)).Append('?');
                    break;
            }
        }
    }

    private static string EscapeAscii(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("{", "\\{", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal);

    private static string EscapeMarkdown(string value)
    {
        var output = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch is '\\' or '*' or '_' or '[' or ']') output.Append('\\');
            output.Append(ch);
        }
        return output.ToString();
    }

    private sealed record RtfState(
        bool Bold = false,
        bool Italic = false,
        bool Underline = false,
        bool SmallCaps = false,
        bool Rtl = false,
        int? FontId = null,
        int? FontSizeHalfPoints = null,
        int? LanguageId = null,
        int? TrackingTwips = null);
}