using System.Globalization;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Keeps the semantic renderer as the source of truth while normalizing the generated
/// LuaLaTeX package stack for OpenType/Unicode math. unicode-math owns the symbol table,
/// so legacy AMS symbol/font packages are removed to avoid command redefinition errors.
/// </summary>
public sealed class LuaLatexSafeDocumentRenderer : IDocumentRenderer
{
    private static readonly HashSet<string> ManagedMathPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "amsmath",
        "mathtools",
        "unicode-math"
    };

    private static readonly HashSet<string> IncompatibleUnicodeMathPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "amssymb",
        "amsfonts"
    };

    private readonly AdvancedDocumentRenderer _inner = new();

    public string RenderPreview(DocumentAst document) => _inner.RenderPreview(document);

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
    {
        var latex = NormalizeUnicodeMathPreamble(_inner.RenderLatex(document, title, style));
        return NormalizeRunningHeadFontSize(latex, style.HeaderFooterFontSizePoints);
    }

    internal static string NormalizeUnicodeMathPreamble(string latex)
    {
        if (string.IsNullOrEmpty(latex)) return latex;

        var normalized = latex.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var output = new StringBuilder(normalized.Length + 128);
        var managedMathStackWritten = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (string.Equals(trimmed, "\\usepackage{fontspec}", StringComparison.Ordinal))
            {
                AppendLine(output, line);
                AppendManagedMathStack(output);
                managedMathStackWritten = true;
                continue;
            }

            if (TryNormalizePackageLine(line, managedMathStackWritten, out var packageLine))
            {
                if (packageLine.Length > 0) AppendLine(output, packageLine);
                continue;
            }

            AppendLine(output, line);
        }

        // The semantic renderer emits fontspec today, but keep the normalizer defensive
        // so future renderer changes still receive a valid math stack.
        if (!managedMathStackWritten)
        {
            var prefix = new StringBuilder();
            AppendManagedMathStack(prefix);
            prefix.Append(output);
            output = prefix;
        }

        return output.ToString().TrimEnd('\n') + Environment.NewLine;
    }

    /// <summary>
    /// DocumentRenderer historically applied HeaderFooterFontSizePoints only to the left header.
    /// Normalize every fancyhdr cell here so center/right headers, all footer cells and automatic
    /// page numbers use the same configured size in both live preview and final publishing.
    /// </summary>
    internal static string NormalizeRunningHeadFontSize(string latex, double fontSizePoints)
    {
        if (string.IsNullOrEmpty(latex)) return latex;

        var normalized = latex.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var size = Format(fontSizePoints);
        var leading = Format(fontSizePoints * 1.2);
        var command = $"\\fontsize{{{size}}}{{{leading}}}\\selectfont ";

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("\\fancyhead[", StringComparison.Ordinal) &&
                !trimmed.StartsWith("\\fancyfoot[", StringComparison.Ordinal))
                continue;
            if (line.Contains("\\fontsize{", StringComparison.Ordinal)) continue;

            var closeBracket = line.IndexOf(']');
            if (closeBracket < 0) continue;
            var openBrace = line.IndexOf('{', closeBracket + 1);
            if (openBrace < 0) continue;

            lines[index] = line.Insert(openBrace + 1, command);
        }

        return string.Join(Environment.NewLine, lines).TrimEnd('\r', '\n') + Environment.NewLine;
    }

    private static bool TryNormalizePackageLine(string line, bool managedMathStackWritten, out string normalized)
    {
        normalized = line;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("\\usepackage", StringComparison.Ordinal)) return false;

        var open = line.IndexOf('{');
        if (open < 0) return false;
        var close = line.IndexOf('}', open + 1);
        if (close <= open) return false;

        var packages = line[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (packages.Length == 0) return false;

        var changed = false;
        var kept = new List<string>(packages.Length);
        foreach (var package in packages)
        {
            if (IncompatibleUnicodeMathPackages.Contains(package))
            {
                changed = true;
                continue;
            }

            if (managedMathStackWritten && ManagedMathPackages.Contains(package))
            {
                changed = true;
                continue;
            }

            kept.Add(package);
        }

        if (!changed) return false;
        if (kept.Count == 0)
        {
            normalized = string.Empty;
            return true;
        }

        normalized = line[..(open + 1)] + string.Join(',', kept) + line[close..];
        return true;
    }

    private static void AppendManagedMathStack(StringBuilder output)
    {
        output.AppendLine("% Typescribe managed LuaLaTeX math stack");
        output.AppendLine("\\usepackage{amsmath}");
        output.AppendLine("\\usepackage{mathtools}");
        output.AppendLine("\\usepackage{unicode-math}");
    }

    private static void AppendLine(StringBuilder output, string line)
    {
        output.Append(line);
        output.Append('\n');
    }

    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
