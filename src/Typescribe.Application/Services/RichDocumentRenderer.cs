using System.Globalization;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Converts Markdown-first rich formatting into renderer-safe semantic tokens. The established
/// AdvancedDocumentRenderer remains responsible for normal Markdown, references, tables and
/// figures; this layer only adds typography/language groups around those existing semantics.
/// </summary>
public sealed class RichDocumentRenderer : IDocumentRenderer
{
    private const string TokenPrefix = "TYPESCRIBERICHRENDER";
    private readonly AdvancedDocumentRenderer _inner = new();

    public string RenderPreview(DocumentAst document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _inner.RenderPreview(Rewrite(document, latex: false).Document);
    }

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);

        var rewritten = Rewrite(document, latex: true);
        var latex = _inner.RenderLatex(rewritten.Document, title, style);
        foreach (var replacement in rewritten.Replacements)
            latex = latex.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);

        if (rewritten.Languages.Count > 0 || rewritten.RequiresBidi)
            latex = InjectMultilingualPreamble(latex, rewritten.Languages);
        return latex;
    }

    private static RewriteResult Rewrite(DocumentAst source, bool latex)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokenNumber = 0;
        var requiresBidi = false;

        (string Open, string Close) CreateTokens(CharacterFormatting formatting)
        {
            var id = tokenNumber.ToString("D6", CultureInfo.InvariantCulture);
            tokenNumber++;
            var openToken = TokenPrefix + "OPEN" + id;
            var closeToken = TokenPrefix + "CLOSE" + id;
            if (!latex)
            {
                replacements[openToken] = string.Empty;
                replacements[closeToken] = string.Empty;
                return (openToken, closeToken);
            }

            var rendered = RenderCharacterGroup(formatting, languages, ref requiresBidi);
            replacements[openToken] = rendered.Open;
            replacements[closeToken] = rendered.Close;
            return (openToken, closeToken);
        }

        IReadOnlyList<AstInline> RewriteInlines(IReadOnlyList<AstInline> inlines)
        {
            var output = new List<AstInline>();
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case RichSpanInline rich:
                    {
                        if (!latex)
                        {
                            output.AddRange(RewriteInlines(rich.Children));
                            break;
                        }

                        var tokens = CreateTokens(rich.Formatting);
                        output.Add(new TextInline(tokens.Open));
                        output.AddRange(RewriteInlines(rich.Children));
                        output.Add(new TextInline(tokens.Close));
                        break;
                    }
                    case StrongInline strong:
                        output.Add(strong with { Children = RewriteInlines(strong.Children) });
                        break;
                    case EmphasisInline emphasis:
                        output.Add(emphasis with { Children = RewriteInlines(emphasis.Children) });
                        break;
                    case LinkInline link:
                        output.Add(link with { Label = RewriteInlines(link.Label) });
                        break;
                    default:
                        output.Add(inline);
                        break;
                }
            }
            return output;
        }

        IReadOnlyList<AstInline> ApplyParagraphFormatting(
            IReadOnlyList<AstInline> inlines,
            RichBlockFormatting? formatting)
        {
            var rewritten = RewriteInlines(inlines);
            if (!latex) return rewritten;

            var character = EffectiveCharacterFormatting(formatting?.Paragraph, inlines.ToPlainText());
            if (character is null) return rewritten;

            var tokens = CreateTokens(character);
            return [new TextInline(tokens.Open), .. rewritten, new TextInline(tokens.Close)];
        }

        TableCell RewriteCell(TableCell cell)
            => cell with { Inlines = ApplyParagraphFormatting(cell.Inlines, formatting: null) };

        AstBlock RewriteBlock(AstBlock block)
            => block switch
            {
                HeadingBlock heading => heading with
                {
                    Inlines = ApplyParagraphFormatting(heading.Inlines, heading.Formatting)
                },
                ParagraphBlock paragraph => paragraph with
                {
                    Inlines = ApplyParagraphFormatting(paragraph.Inlines, paragraph.Formatting)
                },
                QuoteBlock quote => quote with
                {
                    Inlines = ApplyParagraphFormatting(quote.Inlines, quote.Formatting)
                },
                ListItemBlock item => item with
                {
                    Inlines = ApplyParagraphFormatting(item.Inlines, item.Formatting)
                },
                FootnoteDefinitionBlock footnote => footnote with
                {
                    Inlines = ApplyParagraphFormatting(footnote.Inlines, footnote.Formatting)
                },
                TableBlock table => table with
                {
                    Header = table.Header.Select(RewriteCell).ToArray(),
                    Rows = table.Rows.Select(row => (IReadOnlyList<TableCell>)row.Select(RewriteCell).ToArray()).ToArray()
                },
                _ => block
            };

        var blocks = source.Blocks.Select(RewriteBlock).ToArray();
        return new RewriteResult(new DocumentAst(blocks), replacements, languages, requiresBidi);
    }

    private static CharacterFormatting? EffectiveCharacterFormatting(ParagraphFormatting? paragraph, string text)
    {
        var defaults = paragraph?.CharacterDefaults ?? new CharacterFormatting();
        var language = paragraph?.Language ?? defaults.Language;
        var script = paragraph?.Script ?? defaults.Script;
        var direction = paragraph?.Direction ?? defaults.Direction;

        var hasExplicitValues = defaults.Font is not null || defaults.FontSizePoints is not null ||
                                defaults.Bold is not null || defaults.Italic is not null || defaults.Underline is not null ||
                                defaults.SmallCaps is not null || defaults.Ligatures is not null || defaults.Kerning is not null ||
                                defaults.TrackingEm is not null || defaults.BaselineShiftPoints is not null ||
                                defaults.ColorHex is not null || defaults.OpenTypeFeatures is not null ||
                                defaults.VariableAxes is not null || language is not null || script is not null || direction is not null;

        var effectiveScript = script is null or ScriptMode.Auto
            ? UnicodeScriptClassifier.DetectScript(text, language)
            : script.Value;
        var inferredArabicScript = effectiveScript is ScriptMode.Arabic or ScriptMode.UrduNastaliq or ScriptMode.Persian;

        // Plain Markdown has no rich formatting metadata. Still recognize Arabic-script blocks so
        // publishing gets the same Unicode-driven language/direction behavior as the editor.
        if (!hasExplicitValues && !inferredArabicScript)
            return null;

        var effectiveDirection = direction is null or TextDirectionMode.Auto
            ? UnicodeScriptClassifier.DetectDirection(text)
            : direction.Value;

        return defaults with
        {
            Language = language,
            Script = effectiveScript == ScriptMode.Auto ? script : effectiveScript,
            Direction = effectiveDirection == TextDirectionMode.Auto ? direction : effectiveDirection
        };
    }

    private static RenderedGroup RenderCharacterGroup(
        CharacterFormatting formatting,
        ISet<string> languages,
        ref bool requiresBidi)
    {
        var open = new StringBuilder("{");
        var close = new StringBuilder("}");

        var babelLanguage = BabelLanguage(formatting.Language, formatting.Script);
        if (babelLanguage is not null)
        {
            languages.Add(babelLanguage);
            open.Append("\\foreignlanguage{").Append(babelLanguage).Append("}{");
            close.Insert(0, '}');
        }

        var script = EffectiveScript(formatting);
        if (formatting.Direction == TextDirectionMode.RightToLeft ||
            script is ScriptMode.Arabic or ScriptMode.UrduNastaliq or ScriptMode.Persian)
            requiresBidi = true;

        if (formatting.Font is { } font)
            AppendFontSpec(open, font, formatting, script);
        else
            AppendScriptFallbackFont(open, script);

        if (formatting.FontSizePoints is { } size && size > 0)
        {
            var clamped = Math.Clamp(size, 5, 240);
            open.Append("\\fontsize{").Append(Format(clamped)).Append("pt}{")
                .Append(Format(clamped * 1.2)).Append("pt}\\selectfont ");
        }

        if (formatting.Bold == true) open.Append("\\bfseries ");
        if (formatting.Italic == true) open.Append("\\itshape ");
        if (formatting.SmallCaps == true) open.Append("\\scshape ");
        if (formatting.Underline == true)
        {
            open.Append("\\underline{");
            close.Insert(0, '}');
        }

        if (formatting.BaselineShiftPoints is { } shift && Math.Abs(shift) > .001)
        {
            open.Append("\\raisebox{").Append(Format(shift)).Append("pt}{");
            close.Insert(0, '}');
        }

        return new RenderedGroup(open.ToString(), close.ToString());
    }

    private static void AppendFontSpec(
        StringBuilder output,
        FontReference font,
        CharacterFormatting formatting,
        ScriptMode script)
    {
        var options = new List<string>();
        if (script is ScriptMode.Arabic or ScriptMode.UrduNastaliq or ScriptMode.Persian)
        {
            options.Add("Renderer=HarfBuzz");
            options.Add("Script=Arabic");
        }

        if (formatting.Ligatures == false) options.Add("Ligatures=NoCommon");
        else if (formatting.Ligatures == true) options.Add("Ligatures=Common");
        if (formatting.Kerning == false) options.Add("Kerning=Off");
        if (formatting.SmallCaps == true) options.Add("Letters=SmallCaps");

        foreach (var feature in formatting.OpenTypeFeatures ?? [])
        {
            if (!ValidOpenTypeTag(feature.Tag)) continue;
            var sign = feature.Value == 0 ? '-' : '+';
            options.Add($"RawFeature={{{sign}{feature.Tag}}}");
        }

        string target;
        if (font.Source == FontSourceKind.Project && !string.IsNullOrWhiteSpace(font.ProjectPath))
        {
            var normalized = font.ProjectPath!.Replace('\\', '/').Trim();
            var slash = normalized.LastIndexOf('/');
            if (slash >= 0)
            {
                var directory = normalized[..(slash + 1)];
                target = normalized[(slash + 1)..];
                if (directory.Length > 0) options.Insert(0, "Path={" + EscapeFontSpecValue(directory) + "}");
            }
            else
            {
                target = normalized;
            }
        }
        else
        {
            target = font.Family;
        }

        output.Append("\\fontspec");
        if (options.Count > 0) output.Append('[').Append(string.Join(',', options)).Append(']');
        output.Append('{').Append(EscapeFontSpecValue(target)).Append("} ");
    }

    private static void AppendScriptFallbackFont(StringBuilder output, ScriptMode script)
    {
        string[] candidates = script switch
        {
            ScriptMode.UrduNastaliq =>
            [
                "Noto Nastaliq Urdu",
                "Awami Nastaliq",
                "Jameel Noori Nastaleeq",
                "Nafees Nastaleeq",
                "Urdu Typesetting",
                "Mehr Nastaliq Web",
                "Noto Naskh Arabic",
                "Amiri"
            ],
            ScriptMode.Persian =>
            [
                "Noto Naskh Arabic",
                "Vazirmatn",
                "Amiri",
                "Noto Sans Arabic"
            ],
            ScriptMode.Arabic =>
            [
                "Noto Naskh Arabic",
                "Amiri",
                "Scheherazade New",
                "Noto Sans Arabic"
            ],
            _ => []
        };

        if (candidates.Length == 0) return;

        foreach (var candidate in candidates)
        {
            var escaped = EscapeFontSpecValue(candidate);
            output.Append("\\IfFontExistsTF{").Append(escaped).Append("}{")
                .Append("\\fontspec[Renderer=HarfBuzz,Script=Arabic,BoldFont={")
                .Append(escaped)
                .Append("},ItalicFont={")
                .Append(escaped)
                .Append("},BoldItalicFont={")
                .Append(escaped)
                .Append("}]{")
                .Append(escaped)
                .Append("} ")
                .Append("}{");
        }

        output.Append('}', candidates.Length);
        output.Append(' ');
    }

    private static string InjectMultilingualPreamble(string latex, IReadOnlySet<string> languages)
    {
        const string fontspec = "\\usepackage{fontspec}";
        var lines = new List<string>
        {
            "% Typescribe multilingual LuaLaTeX support",
            "\\usepackage[bidi=basic]{babel}"
        };
        foreach (var language in languages.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
            lines.Add($"\\babelprovide[import]{{{language}}}");

        var block = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (latex.Contains("\\usepackage", StringComparison.Ordinal) &&
            latex.Contains("{babel}", StringComparison.Ordinal))
        {
            // Do not load babel twice. Existing project-level babel configuration stays authoritative;
            // language declarations are still safe to append after fontspec.
            var declarations = string.Join(Environment.NewLine, lines.Skip(2));
            if (declarations.Length == 0) return latex;
            var position = latex.IndexOf(fontspec, StringComparison.Ordinal);
            if (position < 0) return declarations + Environment.NewLine + latex;
            position += fontspec.Length;
            return latex.Insert(position, Environment.NewLine + declarations);
        }

        var index = latex.IndexOf(fontspec, StringComparison.Ordinal);
        if (index < 0) return block + latex;
        return latex.Insert(index + fontspec.Length, Environment.NewLine + block.TrimEnd());
    }

    private static ScriptMode EffectiveScript(CharacterFormatting formatting)
    {
        if (formatting.Script is { } script && script != ScriptMode.Auto) return script;
        return UnicodeScriptClassifier.DetectScript(null, formatting.Language);
    }

    private static string? BabelLanguage(string? language, ScriptMode? script)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            var normalized = language.Trim().ToLowerInvariant();
            if (normalized.StartsWith("ur", StringComparison.Ordinal)) return "urdu";
            if (normalized.StartsWith("fa", StringComparison.Ordinal)) return "persian";
            if (normalized.StartsWith("ar", StringComparison.Ordinal)) return "arabic";
            if (normalized.StartsWith("en", StringComparison.Ordinal)) return "english";
        }

        return script switch
        {
            ScriptMode.UrduNastaliq => "urdu",
            ScriptMode.Persian => "persian",
            ScriptMode.Arabic => "arabic",
            _ => null
        };
    }

    private static bool ValidOpenTypeTag(string? tag)
        => tag is { Length: 4 } && tag.All(static value => char.IsAsciiLetterOrDigit(value));

    private static string EscapeFontSpecValue(string value)
        => value.Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record RenderedGroup(string Open, string Close);

    private sealed record RewriteResult(
        DocumentAst Document,
        IReadOnlyDictionary<string, string> Replacements,
        IReadOnlySet<string> Languages,
        bool RequiresBidi);
}
