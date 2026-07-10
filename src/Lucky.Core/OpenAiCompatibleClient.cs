using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lucky.Core;

public interface ILlmClient
{
    Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings provider, string? apiKey, CancellationToken cancellationToken = default);

    Task<LlmResponse> CompleteChatAsync(
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default,
        IProgress<LlmStreamDelta>? streamProgress = null);
}

public sealed class OpenAiCompatibleClient : ILlmClient
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(140) };
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        ProviderSettings provider,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(provider.BaseUrl, "models"));
        AddAuth(request, ProviderApiKey(provider, apiKey));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Cast<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<LlmResponse> CompleteChatAsync(
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default,
        IProgress<LlmStreamDelta>? streamProgress = null)
    {
        var payload = BuildChatPayload(provider, messages, tools, streamProgress is not null);
        if (streamProgress is not null)
        {
            return await CompleteStreamingAsync(
                provider,
                apiKey,
                payload,
                tools,
                streamProgress,
                cancellationToken).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(provider.BaseUrl, "chat/completions"));
        AddAuth(request, ProviderApiKey(provider, apiKey));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: { TrimForDisplay(responseText, 700) }");
        }

        using var document = JsonDocument.Parse(responseText);
        return ParseCompletionResponse(document.RootElement, provider.Model, tools);
    }

    private static Dictionary<string, object?> BuildChatPayload(
        ProviderSettings provider,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["messages"] = messages.Select(SerializeMessage).ToArray(),
            ["stream"] = stream
        };

        if (stream && provider.SupportsThinking)
        {
            payload["stream_options"] = new
            {
                include_usage = true
            };
        }

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(SerializeTool).ToArray();
            // DeepSeek V4 thinking mode rejects tool_choice even though it supports tools.
            // Omitting the field retains the provider's automatic selection behavior.
            if (!IsDeepSeekV4Thinking(provider))
            {
                payload["tool_choice"] = "auto";
            }
        }

        if (provider.SupportsThinking)
        {
            payload["thinking"] = new
            {
                type = provider.ThinkingEnabled ? "enabled" : "disabled"
            };

            if (provider.ThinkingEnabled)
            {
                payload["reasoning_effort"] = provider.ReasoningEffort;
            }
            else
            {
                payload["temperature"] = 0.4;
            }
        }
        else
        {
            payload["temperature"] = 0.4;
        }

        return payload;
    }

    private async Task<LlmResponse> CompleteStreamingAsync(
        ProviderSettings provider,
        string? apiKey,
        Dictionary<string, object?> payload,
        IReadOnlyList<LlmToolDefinition>? tools,
        IProgress<LlmStreamDelta> streamProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(provider.BaseUrl, "chat/completions"));
        AddAuth(request, ProviderApiKey(provider, apiKey));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: { TrimForDisplay(responseText, 700) }");
        }

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new SortedDictionary<int, StreamingToolCall>();
        var model = provider.Model;
        LlmTokenUsage? usage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                break;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("model", out var modelElement))
            {
                model = modelElement.GetString() ?? model;
            }

            usage = TryParseUsage(root) ?? usage;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            var contentDelta = StringProperty(delta, "content");
            var reasoningDelta = StringProperty(delta, "reasoning_content");
            if (!string.IsNullOrEmpty(contentDelta))
            {
                content.Append(contentDelta);
            }

            if (!string.IsNullOrEmpty(reasoningDelta))
            {
                reasoning.Append(reasoningDelta);
            }

            // When tools are available, some OpenAI-compatible providers stream a textual
            // tool protocol in `content` instead of native tool_calls. Buffer answer text until
            // the response is classified so protocol markup can never leak into the chat UI.
            var reportContentDelta = tools is not { Count: > 0 } ? contentDelta : null;
            if (!string.IsNullOrEmpty(reportContentDelta) || !string.IsNullOrEmpty(reasoningDelta))
            {
                streamProgress.Report(new LlmStreamDelta(reportContentDelta ?? "", reasoningDelta));
            }

            AccumulateToolCallDeltas(delta, toolCalls);
        }

        IReadOnlyList<ToolCallRequest> parsedToolCalls = toolCalls
            .Values
            .Where(call => call.Name.Length > 0)
            .Select(call => new ToolCallRequest(
                call.Id.Length == 0 ? $"call_{Guid.NewGuid():N}" : call.Id.ToString(),
                call.Name.ToString(),
                call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()))
            .ToArray();

        var cleanContent = content.ToString().Trim();
        if (parsedToolCalls.Count == 0 && tools is { Count: > 0 })
        {
            var textual = TextualToolCallParser.Parse(cleanContent, tools.Select(tool => tool.Name));
            cleanContent = textual.CleanContent;
            parsedToolCalls = textual.ToolCalls;
        }

        if (parsedToolCalls.Count == 0 && tools is { Count: > 0 } && cleanContent.Length > 0)
        {
            streamProgress.Report(new LlmStreamDelta(cleanContent, null));
        }

        return new LlmResponse(
            cleanContent,
            model,
            parsedToolCalls,
            reasoning.Length == 0 ? null : reasoning.ToString(),
            usage);
    }

    private static LlmResponse ParseCompletionResponse(
        JsonElement root,
        string fallbackModel,
        IReadOnlyList<LlmToolDefinition>? tools)
    {
        var model = root.TryGetProperty("model", out var modelElement)
            ? modelElement.GetString() ?? fallbackModel
            : fallbackModel;

        var message = root.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind != JsonValueKind.Null
            ? contentElement.GetString()
            : "";
        var reasoningContent = message.TryGetProperty("reasoning_content", out var reasoningElement) &&
                               reasoningElement.ValueKind != JsonValueKind.Null
            ? reasoningElement.GetString()
            : null;
        IReadOnlyList<ToolCallRequest> toolCalls = ParseToolCalls(message);
        var cleanContent = content?.Trim() ?? "";
        if (toolCalls.Count == 0 && tools is { Count: > 0 })
        {
            var textual = TextualToolCallParser.Parse(cleanContent, tools.Select(tool => tool.Name));
            cleanContent = textual.CleanContent;
            toolCalls = textual.ToolCalls;
        }
        var usage = TryParseUsage(root);

        return new LlmResponse(cleanContent, model, toolCalls, reasoningContent, usage);
    }

    private static LlmTokenUsage? TryParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var promptTokens = IntProperty(usage, "prompt_tokens");
        return new LlmTokenUsage(
            promptTokens,
            IntProperty(usage, "completion_tokens"),
            IntProperty(usage, "total_tokens"),
            ContextTokens: promptTokens);
    }

    private static int? IntProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static void AccumulateToolCallDeltas(JsonElement delta, IDictionary<int, StreamingToolCall> toolCalls)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCallsElement) ||
            toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            var index = toolCall.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : toolCalls.Count;
            if (!toolCalls.TryGetValue(index, out var accumulator))
            {
                accumulator = new StreamingToolCall();
                toolCalls[index] = accumulator;
            }

            var id = StringProperty(toolCall, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                accumulator.Id.Clear();
                accumulator.Id.Append(id);
            }

            if (!toolCall.TryGetProperty("function", out var functionElement))
            {
                continue;
            }

            var name = StringProperty(functionElement, "name");
            if (!string.IsNullOrEmpty(name))
            {
                accumulator.Name.Append(name);
            }

            var arguments = StringProperty(functionElement, "arguments");
            if (!string.IsNullOrEmpty(arguments))
            {
                accumulator.Arguments.Append(arguments);
            }
        }
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;
    }

    private static object SerializeMessage(LlmChatMessage message)
    {
        var serialized = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            serialized["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            serialized["tool_calls"] = message.ToolCalls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = call.ArgumentsJson
                }
            }).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
        {
            // Required by DeepSeek thinking mode for every assistant tool-call message
            // replayed in subsequent rounds.
            serialized["reasoning_content"] = message.ReasoningContent;
        }

        return serialized;
    }

    private static object SerializeTool(LlmToolDefinition tool)
    {
        object parameters;
        if (tool.InputSchema is JsonElement inputSchema)
        {
            // MCP tools can have nested JSON Schema. Preserve it exactly instead of flattening it
            // into Lucky's simpler built-in tool parameter representation.
            parameters = inputSchema;
        }
        else
        {
            var properties = tool.Parameters.ToDictionary(
                pair => pair.Key,
                pair => (object)new
                {
                    type = pair.Value.Type,
                    description = pair.Value.Description
                },
                StringComparer.Ordinal);
            parameters = new
            {
                type = "object",
                properties,
                required = tool.Required.ToArray(),
                additionalProperties = false
            };
        }

        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters
            }
        };
    }

    private static IReadOnlyList<ToolCallRequest> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement) ||
            toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var toolCalls = new List<ToolCallRequest>();
        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            var id = toolCall.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? IdFallback()
                : IdFallback();
            if (!toolCall.TryGetProperty("function", out var functionElement))
            {
                continue;
            }

            var name = functionElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var arguments = functionElement.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.GetString() ?? "{}"
                : "{}";
            toolCalls.Add(new ToolCallRequest(id, name, arguments));
        }

        return toolCalls;

        static string IdFallback() => $"call_{Guid.NewGuid():N}";
    }

    private static Uri Endpoint(string baseUrl, string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:1234/v1" : baseUrl.Trim();
        return new Uri($"{trimmed.TrimEnd('/')}/{path}");
    }

    private static bool IsDeepSeekV4Thinking(ProviderSettings provider) =>
        provider.ThinkingEnabled &&
        provider.Model.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase);

    private static void AddAuth(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    private static string? ProviderApiKey(ProviderSettings provider, string? apiKey) =>
        string.Equals(provider.DisplayName, "LM Studio", StringComparison.OrdinalIgnoreCase)
            ? null
            : apiKey;

    private static string TrimForDisplay(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}...";
    }

    private sealed class StreamingToolCall
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}
