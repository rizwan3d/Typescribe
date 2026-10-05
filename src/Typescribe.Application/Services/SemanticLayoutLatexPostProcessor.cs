using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Applies the richer semantic layout model after the established renderer has emitted its
/// conservative LaTeX. This keeps the current renderer stable while making named figure/table
/// styles and instance overrides affect PDF output immediately.
/// </summary>
internal static class SemanticLayoutLatexPostProcessor
{
    public static string Apply(string latex, DocumentAst document, BookStyle style)
    {
        if (string.IsNullOrWhiteSpace(latex) || document.Blocks.Count == 0) return latex;
        var cursor = 0;
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case FigureBlock figure:
                    latex = RewriteNextBlock(latex, "\\begin{figure}", "\\end{figure}", ref cursor,
                        value => RewriteFigure(value, figure, style));
                    break;
                case TableBlock table:
                    latex = RewriteNextBlock(latex, "\\begin{table}", "\\end{table}", ref cursor,
                        value => RewriteTable(value, table, style));
                    break;
            }
        }
        return latex;
    }

    private static string RewriteNextBlock(
        string latex,
        string startToken,
        string endToken,
        ref int cursor,
        Func<string, string> rewrite)
    {
        var start = latex.IndexOf(startToken, cursor, StringComparison.Ordinal);
        if (start < 0) return latex;
        var end = latex.IndexOf(endToken, start, StringComparison.Ordinal);
        if (end < 0) return latex;
        end += endToken.Length;
        var original = latex[start..end];
        var replacement = rewrite(original);
        latex = latex[..start] + replacement + latex[end..];
        cursor = start + replacement.Length;
        return latex;
    }

    private static string RewriteFigure(string block, FigureBlock figure, BookStyle style)
    {
        var named = style.NamedStyles.ResolveFigure(figure.StyleId ?? style.DefaultFigureStyleId);
        var layout = figure.Layout;
        var width = Math.Clamp(layout?.WidthPercent ?? named?.MaxWidthPercent ?? 90, 5, 100);
        var alignment = layout?.Alignment ?? named?.Alignment ?? FigureAlignment.Center;
        var placement = layout?.Placement ?? named?.Placement ?? FigurePlacement.HereOrTop;
        var captionPosition = named?.CaptionPosition ?? CaptionPosition.Bottom;
        var rotation = layout?.RotationDegrees ?? 0;

        block = ReplaceEnvironmentOptions(block, "figure", Placement(placement));
        block = ReplaceFirst(block, "\\centering", alignment switch
        {
            FigureAlignment.Left => "\\raggedright",
            FigureAlignment.Right => "\\raggedleft",
            _ => "\\centering"
        });

        var options = new List<string> { $"width={Format(width / 100d)}\\linewidth" };
        if (layout?.HeightInches is > 0) options.Add($"height={Format(layout.HeightInches.Value)}in");
        if (Math.Abs(rotation) > .001) options.Add($"angle={Format(rotation)}");
        if (layout?.Fit != FigureFitMode.Fill) options.Add("keepaspectratio");
        block = ReplaceGraphicsOptions(block, string.Join(',', options));

        if (captionPosition == CaptionPosition.Top)
            block = MoveCaptionBeforeGraphics(block);

        if (!string.IsNullOrWhiteSpace(figure.Credit))
        {
            var end = block.LastIndexOf("\\end{figure}", StringComparison.Ordinal);
            if (end >= 0)
                block = block.Insert(end, "\\par\\smallskip{\\footnotesize " + EscapeLatex(figure.Credit!) + "\\par}" + Environment.NewLine);
        }

        return block;
    }

    private static string RewriteTable(string block, TableBlock table, BookStyle style)
    {
        var named = style.NamedStyles.ResolveTable(table.StyleId ?? style.DefaultTableStyleId);
        var properties = table.Properties;
        var width = Math.Clamp(properties?.WidthPercent ?? named?.WidthPercent ?? 100, 10, 100);
        var captionPosition = properties?.CaptionPosition ?? named?.CaptionPosition ?? CaptionPosition.Top;

        var marker = "\\begin{tabularx}{\\linewidth}";
        if (block.Contains(marker, StringComparison.Ordinal))
            block = block.Replace(marker, $"\\begin{{tabularx}}{{{Format(width / 100d)}\\linewidth}}", StringComparison.Ordinal);

        if (captionPosition == CaptionPosition.Top)
            block = MoveTableCaptionBeforeTabular(block);

        if (table.RepeatHeaderRows > 0 || named?.RepeatHeader == true)
        {
            var begin = block.IndexOf("\\begin{table}", StringComparison.Ordinal);
            if (begin >= 0)
            {
                var lineEnd = block.IndexOf('\n', begin);
                if (lineEnd >= 0)
                    block = block.Insert(lineEnd + 1,
                        $"% Typescribe semantic repeat-header-rows: {Math.Max(1, table.RepeatHeaderRows).ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}");
            }
        }

        return block;
    }

    private static string MoveCaptionBeforeGraphics(string block)
    {
        var captionStart = block.IndexOf("\\caption{", StringComparison.Ordinal);
        if (captionStart < 0) return block;
        var captionEnd = FindCommandEnd(block, captionStart);
        if (captionEnd < 0) return block;
        var caption = block[captionStart..captionEnd].TrimEnd();
        block = block.Remove(captionStart, captionEnd - captionStart);
        var graphics = block.IndexOf("\\IfFileExists", StringComparison.Ordinal);
        if (graphics < 0) return block;
        return block.Insert(graphics, caption + Environment.NewLine);
    }

    private static string MoveTableCaptionBeforeTabular(string block)
    {
        var captionStart = block.IndexOf("\\caption{", StringComparison.Ordinal);
        if (captionStart < 0) return block;
        var captionEnd = FindCommandEnd(block, captionStart);
        if (captionEnd < 0) return block;
        var caption = block[captionStart..captionEnd].TrimEnd();
        block = block.Remove(captionStart, captionEnd - captionStart);
        var tabular = block.IndexOf("\\begin{tabularx}", StringComparison.Ordinal);
        if (tabular < 0) return block;
        return block.Insert(tabular, caption + Environment.NewLine);
    }

    private static int FindCommandEnd(string text, int commandStart)
    {
        var open = text.IndexOf('{', commandStart);
        if (open < 0) return -1;
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var end = index + 1;
                    while (end < text.Length && text[end] is '\r' or '\n') end++;
                    return end;
                }
            }
        }
        return -1;
    }

    private static string ReplaceEnvironmentOptions(string block, string environment, string options)
    {
        var prefix = $"\\begin{{{environment}}}";
        var start = block.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return block;
        var optionStart = start + prefix.Length;
        if (optionStart < block.Length && block[optionStart] == '[')
        {
            var close = block.IndexOf(']', optionStart + 1);
            if (close >= 0) return block[..optionStart] + $"[{options}]" + block[(close + 1)..];
        }
        return block.Insert(optionStart, $"[{options}]");
    }

    private static string ReplaceGraphicsOptions(string block, string options)
    {
        const string prefix = "\\includegraphics[";
        var start = block.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return block;
        var close = block.IndexOf(']', start + prefix.Length);
        if (close < 0) return block;
        return block[..(start + prefix.Length)] + options + block[close..];
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? source : source[..index] + newValue + source[(index + oldValue.Length)..];
    }

    private static string Placement(FigurePlacement value) => value switch
    {
        FigurePlacement.Inline => "h",
        FigurePlacement.Here => "h",
        FigurePlacement.Top => "t",
        FigurePlacement.Bottom => "b",
        FigurePlacement.Page => "p",
        _ => "htbp"
    };

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

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
}
