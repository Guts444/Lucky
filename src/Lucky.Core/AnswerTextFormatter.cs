using System.Text.RegularExpressions;

namespace Lucky.Core;

public static partial class AnswerTextFormatter
{
    public static string ForPlainChat(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var hadToolProtocol = TextualToolCallParser.ContainsProtocolMarkup(content);
        content = TextualToolCallParser.CleanForDisplay(content);
        if (hadToolProtocol && string.IsNullOrWhiteSpace(content))
        {
            return "This earlier response contained an unexecuted tool request, so Lucky hid its internal protocol markup.";
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var output = new List<string>();
        var blankCount = 0;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed is "---" or "***" or "___")
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                line = StripHeadingMarks(line);
            }

            line = StrongMarkerRegex().Replace(
                line,
                match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            line = line.Replace("**", "", StringComparison.Ordinal)
                .Replace("__", "", StringComparison.Ordinal);

            if (string.IsNullOrWhiteSpace(line))
            {
                blankCount++;
                if (blankCount <= 1 && output.Count > 0)
                {
                    output.Add(string.Empty);
                }

                continue;
            }

            blankCount = 0;
            output.Add(line);
        }

        return string.Join(Environment.NewLine, output).Trim();
    }

    private static string StripHeadingMarks(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        while (index < line.Length && line[index] == '#')
        {
            index++;
        }

        if (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        return line[index..];
    }

    [GeneratedRegex(@"\*\*([^*]+)\*\*|__([^_]+)__")]
    private static partial Regex StrongMarkerRegex();
}
