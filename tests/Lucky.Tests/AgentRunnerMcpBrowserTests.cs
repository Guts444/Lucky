using System.Text.Json;
using Lucky.Core;

namespace Lucky.Tests;

public sealed class AgentRunnerMcpBrowserTests
{
    [Fact]
    public async Task RunTurnAsync_FullAccessRegistersAndExecutesTrustedPageReader()
    {
        var root = CreateRoot();
        try
        {
            var state = CreateState(HarnessAccessLevel.FullAccess);
            state.Settings.Browser = new WebBrowserSettings
            {
                Enabled = true,
                AllowedDomains = ["docs.example.test"]
            };
            var project = new LuckyProject { Id = "project", Name = "Project", Path = root };
            var session = LuckyStore.CreateSession(project.Id);
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [new ToolCallRequest("open", "web_open", """{"url":"https://docs.example.test/guide"}""")]),
                new LlmResponse("Read the guide.", "fake"));
            var pageReader = new FakePageReader();
            var runner = new AgentRunner(llmClient: client, webPageReader: pageReader);

            var result = await runner.RunTurnAsync(state, project, session, "Read the trusted guide.");

            Assert.Equal("Read the guide.", result.AssistantMessage);
            Assert.Equal(["https://docs.example.test/guide"], pageReader.Urls);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "web_open");
            Assert.Contains(result.Trace, entry => entry.Tool == "web.open" && !entry.IsError);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WorkspaceDoesNotExposeTrustedPageReader()
    {
        var root = CreateRoot();
        try
        {
            var state = CreateState(HarnessAccessLevel.Workspace);
            state.Settings.Browser = new WebBrowserSettings
            {
                Enabled = true,
                AllowedDomains = ["docs.example.test"]
            };
            var project = new LuckyProject { Id = "project", Name = "Project", Path = root };
            var session = LuckyStore.CreateSession(project.Id);
            var client = new ScriptedLlmClient(new LlmResponse("Workspace response.", "fake"));
            var pageReader = new FakePageReader();
            var runner = new AgentRunner(llmClient: client, webPageReader: pageReader);

            await runner.RunTurnAsync(state, project, session, "Read a page.");

            Assert.DoesNotContain(client.Requests[0].Tools, tool => tool.Name == "web_open");
            Assert.Empty(pageReader.Urls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_FullAccessRegistersDispatchesAndDisposesMcpSession()
    {
        var root = CreateRoot();
        try
        {
            var state = CreateState(HarnessAccessLevel.FullAccess);
            state.Settings.Mcp = new McpSettings { Enabled = true };
            var project = new LuckyProject { Id = "project", Name = "Project", Path = root };
            var session = LuckyStore.CreateSession(project.Id);
            var fakeMcp = new FakeMcpToolService();
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [new ToolCallRequest("greet", "mcp_test_server_greet", """{"name":"Ada"}""")]),
                new LlmResponse("MCP greeted Ada.", "fake"));
            var runner = new AgentRunner(llmClient: client, mcpToolService: fakeMcp);

            var result = await runner.RunTurnAsync(state, project, session, "Ask MCP to greet Ada.");

            Assert.Equal(1, fakeMcp.OpenCalls);
            Assert.True(fakeMcp.Session.Disposed);
            Assert.Equal(["mcp_test_server_greet"], fakeMcp.Session.ExecutedToolNames);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "mcp_test_server_greet");
            Assert.Contains(result.Trace, entry => entry.Tool == "mcp.connect" && !entry.IsError);
            Assert.Contains(result.Trace, entry => entry.Tool == "mcp.test_server.greet" && !entry.IsError);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static LuckyState CreateState(HarnessAccessLevel accessLevel) => new()
    {
        Settings = new AppSettings
        {
            AccessLevel = accessLevel,
            AutoWebSearch = false,
            Subagents = new SubagentSettings { Enabled = false }
        }
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedLlmClient(params LlmResponse[] responses) : ILlmClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);

        public List<LlmRequest> Requests { get; } = [];

        public Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings provider, string? apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<LlmResponse> CompleteChatAsync(
            ProviderSettings provider,
            string? apiKey,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            IProgress<LlmStreamDelta>? streamProgress = null)
        {
            Requests.Add(new LlmRequest(messages.ToArray(), tools?.ToArray() ?? []));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record LlmRequest(IReadOnlyList<LlmChatMessage> Messages, IReadOnlyList<LlmToolDefinition> Tools);

    private sealed class FakePageReader : IWebPageReader
    {
        public List<string> Urls { get; } = [];

        public Task<ToolExecutionResult> OpenAsync(WebBrowserSettings settings, string url, CancellationToken cancellationToken = default)
        {
            Urls.Add(url);
            return Task.FromResult(new ToolExecutionResult("web.open", url, "Page: trusted guide"));
        }
    }

    private sealed class FakeMcpToolService : IMcpToolService
    {
        public int OpenCalls { get; private set; }

        public FakeMcpToolSession Session { get; } = new();

        public Task<IMcpToolSession> OpenSessionAsync(McpSettings settings, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Task.FromResult<IMcpToolSession>(Session);
        }
    }

    private sealed class FakeMcpToolSession : IMcpToolSession
    {
        private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new
                {
                    type = "string",
                    description = "Name to greet"
                }
            },
            required = new[] { "name" }
        });

        public IReadOnlyList<McpDiscoveredTool> Tools { get; } =
        [
            new McpDiscoveredTool(
                "mcp_test_server_greet",
                "server",
                "Test server",
                "greet",
                "Greet a name",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["name"] = new("string", "Name to greet", Required: true)
                },
                ["name"],
                Schema)
        ];

        public IReadOnlyList<ToolTraceEntry> StartupTrace { get; } =
        [new ToolTraceEntry("mcp.connect", "Test server", "Connected over stdio; discovered 1 tool(s).")];

        public List<string> ExecutedToolNames { get; } = [];

        public bool Disposed { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(string modelToolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            ExecutedToolNames.Add(modelToolName);
            return Task.FromResult(new ToolExecutionResult("mcp.test_server.greet", "Test server.greet", "Hello, Ada!"));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
