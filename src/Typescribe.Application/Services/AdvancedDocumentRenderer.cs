using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Adds publishing support for long-form semantic structures while delegating the
/// established typography and book-design pipeline to DocumentRenderer.
/// </summary>
public sealed class AdvancedDocumentRenderer : IDocumentRenderer
{
    private const string TokenPrefix = "TYPESCRIBESEMANTICTOKEN";
    private readonly DocumentRenderer _inner = new();

    public string RenderPreview(DocumentAst document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var transformed = Transform(document, latex: false);
        var rendered = _inner.RenderPreview(transformed.Document);
        return ApplyReplacements(rendered, transformed.Replacements);
    }

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
    {
        ArgumentNullException.ThrowIfNull(document);
        var transformed = Transform(document, latex: true);
        var rendered = _inner.RenderLatex(transformed.Document, title, style);
        rendered = ApplyReplacements(rendered, transformed.Replacements);
        if (transformed.RequiresAdvancedPackages)
            rendered = AddAdvancedPackages(rendered);
        return rendered;
    }

    private static TransformResult Transform(DocumentAst source, bool latex)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var footnotes = source.Blocks
            .OfType<FootnoteDefinitionBlock>()
            .GroupBy(note => note.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Inlines, StringComparer.OrdinalIgnoreCase);

        var tokenNumber = 0;
        var advanced = false;
        string Token(string replacement)
        {
            var token = TokenPrefix + tokenNumber.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            tokenNumber++;
            replacements[token] = replacement;
            return token;
        }

