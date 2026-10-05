using System.Text.RegularExpressions;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>Maps LuaLaTeX generated-line diagnostics back through TYPESCRIBE-SOURCE markers.</summary>
public sealed class DiagnosticPdfPublishingEngine(IPdfPublishingEngine inner) : IPdfPublishingEngine
{
    private static readonly Regex FileLine = new(@"document\.tex:(?<line>\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LatexLine = new(@"(?:^|\s)l\.(?<line>\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private const string SourceMarker = "% TYPESCRIBE-SOURCE:";

    public string Name => inner.Name;
    public bool IsAvailable => inner.IsAvailable;

    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
        => inner.EnsureAvailableAsync(cancellationToken);

    public async Task PublishAsync(string source, string outputPdfPath, int passes = 2, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.PublishAsync(source, outputPdfPath, passes, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not PublishingDiagnosticException)
        {
            var generatedLine = ExtractGeneratedLine(ex.Message);
            var sourceLine = generatedLine is int line ? MapSourceLine(source, line) : null;
            var message = ExtractPrimaryMessage(ex.Message);
            throw new PublishingDiagnosticException(message, generatedLine, sourceLine, ex.Message);
        }
    }

    private static int? ExtractGeneratedLine(string message)
    {
        var match = FileLine.Match(message);
        if (!match.Success) match = LatexLine.Match(message);
        return match.Success && int.TryParse(match.Groups["line"].Value, out var line) ? line : null;
    }

    private static int? MapSourceLine(string latex, int generatedLine)
    {
        var normalized = latex.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var limit = Math.Clamp(generatedLine - 1, 0, Math.Max(0, lines.Length - 1));
        for (var index = limit; index >= 0; index--)
        {
            var marker = lines[index].IndexOf(SourceMarker, StringComparison.Ordinal);
            if (marker < 0) continue;
            var value = lines[index][(marker + SourceMarker.Length)..].Trim();
            if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sourceLine))
                return sourceLine;
        }
        return null;
    }

    private static string ExtractPrimaryMessage(string details)
    {
        var normalized = details.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n')
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .ToArray();
        var line = lines.FirstOrDefault(static value => value.Contains("LaTeX Error:", StringComparison.Ordinal)) ??
                   lines.FirstOrDefault(static value => value.StartsWith("!", StringComparison.Ordinal)) ??
                   lines.FirstOrDefault(static value => value.Contains("document.tex:", StringComparison.Ordinal)) ??
                   lines.FirstOrDefault(static value =>
                       value.Contains("Error", StringComparison.OrdinalIgnoreCase) &&
                       !value.Contains("Fatal error occurred", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line)) return "LuaLaTeX build failed.";
        return line.TrimStart('!', ' ');
    }
}
