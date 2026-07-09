using Lucky.Core;

namespace Lucky.Tests;

public sealed class DockerCodeExecutionSandboxServiceTests
{
    [Fact]
    public async Task ExecuteAsync_UsesOnlyALocalImageAndBuildsARestrictedContainer()
    {
        var docker = new RecordingDockerCliRunner(
            new DockerCliResult(0, "null", ""),
            new DockerCliResult(0, "sandbox sentinel", ""));
        var service = new DockerCodeExecutionSandboxService(docker);
        var settings = EnabledSettings();

        var result = await service.ExecuteAsync(settings, "python -c \"print('ok')\"");

        Assert.False(result.IsError);
        Assert.Contains("sandbox sentinel", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, docker.Calls.Count);
        Assert.Equal(
            [
                $"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}",
                "image",
                "inspect",
                "--format",
                "{{json .Config.Volumes}}",
                "local/sandbox:latest"
            ],
            docker.Calls[0].Arguments);

        var arguments = docker.Calls[1].Arguments;
        Assert.Equal($"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}", arguments[0]);
        Assert.Contains("--pull=never", arguments);
        Assert.Contains("--network=none", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("--cap-drop=ALL", arguments);
        Assert.Contains("--security-opt=no-new-privileges=true", arguments);
        Assert.Contains("--pids-limit=128", arguments);
        Assert.Contains("--memory=512m", arguments);
        Assert.Contains("--memory-swap=512m", arguments);
        Assert.Contains("--cpus=1", arguments);
        Assert.Contains("--user=65532:65532", arguments);
        Assert.Contains("--tmpfs", arguments);
        Assert.DoesNotContain(arguments, argument => argument is "--privileged" or "--pid=host" or "--network=host");
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--mount", StringComparison.Ordinal) || argument.StartsWith("--volume", StringComparison.Ordinal) || argument == "-v");
        Assert.Contains("--entrypoint=sh", arguments);
        Assert.Contains("-lc", arguments);
        Assert.Equal("python -c \"print('ok')\"", arguments[^1]);
        Assert.Contains("network disabled", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no host paths are mounted", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_MissingLocalImageDoesNotStartAContainerOrPull()
    {
        var docker = new RecordingDockerCliRunner(
            new DockerCliResult(1, "", "Error response from daemon: No such image"));
        var service = new DockerCodeExecutionSandboxService(docker);

        var result = await service.ExecuteAsync(EnabledSettings(), "echo never-runs");

        Assert.True(result.IsError);
        Assert.Single(docker.Calls);
        Assert.Equal($"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}", docker.Calls[0].Arguments[0]);
        Assert.Equal("image", docker.Calls[0].Arguments[1]);
        Assert.Contains("never pulls sandbox images automatically", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutRequestsForcedContainerRemoval()
    {
        var docker = new RecordingDockerCliRunner(
            new DockerCliResult(0, "null", ""),
            new DockerCliResult(null, "partial", "", TimedOut: true),
            new DockerCliResult(0, "", ""));
        var service = new DockerCodeExecutionSandboxService(docker);

        var result = await service.ExecuteAsync(EnabledSettings(), "sleep 30", timeoutSeconds: 1);

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, docker.Calls.Count);
        Assert.Equal($"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}", docker.Calls[2].Arguments[0]);
        Assert.Equal(["container", "rm", "--force"], docker.Calls[2].Arguments.Skip(1).Take(3));
        Assert.StartsWith("lucky-sandbox-", docker.Calls[2].Arguments[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyProjectMountSettingNeverAddsAHostMount()
    {
        var docker = new RecordingDockerCliRunner(
            new DockerCliResult(0, "null", ""),
            new DockerCliResult(0, "ok", ""));
        var service = new DockerCodeExecutionSandboxService(docker);
        var settings = EnabledSettings();
        settings.AllowReadOnlyProjectMount = true;

        var result = await service.ExecuteAsync(settings, "test ! -e /project");

        Assert.False(result.IsError);
        var arguments = docker.Calls[1].Arguments;
        Assert.DoesNotContain(arguments, argument => argument == "--mount" || argument == "-v" || argument.StartsWith("--volume", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("type=bind,", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("no host paths are mounted", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledSandboxDoesNotInvokeDocker()
    {
        var docker = new RecordingDockerCliRunner();
        var service = new DockerCodeExecutionSandboxService(docker);

        var result = await service.ExecuteAsync(new CodeExecutionSandboxSettings(), "echo nope");

        Assert.True(result.IsError);
        Assert.Empty(docker.Calls);
        Assert.Contains("disabled", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsImageThatCouldBeParsedAsADockerOption()
    {
        var docker = new RecordingDockerCliRunner();
        var service = new DockerCodeExecutionSandboxService(docker);
        var settings = EnabledSettings();
        settings.Image = "--help";

        var result = await service.ExecuteAsync(settings, "echo nope");

        Assert.True(result.IsError);
        Assert.Empty(docker.Calls);
        Assert.Contains("image name", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ImageWithDeclaredVolumesDoesNotStartContainer()
    {
        var docker = new RecordingDockerCliRunner(new DockerCliResult(0, """{"/data":{}}""", ""));
        var service = new DockerCodeExecutionSandboxService(docker);

        var result = await service.ExecuteAsync(EnabledSettings(), "echo nope");

        Assert.True(result.IsError);
        Assert.Single(docker.Calls);
        Assert.Contains("declares Docker volume", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerSandboxDaemonPolicy_RejectsRemoteHostAndNonDefaultContext()
    {
        Assert.NotNull(DockerSandboxDaemonPolicy.Validate("tcp://docker.example.test:2376", null, isWindows: true));
        Assert.NotNull(DockerSandboxDaemonPolicy.Validate(null, "production", isWindows: true));
        Assert.Null(DockerSandboxDaemonPolicy.Validate("npipe:////./pipe/docker_engine", "default", isWindows: true));
        Assert.NotNull(DockerSandboxDaemonPolicy.Validate(null, null, isWindows: false));
    }

    [Fact]
    public async Task AgentRunner_FullAccessRegistersAndDispatchesSandboxExecute()
    {
        var root = CreateProjectRoot();
        try
        {
            var project = new LuckyProject { Id = "project", Name = "Test", Path = root };
            var state = CreateAgentState(HarnessAccessLevel.FullAccess);
            var session = LuckyStore.CreateSession(project.Id);
            var client = new ScriptedLlmClient(
                new LlmResponse("", "fake", [new ToolCallRequest("sandbox", "sandbox_execute", """{"command":"python -c \"print('ok')\"","timeoutSeconds":12}""")]),
                new LlmResponse("Sandbox completed.", "fake"));
            var sandbox = new RecordingSandboxService();
            var runner = new AgentRunner(llmClient: client, codeExecutionSandbox: sandbox);

            var result = await runner.RunTurnAsync(state, project, session, "run the isolated code check");

            Assert.Equal("Sandbox completed.", result.AssistantMessage);
            Assert.Contains(client.Requests[0].Tools, tool => tool.Name == "sandbox_execute");
            Assert.Equal("python -c \"print('ok')\"", sandbox.Command);
            Assert.Equal(12, sandbox.TimeoutSeconds);
            Assert.Contains(result.Trace, entry => entry.Tool == "sandbox.execute" && !entry.IsError);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AgentRunner_WorkspaceDoesNotExposeSandboxExecute()
    {
        var root = CreateProjectRoot();
        try
        {
            var project = new LuckyProject { Id = "project", Name = "Test", Path = root };
            var state = CreateAgentState(HarnessAccessLevel.Workspace);
            var session = LuckyStore.CreateSession(project.Id);
            var client = new ScriptedLlmClient(new LlmResponse("No sandbox at workspace access.", "fake"));
            var sandbox = new RecordingSandboxService();
            var runner = new AgentRunner(llmClient: client, codeExecutionSandbox: sandbox);

            await runner.RunTurnAsync(state, project, session, "inspect safely");

            Assert.DoesNotContain(client.Requests[0].Tools, tool => tool.Name == "sandbox_execute");
            Assert.Null(sandbox.Command);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static CodeExecutionSandboxSettings EnabledSettings() => new()
    {
        Enabled = true,
        Image = "local/sandbox:latest",
        TimeoutSeconds = 60,
        MemoryMiB = 512,
        CpuLimit = 1.0,
        PidsLimit = 128,
        ScratchMiB = 128
    };

    private static LuckyState CreateAgentState(HarnessAccessLevel accessLevel) => new()
    {
        Settings = new AppSettings
        {
            AccessLevel = accessLevel,
            AutoWebSearch = false,
            MemoriesEnabled = false,
            Subagents = new SubagentSettings { Enabled = false },
            Sandbox = EnabledSettings()
        }
    };

    private static string CreateProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.DockerCodeExecutionSandboxServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.txt"), "sandbox test");
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingDockerCliRunner(params DockerCliResult[] results) : IDockerCliRunner
    {
        private readonly Queue<DockerCliResult> _results = new(results);

        public List<DockerCall> Calls { get; } = [];

        public Task<DockerCliResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new DockerCall(arguments.ToArray(), timeout));
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new DockerCliResult(0, "", ""));
        }
    }

    private sealed record DockerCall(IReadOnlyList<string> Arguments, TimeSpan Timeout);

    private sealed class RecordingSandboxService : ICodeExecutionSandboxService
    {
        public string? Command { get; private set; }
        public int? TimeoutSeconds { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            CodeExecutionSandboxSettings settings,
            string command,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            Command = command;
            TimeoutSeconds = timeoutSeconds;
            return Task.FromResult(new ToolExecutionResult("sandbox.execute", command, "sandbox sentinel"));
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
            Task.FromResult<IReadOnlyList<string>>([]);

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
