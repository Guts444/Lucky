using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucky.Core;

public enum LlmProviderKind
{
    DeepSeek,
    LmStudio,
    CustomOpenAiCompatible,
    OpenAiCodex,
    OpenRouter
}

/// <summary>
/// Describes how Lucky reaches a configured model provider. Most providers expose the
/// OpenAI-compatible HTTP chat-completions contract; Codex subscriptions are deliberately
/// routed through the locally installed official Codex app-server instead.
/// </summary>
public enum ProviderTransport
{
    OpenAiCompatible,
    CodexAppServer
}

public enum HarnessAccessLevel
{
    ChatOnly,
    Workspace,
    FullAccess
}

public enum McpTransportKind
{
    Stdio
}

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public enum MemoryKind
{
    Memory,
    UserProfile
}

public sealed class LuckyState
{
    public AppSettings Settings { get; set; } = new();
    public List<LuckyProject> Projects { get; set; } = [];
    public List<ChatSession> Sessions { get; set; } = [];
    public List<MemoryItem> Memories { get; set; } = [];
}

public sealed class AppSettings
{
    public string Persona { get; set; } =
        "You are Lucky, a practical local-first AI harness. Be warm, direct, curious, and useful. " +
        "Prefer explicit user intent, keep tool use visible, and preserve durable preferences only when they are helpful later.";

    public LlmProviderKind ActiveProvider { get; set; } = LlmProviderKind.LmStudio;
    public HarnessAccessLevel AccessLevel { get; set; } = HarnessAccessLevel.Workspace;
    public string? SelectedProjectId { get; set; }
    public bool AutoWebSearch { get; set; } = true;
    public bool MemoriesEnabled { get; set; } = true;
    public string SearxngUrl { get; set; } = "http://127.0.0.1:8080";
    public int WebSearchMaxResults { get; set; } = 4;
    public int MemorySearchLimit { get; set; } = 6;
    public int ContextMessageLimit { get; set; } = 24;
    public int MemoryCharLimit { get; set; } = 2200;
    public int UserProfileCharLimit { get; set; } = 1375;
    public WebBrowserSettings Browser { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public CodeExecutionSandboxSettings Sandbox { get; set; } = new();
    public SubagentSettings Subagents { get; set; } = new();
    public ProviderSettings DeepSeek { get; set; } = new()
    {
        DisplayName = "DeepSeek",
        BaseUrl = "https://api.deepseek.com",
        Model = "deepseek-v4-pro",
        RequiresApiKey = true,
        SupportsThinking = true,
        ThinkingEnabled = true,
        ReasoningEffort = "max",
        ContextWindowTokens = 1000000
    };

    public ProviderSettings LmStudio { get; set; } = new()
    {
        DisplayName = "LM Studio",
        BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "local-model",
        RequiresApiKey = false,
        ContextWindowTokens = 32768
    };

    public ProviderSettings Custom { get; set; } = new()
    {
        DisplayName = "Custom",
        BaseUrl = "http://127.0.0.1:8000/v1",
        Model = "local-model",
        RequiresApiKey = false,
        ContextWindowTokens = 32768
    };

    public ProviderSettings OpenRouter { get; set; } = new()
    {
        DisplayName = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Model = "openai/gpt-4o-mini",
        RequiresApiKey = true,
        SupportsThinking = false,
        ThinkingEnabled = false,
        ReasoningEffort = "medium",
        ContextWindowTokens = 128000,
        ModelCapabilities =
        [
            new ProviderModelCapability
            {
                Id = "openai/gpt-4o-mini",
                DisplayName = "GPT-4o mini",
                Description = "OpenRouter · fast general default",
                ReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
                ContextWindowTokens = 128000,
                IsDefault = true
            },
            new ProviderModelCapability
            {
                Id = "openai/gpt-4o",
                DisplayName = "GPT-4o",
                Description = "OpenRouter · strong general model",
                ReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
                ContextWindowTokens = 128000
            },
            new ProviderModelCapability
            {
                Id = "anthropic/claude-sonnet-4",
                DisplayName = "Claude Sonnet 4",
                Description = "OpenRouter · coding and agents",
                ReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
                ContextWindowTokens = 200000
            },
            new ProviderModelCapability
            {
                Id = "google/gemini-2.5-flash",
                DisplayName = "Gemini 2.5 Flash",
                Description = "OpenRouter · fast multimodal",
                ReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
                ContextWindowTokens = 1000000
            },
            new ProviderModelCapability
            {
                Id = "deepseek/deepseek-chat",
                DisplayName = "DeepSeek Chat",
                Description = "OpenRouter · DeepSeek via OpenRouter",
                ReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
                ContextWindowTokens = 128000
            }
        ]
    };

    public ProviderSettings OpenAiCodex { get; set; } = new()
    {
        DisplayName = "OpenAI Codex",
        Model = "gpt-5.5",
        RequiresApiKey = false,
        Transport = ProviderTransport.CodexAppServer,
        SupportsThinking = true,
        ThinkingEnabled = true,
        ReasoningEffort = "medium",
        // This is Codex's safe default input budget for GPT-5.5, not the public API's
        // total context-window headline. A live Codex token-usage event supersedes it.
        ContextWindowTokens = 258400,
        ModelCapabilities =
        [
            new ProviderModelCapability
            {
                Id = "gpt-5.5",
                DisplayName = "GPT-5.5",
                Description = "Codex subscription model",
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
                DefaultReasoningEffort = "medium",
                ContextWindowTokens = 258400,
                IsDefault = true
            }
        ]
    };

    public ProviderSettings ActiveProviderSettings => ActiveProvider switch
    {
        LlmProviderKind.DeepSeek => DeepSeek,
        LlmProviderKind.LmStudio => LmStudio,
        LlmProviderKind.OpenRouter => OpenRouter,
        LlmProviderKind.OpenAiCodex => OpenAiCodex,
        _ => Custom
    };
}

public sealed class WebBrowserSettings
{
    // Browser access is intentionally opt-in. An empty domain list exposes no page-reader tool.
    public bool Enabled { get; set; }
    public List<string> AllowedDomains { get; set; } = [];
    public int MaxPageChars { get; set; } = 12000;
}

public sealed class McpSettings
{
    // MCP servers can expose arbitrary capabilities, so they are opt-in and FullAccess-gated.
    public bool Enabled { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 60;
    public int MaxToolOutputChars { get; set; } = 16000;
    public List<McpServerDefinition> Servers { get; set; } = [];
}

/// <summary>
/// User-controlled policy for the Docker code-execution sandbox. Disabled and unconfigured by
/// default: Lucky never pulls an image or silently converts host PowerShell into a sandbox.
/// </summary>
public sealed class CodeExecutionSandboxSettings
{
    public bool Enabled { get; set; }
    public string Image { get; set; } = "";
    // Legacy persisted field retained only so old state can migrate safely. The sandbox never
    // mounts host paths and LuckyStore resets this to false during load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowReadOnlyProjectMount { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MemoryMiB { get; set; } = 512;
    public double CpuLimit { get; set; } = 1.0;
    public int PidsLimit { get; set; } = 128;
    public int ScratchMiB { get; set; } = 128;
}

public sealed class McpServerDefinition
{
    public string Id { get; set; } = IdFactory.NewId("mcp");
    public string Name { get; set; } = "MCP server";
    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;

