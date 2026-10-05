using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Post-processes source-mapped LaTeX so rich editor blocks have deterministic PDF output
/// without shell-escape or external Mermaid/ABC executables.
/// </summary>
internal static class RichPdfPostProcessor
{
    private const string SourceMarkerPrefix = "% TYPESCRIBE-SOURCE:";
    private const string EmojiSupportMarker = "% TYPESCRIBE-EMOJI-SUPPORT";

    private static readonly Regex FlowNodeRegex = new(
        @"(?<![A-Za-z0-9_-])(?<id>[A-Za-z_][A-Za-z0-9_-]*)(?:(?:\[\[(?<label1>[^\]]+)\]\])|(?:\[(?<label2>[^\]]+)\])|(?:\(\((?<label3>[^\)]+)\)\))|(?:\((?<label4>[^\)]+)\))|(?:\{(?<label5>[^\}]+)\}))?",
        RegexOptions.CultureInvariant);

    private static readonly Regex AbcTokenRegex = new(
        @"(?<bar>\|+)|(?<note>(?<acc>\^{1,2}|_{1,2}|=)?(?<pitch>[A-Ga-gzZ])(?<oct>[,']*)(?<len>\d+(?:/\d+)?|/+)?)",
        RegexOptions.CultureInvariant);

    internal static string Apply(string latex, DocumentAst document)
    {
        if (string.IsNullOrEmpty(latex) || document.Blocks.Count == 0) return latex;

        var richBlocks = document.Blocks
            .OfType<CodeBlock>()
            .Where(static block => IsMermaid(block.Language) || IsAbc(block.Language))
            .ToArray();

        foreach (var block in richBlocks)
        {
            var replacement = IsMermaid(block.Language)
                ? RenderMermaidLatex(block.Text)
                : RenderAbcLatex(block.Text);
            latex = ReplaceSourceMappedListing(latex, block.SourceLine, replacement);
        }

        var hasEmoji = ContainsKnownEmojiOrShortcode(latex);
        if (hasEmoji)
        {
            latex = ReplaceEmojiOutsideListings(latex);
            latex = InjectEmojiSupport(latex);
        }

        return latex;
    }

