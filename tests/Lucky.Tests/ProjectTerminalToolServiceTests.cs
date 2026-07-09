using Lucky.Core;

namespace Lucky.Tests;

public sealed class ProjectTerminalToolServiceTests
{
    [Fact]
    public async Task RunCommandAsync_RunsAtSelectedProjectRootAndCapturesBothStreams()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectTerminalToolService();

            var result = await service.RunCommandAsync(
                project,
                "Write-Output (Get-Location).Path; [Console]::Error.WriteLine('stderr sentinel')");

            Assert.False(result.IsError);
            Assert.Contains("code 0", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(root, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stdout:", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stderr sentinel", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunCommandAsync_ReportsNonZeroExitAndOutput()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectTerminalToolService();

            var result = await service.RunCommandAsync(
                project,
                "Write-Output 'stdout sentinel'; [Console]::Error.WriteLine('stderr sentinel'); exit 7");

            Assert.True(result.IsError);
            Assert.Contains("code 7", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stdout sentinel", result.Output, StringComparison.Ordinal);
            Assert.Contains("stderr sentinel", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunCommandAsync_RejectsInvalidCommandOrTimeoutBeforeStartingPowerShell()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectTerminalToolService();

            var empty = await service.RunCommandAsync(project, "");
            var invalidTimeout = await service.RunCommandAsync(project, "Write-Output nope", timeoutSeconds: 301);

            Assert.True(empty.IsError);
            Assert.Contains("command is required", empty.Output, StringComparison.OrdinalIgnoreCase);
            Assert.True(invalidTimeout.IsError);
            Assert.Contains("between 1 and 300", invalidTimeout.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunCommandAsync_TimesOutAndReportsConfirmedHostTerminationWithoutOverclaimingTheTree()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectTerminalToolService();

            var result = await service.RunCommandAsync(project, "Start-Sleep -Seconds 20", timeoutSeconds: 1);

            Assert.True(result.IsError);
            Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("requested process-tree termination", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("confirmed that the PowerShell host exited", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Detached child processes are not independently verified", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was stopped", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunCommandAsync_CancellationStopsTheProcessBeforeItCanFinish()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectTerminalToolService();
            using var cancellation = new CancellationTokenSource();
            var startedPath = Path.Combine(root, "started.txt");
            var completedPath = Path.Combine(root, "completed.txt");
            var run = service.RunCommandAsync(
                project,
                "Set-Content -LiteralPath started.txt 'started'; Start-Sleep -Seconds 20; Set-Content -LiteralPath completed.txt 'completed'",
                cancellationToken: cancellation.Token);

            await WaitUntilAsync(() => File.Exists(startedPath), TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await Task.Delay(250);
            Assert.False(File.Exists(completedPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunCommandAsync_CapsLargeOutput()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectTerminalToolService();

            var result = await service.RunCommandAsync(project, "Write-Output ('x' * 60000)");

            Assert.False(result.IsError);
            Assert.Contains("stdout capped", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.Output.Length < 60_000);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AgentRunner_FullAccessExposesAndDispatchesProjectRunCommand()
    {
        var (root, project, state, session) = CreateAgentState(HarnessAccessLevel.FullAccess);
        try
        {
            var toolCall = new ToolCallRequest(
                "call_command",
                "project_run_command",
                """{"command":"dotnet test","timeoutSeconds":90}""");
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [toolCall]),
                new LlmResponse("Tests passed.", "fake"));
            var terminal = new RecordingTerminalService();
            var runner = new AgentRunner(client, projectTerminalTools: terminal);

            var result = await runner.RunTurnAsync(state, project, session, "run the tests");

            Assert.Contains("Tests passed", result.AssistantMessage);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "project_run_command");
            Assert.Equal("dotnet test", terminal.Command);
            Assert.Equal(90, terminal.TimeoutSeconds);
            Assert.Contains(result.Trace, entry =>
                entry.Tool == "project.run_command" &&
                entry.Output.Contains("terminal sentinel", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AgentRunner_WorkspaceDoesNotExposeProjectRunCommand()
    {
        var (root, project, state, session) = CreateAgentState(HarnessAccessLevel.Workspace);
        try
        {
            var client = new ScriptedLlmClient(new LlmResponse("No command access.", "fake"));
            var runner = new AgentRunner(client);

            await runner.RunTurnAsync(state, project, session, "inspect the project");

            Assert.DoesNotContain(client.Requests[0].Tools, tool => tool.Name == "project_run_command");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static (string Root, LuckyProject Project) CreateProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.ProjectTerminalToolServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, new LuckyProject { Name = "Test", Path = root });
    }

    private static (string Root, LuckyProject Project, LuckyState State, ChatSession Session) CreateAgentState(
        HarnessAccessLevel accessLevel)
    {
        var (root, project) = CreateProject();
        project.Id = "project_test";
        var session = new ChatSession { ProjectId = project.Id };
        var state = new LuckyState
        {
            Settings = new AppSettings
            {
                ActiveProvider = LlmProviderKind.LmStudio,
                AccessLevel = accessLevel,
                AutoWebSearch = false,
                MemoriesEnabled = false,
                Subagents = new SubagentSettings { Enabled = false }
            },
            Projects = [project],
            Sessions = [session]
        };
        state.Settings.LmStudio.Model = "fake";
        return (root, project, state, session);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the PowerShell command to start.");
            }

            await Task.Delay(25);
        }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingTerminalService : IProjectTerminalToolService
    {
        public string? Command { get; private set; }
        public int? TimeoutSeconds { get; private set; }

        public Task<ToolExecutionResult> RunCommandAsync(
            LuckyProject project,
            string command,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            Command = command;
            TimeoutSeconds = timeoutSeconds;
            return Task.FromResult(new ToolExecutionResult(
                "project.run_command",
                command,
                "PowerShell exited with code 0. terminal sentinel"));
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
            Requests.Add(new LlmRequest(tools?.ToArray() ?? []));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record LlmRequest(IReadOnlyList<LlmToolDefinition> Tools);
}
