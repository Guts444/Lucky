using System.Net;
using System.Text;
using System.Text.Json;
using Lucky.Core;

namespace Lucky.Tests;

public sealed class OpenAiCompatibleClientTests
{
    [Fact]
    public async Task CompleteChatAsync_SendsExpectedReasoningPayloadAndAuthHeader()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """
            {
              "model": "reasoner-v1",
              "usage": {
                "prompt_tokens": 12,
                "completion_tokens": 7,
                "total_tokens": 19
              },
              "choices": [
                {
                  "message": {
                    "content": " Done. ",
                    "reasoning_content": "I checked the request."
                  }
                }
              ]
            }
            """));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "https://provider.example/v1/",
            Model = "reasoner-v1",
            SupportsThinking = true,
            ThinkingEnabled = true,
            ReasoningEffort = "high"
        };

        var response = await client.CompleteChatAsync(
            provider,
            "  secret-key  ",
            [
                new LlmChatMessage("system", "Be brief."),
                new LlmChatMessage("user", "Hello")
            ]);

        Assert.Equal("Done.", response.Content);
        Assert.Equal("reasoner-v1", response.Model);
        Assert.Equal("I checked the request.", response.ReasoningContent);
        Assert.Equal(12, response.Usage?.PromptTokens);
        Assert.Equal(7, response.Usage?.CompletionTokens);
        Assert.Equal(19, response.Usage?.TotalTokens);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://provider.example/v1/chat/completions", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("secret-key", request.Authorization?.Parameter);
        Assert.Equal("application/json", request.ContentType);

        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.Equal("reasoner-v1", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Be brief.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteChatAsync_WhenThinkingDisabledSendsTemperatureInsteadOfReasoningEffort()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "local response"
                  }
                }
              ]
            }
            """));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "http://localhost:1234/v1",
            Model = "local-model",
            ThinkingEnabled = false
        };

        var response = await client.CompleteChatAsync(
            provider,
            apiKey: null,
            [new LlmChatMessage("user", "Ping")]);

        Assert.Equal("local response", response.Content);
        Assert.Equal("local-model", response.Model);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);

        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.Equal(0.4, root.GetProperty("temperature").GetDouble(), precision: 3);
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task CompleteChatAsync_SendsToolSchemasAndParsesToolCalls()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """
            {
              "model": "tool-model",
              "choices": [
                {
                  "message": {
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_123",
                        "type": "function",
                        "function": {
                          "name": "project_read_file",
                          "arguments": "{\"path\":\"README.md\"}"
                        }
                      }
                    ]
                  }
                }
              ]
            }
            """));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "https://provider.example/v1",
            Model = "tool-model"
        };
        var tools = new[]
        {
            new LlmToolDefinition(
                "project_read_file",
                "Read a file",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "File path", Required: true)
                },
                ["path"])
        };

        var response = await client.CompleteChatAsync(
            provider,
            apiKey: null,
            [new LlmChatMessage("user", "Read README")],
            tools);

        var call = Assert.Single(response.ToolCalls!);
        Assert.Equal("call_123", call.Id);
        Assert.Equal("project_read_file", call.Name);
        Assert.Equal("""{"path":"README.md"}""", call.ArgumentsJson);

        var request = Assert.Single(handler.Requests);
        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        var tool = root.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("project_read_file", tool.GetProperty("name").GetString());
        Assert.Equal("path", tool.GetProperty("parameters").GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task CompleteChatAsync_PreservesFullMcpInputSchema()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "done"
                  }
                }
              ]
            }
            """));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        using var schemaDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "filter": {
                  "type": "object",
                  "properties": { "tag": { "type": "string" } },
                  "required": ["tag"]
                }
              },
              "required": ["filter"],
              "additionalProperties": true
            }
            """);
        var tools = new[]
        {
            new LlmToolDefinition(
                "mcp_catalog_lookup",
                "Look up catalog entries",
                new Dictionary<string, ToolParameterDefinition>(),
                [],
                schemaDocument.RootElement.Clone())
        };

        await client.CompleteChatAsync(
            new ProviderSettings { BaseUrl = "https://provider.example/v1", Model = "tool-model" },
            apiKey: null,
            [new LlmChatMessage("user", "Find a tagged item")],
            tools);

        var request = Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(request.Body!);
        var parameters = payload.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        Assert.True(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("object", parameters.GetProperty("properties").GetProperty("filter").GetProperty("type").GetString());
        Assert.Equal("tag", parameters.GetProperty("properties").GetProperty("filter").GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task CompleteChatAsync_WhenNoToolsProvidedOmitsToolsAndToolChoice()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """
            {
              "model": "no-tools-model",
              "choices": [
                {
                  "message": {
                    "content": "final answer"
                  }
                }
              ]
            }
            """));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "https://provider.example/v1",
            Model = "no-tools-model"
        };

        var response = await client.CompleteChatAsync(
            provider,
            apiKey: null,
            [new LlmChatMessage("user", "Finish")],
            tools: Array.Empty<LlmToolDefinition>());

        Assert.Equal("final answer", response.Content);

        var request = Assert.Single(handler.Requests);
        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task CompleteChatAsync_WhenProgressProvidedStreamsAnswerDeltas()
    {
        var handler = new StubHttpMessageHandler((_, _) => SseResponse(
            """{"model":"stream-model","choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"}}]}"""));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "https://provider.example/v1",
            Model = "stream-model"
        };
        var deltas = new List<string>();

        var response = await client.CompleteChatAsync(
            provider,
            apiKey: null,
            [new LlmChatMessage("user", "Say hello")],
            streamProgress: new ImmediateProgress<LlmStreamDelta>(delta =>
            {
                if (!string.IsNullOrEmpty(delta.ContentDelta))
                {
                    deltas.Add(delta.ContentDelta);
                }
            }));

        Assert.Equal("Hello", response.Content);
        Assert.Equal(["Hel", "lo"], deltas);

        var request = Assert.Single(handler.Requests);
        using var document = JsonDocument.Parse(request.Body!);
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task CompleteChatAsync_StreamsReasoningAndFinalUsage()
    {
        var handler = new StubHttpMessageHandler((_, _) => SseResponse(
            """{"model":"stream-model","choices":[{"delta":{"reasoning_content":"Let me "}}]}""",
            """{"choices":[{"delta":{"reasoning_content":"think."}}]}""",
            """{"choices":[{"delta":{"content":"Done"}}]}""",
            """{"usage":{"prompt_tokens":21,"completion_tokens":8,"total_tokens":29},"choices":[]}"""));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "https://provider.example/v1",
            Model = "stream-model",
            SupportsThinking = true,
            ThinkingEnabled = true
        };
        var reasoningDeltas = new List<string>();

        var response = await client.CompleteChatAsync(
            provider,
            apiKey: null,
            [new LlmChatMessage("user", "Say hello")],
            streamProgress: new ImmediateProgress<LlmStreamDelta>(delta =>
            {
                if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                {
                    reasoningDeltas.Add(delta.ReasoningDelta);
                }
            }));

        Assert.Equal("Done", response.Content);
        Assert.Equal("Let me think.", response.ReasoningContent);
        Assert.Equal(["Let me ", "think."], reasoningDeltas);
        Assert.Equal(21, response.Usage?.PromptTokens);
        Assert.Equal(8, response.Usage?.CompletionTokens);
        Assert.Equal(29, response.Usage?.TotalTokens);

        var request = Assert.Single(handler.Requests);
        using var document = JsonDocument.Parse(request.Body!);
        Assert.True(document.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task CompleteChatAsync_StreamsToolCallDeltas()
    {
        var handler = new StubHttpMessageHandler((_, _) => SseResponse(
            """{"model":"tool-stream","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"project_read_file","arguments":"{\"path\""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"README.md\"}"}}]}}]}"""));
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var provider = new ProviderSettings
        {
            BaseUrl = "https://provider.example/v1",
            Model = "tool-stream"
        };
        var tools = new[]
        {
            new LlmToolDefinition(
                "project_read_file",
                "Read a file",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "File path", Required: true)
                },
                ["path"])
        };

        var response = await client.CompleteChatAsync(
            provider,
            apiKey: null,
            [new LlmChatMessage("user", "Read README")],
            tools,
            streamProgress: new ImmediateProgress<LlmStreamDelta>(_ => { }));

        var call = Assert.Single(response.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("project_read_file", call.Name);
        Assert.Equal("""{"path":"README.md"}""", call.ArgumentsJson);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage SseResponse(params string[] events)
    {
        var builder = new StringBuilder();
        foreach (var item in events)
        {
            builder.Append("data: ");
            builder.Append(item);
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("data: [DONE]");
        builder.AppendLine();

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }
}
