using Lucky.Core;

namespace Lucky.Tests;

public sealed class AgentRunnerCancellationTests
{
    [Fact]
    public async Task RunTurnAsync_PropagatesCallerCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.AgentRunnerCancellationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = new LuckyProject { Id = "project_test", Name = "Test", Path = root };
            var session = new ChatSession { ProjectId = project.Id };
            var state = new LuckyState
            {
                Settings = new AppSettings
                {
                    ActiveProvider = LlmProviderKind.LmStudio,
                    AccessLevel = HarnessAccessLevel.Workspace,
                    AutoWebSearch = false
                },
                Projects = [project],
                Sessions = [session]
            };
            state.Settings.LmStudio.Model = "fake";

            var client = new BlockingLlmClient();
            var runner = new AgentRunner(client);
            using var cancellation = new CancellationTokenSource();

            var turn = runner.RunTurnAsync(state, project, session, "hello", cancellation.Token);
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BlockingLlmClient : ILlmClient
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<string>> ListModelsAsync(
            ProviderSettings provider,
            string? apiKey,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([provider.Model]);
        }

        public async Task<LlmResponse> CompleteChatAsync(
            ProviderSettings provider,
            string? apiKey,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            IProgress<LlmStreamDelta>? streamProgress = null)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new LlmResponse("unreachable", provider.Model);
        }
    }
}
