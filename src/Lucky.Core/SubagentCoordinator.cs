using System.Text;
using System.Text.Json;

namespace Lucky.Core;

public sealed class SubagentCoordinator
{
    private readonly ILlmClient _llmClient;
    private readonly IProjectFileToolService _projectFileTools;
    private readonly IProjectTerminalToolService _projectTerminalTools;

    public SubagentCoordinator(
        ILlmClient? llmClient = null,
        IProjectFileToolService? projectFileTools = null,
        IProjectTerminalToolService? projectTerminalTools = null)
    {
        _llmClient = llmClient ?? new OpenAiCompatibleClient();
        _projectFileTools = projectFileTools ?? new ProjectFileToolService();
        _projectTerminalTools = projectTerminalTools ?? new ProjectTerminalToolService();
    }

    public async Task<SubagentRunResult> RunAsync(
        AppSettings settings,
        LuckyProject? project,
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<SubagentDefinition> definitions,
        AgentInstructionSet instructions,
        string agentName,
        string task,
        string? context = null,
        CancellationToken cancellationToken = default,
        IProgress<AgentProgressEvent>? progress = null)
    {
        if (!settings.Subagents.Enabled)
        {
            return Failure(agentName, task, "Subagents are disabled in Settings.");
        }

        var definition = definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return Failure(agentName, task, $"Unknown subagent '{agentName}'.");
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return Failure(definition.Name, task, "Subagent task is required.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.Subagents.AgentTimeoutSeconds, 15, 1800)));

        var runner = new SubagentRunner(_llmClient, _projectFileTools, _projectTerminalTools);
        try
        {
            progress?.Report(new AgentProgressEvent("tool", $"Starting subagent {definition.Name}", task));
            return await runner.RunAsync(
                settings,
                project,
                provider,
                apiKey,
                definition,
                instructions,
                task.Trim(),
                context,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(definition.Name, task, $"Subagent timed out after {settings.Subagents.AgentTimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return Failure(definition.Name, task, ex.Message);
        }
    }

    private static SubagentRunResult Failure(string agentName, string task, string message)
    {
        return new SubagentRunResult(
            string.IsNullOrWhiteSpace(agentName) ? "subagent" : agentName,
            task,
            message,
            [],
            IsError: true);
    }
}

internal sealed class SubagentRunner
{
    private const int MaxToolCallsPerResponse = 8;

    private readonly ILlmClient _llmClient;
    private readonly IProjectFileToolService _projectFileTools;
    private readonly IProjectTerminalToolService _projectTerminalTools;

    public SubagentRunner(
        ILlmClient llmClient,
        IProjectFileToolService projectFileTools,
        IProjectTerminalToolService projectTerminalTools)
    {
        _llmClient = llmClient;
        _projectFileTools = projectFileTools;
        _projectTerminalTools = projectTerminalTools;
    }

    public async Task<SubagentRunResult> RunAsync(
        AppSettings settings,
        LuckyProject? project,
        ProviderSettings parentProvider,
        string? apiKey,
        SubagentDefinition definition,
        AgentInstructionSet instructions,
        string task,
        string? context,
        CancellationToken cancellationToken)
    {
        var trace = new List<ToolTraceEntry>();
        var provider = CopyProvider(parentProvider, definition);
        var messages = BuildMessages(settings, project, definition, instructions, task, context);
        var tools = BuildTools(settings.AccessLevel, definition, project);
        LlmTokenUsage? aggregateUsage = null;
        var successfulToolSignatures = new HashSet<string>(StringComparer.Ordinal);
        var maxRounds = Math.Clamp(settings.Subagents.MaxToolRounds, 1, 8);

        for (var round = 0; round < maxRounds; round++)
        {
            var response = await _llmClient.CompleteChatAsync(
                provider,
                apiKey,
                messages,
                tools,
                cancellationToken,
                streamProgress: null).ConfigureAwait(false);
            aggregateUsage = AddUsage(aggregateUsage, response.Usage);

            var toolCalls = NormalizeToolCalls(response.ToolCalls, round);
            if (toolCalls.Count == 0)
            {
                return new SubagentRunResult(
                    definition.Name,
                    task,
                    string.IsNullOrWhiteSpace(response.Content) ? "Subagent returned an empty summary." : response.Content,
                    trace,
                    TokenUsage: aggregateUsage);
            }

            var repeatedToolCall = toolCalls.Take(MaxToolCallsPerResponse).FirstOrDefault(toolCall =>
                successfulToolSignatures.Contains(ToolSignature(toolCall)));
            if (repeatedToolCall is not null)
            {
                trace.Add(new ToolTraceEntry(
                    "agent.loop",
                    repeatedToolCall.Name,
                    "The subagent repeated a successful tool call; asking it to summarize without more tools."));
                messages.Add(new LlmChatMessage("system", "A repeated successful tool call was detected. Do not call tools again. Return a concise final summary from the completed tool results."));
                var final = await _llmClient.CompleteChatAsync(
                    provider,
                    apiKey,
                    messages,
                    tools: null,
                    cancellationToken).ConfigureAwait(false);
                aggregateUsage = AddUsage(aggregateUsage, final.Usage);
                return new SubagentRunResult(
                    definition.Name,
                    task,
                    string.IsNullOrWhiteSpace(final.Content) ? "Subagent completed tool work but returned no summary." : final.Content,
                    trace,
                    TokenUsage: aggregateUsage);
            }

            messages.Add(new LlmChatMessage("assistant", response.Content, ToolCalls: toolCalls, ReasoningContent: response.ReasoningContent));
            for (var toolCallIndex = 0; toolCallIndex < toolCalls.Count; toolCallIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toolCall = toolCalls[toolCallIndex];
                var execution = toolCallIndex < MaxToolCallsPerResponse
                    ? await ExecuteToolCallAsync(settings.AccessLevel, project, definition, toolCall, cancellationToken)
                        .ConfigureAwait(false)
                    : ToolCallLimitError(toolCall);
                var completed = DateTimeOffset.UtcNow;
                trace.Add(new ToolTraceEntry(
                    execution.Tool,
                    execution.Input,
                    execution.Output,
                    execution.IsError,
                    StartedAt: completed,
                    CompletedAt: completed));
                messages.Add(new LlmChatMessage("tool", execution.Output, ToolCallId: toolCall.Id));
                if (!execution.IsError)
                {
                    successfulToolSignatures.Add(ToolSignature(toolCall));
                }
            }
        }

        messages.Add(new LlmChatMessage("system", $"The subagent reached its {maxRounds}-round tool limit. Do not call tools. Return a concise final summary from completed tool results."));
        var finalResponse = await _llmClient.CompleteChatAsync(
            provider,
            apiKey,
            messages,
            tools: null,
            cancellationToken).ConfigureAwait(false);
        aggregateUsage = AddUsage(aggregateUsage, finalResponse.Usage);
        return new SubagentRunResult(
            definition.Name,
            task,
            string.IsNullOrWhiteSpace(finalResponse.Content)
                ? $"Subagent reached its {maxRounds}-round tool limit and did not return a final summary."
                : finalResponse.Content,
            trace,
            TokenUsage: aggregateUsage);
    }

    private static IReadOnlyList<ToolCallRequest> NormalizeToolCalls(
        IReadOnlyList<ToolCallRequest>? toolCalls,
        int round)
    {
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return [];
        }

        return toolCalls
            .Select((toolCall, index) => string.IsNullOrWhiteSpace(toolCall.Id)
                ? toolCall with { Id = $"lucky_subagent_tool_{round + 1}_{index + 1}" }
                : toolCall)
            .ToArray();
    }

    private static ToolExecutionResult ToolCallLimitError(ToolCallRequest toolCall) => new(
        TraceToolName(toolCall.Name),
        toolCall.ArgumentsJson,
        $"Lucky accepts at most {MaxToolCallsPerResponse} tool calls in one subagent response. This call was not run; review the completed tool results and return a final summary.",
        IsError: true);

    private static string TraceToolName(string toolName) => toolName switch
    {
        "project_list_files" => "project.list_files",
        "project_read_file" => "project.read_file",
        "project_search" => "project.search",
        "project_write_file" => "project.write_file",
        "project_edit_file" => "project.edit_file",
        "project_apply_patch" => "project.apply_patch",
        "project_run_command" => "project.run_command",
        _ => toolName
    };

    private static ProviderSettings CopyProvider(ProviderSettings provider, SubagentDefinition definition)
    {
        return new ProviderSettings
        {
            DisplayName = provider.DisplayName,
            BaseUrl = provider.BaseUrl,
            Model = string.IsNullOrWhiteSpace(definition.ModelOverride) ? provider.Model : definition.ModelOverride,
            RequiresApiKey = provider.RequiresApiKey,
            EncryptedApiKey = provider.EncryptedApiKey,
            SupportsThinking = provider.SupportsThinking,
            ThinkingEnabled = provider.ThinkingEnabled,
            ReasoningEffort = string.IsNullOrWhiteSpace(definition.ReasoningEffortOverride)
                ? provider.ReasoningEffort
                : definition.ReasoningEffortOverride,
            ContextWindowTokens = provider.ContextWindowTokens
        };
    }

    private static List<LlmChatMessage> BuildMessages(
        AppSettings settings,
        LuckyProject? project,
        SubagentDefinition definition,
        AgentInstructionSet instructions,
        string task,
        string? context)
    {
        var system = new StringBuilder();
        system.AppendLine($"You are Lucky subagent '{definition.Name}'.");
        system.AppendLine(definition.Instructions.Trim());
        system.AppendLine();
        system.AppendLine($"Harness access level: {settings.AccessLevel}. Your effective access cannot exceed the parent Lucky turn.");
        system.AppendLine("Do only the assigned task. Keep intermediate exploration out of the final answer. Return a compact summary with concrete file references when available.");
        system.AppendLine("Do not claim you changed files unless a write/edit tool result confirms it.");
        if (project is not null)
        {
            system.AppendLine($"Selected project: {project.Name}");
            system.AppendLine($"Project path: {project.Path}");
        }

        var renderedInstructions = AgentInstructionsService.RenderForPrompt(instructions);
        if (!string.IsNullOrWhiteSpace(renderedInstructions))
        {
            system.AppendLine();
            system.AppendLine(renderedInstructions);
        }

        var user = new StringBuilder();
        user.AppendLine("Subagent task:");
        user.AppendLine(task.Trim());
        if (!string.IsNullOrWhiteSpace(context))
        {
            user.AppendLine();
            user.AppendLine("Parent-supplied context:");
            user.AppendLine(context.Trim());
        }

        return [new LlmChatMessage("system", system.ToString().Trim()), new LlmChatMessage("user", user.ToString().Trim())];
    }

    private static IReadOnlyList<LlmToolDefinition> BuildTools(
        HarnessAccessLevel parentAccessLevel,
        SubagentDefinition definition,
        LuckyProject? project)
    {
        if (project is null || parentAccessLevel == HarnessAccessLevel.ChatOnly)
        {
            return [];
        }

        var allowed = definition.Tools.Where(tool => IsToolAllowed(parentAccessLevel, definition.AccessLevel, tool)).ToHashSet(StringComparer.Ordinal);
        var tools = new List<LlmToolDefinition>();
        if (allowed.Contains("project_list_files"))
        {
            tools.Add(new LlmToolDefinition(
                "project_list_files",
                "List files and folders inside the selected project.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "Project-relative folder or file path. Use '.' for the project root.")
                },
                []));
        }

        if (allowed.Contains("project_read_file"))
        {
            tools.Add(new LlmToolDefinition(
                "project_read_file",
                "Read a UTF-8 text file inside the selected project.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "Project-relative file path to read.", Required: true)
                },
                ["path"]));
        }

        if (allowed.Contains("project_search"))
        {
            tools.Add(new LlmToolDefinition(
                "project_search",
                "Search text files inside the selected project using a regex or plain text query.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["query"] = new("string", "Regex or plain text query to search for.", Required: true),
                    ["path"] = new("string", "Project-relative folder or file path to search. Defaults to '.'."),
                    ["glob"] = new("string", "Optional simple file pattern such as '*.cs' or '*.md'.")
                },
                ["query"]));
        }

