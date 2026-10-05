using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

internal static class HtmlClipboardCodec
{
    private static readonly Regex TokenRegex = new("<[^>]+>|[^<]+", RegexOptions.CultureInvariant);
    private static readonly Regex AttributeRegex = new("(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)(?:\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s>]+)))?", RegexOptions.CultureInvariant);
    private static readonly Regex FeatureRegex = new("[\"']?(?<tag>[A-Za-z0-9]{4})[\"']?\\s+(?<value>-?[0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant);

    public static RichClipboardImport Import(byte[] bytes)
    {
        var warnings = new List<string>();
        var html = ExtractFragment(Encoding.UTF8.GetString(bytes));
        var markdown = ConvertHtml(html, warnings);
        return new RichClipboardImport(markdown.Trim(), "HTML", warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string ConvertHtml(string html, ICollection<string> warnings)
    {
        var output = new StringBuilder();
        var inlineStack = new Stack<InlineFrame>();
        var lists = new Stack<bool>();
        var state = new InlineState();
        var table = new TableState();
        var quoteDepth = 0;
        var preformatted = false;

        foreach (Match match in TokenRegex.Matches(html))
        {
            var token = match.Value;
            if (!token.StartsWith('<'))
            {
                var text = WebUtility.HtmlDecode(token);
                if (!preformatted) text = CollapseWhitespace(text);
                if (text.Length == 0) continue;
                AppendText(CurrentTarget(output, table), text, state);
                continue;
            }

            if (token.StartsWith("<!--", StringComparison.Ordinal)) continue;
            var closing = token.StartsWith("</", StringComparison.Ordinal);
            var tag = TagName(token);
            if (tag.Length == 0) continue;
            var attrs = ParseAttributes(token);
            var selfClosing = token.EndsWith("/>", StringComparison.Ordinal) || tag is "br" or "img" or "hr" or "meta" or "link";

            if (closing)
            {
                if (tag is "b" or "strong" or "i" or "em" or "u" or "small" or "span" or "font" or "a")
                    PopInline(tag, inlineStack, ref state);
                else if (tag is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li")
                    EnsureNewline(CurrentTarget(output, table));
                else if (tag == "blockquote")
                {
                    quoteDepth = Math.Max(0, quoteDepth - 1);
                    EnsureNewline(output);
                }
                else if (tag is "ol" or "ul")
                {
                    if (lists.Count > 0) lists.Pop();
                    EnsureNewline(output);
                }
                else if (tag == "pre")
                {
                    preformatted = false;
                    EnsureNewline(output);
                    output.AppendLine("```");
                }
                else if (tag is "td" or "th") table.EndCell();
                else if (tag == "tr") table.EndRow();
                else if (tag == "table")
                {
                    table.Emit(output);
                    table.Reset();
                }
                continue;
            }

            if (tag is "ol" or "ul")
            {
                lists.Push(tag == "ol");
                EnsureNewline(output);
                continue;
            }
            if (tag == "table")
            {
                EnsureNewline(output);
                table.Begin();
                continue;
            }
            if (tag == "tr") { table.BeginRow(); continue; }
            if (tag is "td" or "th") { table.BeginCell(tag == "th"); continue; }
            if (tag == "br") { CurrentTarget(output, table).AppendLine(); continue; }
            if (tag == "hr") { EnsureNewline(output); output.AppendLine("---"); continue; }
            if (tag == "pre")
            {
                EnsureNewline(output);
                output.AppendLine("```");
                preformatted = true;
                continue;
            }
            if (tag == "img")
            {
                var source = Get(attrs, "src") ?? string.Empty;
                var alt = Get(attrs, "alt") ?? "Image";
                CurrentTarget(output, table).Append("![").Append(EscapeMarkdown(alt)).Append("](").Append(source).Append(')');
                continue;
            }
            if (tag == "blockquote")
            {
                EnsureNewline(output);
                quoteDepth++;
                for (var i = 0; i < quoteDepth; i++) output.Append("> ");
                AppendBlockMetadata(output, attrs, warnings);
                continue;
            }
            if (tag is "p" or "div")
            {
                EnsureNewline(CurrentTarget(output, table));
                AppendBlockMetadata(CurrentTarget(output, table), attrs, warnings);
                continue;
            }
            if (tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6')
            {
                EnsureNewline(CurrentTarget(output, table));
                AppendBlockMetadata(CurrentTarget(output, table), attrs, warnings);
                CurrentTarget(output, table).Append(new string('#', tag[1] - '0')).Append(' ');
                continue;
            }
            if (tag == "li")
            {
                EnsureNewline(CurrentTarget(output, table));
                AppendBlockMetadata(CurrentTarget(output, table), attrs, warnings);
                CurrentTarget(output, table).Append(lists.Count > 0 && lists.Peek() ? "1. " : "- ");
                continue;
            }

            if (tag is "b" or "strong" or "i" or "em" or "u" or "small" or "span" or "font" or "a")
            {
                inlineStack.Push(new InlineFrame(tag, state));
                state = ApplyInline(tag, attrs, state, warnings);
                if (selfClosing) PopInline(tag, inlineStack, ref state);
            }
        }

        if (table.Active)
        {
            table.Emit(output);
            table.Reset();
        }
        return NormalizeBlankLines(output.ToString());
    }

    private static InlineState ApplyInline(
        string tag,
        IReadOnlyDictionary<string, string> attrs,
        InlineState current,
        ICollection<string> warnings)
    {
        var next = current;
        if (tag is "b" or "strong") next = next with { Bold = true };
        if (tag is "i" or "em") next = next with { Italic = true };
        if (tag == "u") next = next with { Formatting = next.Formatting with { Underline = true } };
        if (tag == "small") next = next with { Formatting = next.Formatting with { FontSizePoints = 9 } };
        if (tag == "a") next = next with { Link = Get(attrs, "href") };
        if (tag == "font")
        {
            var face = Get(attrs, "face");
            if (!string.IsNullOrWhiteSpace(face))
                next = next with { Formatting = next.Formatting with { Font = new FontReference(CleanFontFamily(face)) } };
        }

        var lang = Get(attrs, "lang") ?? Get(attrs, "xml:lang");
        var dir = Get(attrs, "dir");
        var style = Get(attrs, "style");
        var formatting = ApplyStyle(next.Formatting, style, warnings);
        if (!string.IsNullOrWhiteSpace(lang)) formatting = formatting with { Language = lang.Trim() };
        if (string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase)) formatting = formatting with { Direction = TextDirectionMode.RightToLeft };
        else if (string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase)) formatting = formatting with { Direction = TextDirectionMode.LeftToRight };

        var bold = next.Bold;
        var italic = next.Italic;
        if (formatting.Bold == true) { bold = true; formatting = formatting with { Bold = null }; }
        if (formatting.Italic == true) { italic = true; formatting = formatting with { Italic = null }; }
        return next with { Bold = bold, Italic = italic, Formatting = formatting };
    }

    private static CharacterFormatting ApplyStyle(CharacterFormatting source, string? style, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(style)) return source;
        var result = source;
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = declaration.IndexOf(':');
            if (split <= 0) continue;
            var name = declaration[..split].Trim().ToLowerInvariant();
            var value = declaration[(split + 1)..].Trim();
            switch (name)
            {
                case "font-family":
                    var family = value.Split(',')[0].Trim();
                    if (family.Length > 0) result = result with { Font = new FontReference(CleanFontFamily(family)) };
                    break;
                case "font-size":
                    if (TryCssSize(value, out var points)) result = result with { FontSizePoints = points };
                    break;
                case "font-weight":
                    if (value.Equals("bold", StringComparison.OrdinalIgnoreCase) || (int.TryParse(value, out var weight) && weight >= 600))
                        result = result with { Bold = true };
                    break;
                case "font-style":
                    if (value.Contains("italic", StringComparison.OrdinalIgnoreCase) || value.Contains("oblique", StringComparison.OrdinalIgnoreCase))
                        result = result with { Italic = true };
                    break;
                case "text-decoration":
                case "text-decoration-line":
                    if (value.Contains("underline", StringComparison.OrdinalIgnoreCase)) result = result with { Underline = true };
                    break;
                case "font-variant":
                case "font-variant-caps":
                    if (value.Contains("small-caps", StringComparison.OrdinalIgnoreCase)) result = result with { SmallCaps = true };
                    break;
                case "letter-spacing":
                    if (TryEm(value, out var tracking)) result = result with { TrackingEm = tracking };
                    else warnings.Add("HTML letter-spacing that is not expressed in em could not be preserved exactly.");
                    break;
                case "font-kerning":
                    result = result with { Kerning = !value.Equals("none", StringComparison.OrdinalIgnoreCase) };
                    break;
                case "font-feature-settings":
                    var features = ParseFeatures(value);
                    if (features.Count > 0) result = result with { OpenTypeFeatures = features };
                    break;
                case "font-variation-settings":
                    var axes = ParseAxes(value);
                    if (axes.Count > 0) result = result with { VariableAxes = axes };
                    break;
                case "direction":
                    if (value.Equals("rtl", StringComparison.OrdinalIgnoreCase)) result = result with { Direction = TextDirectionMode.RightToLeft };
                    else if (value.Equals("ltr", StringComparison.OrdinalIgnoreCase)) result = result with { Direction = TextDirectionMode.LeftToRight };
                    break;
            }
        }
        return result;
    }

    private static void AppendBlockMetadata(StringBuilder output, IReadOnlyDictionary<string, string> attrs, ICollection<string> warnings)
    {
        var language = Get(attrs, "lang") ?? Get(attrs, "xml:lang");
        var dir = Get(attrs, "dir");
        var character = ApplyStyle(new CharacterFormatting(), Get(attrs, "style"), warnings);
        var direction = string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase) || character.Direction == TextDirectionMode.RightToLeft
            ? TextDirectionMode.RightToLeft
            : string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase) || character.Direction == TextDirectionMode.LeftToRight
                ? TextDirectionMode.LeftToRight
                : (TextDirectionMode?)null;
        character = character with { Direction = null, Language = null };
        if (string.IsNullOrWhiteSpace(language) && direction is null && IsEmpty(character)) return;
        var paragraph = new ParagraphFormatting(
            CharacterDefaults: IsEmpty(character) ? null : character,
            Direction: direction,
            Language: string.IsNullOrWhiteSpace(language) ? null : language.Trim());
        output.AppendLine(RichMarkdownFormattingCodec.CreateBlockMetadata(new RichBlockFormatting(Paragraph: paragraph)));
    }

    private static void AppendText(StringBuilder output, string text, InlineState state)
    {
        var markdown = EscapeMarkdown(text);
        if (state.Bold && state.Italic) markdown = "***" + markdown + "***";
        else if (state.Bold) markdown = "**" + markdown + "**";
        else if (state.Italic) markdown = "*" + markdown + "*";
        if (!string.IsNullOrWhiteSpace(state.Link)) markdown = "[" + markdown + "](" + state.Link + ")";
        if (!IsEmpty(state.Formatting)) markdown = RichMarkdownFormattingCodec.WrapInline(markdown, state.Formatting);
        output.Append(markdown);
    }

    private static void PopInline(string tag, Stack<InlineFrame> stack, ref InlineState state)
    {
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            state = frame.Previous;
            if (string.Equals(frame.Tag, tag, StringComparison.Ordinal)) break;
        }
    }

    private static Dictionary<string, string> ParseAttributes(string token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var firstSpace = token.IndexOfAny([' ', '\t', '\r', '\n']);
        if (firstSpace < 0) return result;
        var content = token[firstSpace..].TrimEnd('>', '/').Trim();
        foreach (Match match in AttributeRegex.Matches(content))
        {
            var name = match.Groups["name"].Value;
            if (name.Length == 0) continue;
            var value = match.Groups["dq"].Success ? match.Groups["dq"].Value
                : match.Groups["sq"].Success ? match.Groups["sq"].Value
                : match.Groups["bare"].Success ? match.Groups["bare"].Value
                : string.Empty;
            result[name] = WebUtility.HtmlDecode(value);
        }
        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string> attrs, string name)
        => attrs.TryGetValue(name, out var value) ? value : null;

    private static string TagName(string token)
    {
        var index = token.StartsWith("</", StringComparison.Ordinal) ? 2 : 1;
        while (index < token.Length && char.IsWhiteSpace(token[index])) index++;
        var start = index;
        while (index < token.Length && (char.IsLetterOrDigit(token[index]) || token[index] is ':' or '-')) index++;
        return token[start..index].ToLowerInvariant();
    }

    private static string ExtractFragment(string html)
    {
        html = html.TrimEnd('\0');
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        var start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) return html[(start + startMarker.Length)..end];

        var headerEnd = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        return headerEnd >= 0 ? html[headerEnd..] : html;
    }

    private static IReadOnlyList<OpenTypeFeatureSetting> ParseFeatures(string value)
        => FeatureRegex.Matches(value).Select(match => new OpenTypeFeatureSetting(
            match.Groups["tag"].Value,
            int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1)).ToArray();

    private static IReadOnlyList<VariableFontAxisSetting> ParseAxes(string value)
        => FeatureRegex.Matches(value).Select(match => new VariableFontAxisSetting(
            match.Groups["tag"].Value,
            double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)).ToArray();

    private static bool TryCssSize(string value, out double points)
    {
        points = 0;
        value = value.Trim().ToLowerInvariant();
        var multiplier = value.EndsWith("px", StringComparison.Ordinal) ? .75 : 1;
        var suffix = value.EndsWith("pt", StringComparison.Ordinal) || value.EndsWith("px", StringComparison.Ordinal) ? 2 : 0;
        return double.TryParse(suffix > 0 ? value[..^suffix] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out points) && (points *= multiplier) > 0;
    }

    private static bool TryEm(string value, out double em)
    {
        em = 0;
        value = value.Trim().ToLowerInvariant();
        if (!value.EndsWith("em", StringComparison.Ordinal)) return false;
        return double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out em);
    }

    private static string CleanFontFamily(string value)
        => value.Trim().Trim('"', '\'').Trim();

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var whitespace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) { whitespace = true; continue; }
            if (whitespace && builder.Length > 0) builder.Append(' ');
            whitespace = false;
            builder.Append(ch);
        }
        if (whitespace && builder.Length > 0) builder.Append(' ');
        return builder.ToString();
    }

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

    private static void EnsureNewline(StringBuilder output)
    {
        if (output.Length == 0) return;
        if (output[^1] != '\n') output.AppendLine();
    }

    private static string NormalizeBlankLines(string value)
    {
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        while (value.Contains("\n\n\n", StringComparison.Ordinal)) value = value.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return value.Trim();
    }

    private static bool IsEmpty(CharacterFormatting value)
        => value.StyleId is null && value.Font is null && value.FontSizePoints is null && value.Bold is null && value.Italic is null &&
           value.Underline is null && value.SmallCaps is null && value.Ligatures is null && value.Kerning is null && value.TrackingEm is null &&
           value.BaselineShiftPoints is null && value.ColorHex is null && value.Language is null && value.Script is null && value.Direction is null &&
           value.OpenTypeFeatures is null && value.VariableAxes is null;

    private static StringBuilder CurrentTarget(StringBuilder output, TableState table)
        => table.Active && table.CurrentCell is not null ? table.CurrentCell : output;

    private sealed record InlineState(
        bool Bold = false,
        bool Italic = false,
        string? Link = null,
        CharacterFormatting? Rich = null)
    {
        public CharacterFormatting Formatting { get; init; } = Rich ?? new CharacterFormatting();
    }

    private sealed record InlineFrame(string Tag, InlineState Previous);

    private sealed class TableState
    {
        private readonly List<List<string>> _rows = [];
        private List<string>? _row;
        private bool _currentHeader;
        public bool Active { get; private set; }
        public StringBuilder? CurrentCell { get; private set; }

        public void Begin() => Active = true;
        public void BeginRow() { if (!Active) Begin(); _row = []; }
        public void BeginCell(bool header)
        {
            if (_row is null) BeginRow();
            CurrentCell = new StringBuilder();
            _currentHeader = header;
        }
        public void EndCell()
        {
            if (_row is null || CurrentCell is null) return;
            _row.Add(CurrentCell.ToString().Trim());
            CurrentCell = null;
        }
        public void EndRow()
        {
            EndCell();
            if (_row is { Count: > 0 }) _rows.Add(_row);
            _row = null;
        }
        public void Emit(StringBuilder output)
        {
            EndRow();
            if (_rows.Count == 0) return;
            EnsureNewline(output);
            var width = _rows.Max(static row => row.Count);
            var header = _rows[0].Concat(Enumerable.Repeat(string.Empty, width - _rows[0].Count)).ToArray();
            output.Append("| ").Append(string.Join(" | ", header.Select(EscapeCell))).AppendLine(" |");
            output.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).AppendLine(" |");
            foreach (var row in _rows.Skip(1))
            {
                var cells = row.Concat(Enumerable.Repeat(string.Empty, width - row.Count)).ToArray();
                output.Append("| ").Append(string.Join(" | ", cells.Select(EscapeCell))).AppendLine(" |");
            }
        }
        public void Reset()
        {
            _rows.Clear(); _row = null; CurrentCell = null; Active = false; _currentHeader = false;
        }
        private static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}