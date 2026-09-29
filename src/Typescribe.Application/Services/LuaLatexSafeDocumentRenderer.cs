using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Keeps the semantic renderer as the source of truth while normalizing package combinations
/// that are unsafe with LuaLaTeX + unicode-math. In particular, amssymb/amsfonts redefine
/// symbols that unicode-math owns (for example \eth), which can abort compilation.
/// </summary>
public sealed class LuaLatexSafeDocumentRenderer : IDocumentRenderer
{
    private readonly DocumentRenderer _inner = new();

    public string RenderPreview(DocumentAst document) => _inner.RenderPreview(document);

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
        => NormalizeUnicodeMathPreamble(_inner.RenderLatex(document, title, style));

    internal static string NormalizeUnicodeMathPreamble(string latex)
    {
        if (string.IsNullOrEmpty(latex)) return latex;

        var lines = latex.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(latex.Length + 64);
        var wroteAmsMath = false;
        var wroteMathtools = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // The legacy renderer emitted these three together. unicode-math already owns
            // the AMS symbol namespace, so keep the equation/layout packages but not amssymb.
            if (string.Equals(trimmed, "\\usepackage{amsmath,amssymb,mathtools}", StringComparison.Ordinal))
            {
                if (!wroteAmsMath)
                {
                    output.AppendLine("\\usepackage{amsmath}");
                    wroteAmsMath = true;
                }
                if (!wroteMathtools)
                {
                    output.AppendLine("\\usepackage{mathtools}");
                    wroteMathtools = true;
                }
                continue;
            }

            // Extra packages are user-configurable. Do not allow a project style to
            // accidentally reintroduce the same unicode-math symbol conflict.
            if (string.Equals(trimmed, "\\usepackage{amssymb}", StringComparison.Ordinal) ||
                string.Equals(trimmed, "\\usepackage{amsfonts}", StringComparison.Ordinal))
                continue;

            if (string.Equals(trimmed, "\\usepackage{amsmath}", StringComparison.Ordinal))
            {
                if (wroteAmsMath) continue;
                wroteAmsMath = true;
            }
            else if (string.Equals(trimmed, "\\usepackage{mathtools}", StringComparison.Ordinal))
            {
                if (wroteMathtools) continue;
                wroteMathtools = true;
            }

            output.AppendLine(line);
        }

        return output.ToString();
    }
}
