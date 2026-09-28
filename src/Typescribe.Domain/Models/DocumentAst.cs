namespace Typescribe.Domain.Models;

public sealed record DocumentAst(IReadOnlyList<AstBlock> Blocks);

public abstract record AstBlock(int SourceLine);

public sealed record HeadingBlock(int SourceLine, int Level, string Text) : AstBlock(SourceLine);
public sealed record ParagraphBlock(int SourceLine, string Text) : AstBlock(SourceLine);
public sealed record QuoteBlock(int SourceLine, string Text) : AstBlock(SourceLine);
public sealed record ListItemBlock(int SourceLine, string Text) : AstBlock(SourceLine);
public sealed record CodeBlock(int SourceLine, string Language, string Text) : AstBlock(SourceLine);
public sealed record DisplayMathBlock(int SourceLine, string Text) : AstBlock(SourceLine);
