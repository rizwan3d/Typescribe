using System.Globalization;
using System.Net;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public sealed record RichClipboardExport(
    string RichMarkdown,
    string Html,
    byte[] Rtf,
    string PlainText,
    IReadOnlyList<string> Warnings);

public sealed record RichClipboardImport(
    string Markdown,
    string SourceFormat,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Cross-application clipboard serialization for TypeScribe. The application-specific Markdown
/// flavor is the fidelity path between TypeScribe instances; HTML and RTF are interoperability
/// paths for browsers, Word and LibreOffice; plain text is the final lossless-Unicode fallback.
/// </summary>
public static class RichClipboardCodec
{
    public const string ApplicationFormatId = "typescribe.rich-markdown.v1";

    public static RichClipboardExport Export(string markdown, IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        markdown ??= string.Empty;
        var ast = parser.Parse(markdown);
        var warnings = new List<string>();
        var html = RenderHtml(ast);
        var rtf = RtfClipboardCodec.Export(ast, warnings);
        return new RichClipboardExport(
            markdown,
            html,
            rtf,
            RenderPlainText(ast),
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static RichClipboardImport Import(
        string? richMarkdown,
        byte[]? html,
        byte[]? rtf,
        string? plainText)
    {
        if (!string.IsNullOrEmpty(richMarkdown))
            return new RichClipboardImport(richMarkdown, "TypeScribe rich Markdown", []);

        if (html is { Length: > 0 })
        {
            var imported = HtmlClipboardCodec.Import(html);
            if (!string.IsNullOrWhiteSpace(imported.Markdown)) return imported;
        }

        if (rtf is { Length: > 0 })
        {
            var imported = RtfClipboardCodec.Import(rtf);
            if (!string.IsNullOrWhiteSpace(imported.Markdown)) return imported;
        }

        return new RichClipboardImport(plainText ?? string.Empty, "plain text", []);
    }

    public static byte[] EncodeHtmlForClipboard(string html, bool windowsClipboardHeader)
    {
        html ??= string.Empty;
        var fragment = "<!--StartFragment-->" + html + "<!--EndFragment-->";
        if (!windowsClipboardHeader) return Encoding.UTF8.GetBytes(fragment);

        const string template = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var placeholder = string.Format(CultureInfo.InvariantCulture, template, 0, 0, 0, 0);
        var startHtml = Encoding.ASCII.GetByteCount(placeholder);
        var prefix = "<html><body>";
        var suffix = "</body></html>";
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix + "<!--StartFragment-->");
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(prefix + fragment + suffix);
        var header = string.Format(CultureInfo.InvariantCulture, template, startHtml, endHtml, startFragment, endFragment);
        return Encoding.UTF8.GetBytes(header + prefix + fragment + suffix);
    }

    private static string RenderHtml(DocumentAst ast)
    {
        var output = new StringBuilder();
        var listOpen = false;
        var listOrdered = false;

        void CloseList()
        {
            if (!listOpen) return;
            output.Append(listOrdered ? "</ol>" : "</ul>");
            listOpen = false;
        }

        foreach (var block in ast.Blocks)
        {
            if (block is ListItemBlock item)
            {
                if (!listOpen || listOrdered != item.Ordered)
                {
                    CloseList();
                    listOpen = true;
                    listOrdered = item.Ordered;
                    output.Append(item.Ordered ? "<ol>" : "<ul>");
                }
                output.Append("<li").Append(BlockAttributes(block.Formatting)).Append('>')
                    .Append(RenderInlines(item.Inlines)).Append("</li>");
                continue;
            }

            CloseList();
            switch (block)
            {
                case HeadingBlock heading:
                    var level = Math.Clamp(heading.Level, 1, 6);
                    output.Append("<h").Append(level).Append(BlockAttributes(block.Formatting)).Append('>')
                        .Append(RenderInlines(heading.Inlines)).Append("</h").Append(level).Append('>');
                    break;
                case ParagraphBlock paragraph:
                    output.Append("<p").Append(BlockAttributes(block.Formatting)).Append('>')
                        .Append(RenderInlines(paragraph.Inlines)).Append("</p>");
                    break;
                case QuoteBlock quote:
                    output.Append("<blockquote").Append(BlockAttributes(block.Formatting)).Append("><p>")
                        .Append(RenderInlines(quote.Inlines)).Append("</p></blockquote>");
                    break;
                case CodeBlock code:
                    output.Append("<pre><code>").Append(WebUtility.HtmlEncode(code.Text)).Append("</code></pre>");
                    break;
                case DisplayMathBlock math:
                    output.Append("<div class=\"display-math\">").Append(WebUtility.HtmlEncode(math.Text)).Append("</div>");
                    break;
                case ThematicBreakBlock:
                    output.Append("<hr/>");
                    break;
                case TableBlock table:
                    output.Append("<table><thead><tr>");
                    foreach (var cell in table.Header)
                        output.Append("<th>").Append(RenderInlines(cell.Inlines)).Append("</th>");
                    output.Append("</tr></thead><tbody>");
                    foreach (var row in table.Rows)
                    {
                        output.Append("<tr>");
                        foreach (var cell in row)
                            output.Append("<td>").Append(RenderInlines(cell.Inlines)).Append("</td>");
                        output.Append("</tr>");
                    }
                    output.Append("</tbody></table>");
                    break;
                case FigureBlock figure:
                    output.Append("<figure><img src=\"").Append(WebUtility.HtmlEncode(figure.Source)).Append("\" alt=\"")
                        .Append(WebUtility.HtmlEncode(figure.AltText ?? figure.Caption)).Append("\"/>");
                    if (!string.IsNullOrWhiteSpace(figure.Caption))
                        output.Append("<figcaption>").Append(WebUtility.HtmlEncode(figure.Caption)).Append("</figcaption>");
                    output.Append("</figure>");
                    break;
                case FootnoteDefinitionBlock note:
                    output.Append("<p data-typescribe-footnote=\"").Append(WebUtility.HtmlEncode(note.Identifier)).Append("\">")
                        .Append(RenderInlines(note.Inlines)).Append("</p>");
                    break;
            }
        }
        CloseList();
        return output.ToString();
    }

    private static string RenderInlines(IEnumerable<AstInline> inlines)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case RichSpanInline rich:
                    output.Append("<span").Append(CharacterAttributes(rich.Formatting)).Append('>')
                        .Append(RenderInlines(rich.Children)).Append("</span>");
                    break;
                case TextInline text:
                    output.Append(WebUtility.HtmlEncode(text.Text));
                    break;
                case StrongInline strong:
                    output.Append("<strong>").Append(RenderInlines(strong.Children)).Append("</strong>");
                    break;
                case EmphasisInline emphasis:
                    output.Append("<em>").Append(RenderInlines(emphasis.Children)).Append("</em>");
                    break;
                case CodeInline code:
                    output.Append("<code>").Append(WebUtility.HtmlEncode(code.Text)).Append("</code>");
                    break;
                case LinkInline link:
                    output.Append("<a href=\"").Append(WebUtility.HtmlEncode(link.Url)).Append("\">")
                        .Append(RenderInlines(link.Label)).Append("</a>");
                    break;
                case MathInline math:
                    output.Append("<span class=\"math\">").Append(WebUtility.HtmlEncode(math.Text)).Append("</span>");
                    break;
                case FootnoteReferenceInline note:
                    output.Append("<sup data-typescribe-footnote-ref=\"").Append(WebUtility.HtmlEncode(note.Identifier)).Append("\">[")
                        .Append(WebUtility.HtmlEncode(note.Identifier)).Append("]</sup>");
                    break;
                case CitationInline citation:
                    output.Append("<span data-typescribe-citation=\"").Append(WebUtility.HtmlEncode(citation.Key)).Append("\">[")
                        .Append(WebUtility.HtmlEncode(citation.Key));
                    if (!string.IsNullOrWhiteSpace(citation.Locator)) output.Append(", ").Append(WebUtility.HtmlEncode(citation.Locator));
                    output.Append("]</span>");
                    break;
                case CrossReferenceInline reference:
                    output.Append("<span data-typescribe-ref=\"").Append(WebUtility.HtmlEncode(reference.Identifier)).Append("\">[")
                        .Append(WebUtility.HtmlEncode(reference.Identifier)).Append("]</span>");
                    break;
            }
        }
        return output.ToString();
    }

    private static string BlockAttributes(RichBlockFormatting? formatting)
    {
        var paragraph = formatting?.Paragraph;
        if (paragraph is null) return string.Empty;
        var attributes = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(paragraph.Language))
            attributes.Append(" lang=\"").Append(WebUtility.HtmlEncode(paragraph.Language)).Append('"');
        if (paragraph.Direction is TextDirectionMode.RightToLeft) attributes.Append(" dir=\"rtl\"");
        else if (paragraph.Direction is TextDirectionMode.LeftToRight) attributes.Append(" dir=\"ltr\"");

        var style = ParagraphCss(paragraph);
        if (style.Length > 0) attributes.Append(" style=\"").Append(WebUtility.HtmlEncode(style)).Append('"');
        return attributes.ToString();
    }

    private static string CharacterAttributes(CharacterFormatting formatting)
    {
        var attributes = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(formatting.Language))
            attributes.Append(" lang=\"").Append(WebUtility.HtmlEncode(formatting.Language)).Append('"');
        if (formatting.Direction is TextDirectionMode.RightToLeft) attributes.Append(" dir=\"rtl\"");
        else if (formatting.Direction is TextDirectionMode.LeftToRight) attributes.Append(" dir=\"ltr\"");
        var style = CharacterCss(formatting);
        if (style.Length > 0) attributes.Append(" style=\"").Append(WebUtility.HtmlEncode(style)).Append('"');
        return attributes.ToString();
    }

    internal static string CharacterCss(CharacterFormatting formatting)
    {
        var css = new List<string>();
        if (formatting.Font is { Family.Length: > 0 } font)
            css.Add("font-family:" + CssQuoted(font.Family));
        if (formatting.FontSizePoints is { } size && size > 0)
            css.Add("font-size:" + size.ToString("0.###", CultureInfo.InvariantCulture) + "pt");
        if (formatting.Bold == true) css.Add("font-weight:700");
        if (formatting.Italic == true) css.Add("font-style:italic");
        if (formatting.Underline == true) css.Add("text-decoration:underline");
        if (formatting.SmallCaps == true) css.Add("font-variant-caps:small-caps");
        if (formatting.TrackingEm is { } tracking)
            css.Add("letter-spacing:" + tracking.ToString("0.###", CultureInfo.InvariantCulture) + "em");
        if (formatting.Kerning is { } kerning) css.Add("font-kerning:" + (kerning ? "normal" : "none"));
        if (formatting.OpenTypeFeatures is { Count: > 0 } features)
            css.Add("font-feature-settings:" + string.Join(",", features.Select(static feature => $"\"{feature.Tag}\" {feature.Value}")));
        if (formatting.VariableAxes is { Count: > 0 } axes)
            css.Add("font-variation-settings:" + string.Join(",", axes.Select(static axis => $"\"{axis.Tag}\" {axis.Value.ToString(CultureInfo.InvariantCulture)}")));
        if (!string.IsNullOrWhiteSpace(formatting.ColorHex)) css.Add("color:" + formatting.ColorHex);
        return string.Join(';', css);
    }

    private static string ParagraphCss(ParagraphFormatting formatting)
    {
        var css = new List<string>();
        if (formatting.CharacterDefaults is { } defaults)
        {
            var character = CharacterCss(defaults);
            if (character.Length > 0) css.Add(character);
        }
        if (formatting.Alignment is { } alignment)
            css.Add("text-align:" + alignment.ToString().ToLowerInvariant());
        if (formatting.LineSpacing is { } line) css.Add("line-height:" + line.ToString("0.###", CultureInfo.InvariantCulture));
        if (formatting.SpaceBeforePoints is { } before) css.Add("margin-top:" + before.ToString("0.###", CultureInfo.InvariantCulture) + "pt");
        if (formatting.SpaceAfterPoints is { } after) css.Add("margin-bottom:" + after.ToString("0.###", CultureInfo.InvariantCulture) + "pt");
        if (formatting.FirstLineIndentPoints is { } first) css.Add("text-indent:" + first.ToString("0.###", CultureInfo.InvariantCulture) + "pt");
        return string.Join(';', css);
    }

    private static string RenderPlainText(DocumentAst ast)
    {
        var output = new StringBuilder();
        foreach (var block in ast.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading: output.AppendLine(heading.Inlines.ToPlainText()); break;
                case ParagraphBlock paragraph: output.AppendLine(paragraph.Inlines.ToPlainText()); break;
                case QuoteBlock quote: output.AppendLine(quote.Inlines.ToPlainText()); break;
                case ListItemBlock item: output.Append(item.Ordered ? "1. " : "• ").AppendLine(item.Inlines.ToPlainText()); break;
                case CodeBlock code: output.AppendLine(code.Text); break;
                case DisplayMathBlock math: output.AppendLine(math.Text); break;
                case ThematicBreakBlock: output.AppendLine("———"); break;
                case TableBlock table:
                    output.AppendLine(string.Join('\t', table.Header.Select(static cell => cell.Inlines.ToPlainText())));
                    foreach (var row in table.Rows) output.AppendLine(string.Join('\t', row.Select(static cell => cell.Inlines.ToPlainText())));
                    break;
                case FigureBlock figure: output.AppendLine(figure.Caption); break;
                case FootnoteDefinitionBlock note: output.Append('[').Append(note.Identifier).Append("] ").AppendLine(note.Inlines.ToPlainText()); break;
            }
        }
        return output.ToString().TrimEnd();
    }

    private static string CssQuoted(string value)
        => "'" + value.Replace("'", "\\'", StringComparison.Ordinal) + "'";
}