    private static bool IsMermaid(string language)
        => string.Equals(language.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbc(string language)
        => string.Equals(language.Trim(), "abc", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(language.Trim(), "abcnotation", StringComparison.OrdinalIgnoreCase);

    private static string ReplaceSourceMappedListing(string latex, int sourceLine, string replacement)
    {
        var marker = SourceMarkerPrefix + Math.Max(1, sourceLine).ToString(CultureInfo.InvariantCulture);
        var markerIndex = latex.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return latex;

        var nextMarker = latex.IndexOf(SourceMarkerPrefix, markerIndex + marker.Length, StringComparison.Ordinal);
        var listingStart = latex.IndexOf("\\begin{lstlisting}", markerIndex + marker.Length, StringComparison.Ordinal);
        if (listingStart < 0 || (nextMarker >= 0 && listingStart > nextMarker)) return latex;

        var listingEnd = latex.IndexOf("\\end{lstlisting}", listingStart, StringComparison.Ordinal);
        if (listingEnd < 0 || (nextMarker >= 0 && listingEnd > nextMarker)) return latex;
        listingEnd += "\\end{lstlisting}".Length;

        return latex[..listingStart] + replacement + latex[listingEnd..];
    }

    private static string RenderMermaidLatex(string source)
    {
        var normalized = NormalizeLines(source);
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return RenderFallbackBox("Mermaid diagram", "Empty diagram");

        return lines[0].StartsWith("classDiagram", StringComparison.OrdinalIgnoreCase)
            ? RenderClassDiagram(lines)
            : RenderFlowDiagram(lines);
    }

    private static string RenderClassDiagram(IReadOnlyList<string> lines)
    {
        var classes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        var edges = new List<DiagramEdge>();
        string? currentClass = null;

        void EnsureClass(string id)
        {
            if (classes.ContainsKey(id)) return;
            classes[id] = [];
            order.Add(id);
        }

        for (var index = 1; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;

            if (currentClass is not null)
            {
                if (line.StartsWith('}'))
                {
                    currentClass = null;
                    continue;
                }
                classes[currentClass].Add(line.Trim().TrimEnd(';'));
                continue;
            }

            if (line.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
            {
                var body = line[6..].Trim();
                var brace = body.IndexOf('{');
                var name = (brace >= 0 ? body[..brace] : body).Trim();
                if (name.Length > 0)
                {
                    EnsureClass(name);
                    if (brace >= 0) currentClass = name;
                }
                continue;
            }

            if (TryParseMermaidEdge(line, out var edge))
            {
                EnsureClass(edge.From);
                EnsureClass(edge.To);
                edges.Add(edge);
                continue;
            }
        }

        if (order.Count == 0)
        {
            foreach (var line in lines.Skip(1))
            {
                foreach (Match match in FlowNodeRegex.Matches(line))
                {
                    var id = match.Groups["id"].Value;
                    if (IsMermaidKeyword(id)) continue;
                    EnsureClass(id);
                }
            }
        }

        if (order.Count == 0) return RenderFallbackBox("Mermaid class diagram", "No classes found");

        var positions = GridPositions(order.Count, horizontalFirst: true, cellWidth: 48, cellHeight: 30, columns: 2);
        var width = Math.Max(110, positions.Max(static p => p.X) + 44);
        var height = Math.Max(45, positions.Max(static p => p.Y) + 26);
        var output = BeginPicture(width, height);

        for (var index = 0; index < order.Count; index++)
        {
            var name = order[index];
            var members = classes[name].Take(7).ToArray();
            var pos = positions[index];
            var content = new StringBuilder();
            content.Append("\\centering\\textbf{").Append(EscapeLatex(name)).Append('}');
            if (members.Length > 0)
            {
                content.Append("\\\\[-0.5mm]\\rule{38mm}{0.2pt}\\\\[-0.5mm]\\raggedright\\scriptsize ");
                content.Append(string.Join("\\\\", members.Select(EscapeLatex)));
            }
            output.Append("\\put(").Append(F(pos.X)).Append(',').Append(F(pos.Y)).Append("){\\framebox(42,22){\\parbox[c][20mm][c]{38mm}{")
                .Append(content).AppendLine("}}}");
        }

        AppendEdges(output, order, positions, edges, boxWidth: 42, boxHeight: 22);
        EndPicture(output);
        return output.ToString();
    }

    private static string RenderFlowDiagram(IReadOnlyList<string> lines)
    {
        var first = lines[0];
        var horizontal = first.Contains(" LR", StringComparison.OrdinalIgnoreCase) ||
                         first.Contains(" RL", StringComparison.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        var edges = new List<DiagramEdge>();

        void EnsureNode(string id, string? label = null)
        {
            if (!nodes.ContainsKey(id)) order.Add(id);
            nodes[id] = string.IsNullOrWhiteSpace(label) ? (nodes.TryGetValue(id, out var old) ? old : id) : label.Trim();
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("%%", StringComparison.Ordinal)) continue;
            var matches = FlowNodeRegex.Matches(line);
            foreach (Match match in matches)
            {
                var id = match.Groups["id"].Value;
                if (IsMermaidKeyword(id)) continue;
                var label = Enumerable.Range(1, 5)
                    .Select(number => match.Groups["label" + number].Value)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
                EnsureNode(id, label);
            }
            if (TryParseMermaidEdge(line, out var edge))
            {
                EnsureNode(edge.From);
                EnsureNode(edge.To);
                edges.Add(edge);
            }
        }

        if (order.Count == 0) return RenderFallbackBox("Mermaid diagram", "No nodes found");

        var positions = horizontal
            ? GridPositions(order.Count, horizontalFirst: true, cellWidth: 42, cellHeight: 22, columns: Math.Min(4, order.Count))
            : GridPositions(order.Count, horizontalFirst: false, cellWidth: 42, cellHeight: 22, columns: Math.Min(3, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(order.Count)))));
        var width = Math.Max(100, positions.Max(static p => p.X) + 36);
        var height = Math.Max(38, positions.Max(static p => p.Y) + 18);
        var output = BeginPicture(width, height);

        for (var index = 0; index < order.Count; index++)
        {
            var id = order[index];
            var pos = positions[index];
            output.Append("\\put(").Append(F(pos.X)).Append(',').Append(F(pos.Y)).Append("){\\framebox(34,12){\\parbox[c][10mm][c]{30mm}{\\centering\\small ")
                .Append(EscapeLatex(nodes[id])).AppendLine("}}}");
        }

        AppendEdges(output, order, positions, edges, boxWidth: 34, boxHeight: 12);
        EndPicture(output);
        return output.ToString();
    }

    private static bool TryParseMermaidEdge(string line, out DiagramEdge edge)
    {
        edge = default!;
        var matches = FlowNodeRegex.Matches(line);
        if (matches.Count < 2) return false;
        if (!line.Contains("--", StringComparison.Ordinal) && !line.Contains("..", StringComparison.Ordinal) && !line.Contains("==", StringComparison.Ordinal))
            return false;

        var from = matches[0].Groups["id"].Value;
        var to = matches[matches.Count - 1].Groups["id"].Value;
        if (IsMermaidKeyword(from) || IsMermaidKeyword(to) || string.Equals(from, to, StringComparison.Ordinal)) return false;

        var label = string.Empty;
        var colon = line.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < line.Length) label = line[(colon + 1)..].Trim();
        else
        {
            var pipeStart = line.IndexOf('|');
            var pipeEnd = pipeStart >= 0 ? line.IndexOf('|', pipeStart + 1) : -1;
            if (pipeStart >= 0 && pipeEnd > pipeStart) label = line[(pipeStart + 1)..pipeEnd].Trim();
        }

        edge = new DiagramEdge(from, to, label);
        return true;
    }

