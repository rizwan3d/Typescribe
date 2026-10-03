using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Emits the normal LuaLaTeX document plus inert source markers that let the desktop preview
/// correlate SyncTeX records back to manuscript line numbers. The comments do not affect layout.
/// </summary>
public sealed class SourceMappedDocumentRenderer : IDocumentRenderer
{
    private const string MarkerPrefix = "% TYPESCRIBE-SOURCE:";
    private readonly LuaLatexSafeDocumentRenderer _inner = new();

    public string RenderPreview(DocumentAst document) => _inner.RenderPreview(document);

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);

        var latex = _inner.RenderLatex(document, title, style);
        if (document.Blocks.Count == 0) return latex;

        var insertions = new List<(int Index, int SourceLine)>();
        var cursor = 0;
        foreach (var block in document.Blocks)
        {
            var needle = RenderBlockNeedle(block, style);
            if (string.IsNullOrWhiteSpace(needle)) continue;

            var index = latex.IndexOf(needle, cursor, StringComparison.Ordinal);
            if (index < 0) continue;

            insertions.Add((index, Math.Max(1, block.SourceLine)));
            cursor = index + needle.Length;
        }

        for (var index = insertions.Count - 1; index >= 0; index--)
        {
            var insertion = insertions[index];
            latex = latex.Insert(
                insertion.Index,
                MarkerPrefix + insertion.SourceLine.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine);
        }

        latex = RichPdfPostProcessor.Apply(latex, document);
        return MergedTableLatexPostProcessor.Apply(latex, document);
    }

    private string RenderBlockNeedle(AstBlock block, BookStyle style)
    {
        var single = _inner.RenderLatex(
            new DocumentAst([block]),
            "Typescribe source map",
            style with { IncludeTableOfContents = false });
        var body = ExtractBody(single);
        if (body.Length == 0) return string.Empty;

        if (block is ListItemBlock)
        {
            var item = body.IndexOf("\\item ", StringComparison.Ordinal);
            if (item < 0) return string.Empty;
            var end = body.IndexOf('\n', item);
            return (end < 0 ? body[item..] : body[item..end]).TrimEnd('\r');
        }

        return body.Trim();
    }

    private static string ExtractBody(string latex)
    {
        var title = latex.IndexOf("\\maketitle", StringComparison.Ordinal);
        if (title < 0) return string.Empty;
        var start = latex.IndexOf('\n', title);
        if (start < 0) return string.Empty;
        start++;

        var end = latex.LastIndexOf("\\end{document}", StringComparison.Ordinal);
        if (end <= start) return string.Empty;
        return latex[start..end].Trim();
    }
}
