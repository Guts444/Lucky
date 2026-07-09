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
    public async Task RunTurnAsync_FullAccessCanApplyUnifiedDiffPatch()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            var path = Path.Combine(root, "note.txt");
            await File.WriteAllTextAsync(path, "old\n");
            var patchCall = new ToolCallRequest(
                "call_patch",
                "project_apply_patch",
                """{"patch":"--- a/note.txt\n+++ b/note.txt\n@@ -1 +1 @@\n-old\n+new\n"}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [patchCall]),
                new LlmResponse("Patched note.txt.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "update note.txt with a patch");

            Assert.Equal("new\n", await File.ReadAllTextAsync(path));
            Assert.Contains(result.Trace, entry => entry.Tool == "project.apply_patch" && !entry.IsError);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "project_apply_patch");
            Assert.Contains("Patched note", result.AssistantMessage);
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
    public async Task RunTurnAsync_ExplicitWebSearchFramesPrefetchedResultsAsSearxngAccess()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.AutoWebSearch = false;
            var client = new ScriptedLlmClient(new LlmResponse("I can answer from SearXNG results.", "fake"));
            var search = new FakeSearchClient([new SearchResult("SearXNG Result", "https://example.test/search", "Fresh result.")]);
            var runner = new AgentRunner(client, search);

            await runner.RunTurnAsync(state, project, session, "/web can you search now?");

            Assert.Equal(1, search.SearchCalls);
            Assert.Equal("can you search now?", search.LastQuery);
            var systemPrompt = client.Requests[0].Messages[0].Content;
            Assert.Contains("Lucky web access is through the user's configured SearXNG endpoint", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("Lucky fetched them for this turn", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("do not claim you lack internet access", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("SearXNG Result", systemPrompt, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WebSearchToolCallsSearxngInChatOnly()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.ChatOnly);
        try
        {
            state.Settings.AutoWebSearch = false;
            var searchCall = new ToolCallRequest("call_web", "web_search", """{"query":"ps4 update timing"}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [searchCall]),
                new LlmResponse("SearXNG found PS4 update timing.", "fake"));
            var search = new FakeSearchClient([new SearchResult("PS4 Update", "https://example.test/ps4", "Timing notes.")]);
            var runner = new AgentRunner(client, search);

            var result = await runner.RunTurnAsync(state, project, session, "can you search the web?");

            Assert.Contains("SearXNG found", result.AssistantMessage);
            Assert.Equal(1, search.SearchCalls);
            Assert.Equal("ps4 update timing", search.LastQuery);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "web_search");
            Assert.DoesNotContain(client.Requests[0].Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));
            Assert.Contains(result.Trace, entry =>
                entry.Tool == "web.search" &&
                !entry.IsError &&
                entry.Output.Contains("PS4 Update", StringComparison.Ordinal));
            Assert.Contains(client.Requests[1].Messages, message =>
                message.Role == "tool" &&
                message.Content.Contains("SearXNG results", StringComparison.Ordinal) &&
                message.Content.Contains("Timing notes", StringComparison.Ordinal));
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

            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "web_search");
            Assert.DoesNotContain(client.Requests[0].Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_ChatOnlyDoesNotLoadOrInjectProjectContext()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.ChatOnly);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "PROJECT_INSTRUCTIONS_SENTINEL");
            project.Name = "PROJECT_NAME_SENTINEL";
            state.Memories.Add(new MemoryItem
            {
                Summary = "GLOBAL_MEMORY_SENTINEL",
                Pinned = true,
                ProjectId = null
            });
            state.Memories.Add(new MemoryItem
            {
                Summary = "PROJECT_MEMORY_SENTINEL",
                Pinned = true,
                ProjectId = project.Id
            });
            var client = new ScriptedLlmClient(new LlmResponse("No workspace context.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "spawn a reviewer");

            var request = Assert.Single(client.Requests);
            var systemPrompt = request.Messages[0].Content;
            Assert.DoesNotContain("Selected project:", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Project path:", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(root, systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("PROJECT_NAME_SENTINEL", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("PROJECT_INSTRUCTIONS_SENTINEL", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("PROJECT_MEMORY_SENTINEL", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("GLOBAL_MEMORY_SENTINEL", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Trace, entry => entry.Tool == "instructions");
            Assert.DoesNotContain(request.Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WhenMemoriesDisabledDoesNotCaptureRecallOrInject()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.MemoriesEnabled = false;
            state.Memories.Add(new MemoryItem
            {
                Summary = "EXISTING_MEMORY_SENTINEL",
                Pinned = true,
                ProjectId = project.Id
            });
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(
                state,
                project,
                session,
                "remember that DISABLED_CAPTURE_SENTINEL should not be saved");

            var request = Assert.Single(client.Requests);
            Assert.Empty(result.CapturedMemories);
            Assert.Empty(result.RecalledMemories);
            Assert.Single(state.Memories);
            Assert.DoesNotContain("EXISTING_MEMORY_SENTINEL", request.Messages[0].Content, StringComparison.Ordinal);
            Assert.DoesNotContain("DISABLED_CAPTURE_SENTINEL", state.Memories[0].Summary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_EnforcesMemoryPromptBudgets()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.UserProfileCharLimit = 32;
            state.Settings.MemoryCharLimit = 34;
            state.Memories.Add(new MemoryItem
            {
                Kind = MemoryKind.UserProfile,
                Summary = "PROFILE_ALPHA beta gamma delta epsilon",
                Tags = ["alpha"],
                ProjectId = null,
                Pinned = true
            });
            state.Memories.Add(new MemoryItem
            {
                Kind = MemoryKind.Memory,
                Summary = "DURABLE_ALPHA beta gamma delta epsilon",
                Tags = ["alpha"],
                ProjectId = project.Id,
                Pinned = true
            });
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "alpha");

            var systemPrompt = client.Requests[0].Messages[0].Content;
            Assert.DoesNotContain("PROFILE_ALPHA beta gamma delta epsilon", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("DURABLE_ALPHA beta gamma delta epsilon", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("PROFILE_ALPHA", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("DURABLE_ALPHA", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("...", systemPrompt, StringComparison.Ordinal);
            Assert.All(SectionMemoryLines(systemPrompt, "USER profile notes:"), line => Assert.True(line.Length <= state.Settings.UserProfileCharLimit));
            Assert.All(SectionMemoryLines(systemPrompt, "Relevant durable memories:"), line => Assert.True(line.Length <= state.Settings.MemoryCharLimit));
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

    private static IReadOnlyList<string> SectionMemoryLines(string text, string heading)
    {
        var lines = text.Split(Environment.NewLine);
        var start = Array.FindIndex(lines, line => line == heading);
        if (start < 0)
        {
            return [];
        }

        return lines
            .Skip(start + 1)
            .TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();
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
