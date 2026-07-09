using Lucky.Core;

namespace Lucky.Tests;

public sealed class AgentRunnerSubagentTests
{
    [Fact]
    public async Task RunTurnAsync_IncludesAgentsMdAndSubagentCatalogInParentPrompt()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "Always inspect docs first.");
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "review the project shape");

            Assert.Equal("Done.", result.AssistantMessage);
            Assert.Contains(result.Trace, entry => entry.Tool == "instructions" && !entry.IsError);
            var request = Assert.Single(client.Requests);
            Assert.Contains("Always inspect docs first", request.Messages[0].Content);
            Assert.Contains("Available subagents", request.Messages[0].Content);
            Assert.Contains(request.Tools, tool => tool.Name == "subagent_run");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_SubagentReceivesAgentsMdAndReturnsSummaryToParent()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "Follow the project guide.");
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "hello from README");
            var client = new ScriptedLlmClient(
                new LlmResponse(
                    "",
                    "fake",
                    [new ToolCallRequest("call_sub", "subagent_run", """{"agent":"explorer","task":"Read README.md and summarize it."}""")],
                    Usage: new LlmTokenUsage(10, 1, 11)),
                new LlmResponse(
                    "",
                    "fake",
                    [new ToolCallRequest("call_read", "project_read_file", """{"path":"README.md"}""")],
                    Usage: new LlmTokenUsage(12, 1, 13)),
                new LlmResponse(
                    "README.md says hello from README.",
                    "fake",
                    Usage: new LlmTokenUsage(20, 6, 26)),
                new LlmResponse(
                    "Explorer found README.md says hello from README.",
                    "fake",
                    Usage: new LlmTokenUsage(30, 8, 38)));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "spawn an explorer for README");

            Assert.Contains("Explorer found", result.AssistantMessage);
            Assert.Contains(result.Trace, entry => entry.Tool == "subagent.explorer" && !entry.IsError);
            Assert.Contains(result.Trace, entry => entry.Tool == "subagent.explorer.project.read_file" && !entry.IsError);
            Assert.Equal(88, result.TokenUsage?.TotalTokens);

            var childRequest = client.Requests[1];
            Assert.Contains("Follow the project guide", childRequest.Messages[0].Content);
            Assert.Contains(childRequest.Tools, tool => tool.Name == "project_read_file");
            Assert.DoesNotContain(childRequest.Tools, tool => tool.Name == "project_write_file");

            var parentFollowup = client.Requests[3];
            Assert.Contains(parentFollowup.Messages, message =>
                message.Role == "tool" &&
                message.Content.Contains("Subagent: explorer", StringComparison.Ordinal) &&
                message.Content.Contains("README.md says hello", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_CustomSubagentCannotEscalateAboveWorkspaceAccess()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.Subagents.CustomAgents.Add(new SubagentDefinition
            {
                Name = "editor",
                Description = "Attempts project edits.",
                Instructions = "Try to edit a file.",
                AccessLevel = HarnessAccessLevel.FullAccess,
                Tools = ["project_write_file"],
                AutoActivate = false
            });
            var client = new ScriptedLlmClient(
                new LlmResponse(
                    "",
                    "fake",
                    [new ToolCallRequest("call_sub", "subagent_run", """{"agent":"editor","task":"Write note.txt."}""")]),
                new LlmResponse(
                    "",
                    "fake",
                    [new ToolCallRequest("call_write", "project_write_file", """{"path":"note.txt","content":"nope","overwrite":true}""")]),
                new LlmResponse("I could not write note.txt.", "fake"),
                new LlmResponse("The editor subagent could not write note.txt.", "fake"));
            var runner = new AgentRunner(client);

            var result = await runner.RunTurnAsync(state, project, session, "delegate to editor subagent");

            Assert.Contains("could not write", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "note.txt")));
            Assert.Contains(result.Trace, entry =>
                entry.Tool == "subagent.editor.project_write_file" &&
                entry.IsError &&
                entry.Output.Contains("Denied", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(client.Requests[1].Tools, tool => tool.Name == "project_write_file");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_ChatOnlyExposesOnlySubagentTool()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.ChatOnly);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("No workspace tools.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "spawn a reviewer");

            var request = Assert.Single(client.Requests);
            Assert.Contains(request.Tools, tool => tool.Name == "subagent_run");
            Assert.DoesNotContain(request.Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_AutoDelegationOmitsExplicitOnlySubagents()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.Subagents.AutoDelegateEnabled = true;
            state.Settings.Subagents.CustomAgents.Add(new SubagentDefinition
            {
                Name = "manual-helper",
                Description = "Manual-only helper.",
                Instructions = "Only run when explicitly requested.",
                AutoActivate = false
            });
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "review the project shape");

            var systemPrompt = client.Requests[0].Messages[0].Content;
            Assert.Contains("Available subagents", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("- reviewer (auto):", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("- worker (explicit):", systemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("manual-helper", systemPrompt, StringComparison.Ordinal);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "subagent_run");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_ExplicitSubagentRequestIncludesExplicitOnlySubagents()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            state.Settings.Subagents.AutoDelegateEnabled = true;
            state.Settings.Subagents.CustomAgents.Add(new SubagentDefinition
            {
                Name = "manual-helper",
                Description = "Manual-only helper.",
                Instructions = "Only run when explicitly requested.",
                AutoActivate = false
            });
            var client = new ScriptedLlmClient(new LlmResponse("Done.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "spawn manual-helper and worker subagents");

            var systemPrompt = client.Requests[0].Messages[0].Content;
            Assert.Contains("- worker (explicit):", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("- manual-helper (explicit):", systemPrompt, StringComparison.Ordinal);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "subagent_run");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static (string Root, LuckyState State, LuckyProject Project, ChatSession Session) CreateState(HarnessAccessLevel accessLevel)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.AgentRunnerSubagentTests", Guid.NewGuid().ToString("N"));
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
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse("done", provider.Model));
        }
    }

    private sealed record LlmRequest(IReadOnlyList<LlmChatMessage> Messages, IReadOnlyList<LlmToolDefinition> Tools);
}