        if (allowed.Contains("project_write_file"))
        {
            tools.Add(new LlmToolDefinition(
                "project_write_file",
                "Create or overwrite a UTF-8 text file inside the selected project.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "Project-relative file path to write.", Required: true),
                    ["content"] = new("string", "Complete file content.", Required: true),
                    ["overwrite"] = new("boolean", "Whether to overwrite an existing file.", Required: true)
                },
                ["path", "content", "overwrite"]));
        }

        if (allowed.Contains("project_edit_file"))
        {
            tools.Add(new LlmToolDefinition(
                "project_edit_file",
                "Edit one exact text occurrence inside a UTF-8 text file in the selected project.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "Project-relative file path to edit.", Required: true),
                    ["oldText"] = new("string", "Exact existing text to replace. Must match exactly once.", Required: true),
                    ["newText"] = new("string", "Replacement text.", Required: true)
                },
                ["path", "oldText", "newText"]));
        }

        if (allowed.Contains("project_apply_patch"))
        {
            tools.Add(new LlmToolDefinition(
                "project_apply_patch",
                "Apply a standard unified diff to text files inside the selected project. Validate all hunks first; use this for multi-line or multi-file edits.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["patch"] = new("string", "Complete unified diff beginning with --- and +++ file headers, followed by @@ hunks.", Required: true)
                },
                ["patch"]));
        }

        if (allowed.Contains("project_run_command"))
        {
            tools.Add(new LlmToolDefinition(
                "project_run_command",
                "Run a PowerShell command from the verified selected-project root. Use it for focused build, test, and diagnostic work only.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["command"] = new("string", "PowerShell command to run from the selected project root.", Required: true),
                    ["timeoutSeconds"] = new("integer", "Optional timeout from 1 to 300 seconds; defaults to 60.")
                },
                ["command"]));
        }

        return tools;
    }

    private async Task<ToolExecutionResult> ExecuteToolCallAsync(
        HarnessAccessLevel parentAccessLevel,
        LuckyProject? project,
        SubagentDefinition definition,
        ToolCallRequest toolCall,
        CancellationToken cancellationToken)
    {
        if (!IsToolAllowed(parentAccessLevel, definition.AccessLevel, toolCall.Name) ||
            !definition.Tools.Contains(toolCall.Name, StringComparer.Ordinal))
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, $"Denied for subagent '{definition.Name}'.", IsError: true);
        }

        if (project is null)
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, "No project folder is selected.", IsError: true);
        }

        var args = ParseToolArguments(toolCall.ArgumentsJson);
        try
        {
            return toolCall.Name switch
            {
                "project_list_files" => await _projectFileTools.ListAsync(
                    project,
                    StringArg(args, "path", "."),
                    cancellationToken).ConfigureAwait(false),
                "project_read_file" => await _projectFileTools.ReadAsync(
                    project,
                    RequiredStringArg(args, "path"),
                    cancellationToken).ConfigureAwait(false),
                "project_search" => await _projectFileTools.SearchAsync(
                    project,
                    RequiredStringArg(args, "query"),
                    StringArg(args, "path", "."),
                    StringArg(args, "glob", null),
                    cancellationToken).ConfigureAwait(false),
                "project_write_file" => await _projectFileTools.WriteAsync(
                    project,
                    RequiredStringArg(args, "path"),
                    RequiredStringArg(args, "content"),
                    BoolArg(args, "overwrite"),
                    cancellationToken).ConfigureAwait(false),
                "project_edit_file" => await _projectFileTools.EditAsync(
                    project,
                    RequiredStringArg(args, "path"),
                    RequiredStringArg(args, "oldText"),
                    RequiredStringArg(args, "newText"),
                    cancellationToken).ConfigureAwait(false),
                "project_apply_patch" => await _projectFileTools.ApplyPatchAsync(
                    project,
                    RequiredStringArg(args, "patch"),
                    cancellationToken).ConfigureAwait(false),
                "project_run_command" => await _projectTerminalTools.RunCommandAsync(
                    project,
                    RequiredStringArg(args, "command"),
                    NullableIntArg(args, "timeoutSeconds"),
                    cancellationToken).ConfigureAwait(false),
                _ => new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, "Unknown tool.", IsError: true)
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, ex.Message, IsError: true);
        }
    }

    private static bool IsToolAllowed(HarnessAccessLevel parentAccessLevel, HarnessAccessLevel definitionAccessLevel, string toolName)
    {
        var effectiveAccess = MinAccess(parentAccessLevel, definitionAccessLevel);
        if (toolName is "project_list_files" or "project_read_file" or "project_search")
        {
            return effectiveAccess is HarnessAccessLevel.Workspace or HarnessAccessLevel.FullAccess;
        }

        if (toolName is "project_write_file" or "project_edit_file" or "project_apply_patch" or "project_run_command")
        {
            return effectiveAccess == HarnessAccessLevel.FullAccess;
        }

        return false;
    }

    private static HarnessAccessLevel MinAccess(HarnessAccessLevel left, HarnessAccessLevel right)
    {
        return (HarnessAccessLevel)Math.Min((int)left, (int)right);
    }

    private static Dictionary<string, JsonElement> ParseToolArguments(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string RequiredStringArg(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        var value = StringArg(args, name, null);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value;
    }

    private static string? StringArg(IReadOnlyDictionary<string, JsonElement> args, string name, string? fallback)
    {
        return args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : fallback;
    }

    private static bool BoolArg(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        return args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.True ||
               args.TryGetValue(name, out value) && value.ValueKind == JsonValueKind.String &&
               bool.TryParse(value.GetString(), out var parsed) && parsed;
    }

    private static int? NullableIntArg(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"{name} must be an integer.");
    }

    private static string ToolSignature(ToolCallRequest toolCall)
    {
        return $"{toolCall.Name}:{NormalizeJson(toolCall.ArgumentsJson)}";
    }

    private static string NormalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static LlmTokenUsage? AddUsage(LlmTokenUsage? left, LlmTokenUsage? right)
    {
        if (right is null)
        {
            return left;
        }

        if (left is null)
        {
            return right;
        }

        return new LlmTokenUsage(
            AddNullable(left.PromptTokens, right.PromptTokens),
            AddNullable(left.CompletionTokens, right.CompletionTokens),
            AddNullable(left.TotalTokens, right.TotalTokens));

        static int? AddNullable(int? a, int? b) => a.HasValue || b.HasValue
            ? (a ?? 0) + (b ?? 0)
            : null;
    }
}
