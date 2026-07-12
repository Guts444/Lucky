using Lucky.Core;

namespace Lucky.Tests;

public sealed class LuckyStoreTests
{
    [Fact]
    public async Task LoadAsync_WhenFileMissingCreatesStateFileAndSaveAsyncRoundTripsState()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var statePath = Path.Combine(tempRoot, "state", "lucky-state.json");
            var store = new LuckyStore(statePath);

            var state = await store.LoadAsync();

            Assert.True(File.Exists(statePath));
            Assert.Empty(state.Projects);
            Assert.Empty(state.Sessions);

            var projectDirectory = Directory.CreateDirectory(Path.Combine(tempRoot, "ProjectAlpha")).FullName;
            var project = LuckyStore.EnsureProject(state, projectDirectory);
            state.Settings.MemoriesEnabled = false;
            var session = Assert.Single(state.Sessions);
            session.Title = "Planning";
            session.Messages.Add(new ChatMessage
            {
                Role = ChatRole.User,
                Content = "Remember my editor preferences."
            });
            session.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "I will keep that in mind.",
                PromptTokens = 42,
                CompletionTokens = 8,
                TotalTokens = 50,
                ContextTokens = 42,
                ContextWindowTokens = 258400,
                ProviderKind = LlmProviderKind.OpenAiCodex,
                ModelId = "gpt-5.5"
            });
            state.Memories.Add(new MemoryItem
            {
                Summary = "I prefer concise code review feedback",
                ProjectId = project.Id,
                SourceSessionId = session.Id,
                Tags = ["concise", "review"]
            });

            await store.SaveAsync(state);

            var loaded = await new LuckyStore(statePath).LoadAsync();

            var loadedProject = Assert.Single(loaded.Projects);
            Assert.Equal(project.Id, loadedProject.Id);
            Assert.Equal(Path.GetFullPath(projectDirectory), loadedProject.Path);
            Assert.Equal(project.Id, loaded.Settings.SelectedProjectId);

            var loadedSession = Assert.Single(loaded.Sessions);
            Assert.Equal(project.Id, loadedSession.ProjectId);
            Assert.Equal("Planning", loadedSession.Title);
            Assert.Collection(
                loadedSession.Messages,
                message =>
                {
                    Assert.Equal(ChatRole.User, message.Role);
                    Assert.Equal("Remember my editor preferences.", message.Content);
                },
                message =>
                {
                    Assert.Equal(ChatRole.Assistant, message.Role);
                    Assert.Equal(42, message.ContextTokens);
                    Assert.Equal(258400, message.ContextWindowTokens);
                    Assert.Equal(LlmProviderKind.OpenAiCodex, message.ProviderKind);
                    Assert.Equal("gpt-5.5", message.ModelId);
                });

            var loadedMemory = Assert.Single(loaded.Memories);
            Assert.Equal("I prefer concise code review feedback", loadedMemory.Summary);
            Assert.Equal(project.Id, loadedMemory.ProjectId);
            Assert.Equal(session.Id, loadedMemory.SourceSessionId);
            Assert.False(loaded.Settings.MemoriesEnabled);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void EnsureProject_ReusesExistingProjectAndKeepsSingleInitialSession()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var state = new LuckyState();
            var projectDirectory = Directory.CreateDirectory(Path.Combine(tempRoot, "ProjectBeta")).FullName;
            var project = LuckyStore.EnsureProject(state, projectDirectory);
            var oldLastOpenedAt = DateTimeOffset.UtcNow.AddDays(-1);
            project.LastOpenedAt = oldLastOpenedAt;

            var reopened = LuckyStore.EnsureProject(state, Path.Combine(projectDirectory, "."));

            Assert.Same(project, reopened);
            Assert.Single(state.Projects);
            Assert.Single(state.Sessions);
            Assert.Equal(project.Id, state.Settings.SelectedProjectId);
            Assert.True(reopened.LastOpenedAt > oldLastOpenedAt);

            var titledSession = LuckyStore.CreateSession(project.Id, "  Research plan  ");
            Assert.Equal(project.Id, titledSession.ProjectId);
            Assert.Equal("Research plan", titledSession.Title);

            var defaultSession = LuckyStore.CreateSession(project.Id, "   ");
            Assert.Equal("New chat", defaultSession.Title);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_NormalizesOldDeepSeekDefaults()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var statePath = Path.Combine(tempRoot, "lucky-state.json");
            await File.WriteAllTextAsync(statePath, """
            {
              "Settings": {
                "Persona": "test",
                "ActiveProvider": 0,
                "AccessLevel": 1,
                "DeepSeek": {
                  "DisplayName": "DeepSeek",
                  "BaseUrl": "https://api.deepseek.com/v1",
                  "Model": "deepseek-chat",
                  "RequiresApiKey": true,
                  "ThinkingEnabled": true,
                  "ReasoningEffort": "max"
                },
                "LmStudio": { "DisplayName": "LM Studio", "BaseUrl": "http://127.0.0.1:1234/v1", "Model": "local-model" },
                "Custom": { "DisplayName": "Custom", "BaseUrl": "http://127.0.0.1:8000/v1", "Model": "local-model" }
              },
              "Projects": [],
              "Sessions": [],
              "Memories": []
            }
            """);

            var state = await new LuckyStore(statePath).LoadAsync();

            Assert.Equal("https://api.deepseek.com", state.Settings.DeepSeek.BaseUrl);
            Assert.True(state.Settings.DeepSeek.SupportsThinking);
            Assert.Equal(1000000, state.Settings.DeepSeek.ContextWindowTokens);
            Assert.Equal(32768, state.Settings.LmStudio.ContextWindowTokens);
            Assert.Equal(2200, state.Settings.MemoryCharLimit);
            Assert.Equal(1375, state.Settings.UserProfileCharLimit);
            Assert.True(state.Settings.MemoriesEnabled);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_NormalizesSavedDeepSeekV4Context()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var statePath = Path.Combine(tempRoot, "lucky-state.json");
            await File.WriteAllTextAsync(statePath, """
            {
              "Settings": {
                "Persona": "test",
                "ActiveProvider": 0,
                "AccessLevel": 1,
                "DeepSeek": {
                  "DisplayName": "DeepSeek",
                  "BaseUrl": "https://api.deepseek.com",
                  "Model": "deepseek-v4-flash",
                  "RequiresApiKey": true,
                  "SupportsThinking": true,
                  "ThinkingEnabled": true,
                  "ReasoningEffort": "max",
                  "ContextWindowTokens": 131072
                },
                "LmStudio": { "DisplayName": "LM Studio", "BaseUrl": "http://127.0.0.1:1234/v1", "Model": "local-model" },
                "Custom": { "DisplayName": "Custom", "BaseUrl": "http://127.0.0.1:8000/v1", "Model": "local-model" }
              },
              "Projects": [],
              "Sessions": [],
              "Memories": []
            }
            """);

            var state = await new LuckyStore(statePath).LoadAsync();

            Assert.Equal(1_000_000, state.Settings.DeepSeek.ContextWindowTokens);
            Assert.True(state.Settings.DeepSeek.SupportsThinking);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_ProvidesSafeCodexSubscriptionDefaultsWithoutPersistingOAuthMaterial()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var statePath = Path.Combine(tempRoot, "lucky-state.json");
            var state = await new LuckyStore(statePath).LoadAsync();

            var codex = state.Settings.OpenAiCodex;
            Assert.Equal(ProviderTransport.CodexAppServer, codex.Transport);
            Assert.False(codex.RequiresApiKey);
            Assert.True(codex.SupportsThinking);
            Assert.True(codex.ThinkingEnabled);
            Assert.Equal("gpt-5.5", codex.Model);
            Assert.Equal(258400, codex.ContextWindowTokens);
            var model = Assert.Single(codex.ModelCapabilities);
            Assert.Equal("gpt-5.5", model.Id);
            Assert.Equal(["low", "medium", "high", "xhigh"], model.ReasoningEfforts);
            Assert.Null(codex.EncryptedApiKey);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_ProvidesOpenRouterDefaultsAndLocksEndpoint()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var statePath = Path.Combine(tempRoot, "lucky-state.json");
            await File.WriteAllTextAsync(statePath, """
            {
              "Settings": {
                "Persona": "test",
                "ActiveProvider": 4,
                "AccessLevel": 1,
                "DeepSeek": { "DisplayName": "DeepSeek", "BaseUrl": "https://api.deepseek.com", "Model": "deepseek-v4-pro" },
                "LmStudio": { "DisplayName": "LM Studio", "BaseUrl": "http://127.0.0.1:1234/v1", "Model": "local-model" },
                "Custom": { "DisplayName": "Custom", "BaseUrl": "http://127.0.0.1:8000/v1", "Model": "local-model" },
                "OpenRouter": {
                  "DisplayName": "OpenRouter",
                  "BaseUrl": "https://evil.example/v1",
                  "Model": "",
                  "RequiresApiKey": false,
                  "ContextWindowTokens": 0
                }
              },
              "Projects": [],
              "Sessions": [],
              "Memories": []
            }
            """);

            var state = await new LuckyStore(statePath).LoadAsync();
            var openRouter = state.Settings.OpenRouter;

            Assert.Equal("OpenRouter", openRouter.DisplayName);
            Assert.Equal("https://openrouter.ai/api/v1", openRouter.BaseUrl);
            Assert.True(openRouter.RequiresApiKey);
            Assert.Equal(ProviderTransport.OpenAiCompatible, openRouter.Transport);
            Assert.False(openRouter.SupportsThinking);
            Assert.False(openRouter.ThinkingEnabled);
            Assert.Equal("openai/gpt-4o-mini", openRouter.Model);
            Assert.Equal(128000, openRouter.ContextWindowTokens);
            Assert.Contains(openRouter.ModelCapabilities, model => model.Id == "openai/gpt-4o-mini");
            Assert.Equal(LlmProviderKind.OpenRouter, state.Settings.ActiveProvider);
            Assert.Same(openRouter, state.Settings.ActiveProviderSettings);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "Lucky.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
