using System.Text.Json;
using Lucky.Core;

namespace Lucky.Tests;

public sealed class AgentRunnerResilienceTests
{
    [Fact]
    public async Task RunTurnAsync_AllowsAReadToRepeatAfterWorkspaceChanges()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "before");
            var read = new ToolCallRequest("read", "project_read_file", """{"path":"README.md"}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [read]),
                new LlmResponse("", "fake", [new ToolCallRequest("write", "project_write_file", """{"path":"README.md","content":"after","overwrite":true}""")]),
                new LlmResponse("", "fake", [read]),
                new LlmResponse("The updated README says after.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "update and verify README");

            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(root, "README.md")));
            Assert.Equal(2, result.Trace.Count(entry => entry.Tool == "project.read_file" && !entry.IsError));
            Assert.Equal(4, client.Requests.Count);
            Assert.DoesNotContain(result.Trace, entry =>
                entry.Tool == "agent.loop" && entry.Output.Contains("same tool call", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_FinalizesAfterThreeConsecutiveFailedToolRounds()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var missingRead = new ToolCallRequest("missing", "project_read_file", """{"path":"missing.txt"}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [missingRead]),
                new LlmResponse("", "fake", [missingRead]),
                new LlmResponse("", "fake", [missingRead]),
                new LlmResponse("The file is missing.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "read missing.txt");

            Assert.Equal("The file is missing.", result.AssistantMessage);
            Assert.Equal(3, result.Trace.Count(entry => entry.Tool == "project.read_file" && entry.IsError));
            Assert.Contains(result.Trace, entry =>
                entry.Tool == "agent.loop" &&
                entry.Output.Contains("Three consecutive tool rounds failed", StringComparison.Ordinal));
            Assert.Equal(4, client.Requests.Count);
            Assert.Empty(client.Requests[3].Tools);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_BoundsLargeToolOutputBeforeTheNextModelRound()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var content = new string('a', 19_000) + "TAIL_SENTINEL";
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), content);
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [new ToolCallRequest("read", "project_read_file", """{"path":"README.md"}""")]),
                new LlmResponse("Read it.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "read README");

            var toolMessage = Assert.Single(client.Requests[1].Messages, message => message.Role == "tool");
            Assert.True(toolMessage.Content.Length <= 16_000);
            Assert.Contains("truncated this tool output", toolMessage.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TAIL_SENTINEL", toolMessage.Content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_AssignsAStableIdToProviderToolCallsWithoutOne()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "Lucky");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [new ToolCallRequest("", "project_read_file", """{"path":"README.md"}""")]),
                new LlmResponse("Read Lucky.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "read README");

            var toolMessage = Assert.Single(client.Requests[1].Messages, message => message.Role == "tool");
            Assert.Equal("lucky_tool_1_1", toolMessage.ToolCallId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_TrimsOldConversationTurnsToRespectTheProviderContextBudget()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.LmStudio.ContextWindowTokens = 4096;
            for (var index = 0; index < 18; index++)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = ChatRole.User,
                    Content = $"OLD_USER_{index} {new string('u', 500)}"
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = $"OLD_ASSISTANT_{index} {new string('a', 500)}"
                });
            }
            session.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "LATEST_REQUEST_SENTINEL" });
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "LATEST_REQUEST_SENTINEL");

            var request = Assert.Single(client.Requests);
            Assert.Contains(request.Messages, message => message.Content == "LATEST_REQUEST_SENTINEL");
            Assert.DoesNotContain(request.Messages, message => message.Content.Contains("OLD_USER_0", StringComparison.Ordinal));
            Assert.DoesNotContain(request.Messages, message => message.Content.Contains("OLD_ASSISTANT_0", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_BoundsOversizedSystemContextBeforeCallingTheProvider()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.LmStudio.ContextWindowTokens = 1024;
            state.Settings.Persona = new string('p', 10_000);
            session.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "LATEST_CONTEXT_SENTINEL" });
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "LATEST_CONTEXT_SENTINEL");

            var request = Assert.Single(client.Requests);
            var system = Assert.Single(request.Messages, message => message.Role == "system");
            Assert.True(system.Content.Length <= 2_152);
            Assert.Contains("truncated older system context", system.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(request.Messages, message => message.Content == "LATEST_CONTEXT_SENTINEL");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_AllowsAReadToRepeatAfterASuccessfulTerminalCommand()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "before");
            var read = new ToolCallRequest("read", "project_read_file", """{"path":"README.md"}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [read]),
                new LlmResponse("", "fake", [new ToolCallRequest("command", "project_run_command", """{"command":"write README"}""")]),
                new LlmResponse("", "fake", [read]),
                new LlmResponse("The terminal changed README to after.", "fake"));
            var runner = new AgentRunner(client, projectTerminalTools: new MutatingTerminal());

            var result = await runner.RunTurnAsync(state, project, session, "update and verify README with a command");

            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(root, "README.md")));
            Assert.Equal(2, result.Trace.Count(entry => entry.Tool == "project.read_file" && !entry.IsError));
            Assert.Equal(4, client.Requests.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_RebudgetsEveryToolRoundAndCompactsLargeMcpSchemas()
    {
        var (_, state, _, session) = CreateState(HarnessAccessLevel.FullAccess);
        state.Settings.LmStudio.ContextWindowTokens = 1024;
        state.Settings.Mcp.Enabled = true;
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            description = new string('s', 10_000),
            properties = new
            {
                query = new { type = "string", description = "A focused query" }
            },
            required = new[] { "query" }
        });
        var tool = new McpDiscoveredTool(
            "mcp_test_big",
            "test",
            "Test server",
            "big",
            "A deliberately oversized test tool schema.",
            new Dictionary<string, ToolParameterDefinition> { ["query"] = new("string", "A focused query", true) },
            ["query"],
            schema);
        var mcp = new TestMcpService(tool, new string('o', 20_000));
        var client = new ScriptedLlmClient(
            new LlmResponse("", "fake", [new ToolCallRequest("big", "mcp_test_big", "{\"query\":\"test\"}")]),
            new LlmResponse("Completed.", "fake"));
        var runner = new AgentRunner(client, mcpToolService: mcp);

        await runner.RunTurnAsync(state, project: null, session, "Run the configured MCP tool.");

        Assert.Equal(2, client.Requests.Count);
        var exposed = Assert.Single(client.Requests[0].Tools, candidate => candidate.Name == "mcp_test_big");
        Assert.True(exposed.InputSchema is null || exposed.InputSchema.Value.GetRawText().Length <= 4096);
        var toolResult = Assert.Single(client.Requests[1].Messages, message => message.Role == "tool");
        Assert.True(toolResult.Content.Length <= 512);
        Assert.All(client.Requests, request => Assert.InRange(EstimateRequestTokens(request), 0, 512));
    }

    private static (string Root, LuckyState State, LuckyProject Project, ChatSession Session) CreateState(HarnessAccessLevel accessLevel)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.AgentRunnerResilienceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = new LuckyProject { Id = "project_test", Name = "Test", Path = root };
        var session = new ChatSession { ProjectId = project.Id };
        var state = new LuckyState
        {
            Settings = new AppSettings
            {
                ActiveProvider = LlmProviderKind.LmStudio,
                AccessLevel = accessLevel,
                AutoWebSearch = false
            },
            Projects = [project],
            Sessions = [session]
        };
        state.Settings.LmStudio.Model = "fake";
        return (root, state, project, session);
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

        public Task<IReadOnlyList<string>> ListModelsAsync(
            ProviderSettings provider,
            string? apiKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([provider.Model]);

        public Task<LlmResponse> CompleteChatAsync(
            ProviderSettings provider,
            string? apiKey,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            IProgress<LlmStreamDelta>? streamProgress = null)
        {
            Requests.Add(new LlmRequest([.. messages], tools?.ToArray() ?? []));
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new LlmResponse("done", provider.Model));
        }
    }

    private sealed record LlmRequest(IReadOnlyList<LlmChatMessage> Messages, IReadOnlyList<LlmToolDefinition> Tools);

    private static int EstimateRequestTokens(LlmRequest request)
    {
        var messageCharacters = request.Messages.Sum(message =>
            message.Content.Length + message.Role.Length + (message.ToolCallId?.Length ?? 0) + (message.ReasoningContent?.Length ?? 0) +
            (message.ToolCalls?.Sum(call => call.Id.Length + call.Name.Length + call.ArgumentsJson.Length) ?? 0));
        var toolCharacters = request.Tools.Sum(tool =>
            tool.Name.Length + tool.Description.Length +
            tool.Parameters.Sum(parameter => parameter.Key.Length + parameter.Value.Type.Length + parameter.Value.Description.Length) +
            (tool.InputSchema?.GetRawText().Length ?? 0));
        return (int)Math.Ceiling((messageCharacters + toolCharacters) / 4.0);
    }

    private sealed class TestMcpService(McpDiscoveredTool tool, string output) : IMcpToolService
    {
        public Task<IMcpToolSession> OpenSessionAsync(McpSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult<IMcpToolSession>(new TestMcpSession(tool, output));
    }

    private sealed class TestMcpSession(McpDiscoveredTool tool, string output) : IMcpToolSession
    {
        public IReadOnlyList<McpDiscoveredTool> Tools { get; } = [tool];

        public IReadOnlyList<ToolTraceEntry> StartupTrace { get; } = [];

        public Task<ToolExecutionResult> ExecuteAsync(string modelToolName, string argumentsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolExecutionResult("mcp.test_server.big", "Test server.big", output));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutatingTerminal : IProjectTerminalToolService
    {
        public async Task<ToolExecutionResult> RunCommandAsync(
            LuckyProject project,
            string command,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(Path.Combine(project.Path, "README.md"), "after", cancellationToken);
            return new ToolExecutionResult("project.run_command", command, "PowerShell exited with code 0.");
        }
    }
}
