using System.Text;

namespace Lucky.Core;

/// <summary>
/// Parses the small, shell-free command-line syntax exposed in Lucky's MCP settings. The parsed
/// values are passed through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so
/// characters such as |, &amp;, and ; are never evaluated by a shell.
/// </summary>
public static class CommandLineArgumentParser
{
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var argumentStarted = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length && value[index + 1] == '"')
            {
                current.Append('"');
                argumentStarted = true;
                index++;
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                argumentStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (argumentStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    argumentStarted = false;
                }

                continue;
            }

            current.Append(character);
            argumentStarted = true;
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("MCP arguments contain an unmatched double quote.");
        }

        if (argumentStarted)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    public static string Format(IEnumerable<string>? arguments) => string.Join(
        " ",
        (arguments ?? []).Select(FormatOne));

    private static string FormatOne(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }
}
