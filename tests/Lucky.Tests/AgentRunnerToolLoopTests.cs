using Lucky.Core;

namespace Lucky.Tests;

public sealed class AgentRunnerToolLoopTests
{
    [Fact]
    public async Task RunTurnAsync_ReadsProjectFileAndAnswersFromToolResult()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "Lucky can read this.");
            var client = new ScriptedLlmClient(
                new LlmResponse(
                    "",
                    "fake",
                    [new ToolCallRequest("call_1", "project_read_file", """{"path":"README.md"}""")],
                    Usage: new LlmTokenUsage(10, 2, 12)),
                new LlmResponse(
                    "I read README.md: Lucky can read this.",
                    "fake",
                    Usage: new LlmTokenUsage(18, 7, 25)));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "read your README");

            Assert.Contains("Lucky can read this", result.AssistantMessage);
            Assert.Contains(result.Trace, entry => entry.Tool == "project.read_file" && !entry.IsError);
            Assert.Equal(2, client.Requests.Count);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "project_read_file");
            Assert.Equal(37, result.TokenUsage?.TotalTokens);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WorkspaceAccessDeniesWrites()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("Should not be called.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "create a file");

            Assert.Contains("Full access", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "new.txt")));
            Assert.Empty(client.Requests);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_FullAccessCanWriteAndEditProjectFiles()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [new ToolCallRequest("call_1", "project_write_file", """{"path":"new.txt","content":"created","overwrite":false}""")]),
                new LlmResponse("", "fake", [new ToolCallRequest("call_2", "project_edit_file", """{"path":"new.txt","oldText":"created","newText":"edited"}""")]),
                new LlmResponse("Created and edited new.txt.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "create and edit a file");

            Assert.Contains("Created and edited", result.AssistantMessage);
            Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(root, "new.txt")));
            Assert.Contains(result.Trace, entry => entry.Tool == "project.write_file" && !entry.IsError);
            Assert.Contains(result.Trace, entry => entry.Tool == "project.edit_file" && !entry.IsError);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "project_write_file");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_RepeatedSuccessfulWriteFinalizesWithoutHittingToolCap()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            var repeatedWrite = new ToolCallRequest(
                "call_write",
                "project_write_file",
                """{"path":"neon-maze-chase.html","content":"<html></html>","overwrite":true}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [repeatedWrite], Usage: new LlmTokenUsage(10, 2, 12)),
                new LlmResponse("", "fake", [repeatedWrite], Usage: new LlmTokenUsage(14, 2, 16)),
                new LlmResponse("Created neon-maze-chase.html.", "fake", Usage: new LlmTokenUsage(20, 5, 25)));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "create a standalone HTML file");

            Assert.Equal("<html></html>", await File.ReadAllTextAsync(Path.Combine(root, "neon-maze-chase.html")));
            Assert.Contains("Created neon-maze-chase.html", result.AssistantMessage);
            Assert.DoesNotContain("stopped after", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single(result.Trace, entry => entry.Tool == "project.write_file" && !entry.IsError);
            Assert.Contains(result.Trace, entry => entry.Tool == "agent.loop" && entry.Output.Contains("same tool call", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, client.Requests.Count);
            Assert.Empty(client.Requests[2].Tools);
            Assert.Equal(53, result.TokenUsage?.TotalTokens);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_RepeatedSuccessfulReadFinalizesWithoutHittingToolCap()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "Lucky can read this.");
            var repeatedRead = new ToolCallRequest("call_read", "project_read_file", """{"path":"README.md"}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [repeatedRead]),
                new LlmResponse("", "fake", [repeatedRead]),
                new LlmResponse("README.md says Lucky can read this.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "read README twice");

            Assert.Contains("Lucky can read this", result.AssistantMessage);
            Assert.Single(result.Trace, entry => entry.Tool == "project.read_file" && !entry.IsError);
            Assert.Contains(result.Trace, entry => entry.Tool == "agent.loop" && entry.Output.Contains("same tool call", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, client.Requests.Count);
            Assert.Empty(client.Requests[2].Tools);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_SkipsAutoSearchForOfflineBuildRequests()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            state.Settings.AutoWebSearch = true;
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var search = new FakeSearchClient([new SearchResult("Unexpected", "https://example.test", "Should not be used.")]);
            var runner = new AgentRunner(client, search);

            await runner.RunTurnAsync(state, project, session, "Create a single standalone HTML file for a current neon canvas game.");

            Assert.Equal(0, search.SearchCalls);
            Assert.Single(client.Requests);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_AutoSearchStillRunsForCurrentNews()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.AutoWebSearch = true;
            var client = new ScriptedLlmClient(new LlmResponse("Latest Xbox news summary.", "fake"));
            var search = new FakeSearchClient([new SearchResult("Xbox Wire", "https://news.example/xbox", "Console news.")]);
            var runner = new AgentRunner(client, search);

            var result = await runner.RunTurnAsync(state, project, session, "give me the latest xbox news");

            Assert.Equal(1, search.SearchCalls);
            Assert.Equal("give me the latest xbox news", search.LastQuery);
            Assert.Contains("Latest Xbox news", result.AssistantMessage);
            Assert.Contains(client.Requests[0].Messages, message => message.Content.Contains("Xbox Wire", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_ChatOnlyDoesNotExposeFilesystemTools()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.ChatOnly);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("No tools available.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "list files");

            Assert.DoesNotContain(client.Requests[0].Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_ReportsStreamedAnswerDeltas()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("Hello there.", "fake"))
            {
                StreamDeltas = ["Hello ", "there."]
            };
            var runner = new AgentRunner(client);
            var progress = new List<AgentProgressEvent>();

            var result = await runner.RunTurnAsync(
                state,
                project,
                session,
                "say hello",
                progress: new ImmediateProgress<AgentProgressEvent>(progress.Add));

            Assert.Equal("Hello there.", result.AssistantMessage);
            Assert.Contains(progress, item => item.Stage == "answer" && item.Detail == "Hello ");
            Assert.Contains(progress, item => item.Stage == "answer" && item.Detail == "there.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_ReportsActualReasoningDeltas()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"))
            {
                ReasoningDeltas = ["I am checking ", "the prompt."]
            };
            var runner = new AgentRunner(client);
            var progress = new List<AgentProgressEvent>();

            var result = await runner.RunTurnAsync(
                state,
                project,
                session,
                "say done",
                progress: new ImmediateProgress<AgentProgressEvent>(progress.Add));

            Assert.Equal("Done.", result.AssistantMessage);
            Assert.Contains(progress, item => item.Stage == "reasoning" && item.Detail == "I am checking ");
            Assert.Contains(progress, item => item.Stage == "reasoning" && item.Detail == "the prompt.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_IncludesCleanAnswerStyleInSystemPrompt()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "give me the latest xbox news");

            var systemPrompt = client.Requests[0].Messages[0].Content;
            Assert.Contains("clean chat prose", systemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("decorative Markdown separators", systemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("heading hashes", systemPrompt, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static (string Root, LuckyState State, LuckyProject Project, ChatSession Session) CreateState(HarnessAccessLevel accessLevel)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.AgentRunnerToolLoopTests", Guid.NewGuid().ToString("N"));
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
        public IReadOnlyList<string> StreamDeltas { get; init; } = [];
        public IReadOnlyList<string> ReasoningDeltas { get; init; } = [];

        public Task<IReadOnlyList<string>> ListModelsAsync(
            ProviderSettings provider,
            string? apiKey,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([provider.Model]);
        }

        public Task<LlmResponse> CompleteChatAsync(
            ProviderSettings provider,
            string? apiKey,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            IProgress<LlmStreamDelta>? streamProgress = null)
        {
            Requests.Add(new LlmRequest([.. messages], tools?.ToArray() ?? []));
            foreach (var delta in StreamDeltas)
            {
                streamProgress?.Report(new LlmStreamDelta(delta));
            }

            foreach (var delta in ReasoningDeltas)
            {
                streamProgress?.Report(new LlmStreamDelta(ReasoningDelta: delta));
            }

            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse("done", provider.Model));
        }
    }

    private sealed class FakeSearchClient(IReadOnlyList<SearchResult> results) : IWebSearchClient
    {
        public int SearchCalls { get; private set; }
        public string? LastQuery { get; private set; }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string searxngUrl,
            string query,
            int maxResults,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            LastQuery = query;
            return Task.FromResult(results);
        }
    }

    private sealed record LlmRequest(IReadOnlyList<LlmChatMessage> Messages, IReadOnlyList<LlmToolDefinition> Tools);
}
