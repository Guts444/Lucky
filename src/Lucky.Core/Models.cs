using System.Text.Json.Serialization;

namespace Lucky.Core;

public enum LlmProviderKind
{
    DeepSeek,
    LmStudio,
    CustomOpenAiCompatible
}

public enum HarnessAccessLevel
{
    ChatOnly,
    Workspace,
    FullAccess
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
    public string SearxngUrl { get; set; } = "http://127.0.0.1:8080";
    public int WebSearchMaxResults { get; set; } = 4;
    public int MemorySearchLimit { get; set; } = 6;
    public int ContextMessageLimit { get; set; } = 24;
    public int MemoryCharLimit { get; set; } = 2200;
    public int UserProfileCharLimit { get; set; } = 1375;
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

    public ProviderSettings ActiveProviderSettings => ActiveProvider switch
    {
        LlmProviderKind.DeepSeek => DeepSeek,
        LlmProviderKind.LmStudio => LmStudio,
        _ => Custom
    };
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
    public bool RequiresApiKey { get; set; }
    public string? EncryptedApiKey { get; set; }
    public bool SupportsThinking { get; set; }
    public bool ThinkingEnabled { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public int ContextWindowTokens { get; set; } = 32768;
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
    IReadOnlyList<string> Required);

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

public sealed record LlmTokenUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

public sealed record AgentTurnResult(
    string AssistantMessage,
    IReadOnlyList<ToolTraceEntry> Trace,
    IReadOnlyList<MemoryItem> RecalledMemories,
    IReadOnlyList<MemoryItem> CapturedMemories,
    bool UsedModel,
    string? ReasoningContent = null,
    LlmTokenUsage? TokenUsage = null);

public sealed record AgentInstructionDocument(string RelativePath, string Content);

public sealed record AgentInstructionSet(IReadOnlyList<AgentInstructionDocument> Documents)
{
    public bool HasInstructions => Documents.Count > 0;

    public string SourceSummary => Documents.Count == 0
        ? "No AGENTS.md files found."
        : string.Join(", ", Documents.Select(document => document.RelativePath));
}

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
internal sealed partial class LuckyJsonContext : JsonSerializerContext;
