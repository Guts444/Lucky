using System.Text.Json;

namespace Lucky.Core;

public sealed class LuckyStore
{
    private readonly string _statePath;

    public LuckyStore(string? statePath = null)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _statePath = statePath ?? Path.Combine(root, "Lucky", "lucky-state.json");
    }

    public string StatePath => _statePath;

    public async Task<LuckyState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath))
        {
            var fresh = new LuckyState();
            await SaveAsync(fresh, cancellationToken).ConfigureAwait(false);
            return fresh;
        }

        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync(stream, LuckyJsonContext.Default.LuckyState, cancellationToken)
            .ConfigureAwait(false);

        state ??= new LuckyState();
        Normalize(state);
        return state;
    }

    public async Task SaveAsync(LuckyState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, LuckyJsonContext.Default.LuckyState, cancellationToken)
                .ConfigureAwait(false);
        }

        if (File.Exists(_statePath))
        {
            File.Replace(tempPath, _statePath, null);
        }
        else
        {
            File.Move(tempPath, _statePath);
        }
    }

    public static LuckyProject EnsureProject(LuckyState state, string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var existing = state.Projects.FirstOrDefault(project =>
            string.Equals(Path.GetFullPath(project.Path), fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.LastOpenedAt = DateTimeOffset.UtcNow;
            state.Settings.SelectedProjectId = existing.Id;
            return existing;
        }

        var project = new LuckyProject
        {
            Name = new DirectoryInfo(fullPath).Name,
            Path = fullPath
        };

        state.Projects.Insert(0, project);
        state.Settings.SelectedProjectId = project.Id;
        state.Sessions.Insert(0, CreateSession(project.Id));
        return project;
    }

    public static ChatSession CreateSession(string projectId, string? title = null)
    {
        return new ChatSession
        {
            ProjectId = projectId,
            Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim()
        };
    }

    private static void Normalize(LuckyState state)
    {
        var migratedOldDeepSeekBaseUrl = string.Equals(
            state.Settings.DeepSeek.BaseUrl,
            "https://api.deepseek.com/v1",
            StringComparison.OrdinalIgnoreCase);

        if (migratedOldDeepSeekBaseUrl)
        {
            state.Settings.DeepSeek.BaseUrl = "https://api.deepseek.com";
        }

        state.Settings.DeepSeek.SupportsThinking = true;
        if (state.Settings.DeepSeek.ContextWindowTokens <= 0 ||
            (migratedOldDeepSeekBaseUrl && state.Settings.DeepSeek.ContextWindowTokens == 32768) ||
            (state.Settings.DeepSeek.Model.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase) &&
             state.Settings.DeepSeek.ContextWindowTokens < 1_000_000))
        {
            state.Settings.DeepSeek.ContextWindowTokens = 1_000_000;
        }

        if (state.Settings.LmStudio.ContextWindowTokens <= 0)
        {
            state.Settings.LmStudio.ContextWindowTokens = 32768;
        }

        if (state.Settings.Custom.ContextWindowTokens <= 0)
        {
            state.Settings.Custom.ContextWindowTokens = 32768;
        }

        if (state.Settings.MemoryCharLimit <= 0)
        {
            state.Settings.MemoryCharLimit = 2200;
        }

        if (state.Settings.UserProfileCharLimit <= 0)
        {
            state.Settings.UserProfileCharLimit = 1375;
        }

        state.Settings.Subagents ??= new SubagentSettings();
        state.Settings.Subagents.MaxAgentsPerTurn = Math.Clamp(state.Settings.Subagents.MaxAgentsPerTurn, 0, 12);
        if (state.Settings.Subagents.MaxParallelAgents <= 0)
        {
            state.Settings.Subagents.MaxParallelAgents = 3;
        }

        state.Settings.Subagents.MaxParallelAgents = Math.Clamp(state.Settings.Subagents.MaxParallelAgents, 1, 12);
        state.Settings.Subagents.MaxToolRounds = Math.Clamp(
            state.Settings.Subagents.MaxToolRounds <= 0 ? 4 : state.Settings.Subagents.MaxToolRounds,
            1,
            8);
        state.Settings.Subagents.AgentTimeoutSeconds = Math.Clamp(
            state.Settings.Subagents.AgentTimeoutSeconds <= 0 ? 180 : state.Settings.Subagents.AgentTimeoutSeconds,
            15,
            1800);
        state.Settings.Subagents.CustomAgents ??= [];
        state.Settings.Subagents.CustomAgents = state.Settings.Subagents.CustomAgents
            .Where(agent =>
                !string.IsNullOrWhiteSpace(agent.Name) &&
                !string.IsNullOrWhiteSpace(agent.Description) &&
                !string.IsNullOrWhiteSpace(agent.Instructions))
            .ToList();
    }
}