        IReadOnlyList<AstInline> RewriteInlines(IReadOnlyList<AstInline> inlines)
        {
            var output = new List<AstInline>(inlines.Count);
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case FootnoteReferenceInline footnote:
                    {
                        advanced = true;
                        var replacement = latex
                            ? RenderFootnoteLatex(footnote, footnotes)
                            : $"[^{footnote.Identifier}]";
                        output.Add(new TextInline(Token(replacement)));
                        break;
                    }
                    case CitationInline citation:
                    {
                        advanced = true;
                        var replacement = latex
                            ? RenderCitationLatex(citation)
                            : RenderCitationPreview(citation);
                        output.Add(new TextInline(Token(replacement)));
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

        var blocks = new List<AstBlock>(source.Blocks.Count);
        foreach (var block in source.Blocks)
        {
            switch (block)
            {
                case FootnoteDefinitionBlock footnote:
                {
                    advanced = true;
                    var replacement = latex
                        ? string.Empty
                        : $"↳ [^{footnote.Identifier}] {footnote.Inlines.ToPlainText()}";
                    blocks.Add(new ParagraphBlock(footnote.SourceLine, [new TextInline(Token(replacement))]));
                    break;
                }
                case TableBlock table:
                {
                    advanced = true;
                    var replacement = latex ? RenderTableLatex(table, footnotes) : RenderTablePreview(table);
                    blocks.Add(new ParagraphBlock(table.SourceLine, [new TextInline(Token(replacement))]));
                    break;
                }
                case FigureBlock figure:
                {
                    advanced = true;
                    var replacement = latex ? RenderFigureLatex(figure) : RenderFigurePreview(figure);
                    blocks.Add(new ParagraphBlock(figure.SourceLine, [new TextInline(Token(replacement))]));
                    break;
                }
                case HeadingBlock heading:
                    blocks.Add(heading with { Inlines = RewriteInlines(heading.Inlines) });
                    break;
                case ParagraphBlock paragraph:
                    blocks.Add(paragraph with { Inlines = RewriteInlines(paragraph.Inlines) });
                    break;
                case QuoteBlock quote:
                    blocks.Add(quote with { Inlines = RewriteInlines(quote.Inlines) });
                    break;
                case ListItemBlock item:
                    blocks.Add(item with { Inlines = RewriteInlines(item.Inlines) });
                    break;
                default:
                    blocks.Add(block);
                    break;
            }
        }

        return new TransformResult(new DocumentAst(blocks), replacements, advanced);
    }

    private static string RenderTablePreview(TableBlock table)
    {
        var output = new StringBuilder();
        output.AppendLine(string.Join("  │  ", table.Header.Select(cell => cell.Inlines.ToPlainText())));
        output.AppendLine(string.Join("──┼──", table.Header.Select(cell => new string('─', Math.Max(3, Math.Min(18, cell.Inlines.ToPlainText().Length))))));
        foreach (var row in table.Rows)
            output.AppendLine(string.Join("  │  ", row.Select(cell => cell.Inlines.ToPlainText())));
        return output.ToString().TrimEnd();
    }

    private static string RenderTableLatex(
        TableBlock table,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> footnotes)
    {
        var columns = Math.Max(1, table.Header.Count);
        var output = new StringBuilder();
        output.AppendLine("\\begin{table}[htbp]");
        output.AppendLine("\\centering");
        output.Append("\\begin{tabularx}{\\linewidth}{");
        for (var index = 0; index < columns; index++) output.Append('X');
        output.AppendLine("}");
        output.AppendLine("\\toprule");
        output.AppendLine(string.Join(" & ", table.Header.Select(cell => "\\textbf{" + RenderLatexInlines(cell.Inlines, footnotes) + "}")) + " \\\\");
        output.AppendLine("\\midrule");
        foreach (var row in table.Rows)
        {
            var cells = new string[columns];
            for (var column = 0; column < columns; column++)
                cells[column] = column < row.Count ? RenderLatexInlines(row[column].Inlines, footnotes) : string.Empty;
            output.AppendLine(string.Join(" & ", cells) + " \\\\");
        }
        output.AppendLine("\\bottomrule");
        output.AppendLine("\\end{tabularx}");
        output.Append("\\end{table}");
        return output.ToString();
    }

    private static string RenderFigurePreview(FigureBlock figure)
    {
        var label = string.IsNullOrWhiteSpace(figure.Identifier) ? string.Empty : $" [{figure.Identifier}]";
        var caption = string.IsNullOrWhiteSpace(figure.Caption) ? "Figure" : figure.Caption;
        return $"▣ {caption}{label}\n  {figure.Source}";
    }

    private static string RenderFigureLatex(FigureBlock figure)
    {
        var output = new StringBuilder();
        output.AppendLine("\\begin{figure}[htbp]");
        output.AppendLine("\\centering");
        output.Append("\\includegraphics[width=0.9\\linewidth]{\\detokenize{")
            .Append(SanitizeDetokenize(figure.Source)).AppendLine("}}");
        if (!string.IsNullOrWhiteSpace(figure.Caption))
            output.Append("\\caption{").Append(EscapeLatex(figure.Caption)).AppendLine("}");
        if (!string.IsNullOrWhiteSpace(figure.Identifier))
            output.Append("\\label{").Append(SanitizeLabel(figure.Identifier!)).AppendLine("}");
        output.Append("\\end{figure}");
        return output.ToString();
    }

    private static string RenderFootnoteLatex(
        FootnoteReferenceInline footnote,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions)
    {
        if (definitions.TryGetValue(footnote.Identifier, out var inlines))
            return "\\footnote{" + RenderLatexInlines(inlines, definitions) + "}";
        return "\\textsuperscript{" + EscapeLatex("[" + footnote.Identifier + "]") + "}";
    }

    private static string RenderCitationPreview(CitationInline citation)
        => string.IsNullOrWhiteSpace(citation.Locator)
            ? $"[{citation.Key}]"
            : $"[{citation.Key}, {citation.Locator}]";

    private static string RenderCitationLatex(CitationInline citation)
    {
        var key = SanitizeCitationKey(citation.Key);
        if (key.Length == 0) return EscapeLatex("[@" + citation.Key + "]");
        return string.IsNullOrWhiteSpace(citation.Locator)
            ? $"\\cite{{{key}}}"
            : $"\\cite[{EscapeLatex(citation.Locator!)}]{{{key}}}";
    }

    private static string RenderLatexInlines(
        IReadOnlyList<AstInline> inlines,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> footnotes)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    output.Append(EscapeLatex(text.Text));
                    break;
                case StrongInline strong:
                    output.Append("\\textbf{").Append(RenderLatexInlines(strong.Children, footnotes)).Append('}');
                    break;
                case EmphasisInline emphasis:
                    output.Append("\\emph{").Append(RenderLatexInlines(emphasis.Children, footnotes)).Append('}');
                    break;
                case CodeInline code:
                    output.Append("\\texttt{").Append(EscapeLatex(code.Text)).Append('}');
                    break;
                case LinkInline link:
                    output.Append("\\href{").Append(EscapeLatex(link.Url)).Append("}{")
                        .Append(RenderLatexInlines(link.Label, footnotes)).Append('}');
                    break;
                case MathInline math:
                    output.Append('$').Append(math.Text).Append('$');
                    break;
                case FootnoteReferenceInline footnote:
                    output.Append(RenderFootnoteLatex(footnote, footnotes));
                    break;
                case CitationInline citation:
                    output.Append(RenderCitationLatex(citation));
                    break;
            }
        }
        return output.ToString();
    }

    private static string AddAdvancedPackages(string latex)
    {
        const string marker = "\\usepackage{fontspec}";
        const string packages = "\\usepackage{graphicx}\n\\usepackage{booktabs}\n\\usepackage{tabularx}\n\\usepackage{cite}";
        var index = latex.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return packages + Environment.NewLine + latex;
        var insert = index + marker.Length;
        return latex.Insert(insert, Environment.NewLine + packages);
    }

    private static string ApplyReplacements(string text, IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var pair in replacements)
            text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return text;
    }

    private static string EscapeLatex(string value)
    {
        var output = new StringBuilder(value.Length + 16);
        foreach (var character in value)
        {
            output.Append(character switch
            {
                '\\' => "\\textbackslash{}",
                '{' => "\\{",
                '}' => "\\}",
                '$' => "\\$",
                '&' => "\\&",
                '#' => "\\#",
                '_' => "\\_",
                '%' => "\\%",
                '^' => "\\textasciicircum{}",
                '~' => "\\textasciitilde{}",
                _ => character.ToString()
            });
        }
        return output.ToString();
    }

    private static string SanitizeCitationKey(string key)
    {
        var output = new StringBuilder(key.Length);
        foreach (var character in key.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-' or ':' or '.' or '/')
                output.Append(character);
        }
        return output.ToString();
    }

    private static string SanitizeLabel(string label)
    {
        var output = new StringBuilder(label.Length);
        foreach (var character in label.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-' or ':' or '.')
                output.Append(character);
        }
        return output.Length == 0 ? "figure" : output.ToString();
    }

    private static string SanitizeDetokenize(string value)
        => value.Replace('\\', '/').Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);

    private sealed record TransformResult(
        DocumentAst Document,
        IReadOnlyDictionary<string, string> Replacements,
        bool RequiresAdvancedPackages);
}
