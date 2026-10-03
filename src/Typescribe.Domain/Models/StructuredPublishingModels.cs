namespace Typescribe.Domain.Models;

public sealed record BibliographyEntry(
    string CitationKey,
    string Author = "",
    string Title = "",
    string Year = "",
    string Publisher = "",
    string Doi = "",
    string Url = "",
    string Journal = "",
    string Pages = "")
{
    public BibliographyEntry Normalize()
        => this with
        {
            CitationKey = CitationKey.Trim(),
            Author = Author.Trim(),
            Title = Title.Trim(),
            Year = Year.Trim(),
            Publisher = Publisher.Trim(),
            Doi = Doi.Trim(),
            Url = Url.Trim(),
            Journal = Journal.Trim(),
            Pages = Pages.Trim()
        };
}

public sealed record AssetUsage(string DocumentId, string DocumentTitle, int Line);

public sealed record ProjectAsset(
    string RelativePath,
    string FileName,
    long FileSize,
    int? Width,
    int? Height,
    bool Missing,
    IReadOnlyList<AssetUsage> Usages)
{
    public bool IsUnused => !Missing && Usages.Count == 0;
    public string Dimensions => Width is int width && Height is int height ? $"{width} × {height}" : "Unknown";
}

public enum ReferenceTargetKind
{
    Chapter,
    Heading,
    Figure,
    Table,
    Equation
}

public sealed record ReferenceTarget(
    string Identifier,
    ReferenceTargetKind Kind,
    string DisplayText,
    int SourceLine);

public enum PreflightSeverity
{
    Info,
    Warning,
    Error
}

public sealed record PreflightIssue(
    PreflightSeverity Severity,
    string Code,
    string Message,
    string? DocumentId = null,
    string? DocumentTitle = null,
    int? Line = null);

public sealed record PreflightReport(
    int FiguresResolved,
    int CitationsResolved,
    int UnusedAssets,
    IReadOnlyList<PreflightIssue> Issues)
{
    public bool HasErrors => Issues.Any(static issue => issue.Severity == PreflightSeverity.Error);
}

public sealed record CompilerProblem(
    PreflightSeverity Severity,
    string Message,
    string? DocumentId,
    string? DocumentTitle,
    int? SourceLine,
    int? GeneratedLine = null);

public sealed class PublishingDiagnosticException(
    string message,
    int? generatedLine,
    int? sourceLine,
    string details) : InvalidOperationException(message)
{
    public int? GeneratedLine { get; } = generatedLine;
    public int? SourceLine { get; } = sourceLine;
    public string Details { get; } = details;
}