    private static void AppendEdges(
        StringBuilder output,
        IReadOnlyList<string> order,
        IReadOnlyList<Point> positions,
        IReadOnlyList<DiagramEdge> edges,
        double boxWidth,
        double boxHeight)
    {
        var indexById = order.Select((id, index) => (id, index)).ToDictionary(static pair => pair.id, static pair => pair.index, StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!indexById.TryGetValue(edge.From, out var fromIndex) || !indexById.TryGetValue(edge.To, out var toIndex)) continue;
            var from = positions[fromIndex];
            var to = positions[toIndex];
            var x1 = from.X + boxWidth / 2;
            var y1 = from.Y + boxHeight / 2;
            var x2 = to.X + boxWidth / 2;
            var y2 = to.Y + boxHeight / 2;
            var midX = (x1 + x2) / 2;
            var midY = (y1 + y2) / 2;

            output.Append("\\qbezier(").Append(F(x1)).Append(',').Append(F(y1)).Append(")(")
                .Append(F(midX)).Append(',').Append(F(midY)).Append(")(")
                .Append(F(x2)).Append(',').Append(F(y2)).AppendLine(")");
            output.Append("\\put(").Append(F(x2)).Append(',').Append(F(y2)).AppendLine("){\\makebox(0,0){$\\triangleright$}}");
            if (!string.IsNullOrWhiteSpace(edge.Label))
                output.Append("\\put(").Append(F(midX)).Append(',').Append(F(midY + 2)).Append("){\\makebox(0,0)[b]{\\scriptsize ")
                    .Append(EscapeLatex(edge.Label)).AppendLine("}}");
        }
    }

    private static IReadOnlyList<Point> GridPositions(int count, bool horizontalFirst, double cellWidth, double cellHeight, int columns)
    {
        columns = Math.Max(1, columns);
        var points = new List<Point>(count);
        for (var index = 0; index < count; index++)
        {
            int row;
            int column;
            if (horizontalFirst)
            {
                row = index / columns;
                column = index % columns;
            }
            else
            {
                row = index;
                column = 0;
                if (count > 6)
                {
                    row = index / columns;
                    column = index % columns;
                }
            }
            points.Add(new Point(6 + column * cellWidth, 7 + row * cellHeight));
        }
        return points;
    }

    private static string RenderAbcLatex(string source)
    {
        var lines = NormalizeLines(source).Split('\n');
        var titleLine = lines.FirstOrDefault(static line => line.StartsWith("T:", StringComparison.OrdinalIgnoreCase));
        var meterLine = lines.FirstOrDefault(static line => line.StartsWith("M:", StringComparison.OrdinalIgnoreCase));
        var keyLine = lines.FirstOrDefault(static line => line.StartsWith("K:", StringComparison.OrdinalIgnoreCase));
        var title = titleLine is null ? "Music" : titleLine[2..].Trim();
        var meter = meterLine is null ? string.Empty : meterLine[2..].Trim();
        var key = keyLine is null ? string.Empty : keyLine[2..].Trim();
        var body = string.Join(" ", lines.Where(static line => line.Length > 0 && !IsAbcHeader(line) && !line.TrimStart().StartsWith('%')));
        var tokens = AbcTokenRegex.Matches(body).Cast<Match>().Take(96).ToArray();
        if (tokens.Length == 0) return RenderFallbackBox("ABC music", "No notes found");

        const int symbolsPerStaff = 24;
        var staffCount = Math.Max(1, (int)Math.Ceiling(tokens.Length / (double)symbolsPerStaff));
        var pictureHeight = 20 + staffCount * 18;
        var output = BeginPicture(112, pictureHeight);
        output.Append("\\put(4,").Append(F(pictureHeight - 5)).Append("){\\makebox(104,0)[l]{\\textbf{")
            .Append(EscapeLatex(title)).Append('}');
        if (meter.Length > 0 || key.Length > 0)
            output.Append("\\hfill\\scriptsize ").Append(EscapeLatex(string.Join("  ", new[] { meter, key }.Where(static value => value.Length > 0))));
        output.AppendLine("}}");

        for (var staff = 0; staff < staffCount; staff++)
        {
            var baseY = pictureHeight - 14 - staff * 18;
            for (var line = 0; line < 5; line++)
                output.Append("\\put(6,").Append(F(baseY + line * 2)).AppendLine("){\\line(1,0){100}}");
        }

        var noteIndex = 0;
        foreach (var token in tokens)
        {
            var staff = noteIndex / symbolsPerStaff;
            var slot = noteIndex % symbolsPerStaff;
            var baseY = pictureHeight - 14 - staff * 18;
            var x = 9 + slot * 4.05;
            noteIndex++;

            if (token.Groups["bar"].Success)
            {
                output.Append("\\put(").Append(F(x)).Append(',').Append(F(baseY)).AppendLine("){\\line(0,1){8}}");
                continue;
            }

            var pitch = token.Groups["pitch"].Value;
            if (pitch.Equals("z", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("\\put(").Append(F(x)).Append(',').Append(F(baseY + 3.5)).AppendLine("){\\rule{1.6mm}{0.7mm}}");
                continue;
            }

            var step = PitchStep(pitch[0], token.Groups["oct"].Value);
            var y = baseY + step;
            AppendLedgerLines(output, x, baseY, step);
            var accidental = token.Groups["acc"].Value;
            if (accidental.Length > 0)
            {
                var glyph = accidental[0] == '^' ? "\\sharp" : accidental[0] == '_' ? "\\flat" : "\\natural";
                output.Append("\\put(").Append(F(x - 1.8)).Append(',').Append(F(y - 0.5)).Append("){\\makebox(0,0){$\\scriptstyle ")
                    .Append(glyph).AppendLine("$}}");
            }

            output.Append("\\put(").Append(F(x)).Append(',').Append(F(y)).AppendLine("){\\circle*{1.6}}");
            if (step <= 4)
                output.Append("\\put(").Append(F(x + 0.75)).Append(',').Append(F(y)).AppendLine("){\\line(0,1){5}}");
            else
                output.Append("\\put(").Append(F(x - 0.75)).Append(',').Append(F(y - 5)).AppendLine("){\\line(0,1){5}}");
        }

        EndPicture(output);
        return output.ToString();
    }

    private static int PitchStep(char pitch, string octaveMarks)
    {
        var baseStep = pitch switch
        {
            'C' => -2, 'D' => -1, 'E' => 0, 'F' => 1, 'G' => 2, 'A' => 3, 'B' => 4,
            'c' => 5, 'd' => 6, 'e' => 7, 'f' => 8, 'g' => 9, 'a' => 10, 'b' => 11,
            _ => 0
        };
        foreach (var mark in octaveMarks)
            baseStep += mark == '\'' ? 7 : mark == ',' ? -7 : 0;
        return baseStep;
    }

    private static void AppendLedgerLines(StringBuilder output, double x, double baseY, int step)
    {
        if (step <= -2)
        {
            for (var ledger = -2; ledger >= step; ledger -= 2)
                output.Append("\\put(").Append(F(x - 1.7)).Append(',').Append(F(baseY + ledger)).AppendLine("){\\line(1,0){3.4}}");
        }
        else if (step >= 10)
        {
            for (var ledger = 10; ledger <= step; ledger += 2)
                output.Append("\\put(").Append(F(x - 1.7)).Append(',').Append(F(baseY + ledger)).AppendLine("){\\line(1,0){3.4}}");
        }
    }

    private static bool IsAbcHeader(string line)
        => line.Length >= 2 && char.IsLetter(line[0]) && line[1] == ':';

    private static StringBuilder BeginPicture(double width, double height)
    {
        var output = new StringBuilder();
        output.AppendLine("\\par\\medskip\\begin{center}");
        output.AppendLine("\\setlength{\\unitlength}{1mm}%");
        output.Append("\\begin{picture}(").Append(F(width)).Append(',').Append(F(height)).AppendLine(")");
        output.AppendLine("\\linethickness{0.35pt}%");
        return output;
    }

    private static void EndPicture(StringBuilder output)
    {
        output.AppendLine("\\end{picture}");
        output.Append("\\end{center}\\medskip\\par");
    }

    private static string RenderFallbackBox(string title, string message)
        => "\\par\\medskip\\begin{center}\\fbox{\\parbox{0.85\\linewidth}{\\centering\\textbf{" +
           EscapeLatex(title) + "}\\\\\\smallskip " + EscapeLatex(message) + "}}\\end{center}\\medskip\\par";

    private static bool IsMermaidKeyword(string id)
        => id.Equals("flowchart", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("graph", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("classDiagram", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("TD", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("TB", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("BT", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("LR", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("RL", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("class", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsKnownEmojiOrShortcode(string latex)
        => EmojiEntries.Any(entry => latex.Contains(entry.Emoji, StringComparison.Ordinal) || latex.Contains($":{entry.Shortcode}:", StringComparison.Ordinal));

    private static string ReplaceEmojiOutsideListings(string latex)
    {
        var output = new StringBuilder(latex.Length + 128);
        var cursor = 0;
        while (cursor < latex.Length)
        {
            var listing = latex.IndexOf("\\begin{lstlisting}", cursor, StringComparison.Ordinal);
            if (listing < 0)
            {
                output.Append(ReplaceEmojiSegment(latex[cursor..]));
                break;
            }

            output.Append(ReplaceEmojiSegment(latex[cursor..listing]));
            var end = latex.IndexOf("\\end{lstlisting}", listing, StringComparison.Ordinal);
            if (end < 0)
            {
                output.Append(latex[listing..]);
                break;
            }
            end += "\\end{lstlisting}".Length;
            output.Append(latex[listing..end]);
            cursor = end;
        }
        return output.ToString();
    }

    private static string ReplaceEmojiSegment(string segment)
    {
        foreach (var entry in EmojiEntries.OrderByDescending(static entry => entry.Emoji.Length))
        {
            var replacement = $"\\TypescribeEmoji{{{entry.Emoji}}}{{{entry.Shortcode}}}";
            segment = segment.Replace(entry.Emoji, replacement, StringComparison.Ordinal);
            segment = segment.Replace($":{entry.Shortcode}:", replacement, StringComparison.Ordinal);
        }
        return segment;
    }

    private static string InjectEmojiSupport(string latex)
    {
        if (latex.Contains(EmojiSupportMarker, StringComparison.Ordinal)) return latex;
        const string marker = "\\usepackage{fontspec}";
        var support = string.Join(Environment.NewLine,
        [
            EmojiSupportMarker,
            "\\IfFontExistsTF{Noto Emoji}{%",
            "  \\newfontfamily\\TypescribeEmojiFont{Noto Emoji}[Renderer=HarfBuzz,BoldFont={Noto Emoji},ItalicFont={Noto Emoji},BoldItalicFont={Noto Emoji}]%",
            "  \\newcommand{\\TypescribeEmoji}[2]{{\\normalfont\\TypescribeEmojiFont #1}}%",
            "}{\\IfFontExistsTF{Segoe UI Emoji}{%",
            "  \\newfontfamily\\TypescribeEmojiFont{Segoe UI Emoji}[Renderer=HarfBuzz,BoldFont={Segoe UI Emoji},ItalicFont={Segoe UI Emoji},BoldItalicFont={Segoe UI Emoji}]%",
            "  \\newcommand{\\TypescribeEmoji}[2]{{\\normalfont\\TypescribeEmojiFont #1}}%",
            "}{\\IfFontExistsTF{Apple Color Emoji}{%",
            "  \\newfontfamily\\TypescribeEmojiFont{Apple Color Emoji}[Renderer=HarfBuzz,BoldFont={Apple Color Emoji},ItalicFont={Apple Color Emoji},BoldItalicFont={Apple Color Emoji}]%",
            "  \\newcommand{\\TypescribeEmoji}[2]{{\\normalfont\\TypescribeEmojiFont #1}}%",
            "}{%",
            "  \\newcommand{\\TypescribeEmoji}[2]{\\texttt{:#2:}}%",
            "}}}"
        ]);
        var index = latex.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return support + Environment.NewLine + latex;
        index += marker.Length;
        return latex.Insert(index, Environment.NewLine + support);
    }

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

    private static string NormalizeLines(string source)
        => (source ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record DiagramEdge(string From, string To, string Label);
    private sealed record Point(double X, double Y);
    private sealed record EmojiEntry(string Emoji, string Shortcode);

    private static readonly EmojiEntry[] EmojiEntries =
    [
        new("❤️", "heart"), new("⚠️", "warning"), new("😀", "grinning"), new("😃", "smiley"),
        new("😄", "smile"), new("😂", "joy"), new("😊", "blush"), new("😍", "heart_eyes"),
        new("🤔", "thinking"), new("👍", "thumbsup"), new("👎", "thumbsdown"), new("👏", "clap"),
        new("🙏", "pray"), new("💡", "bulb"), new("✅", "white_check_mark"), new("❌", "x"),
        new("⭐", "star"), new("🔥", "fire"), new("🎵", "musical_note"), new("🎉", "tada"), new("🚀", "rocket")
    ];
}