    // The launch configuration can contain an API key or access token. Keep it in memory only
    // while Lucky is running and persist the serialized payload through Windows DPAPI instead of
    // writing individual command-line strings into lucky-state.json.
    [JsonIgnore]
    public string Command { get; set; } = "";

    [JsonIgnore]
    public List<string> Arguments { get; set; } = [];

    [JsonIgnore]
    public string? WorkingDirectory { get; set; }

    public string? EncryptedLaunchConfiguration { get; set; }

    // Backward-compatible read-only JSON aliases. Existing plain-text state is migrated into
    // EncryptedLaunchConfiguration on its next save; new state never emits these properties.
    [JsonPropertyName("Command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCommand
    {
        get => null;
        set => Command = value ?? "";
    }

    [JsonPropertyName("Arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyArguments
    {
        get => null;
        set => Arguments = value ?? [];
    }

    [JsonPropertyName("WorkingDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyWorkingDirectory
    {
        get => null;
        set => WorkingDirectory = value;
    }

    public bool Enabled { get; set; } = true;
}

public sealed class McpServerLaunchConfiguration
{
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
}

public sealed class SubagentSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoDelegateEnabled { get; set; } = true;
    public int MaxAgentsPerTurn { get; set; } = 3;
    public int MaxParallelAgents { get; set; } = 3;
    public int MaxToolRounds { get; set; } = 4;
    public int AgentTimeoutSeconds { get; set; } = 180;
    public List<SubagentDefinition> CustomAgents { get; set; } = [];
}

public sealed class SubagentDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<string> Tools { get; set; } = ["project_list_files", "project_read_file", "project_search"];
    public bool Enabled { get; set; } = true;
    public bool AutoActivate { get; set; } = true;
    public HarnessAccessLevel AccessLevel { get; set; } = HarnessAccessLevel.Workspace;
    public string? ModelOverride { get; set; }
    public string? ReasoningEffortOverride { get; set; }
}

public sealed class ProviderSettings
{
    public string DisplayName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public ProviderTransport Transport { get; set; } = ProviderTransport.OpenAiCompatible;
    public bool RequiresApiKey { get; set; }
    public string? EncryptedApiKey { get; set; }
    public bool SupportsThinking { get; set; }
    public bool ThinkingEnabled { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public int ContextWindowTokens { get; set; } = 32768;
    // This contains provider-advertised model and reasoning metadata. For Codex it is refreshed
    // from the signed-in official app-server catalog; it never contains an OAuth token.
    public List<ProviderModelCapability> ModelCapabilities { get; set; } = [];
    public string? ConnectedAccountPlan { get; set; }
}

public sealed class ProviderModelCapability
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> ReasoningEfforts { get; set; } = [];
    public string DefaultReasoningEffort { get; set; } = "medium";
    // Effective input budget for the model route, used by Lucky's composer context meter.
    public int ContextWindowTokens { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class LuckyProject
{
    public string Id { get; set; } = IdFactory.NewId("project");
    public string Name { get; set; } = "Project";
    public string Path { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatSession
{
    public string Id { get; set; } = IdFactory.NewId("chat");
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMessage
{
    public string Id { get; set; } = IdFactory.NewId("msg");
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Trace { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    // The latest provider-reported input context and its effective limit. These are intentionally
    // separate from billed per-turn totals so the composer meter does not overcount tool loops.
    public int? ContextTokens { get; set; }
    public int? ContextWindowTokens { get; set; }
    // Context is only exact for the provider/model that reported it. Keeping this provenance
    // prevents a meter from reusing an old provider's value after the user switches models.
    public LlmProviderKind? ProviderKind { get; set; }
    public string? ModelId { get; set; }
}

public sealed class MemoryItem
{
    public string Id { get; set; } = IdFactory.NewId("mem");
    public MemoryKind Kind { get; set; } = MemoryKind.Memory;
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string? ProjectId { get; set; }
    public string? SourceSessionId { get; set; }
    public string Evidence { get; set; } = "";
    public double Confidence { get; set; } = 0.65;
    public bool Enabled { get; set; } = true;
    public bool Pinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed record SearchResult(string Title, string Url, string Snippet);

public sealed record ToolTraceEntry(
    string Tool,
    string Input,
    string Output,
    bool IsError = false,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

public sealed record ToolExecutionResult(string Tool, string Input, string Output, bool IsError = false);

public sealed record AgentProgressEvent(string Stage, string Summary, string? Detail = null);

public sealed record LlmStreamDelta(string ContentDelta = "", string? ReasoningDelta = null);

public sealed record LlmToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, ToolParameterDefinition> Parameters,
    IReadOnlyList<string> Required,
    JsonElement? InputSchema = null);

public sealed record ToolParameterDefinition(string Type, string Description, bool Required = false);

public sealed record LlmChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    string? ReasoningContent = null);

public sealed record LlmResponse(
    string Content,
    string Model,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    string? ReasoningContent = null,
    LlmTokenUsage? Usage = null);

public sealed record LlmTokenUsage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? ContextTokens = null,
    int? ContextWindowTokens = null);

public sealed record AgentTurnResult(
    string AssistantMessage,
    IReadOnlyList<ToolTraceEntry> Trace,
    IReadOnlyList<MemoryItem> RecalledMemories,
    IReadOnlyList<MemoryItem> CapturedMemories,
    bool UsedModel,
    string? ReasoningContent = null,
    LlmTokenUsage? TokenUsage = null,
    string? Model = null);

public sealed record AgentInstructionDocument(string RelativePath, string Content);

public sealed record AgentInstructionSet(IReadOnlyList<AgentInstructionDocument> Documents)
{
    public bool HasInstructions => Documents.Count > 0;

    public string SourceSummary => Documents.Count == 0
        ? "No AGENTS.md files found."
        : string.Join(", ", Documents.Select(document => document.RelativePath));
}

public sealed record McpDiscoveredTool(
    string ModelToolName,
    string ServerId,
    string ServerName,
    string ToolName,
    string Description,
    IReadOnlyDictionary<string, ToolParameterDefinition> Parameters,
    IReadOnlyList<string> Required,
    JsonElement InputSchema);

public sealed record SubagentRunResult(
    string AgentName,
    string Task,
    string Summary,
    IReadOnlyList<ToolTraceEntry> Trace,
    bool IsError = false,
    LlmTokenUsage? TokenUsage = null);

internal static class IdFactory
{
    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LuckyState))]
[JsonSerializable(typeof(SubagentDefinition))]
[JsonSerializable(typeof(McpServerLaunchConfiguration))]
internal sealed partial class LuckyJsonContext : JsonSerializerContext;
