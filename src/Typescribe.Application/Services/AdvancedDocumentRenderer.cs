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
        if (transformed.Bibliography.Count > 0)
            rendered = AddBibliography(rendered, transformed.Bibliography);
        return rendered;
    }

    private static TransformResult Transform(DocumentAst source, bool latex)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var footnotes = source.Blocks
            .OfType<FootnoteDefinitionBlock>()
            .GroupBy(note => note.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Inlines, StringComparer.OrdinalIgnoreCase);
        var bibliography = source.Blocks
            .OfType<BibliographyEntryBlock>()
            .Select(static block => block.Entry.Normalize())
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.CitationKey))
            .GroupBy(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var targets = BuildReferenceTargets(source);

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
                            ? RenderFootnoteLatex(footnote, footnotes, targets)
                            : $"[^{footnote.Identifier}]";
                        output.Add(new TextInline(Token(replacement)));
                        break;
                    }
                    case CitationInline citation:
                    {
                        advanced = true;
                        var replacement = latex
                            ? RenderCitationLatex(citation)
                            : RenderCitationPreview(citation, bibliography);
                        output.Add(new TextInline(Token(replacement)));
                        break;
                    }
                    case CrossReferenceInline reference:
                    {
                        advanced = true;
                        var replacement = RenderCrossReference(reference, targets, latex);
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
                case BibliographyEntryBlock:
                {
                    advanced = true;
                    blocks.Add(new ParagraphBlock(block.SourceLine, [new TextInline(Token(string.Empty))]));
                    break;
                }
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
                    var replacement = latex
                        ? RenderTableLatex(table, footnotes, targets)
                        : RenderTablePreview(table);
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
                case DisplayMathBlock { Identifier: not null } equation:
                {
                    advanced = true;
                    var replacement = latex
                        ? RenderEquationLatex(equation)
                        : RenderEquationPreview(equation, targets);
                    blocks.Add(new ParagraphBlock(equation.SourceLine, [new TextInline(Token(replacement))]));
                    break;
                }
                case HeadingBlock heading:
                {
                    var inlines = RewriteInlines(heading.Inlines).ToList();
                    if (latex && !string.IsNullOrWhiteSpace(heading.Identifier))
                    {
                        advanced = true;
                        blocks.Add(heading with { Inlines = inlines });
                        blocks.Add(new ParagraphBlock(
                            heading.SourceLine,
                            [new TextInline(Token("\\label{" + SanitizeLabel(heading.Identifier!) + "}"))]));
                        break;
                    }
                    blocks.Add(heading with { Inlines = inlines });
                    break;
                }
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

        return new TransformResult(new DocumentAst(blocks), replacements, advanced, bibliography);
    }

    public static IReadOnlyDictionary<string, ReferenceTarget> BuildReferenceTargets(DocumentAst source)
    {
        var targets = new Dictionary<string, ReferenceTarget>(StringComparer.OrdinalIgnoreCase);
        var chapter = 0;
        var section = 0;
        var subsection = 0;
        var figure = 0;
        var table = 0;
        var equation = 0;

        void Add(string? identifier, ReferenceTargetKind kind, string display, int line)
        {
            if (string.IsNullOrWhiteSpace(identifier) || targets.ContainsKey(identifier)) return;
            targets[identifier] = new ReferenceTarget(identifier, kind, display, line);
        }

        foreach (var block in source.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading when heading.Level == 1:
                    chapter++;
                    section = 0;
                    subsection = 0;
                    figure = 0;
                    table = 0;
                    equation = 0;
                    Add(heading.Identifier, ReferenceTargetKind.Chapter, $"Chapter {chapter}", heading.SourceLine);
                    break;
                case HeadingBlock heading when heading.Level == 2:
                    section++;
                    subsection = 0;
                    Add(heading.Identifier, ReferenceTargetKind.Heading,
                        chapter > 0 ? $"Section {chapter}.{section}" : $"Section {section}", heading.SourceLine);
                    break;
                case HeadingBlock heading:
                    subsection++;
                    Add(heading.Identifier, ReferenceTargetKind.Heading,
                        chapter > 0 ? $"Section {chapter}.{Math.Max(1, section)}.{subsection}" : $"Section {Math.Max(1, section)}.{subsection}",
                        heading.SourceLine);
                    break;
                case FigureBlock figureBlock:
                    figure++;
                    Add(figureBlock.Identifier, ReferenceTargetKind.Figure,
                        chapter > 0 ? $"Figure {chapter}.{figure}" : $"Figure {figure}", figureBlock.SourceLine);
                    break;
                case TableBlock tableBlock:
                    table++;
                    Add(tableBlock.Identifier, ReferenceTargetKind.Table,
                        chapter > 0 ? $"Table {chapter}.{table}" : $"Table {table}", tableBlock.SourceLine);
                    break;
                case DisplayMathBlock { Identifier: not null } equationBlock:
                    equation++;
                    Add(equationBlock.Identifier, ReferenceTargetKind.Equation,
                        chapter > 0 ? $"Equation {chapter}.{equation}" : $"Equation {equation}", equationBlock.SourceLine);
                    break;
            }
        }

        return targets;
    }

    private static string RenderTablePreview(TableBlock table)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(table.Caption)) output.AppendLine(table.Caption);
        output.AppendLine(string.Join("  │  ", table.Header.Select(cell => cell.Inlines.ToPlainText())));
        output.AppendLine(string.Join("──┼──", table.Header.Select(cell => new string('─', Math.Max(3, Math.Min(18, cell.Inlines.ToPlainText().Length))))));
        foreach (var row in table.Rows)
            output.AppendLine(string.Join("  │  ", row.Select(cell => cell.Inlines.ToPlainText())));
        if (!string.IsNullOrWhiteSpace(table.Identifier)) output.Append("[").Append(table.Identifier).Append(']');
        return output.ToString().TrimEnd();
    }

    private static string RenderTableLatex(
        TableBlock table,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> footnotes,
        IReadOnlyDictionary<string, ReferenceTarget> targets)
    {
        var columns = Math.Max(1, table.Header.Count);
        var output = new StringBuilder();
        output.AppendLine("\\begin{table}[htbp]");
        output.AppendLine("\\centering");
        output.Append("\\begin{tabularx}{\\linewidth}{");
        for (var index = 0; index < columns; index++)
        {
            var alignment = table.Alignments is not null && index < table.Alignments.Count ? table.Alignments[index] : TableAlignment.Default;
            output.Append(alignment switch
            {
                TableAlignment.Center => ">{\\centering\\arraybackslash}X",
                TableAlignment.Right => ">{\\raggedleft\\arraybackslash}X",
                _ => ">{\\raggedright\\arraybackslash}X"
            });
        }
        output.AppendLine("}");
        output.AppendLine("\\toprule");
        output.AppendLine(string.Join(" & ", table.Header.Select(cell => "\\textbf{" + RenderLatexInlines(cell.Inlines, footnotes, targets) + "}")) + " \\\\");
        output.AppendLine("\\midrule");
        foreach (var row in table.Rows)
        {
            var cells = new string[columns];
            for (var column = 0; column < columns; column++)
                cells[column] = column < row.Count ? RenderLatexInlines(row[column].Inlines, footnotes, targets) : string.Empty;
            output.AppendLine(string.Join(" & ", cells) + " \\\\");
        }
        output.AppendLine("\\bottomrule");
        output.AppendLine("\\end{tabularx}");
        if (!string.IsNullOrWhiteSpace(table.Caption))
            output.Append("\\caption{").Append(EscapeLatex(table.Caption!)).AppendLine("}");
        if (!string.IsNullOrWhiteSpace(table.Identifier))
            output.Append("\\label{").Append(SanitizeLabel(table.Identifier!)).AppendLine("}");
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
        var source = SanitizeDetokenize(figure.Source);
        output.AppendLine("\\begin{figure}[htbp]");
        output.AppendLine("\\centering");
        output.Append("\\IfFileExists{\\detokenize{").Append(source).AppendLine("}}{% ");
        output.Append("  \\includegraphics[width=0.9\\linewidth]{\\detokenize{").Append(source).AppendLine("}}% ");
        output.AppendLine("}{% ");
        output.AppendLine("  \\fbox{\\parbox{0.86\\linewidth}{\\centering Figure asset not found\\\\\\smallskip");
        output.Append("  \\texttt{\\detokenize{").Append(source).AppendLine("}}}}% ");
        output.AppendLine("}");
        if (!string.IsNullOrWhiteSpace(figure.Caption))
            output.Append("\\caption{").Append(EscapeLatex(figure.Caption)).AppendLine("}");
        if (!string.IsNullOrWhiteSpace(figure.Identifier))
            output.Append("\\label{").Append(SanitizeLabel(figure.Identifier!)).AppendLine("}");
        output.Append("\\end{figure}");
        return output.ToString();
    }

    private static string RenderEquationPreview(DisplayMathBlock equation, IReadOnlyDictionary<string, ReferenceTarget> targets)
    {
        var label = equation.Identifier is not null && targets.TryGetValue(equation.Identifier, out var target)
            ? $"  ({target.DisplayText})"
            : string.Empty;
        return $"⟦ {equation.Text} ⟧{label}";
    }

    private static string RenderEquationLatex(DisplayMathBlock equation)
        => "\\begin{equation}\n" + equation.Text + "\n\\label{" + SanitizeLabel(equation.Identifier!) + "}\n\\end{equation}";

    private static string RenderFootnoteLatex(
        FootnoteReferenceInline footnote,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        IReadOnlyDictionary<string, ReferenceTarget> targets)
    {
        if (definitions.TryGetValue(footnote.Identifier, out var inlines))
            return "\\footnote{" + RenderLatexInlines(inlines, definitions, targets) + "}";
        return "\\textsuperscript{" + EscapeLatex("[" + footnote.Identifier + "]") + "}";
    }

    private static string RenderCitationPreview(CitationInline citation, IReadOnlyDictionary<string, BibliographyEntry> bibliography)
    {
        if (bibliography.TryGetValue(citation.Key, out var entry))
        {
            var author = FirstAuthor(entry.Author);
            var core = string.IsNullOrWhiteSpace(entry.Year) ? author : $"{author}, {entry.Year}";
            return string.IsNullOrWhiteSpace(citation.Locator) ? $"({core})" : $"({core}, {citation.Locator})";
        }
        return string.IsNullOrWhiteSpace(citation.Locator)
            ? $"[{citation.Key}]"
            : $"[{citation.Key}, {citation.Locator}]";
    }

    private static string RenderCitationLatex(CitationInline citation)
    {
        var key = SanitizeCitationKey(citation.Key);
        if (key.Length == 0) return EscapeLatex("[@" + citation.Key + "]");
        return string.IsNullOrWhiteSpace(citation.Locator)
            ? $"\\cite{{{key}}}"
            : $"\\cite[{EscapeLatex(citation.Locator!)}]{{{key}}}";
    }

    private static string RenderCrossReference(
        CrossReferenceInline reference,
        IReadOnlyDictionary<string, ReferenceTarget> targets,
        bool latex)
    {
        if (!targets.TryGetValue(reference.Identifier, out var target))
            return latex ? "\\textbf{??}" : $"[missing ref: {reference.Identifier}]";
        if (!latex) return target.DisplayText;
        return TargetPrefix(target.Kind) + "~\\ref{" + SanitizeLabel(reference.Identifier) + "}";
    }

    private static string TargetPrefix(ReferenceTargetKind kind)
        => kind switch
        {
            ReferenceTargetKind.Chapter => "Chapter",
            ReferenceTargetKind.Heading => "Section",
            ReferenceTargetKind.Figure => "Figure",
            ReferenceTargetKind.Table => "Table",
            ReferenceTargetKind.Equation => "Equation",
            _ => "Reference"
        };

    private static string RenderLatexInlines(
        IReadOnlyList<AstInline> inlines,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> footnotes,
        IReadOnlyDictionary<string, ReferenceTarget> targets)
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
                    output.Append("\\textbf{").Append(RenderLatexInlines(strong.Children, footnotes, targets)).Append('}');
                    break;
                case EmphasisInline emphasis:
                    output.Append("\\emph{").Append(RenderLatexInlines(emphasis.Children, footnotes, targets)).Append('}');
                    break;
                case CodeInline code:
                    output.Append("\\texttt{").Append(EscapeLatex(code.Text)).Append('}');
                    break;
                case LinkInline link:
                    output.Append("\\href{").Append(EscapeLatex(link.Url)).Append("}{")
                        .Append(RenderLatexInlines(link.Label, footnotes, targets)).Append('}');
                    break;
                case MathInline math:
                    output.Append('$').Append(math.Text).Append('$');
                    break;
                case FootnoteReferenceInline footnote:
                    output.Append(RenderFootnoteLatex(footnote, footnotes, targets));
                    break;
                case CitationInline citation:
                    output.Append(RenderCitationLatex(citation));
                    break;
                case CrossReferenceInline reference:
                    output.Append(RenderCrossReference(reference, targets, latex: true));
                    break;
            }
        }
        return output.ToString();
    }

    private static string AddAdvancedPackages(string latex)
    {
        const string marker = "\\usepackage{fontspec}";
        const string packages = "\\usepackage{graphicx}\n\\usepackage{booktabs}\n\\usepackage{tabularx}\n\\usepackage{array}";
        var index = latex.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return packages + Environment.NewLine + latex;
        var insert = index + marker.Length;
        return latex.Insert(insert, Environment.NewLine + packages);
    }

    private static string AddBibliography(string latex, IReadOnlyDictionary<string, BibliographyEntry> entries)
    {
        const string marker = "\\end{document}";
        var index = latex.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return latex;
        var output = new StringBuilder();
        output.AppendLine();
        output.AppendLine("\\begin{thebibliography}{99}");
        foreach (var entry in entries.Values.OrderBy(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase))
        {
            output.Append("\\bibitem{").Append(SanitizeCitationKey(entry.CitationKey)).Append("} ");
            if (!string.IsNullOrWhiteSpace(entry.Author)) output.Append(EscapeLatex(entry.Author)).Append(". ");
            if (!string.IsNullOrWhiteSpace(entry.Title)) output.Append("\\emph{").Append(EscapeLatex(entry.Title)).Append("}. ");
            if (!string.IsNullOrWhiteSpace(entry.Journal)) output.Append(EscapeLatex(entry.Journal)).Append(". ");
            if (!string.IsNullOrWhiteSpace(entry.Publisher)) output.Append(EscapeLatex(entry.Publisher)).Append(". ");
            if (!string.IsNullOrWhiteSpace(entry.Year)) output.Append(EscapeLatex(entry.Year)).Append(". ");
            if (!string.IsNullOrWhiteSpace(entry.Pages)) output.Append("pp. ").Append(EscapeLatex(entry.Pages)).Append(". ");
            if (!string.IsNullOrWhiteSpace(entry.Doi)) output.Append("DOI: ").Append(EscapeLatex(entry.Doi)).Append(". ");
            if (!string.IsNullOrWhiteSpace(entry.Url)) output.Append("\\texttt{").Append(EscapeLatex(entry.Url)).Append("}");
            output.AppendLine();
        }
        output.AppendLine("\\end{thebibliography}");
        return latex.Insert(index, output.ToString());
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
        return output.Length == 0 ? "reference" : output.ToString();
    }

    private static string SanitizeDetokenize(string value)
        => value.Replace('\\', '/').Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);

    private static string FirstAuthor(string authors)
    {
        if (string.IsNullOrWhiteSpace(authors)) return "Unknown";
        var first = authors.Split(" and ", 2, StringSplitOptions.TrimEntries)[0];
        var comma = first.IndexOf(',');
        if (comma > 0) return first[..comma].Trim();
        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "Unknown" : parts[^1];
    }

    private sealed record TransformResult(
        DocumentAst Document,
        IReadOnlyDictionary<string, string> Replacements,
        bool RequiresAdvancedPackages,
        IReadOnlyDictionary<string, BibliographyEntry> Bibliography);
}
