using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class LineMetricsAnalyzer : ILineMetricsAnalyzer
{
    public LineMetrics Analyze(string text, LanguageDefinition language, FileCategory category)
    {
        var lines = SplitLines(text);
        long blank = 0, code = 0, comments = 0, mixed = 0, content = 0;

        if (category == FileCategory.DocumentationText)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) blank++;
                else content++;
            }
            return new(lines.Count, blank, 0, 0, 0, content);
        }

        string? activeBlockEnd = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) && activeBlockEnd is null)
            {
                blank++;
                continue;
            }

            var state = AnalyzeLine(line, language, ref activeBlockEnd);
            if (state.HasCode)
            {
                code++;
                if (state.HasComment) mixed++;
            }
            else if (state.HasComment) comments++;
            else if (string.IsNullOrWhiteSpace(line)) blank++;
            else code++;
        }

        return new(lines.Count, blank, code, comments, mixed, 0);
    }

    private static (bool HasCode, bool HasComment) AnalyzeLine(string line, LanguageDefinition language,
        ref string? activeBlockEnd)
    {
        var hasCode = false;
        var hasComment = false;
        char? quote = null;
        var escaped = false;

        for (var index = 0; index < line.Length;)
        {
            if (activeBlockEnd is not null)
            {
                hasComment = true;
                var end = line.IndexOf(activeBlockEnd, index, StringComparison.Ordinal);
                if (end < 0) break;
                index = end + activeBlockEnd.Length;
                activeBlockEnd = null;
                continue;
            }

            var ch = line[index];
            if (quote is not null)
            {
                hasCode = true;
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == quote) quote = null;
                index++;
                continue;
            }

            if (ch is '\'' or '"' or '`')
            {
                hasCode = true;
                quote = ch;
                index++;
                continue;
            }

            var lineMarker = language.LineCommentMarkers.FirstOrDefault(marker =>
                StartsWith(line, index, marker) && IsValidLineMarker(line, index, marker));
            if (lineMarker is not null)
            {
                hasComment = true;
                break;
            }

            var block = language.BlockCommentPairs.FirstOrDefault(pair => StartsWith(line, index, pair.Start));
            if (block is not null)
            {
                hasComment = true;
                activeBlockEnd = block.End;
                index += block.Start.Length;
                continue;
            }

            if (!char.IsWhiteSpace(ch)) hasCode = true;
            index++;
        }

        return (hasCode, hasComment);
    }

    private static bool StartsWith(string line, int index, string marker) =>
        index + marker.Length <= line.Length && line.AsSpan(index, marker.Length).SequenceEqual(marker);

    private static bool IsValidLineMarker(string line, int index, string marker)
    {
        if (!marker.Equals("REM ", StringComparison.OrdinalIgnoreCase)) return true;
        return index == 0 || char.IsWhiteSpace(line[index - 1]);
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var result = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) result.Add(line);
        return result;
    }
}
