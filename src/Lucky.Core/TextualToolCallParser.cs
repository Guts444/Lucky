using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucky.Core;

/// <summary>
/// Compatibility parser for providers that occasionally return DeepSeek's textual DSML
/// tool dialect in assistant content instead of populating the OpenAI tool_calls field.
/// Only tool names already exposed in the current request are accepted.
/// </summary>
internal static partial class TextualToolCallParser
{
    public static bool ContainsProtocolMarkup(string? content) =>
        !string.IsNullOrWhiteSpace(content) && LooksLikeDsml(content);

    public static string CleanForDisplay(string? content) => Parse(content, []).CleanContent;

    public static TextualToolCallParseResult Parse(string? content, IEnumerable<string> allowedToolNames)
    {
        var text = content?.Trim() ?? "";
        if (text.Length == 0 || !LooksLikeDsml(text))
        {
            return new TextualToolCallParseResult(text, []);
        }

        var allowed = allowedToolNames.ToHashSet(StringComparer.Ordinal);
        var calls = new List<ToolCallRequest>();
        foreach (Match invoke in InvokeRegex().Matches(text))
        {
            var name = invoke.Groups["name"].Value;
            if (!allowed.Contains(name))
            {
                continue;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (Match parameter in ParameterRegex().Matches(invoke.Groups["body"].Value))
            {
                var parameterName = parameter.Groups["name"].Value;
                if (parameterName.Length == 0)
                {
                    continue;
                }

                arguments[parameterName] = ParseParameterValue(
                    parameter.Groups["value"].Value.Trim(),
                    parameter.Groups["attrs"].Value);
            }

            calls.Add(new ToolCallRequest(
                $"dsml_{Guid.NewGuid():N}",
                name,
                JsonSerializer.Serialize(arguments)));
        }

        // Even malformed/unsupported DSML is implementation detail, never user-facing prose.
        var clean = ProtocolBlockRegex().Replace(text, "").Trim();
        clean = InvokeRegex().Replace(clean, "").Trim();
        clean = LooseProtocolTagRegex().Replace(clean, "").Trim();
        return new TextualToolCallParseResult(clean, calls);
    }

    private static object? ParseParameterValue(string value, string attributes)
    {
        if (StringAttributeRegex().IsMatch(attributes))
        {
            return value;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static bool LooksLikeDsml(string text) => DsmlMarkerRegex().IsMatch(text);

    private const string Marker = @"[|\uFF5C](?:\s*[|\uFF5C])?\s*DSML\s*[|\uFF5C](?:\s*[|\uFF5C])?";

    [GeneratedRegex(@"<\s*" + Marker, RegexOptions.IgnoreCase)]
    private static partial Regex DsmlMarkerRegex();

    [GeneratedRegex(
        @"<\s*" + Marker + @"\s*invoke\s+name\s*=\s*[\""'](?<name>[^\""']+)[\""'][^>]*>(?<body>.*?)</\s*" + Marker + @"\s*invoke\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InvokeRegex();

    [GeneratedRegex(
        @"<\s*" + Marker + @"\s*parameter\s+(?<attrs>[^>]*?\bname\s*=\s*[\""'](?<name>[^\""']+)[\""'][^>]*)>(?<value>.*?)</\s*" + Marker + @"\s*parameter\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParameterRegex();

    [GeneratedRegex(@"\bstring\s*=\s*[\""']true[\""']", RegexOptions.IgnoreCase)]
    private static partial Regex StringAttributeRegex();

    [GeneratedRegex(
        @"<\s*" + Marker + @"\s*tool_calls\b[^>]*>.*?</\s*" + Marker + @"\s*tool_calls\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ProtocolBlockRegex();

    [GeneratedRegex(@"</?\s*" + Marker + @"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LooseProtocolTagRegex();
}

internal sealed record TextualToolCallParseResult(
    string CleanContent,
    IReadOnlyList<ToolCallRequest> ToolCalls);
