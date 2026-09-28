using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

internal sealed class BookStyleStore
{
    private const string StyleFolder = "styles";
    private const string StyleFile = "book.style";

    public BookStyle Load(string projectRoot)
    {
        var path = GetPath(projectRoot);
        if (!File.Exists(path)) return BookStyle.Default;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var style = BookStyle.Default with
        {
            Name = Get(values, "name", BookStyle.Default.Name),
            PageWidthInches = GetDouble(values, "page-width-in", BookStyle.Default.PageWidthInches),
            PageHeightInches = GetDouble(values, "page-height-in", BookStyle.Default.PageHeightInches),
            MarginTopInches = GetDouble(values, "margin-top-in", BookStyle.Default.MarginTopInches),
            MarginBottomInches = GetDouble(values, "margin-bottom-in", BookStyle.Default.MarginBottomInches),
            MarginInnerInches = GetDouble(values, "margin-inner-in", BookStyle.Default.MarginInnerInches),
            MarginOuterInches = GetDouble(values, "margin-outer-in", BookStyle.Default.MarginOuterInches),
            BodyFontFamily = Get(values, "body-font", BookStyle.Default.BodyFontFamily),
            BodyFontSizePoints = GetDouble(values, "body-font-size-pt", BookStyle.Default.BodyFontSizePoints),
            LineSpacing = GetDouble(values, "line-spacing", BookStyle.Default.LineSpacing),
            ParagraphIndentEm = GetDouble(values, "paragraph-indent-em", BookStyle.Default.ParagraphIndentEm),
            ParagraphSpacingPoints = GetDouble(values, "paragraph-spacing-pt", BookStyle.Default.ParagraphSpacingPoints),
            JustifyBody = GetBool(values, "justify-body", BookStyle.Default.JustifyBody)
        };
        return style.Validate();
    }

    public Task SaveAsync(string projectRoot, BookStyle style, CancellationToken cancellationToken = default)
    {
        style.Validate();
        var builder = new StringBuilder();
        builder.AppendLine("# Typescribe basic book style");
        Append(builder, "name", style.Name);
        Append(builder, "page-width-in", style.PageWidthInches);
        Append(builder, "page-height-in", style.PageHeightInches);
        Append(builder, "margin-top-in", style.MarginTopInches);
        Append(builder, "margin-bottom-in", style.MarginBottomInches);
        Append(builder, "margin-inner-in", style.MarginInnerInches);
        Append(builder, "margin-outer-in", style.MarginOuterInches);
        Append(builder, "body-font", style.BodyFontFamily);
        Append(builder, "body-font-size-pt", style.BodyFontSizePoints);
        Append(builder, "line-spacing", style.LineSpacing);
        Append(builder, "paragraph-indent-em", style.ParagraphIndentEm);
        Append(builder, "paragraph-spacing-pt", style.ParagraphSpacingPoints);
        Append(builder, "justify-body", style.JustifyBody ? "true" : "false");
        return AtomicFileWriter.WriteTextAsync(GetPath(projectRoot), builder.ToString(), cancellationToken);
    }

    private static string GetPath(string projectRoot)
    {
        var folder = Path.Combine(projectRoot, StyleFolder);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, StyleFile);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
        => values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static void Append(StringBuilder builder, string key, string value)
        => builder.Append(key).Append(" = ").AppendLine(value);

    private static void Append(StringBuilder builder, string key, double value)
        => Append(builder, key, value.ToString("0.###", CultureInfo.InvariantCulture));
}
