using Lucky.Core;

namespace Lucky.Tests;

public sealed class AgentRunnerLoopSafetyTests
{
    [Fact]
    public async Task RunTurnAsync_BoundsParentToolCallsAndReturnsAnErrorForOverflow()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var calls = Enumerable.Range(1, 13)
                .Select(index => new ToolCallRequest(
                    $"read_{index}",
                    "project_read_file",
                    $$"""{"path":"file-{{index}}.txt"}"""))
                .ToArray();
            var client = new RecordingLlmClient(
                new LlmResponse("", "fake", calls),
                new LlmResponse("Finished after the bounded reads.", "fake"));
            var files = new CountingReadFileTools();
            var runner = new AgentRunner(client, projectFileTools: files);

            var result = await runner.RunTurnAsync(state, project, session, "read the project files");

            Assert.Equal(12, files.ReadCalls);
            Assert.Equal(13, result.Trace.Count(entry => entry.Tool == "project.read_file"));
            var overflow = Assert.Single(result.Trace, entry =>
                entry.Tool == "project.read_file" && entry.IsError);
            Assert.Contains("at most 12 tool calls", overflow.Output, StringComparison.OrdinalIgnoreCase);

            var followUpToolResults = client.Requests[1].Messages.Where(message => message.Role == "tool").ToArray();
            Assert.Equal(13, followUpToolResults.Length);
            Assert.Equal("read_13", followUpToolResults[12].ToolCallId);
            Assert.Contains("not run", followUpToolResults[12].Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SubagentCoordinator_BoundsChildToolCallsAndReturnsAnErrorForOverflow()
    {
        var (root, state, project, _) = CreateState(HarnessAccessLevel.Workspace);
        try
        {
            var calls = Enumerable.Range(1, 9)
                .Select(index => new ToolCallRequest(
                    $"child_read_{index}",
                    "project_read_file",
                    $$"""{"path":"file-{{index}}.txt"}"""))
                .ToArray();
            var client = new RecordingLlmClient(
                new LlmResponse("", "fake", calls),
                new LlmResponse("Child summary.", "fake"));
            var files = new CountingReadFileTools();
            var coordinator = new SubagentCoordinator(client, files);
            var definition = new SubagentDefinition
            {
                Name = "reader",
                Description = "Read files.",
                Instructions = "Read the assigned files.",
                Tools = ["project_read_file"],
                AccessLevel = HarnessAccessLevel.Workspace
            };

            var result = await coordinator.RunAsync(
                settings: state.Settings,
                project: project,
                provider: state.Settings.ActiveProviderSettings,
                apiKey: null,
                definitions: [definition],
                instructions: new AgentInstructionSet([]),
                agentName: "reader",
                task: "Read the assigned files.");

            Assert.False(result.IsError);
            Assert.Equal(8, files.ReadCalls);
            Assert.Equal(9, result.Trace.Count(entry => entry.Tool == "project.read_file"));
            var overflow = Assert.Single(result.Trace, entry =>
                entry.Tool == "project.read_file" && entry.IsError);
            Assert.Contains("at most 8 tool calls", overflow.Output, StringComparison.OrdinalIgnoreCase);

            var followUpToolResults = client.Requests[1].Messages.Where(message => message.Role == "tool").ToArray();
            Assert.Equal(9, followUpToolResults.Length);
            Assert.Equal("child_read_9", followUpToolResults[8].ToolCallId);
            Assert.Contains("not run", followUpToolResults[8].Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunTurnAsync_SerializesConcurrentWritableSubagents()
    {
        var (root, state, project, session) = CreateState(HarnessAccessLevel.FullAccess);
        try
        {
            state.Settings.Subagents.MaxAgentsPerTurn = 2;
            state.Settings.Subagents.MaxParallelAgents = 2;
            state.Settings.Subagents.CustomAgents.AddRange(
            [
                WritableSubagent("writer-a"),
                WritableSubagent("writer-b")
            ]);
            var client = new ConcurrentSubagentLlmClient();
            var files = new ConcurrentWriteFileTools();
            var coordinator = new SubagentCoordinator(client, files);
            var runner = new AgentRunner(
                client,
                projectFileTools: files,
                subagentCoordinator: coordinator);

            var result = await runner.RunTurnAsync(
                state,
                project,
                session,
                "run writer-a and writer-b subagents in parallel").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("completed", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, files.WriteCalls);
            Assert.Equal(1, files.MaxConcurrentWrites);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static SubagentDefinition WritableSubagent(string name) => new()
    {
        Name = name,
        Description = "Writes one isolated file.",
        Instructions = "Write the assigned file and summarize the result.",
        Tools = ["project_write_file"],
        AccessLevel = HarnessAccessLevel.FullAccess,
        AutoActivate = false
    };

    private static (string Root, LuckyState State, LuckyProject Project, ChatSession Session) CreateState(
        HarnessAccessLevel accessLevel)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.AgentRunnerLoopSafetyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = new LuckyProject { Id = "project_test", Name = "Test", Path = root };
        var session = new ChatSession { ProjectId = project.Id };
        var state = new LuckyState
        {
            Settings = new AppSettings
            {
                ActiveProvider = LlmProviderKind.LmStudio,
                AccessLevel = accessLevel,
                AutoWebSearch = false,
                MemoriesEnabled = false,
                Subagents = new SubagentSettings
                {
                    Enabled = true,
                    AutoDelegateEnabled = true,
                    MaxAgentsPerTurn = 3,
                    MaxParallelAgents = 3,
                    MaxToolRounds = 4
                }
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

    private sealed class RecordingLlmClient(params LlmResponse[] responses) : ILlmClient
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
            Requests.Add(new LlmRequest([.. messages]));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ConcurrentSubagentLlmClient : ILlmClient
    {
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
            var system = messages.FirstOrDefault(message => message.Role == "system")?.Content ?? "";
            var subagentName = system switch
            {
                var text when text.Contains("Lucky subagent 'writer-a'", StringComparison.Ordinal) => "writer-a",
                var text when text.Contains("Lucky subagent 'writer-b'", StringComparison.Ordinal) => "writer-b",
                _ => null
            };

            if (subagentName is not null)
            {
                var hasToolResult = messages.Any(message => message.Role == "tool");
                return Task.FromResult(hasToolResult
                    ? new LlmResponse($"{subagentName} completed.", provider.Model)
                    : new LlmResponse(
                        "",
                        provider.Model,
                        [new ToolCallRequest(
                            $"write_{subagentName}",
                            "project_write_file",
                            $$"""{"path":"{{subagentName}}.txt","content":"done","overwrite":true}""")]));
            }

            var parentHasToolResults = messages.Any(message => message.Role == "tool");
            return Task.FromResult(parentHasToolResults
                ? new LlmResponse("Both writer subagents completed.", provider.Model)
                : new LlmResponse(
                    "",
                    provider.Model,
                    [
                        new ToolCallRequest("run_writer_a", "subagent_run", """{"agent":"writer-a","task":"Write writer-a.txt."}"""),
                        new ToolCallRequest("run_writer_b", "subagent_run", """{"agent":"writer-b","task":"Write writer-b.txt."}""")
                    ]));
        }
    }

    private sealed class CountingReadFileTools : IProjectFileToolService
    {
        public int ReadCalls { get; private set; }

        public Task<ToolExecutionResult> ListAsync(LuckyProject project, string? relativePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> ReadAsync(LuckyProject project, string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return Task.FromResult(new ToolExecutionResult("project.read_file", relativePath, $"content of {relativePath}"));
        }

        public Task<ToolExecutionResult> SearchAsync(LuckyProject project, string query, string? relativePath = null, string? glob = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> WriteAsync(LuckyProject project, string relativePath, string content, bool overwrite, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> EditAsync(LuckyProject project, string relativePath, string oldText, string newText, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> ApplyPatchAsync(LuckyProject project, string patch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ConcurrentWriteFileTools : IProjectFileToolService
    {
        private readonly object _sync = new();
        private int _activeWrites;

        public int WriteCalls { get; private set; }

        public int MaxConcurrentWrites { get; private set; }

        public Task<ToolExecutionResult> ListAsync(LuckyProject project, string? relativePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> ReadAsync(LuckyProject project, string relativePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> SearchAsync(LuckyProject project, string query, string? relativePath = null, string? glob = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ToolExecutionResult> WriteAsync(
            LuckyProject project,
            string relativePath,
            string content,
            bool overwrite,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                WriteCalls++;
                _activeWrites++;
                MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, _activeWrites);
            }

            try
            {
                await Task.Delay(120, cancellationToken);
                return new ToolExecutionResult("project.write_file", relativePath, $"Wrote {relativePath}.");
            }
            finally
            {
                lock (_sync)
                {
                    _activeWrites--;
                }
            }
        }

        public Task<ToolExecutionResult> EditAsync(LuckyProject project, string relativePath, string oldText, string newText, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolExecutionResult> ApplyPatchAsync(LuckyProject project, string patch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record LlmRequest(IReadOnlyList<LlmChatMessage> Messages);
}
