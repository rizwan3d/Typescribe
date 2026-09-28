using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IDocumentRenderer
{
    string RenderPreview(DocumentAst document);
    string RenderTypst(DocumentAst document, string title, BookStyle style);
    string RenderLatex(DocumentAst document, string title, BookStyle style);
}
