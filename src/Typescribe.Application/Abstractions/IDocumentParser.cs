using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IDocumentParser
{
    DocumentAst Parse(string source);
}
