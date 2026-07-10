using System.Text.Json;

namespace Lucky.Core;

public sealed class LuckyStore
{
    private readonly string _statePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

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
        ArgumentNullException.ThrowIfNull(state);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            Normalize(state);
            ProtectMcpLaunchConfigurations(state.Settings.Mcp);

            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
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

            tempPath = null;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // A stale temp file is harmless and can be cleaned on a later save.
                }
            }

            _saveGate.Release();
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
        // Older builds could accidentally save a DeepSeek key while switching the generic
        // provider editor to LM Studio. Local LM Studio never receives stored bearer secrets.
        state.Settings.LmStudio.EncryptedApiKey = null;

        if (state.Settings.Custom.ContextWindowTokens <= 0)
        {
            state.Settings.Custom.ContextWindowTokens = 32768;
        }

        state.Settings.OpenAiCodex ??= new AppSettings().OpenAiCodex;
        var codex = state.Settings.OpenAiCodex;
        codex.DisplayName = "OpenAI Codex";
        codex.Transport = ProviderTransport.CodexAppServer;
        codex.RequiresApiKey = false;
        codex.SupportsThinking = true;
        codex.ThinkingEnabled = true;
        codex.ModelCapabilities ??= [];
        codex.ModelCapabilities = codex.ModelCapabilities
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(model =>
            {
                model.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName.Trim();
                model.Description ??= "Codex subscription model";
                model.ReasoningEfforts ??= [];
                model.ReasoningEfforts = model.ReasoningEfforts
                    .Where(effort => !string.IsNullOrWhiteSpace(effort))
                    .Select(effort => effort.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                model.DefaultReasoningEffort = string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)
                    ? model.ReasoningEfforts.FirstOrDefault() ?? "medium"
                    : model.DefaultReasoningEffort.Trim().ToLowerInvariant();
                if (model.ReasoningEfforts.Count == 0)
                {
                    model.ReasoningEfforts.Add(model.DefaultReasoningEffort);
                }

                model.ContextWindowTokens = Math.Max(1024,
                    model.ContextWindowTokens > 0
                        ? model.ContextWindowTokens
                        : CodexModelContextDefaults.For(model.Id));
                return model;
            })
            .ToList();
        if (codex.ModelCapabilities.Count == 0)
        {
            codex.ModelCapabilities = new AppSettings().OpenAiCodex.ModelCapabilities;
        }

        var selectedCodexModel = codex.ModelCapabilities.FirstOrDefault(model =>
            string.Equals(model.Id, codex.Model, StringComparison.OrdinalIgnoreCase))
            ?? codex.ModelCapabilities.FirstOrDefault(model => model.IsDefault)
            ?? codex.ModelCapabilities[0];
        codex.Model = selectedCodexModel.Id;
        codex.ContextWindowTokens = selectedCodexModel.ContextWindowTokens;
        if (!selectedCodexModel.ReasoningEfforts.Contains(codex.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
        {
            codex.ReasoningEffort = selectedCodexModel.DefaultReasoningEffort;
        }

        if (state.Settings.MemoryCharLimit <= 0)
        {
            state.Settings.MemoryCharLimit = 2200;
        }

        if (state.Settings.UserProfileCharLimit <= 0)
        {
            state.Settings.UserProfileCharLimit = 1375;
        }

        state.Settings.Browser ??= new WebBrowserSettings();
        state.Settings.Browser.AllowedDomains ??= [];
        state.Settings.Browser.AllowedDomains = state.Settings.Browser.AllowedDomains
            .Select(domain => domain?.Trim().TrimStart('.'))
            .Where(domain => !string.IsNullOrWhiteSpace(domain) &&
                             domain!.Length <= 253 &&
                             domain.All(character => char.IsLetterOrDigit(character) || character is '-' or '.'))
            .Select(domain => domain!.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        state.Settings.Browser.MaxPageChars = Math.Clamp(
            state.Settings.Browser.MaxPageChars <= 0 ? 12000 : state.Settings.Browser.MaxPageChars,
            1000,
            40000);

        state.Settings.Mcp ??= new McpSettings();
        state.Settings.Mcp.RequestTimeoutSeconds = Math.Clamp(
            state.Settings.Mcp.RequestTimeoutSeconds <= 0 ? 60 : state.Settings.Mcp.RequestTimeoutSeconds,
            5,
            300);
        state.Settings.Mcp.MaxToolOutputChars = Math.Clamp(
            state.Settings.Mcp.MaxToolOutputChars <= 0 ? 16000 : state.Settings.Mcp.MaxToolOutputChars,
            1000,
            64000);
        state.Settings.Mcp.Servers ??= [];
        state.Settings.Mcp.Servers = state.Settings.Mcp.Servers
            .Where(server => server is not null)
            .Take(16)
            .Select(NormalizeMcpServer)
            .Where(server => !string.IsNullOrWhiteSpace(server.Command))
            .ToList();

        state.Settings.Sandbox ??= new CodeExecutionSandboxSettings();
        state.Settings.Sandbox.Image = NormalizeSandboxImage(state.Settings.Sandbox.Image);
        // Earlier builds offered a read-only project bind mount. It is intentionally retired:
        // even a careful reparse-point scan has a host filesystem TOCTOU window.
        state.Settings.Sandbox.AllowReadOnlyProjectMount = false;
        state.Settings.Sandbox.TimeoutSeconds = Math.Clamp(
            state.Settings.Sandbox.TimeoutSeconds <= 0 ? 60 : state.Settings.Sandbox.TimeoutSeconds,
            5,
            120);
        state.Settings.Sandbox.MemoryMiB = Math.Clamp(
            state.Settings.Sandbox.MemoryMiB <= 0 ? 512 : state.Settings.Sandbox.MemoryMiB,
            64,
            2048);
        state.Settings.Sandbox.CpuLimit = !double.IsFinite(state.Settings.Sandbox.CpuLimit) || state.Settings.Sandbox.CpuLimit <= 0
            ? 1.0
            : Math.Clamp(state.Settings.Sandbox.CpuLimit, 0.25, 2.0);
        state.Settings.Sandbox.PidsLimit = Math.Clamp(
            state.Settings.Sandbox.PidsLimit <= 0 ? 128 : state.Settings.Sandbox.PidsLimit,
            16,
            256);
        state.Settings.Sandbox.ScratchMiB = Math.Clamp(
            state.Settings.Sandbox.ScratchMiB <= 0 ? 128 : state.Settings.Sandbox.ScratchMiB,
            16,
            512);

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

    private static McpServerDefinition NormalizeMcpServer(McpServerDefinition server)
    {
        HydrateMcpLaunchConfiguration(server);
        server.Id = string.IsNullOrWhiteSpace(server.Id) ? IdFactory.NewId("mcp") : server.Id.Trim();
        server.Name = string.IsNullOrWhiteSpace(server.Name) ? "MCP server" : server.Name.Trim();
        server.Command = server.Command?.Trim() ?? "";
        server.Arguments ??= [];
        server.Arguments = server.Arguments
            .Where(argument => !string.IsNullOrWhiteSpace(argument) &&
                               !argument.Contains('\r') &&
                               !argument.Contains('\n'))
            .Select(argument => argument.Trim())
            .Take(64)
            .ToList();
        server.WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory)
            ? null
            : server.WorkingDirectory.Trim();
        server.Transport = McpTransportKind.Stdio;
        return server;
    }

    private static void HydrateMcpLaunchConfiguration(McpServerDefinition server)
    {
        if (!string.IsNullOrWhiteSpace(server.Command) ||
            string.IsNullOrWhiteSpace(server.EncryptedLaunchConfiguration))
        {
            return;
        }

        var serialized = CredentialProtector.Unprotect(server.EncryptedLaunchConfiguration);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            // The data belongs to another Windows user or has been damaged. Do not attempt to run
            // a partially hydrated server, and never fall back to an unprotected representation.
            server.Enabled = false;
            return;
        }

        try
        {
            var configuration = JsonSerializer.Deserialize(
                serialized,
                LuckyJsonContext.Default.McpServerLaunchConfiguration);
            if (configuration is null)
            {
                server.Enabled = false;
                return;
            }

            server.Command = configuration.Command ?? "";
            server.Arguments = configuration.Arguments ?? [];
            server.WorkingDirectory = configuration.WorkingDirectory;
        }
        catch (JsonException)
        {
            server.Enabled = false;
        }
    }

    private static void ProtectMcpLaunchConfigurations(McpSettings? settings)
    {
        foreach (var server in settings?.Servers ?? [])
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Command))
            {
                continue;
            }

            var configuration = new McpServerLaunchConfiguration
            {
                Command = server.Command,
                Arguments = [.. (server.Arguments ?? [])],
                WorkingDirectory = server.WorkingDirectory
            };
            var serialized = JsonSerializer.Serialize(
                configuration,
                LuckyJsonContext.Default.McpServerLaunchConfiguration);
            server.EncryptedLaunchConfiguration = CredentialProtector.Protect(serialized)
                ?? throw new InvalidOperationException("Lucky could not protect the MCP launch configuration for this Windows user.");
        }
    }

    private static string NormalizeSandboxImage(string? image)
    {
        var normalized = image?.Trim() ?? "";
        if (normalized.Length > 255 ||
            (normalized.Length > 0 && !char.IsLetterOrDigit(normalized[0])) ||
            !normalized.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or ':' or '@'))
        {
            return "";
        }

        return normalized;
    }
}
