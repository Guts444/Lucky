using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucky.Core;

public sealed class AgentRunner
{
    private readonly ILlmClient _llmClient;
    private readonly IWebSearchClient _webSearchClient;
    private readonly MemoryService _memoryService;
    private readonly IProjectFileToolService _projectFileTools;
    private readonly AgentInstructionsService _instructionsService;
    private readonly SubagentDefinitionService _subagentDefinitions;
    private readonly SubagentCoordinator _subagentCoordinator;

    public AgentRunner(
        ILlmClient? llmClient = null,
        IWebSearchClient? webSearchClient = null,
        MemoryService? memoryService = null,
        IProjectFileToolService? projectFileTools = null,
        AgentInstructionsService? instructionsService = null,
        SubagentDefinitionService? subagentDefinitions = null,
        SubagentCoordinator? subagentCoordinator = null)
    {
        _llmClient = llmClient ?? new OpenAiCompatibleClient();
        _webSearchClient = webSearchClient ?? new SearxngSearchClient();
        _memoryService = memoryService ?? new MemoryService();
        _projectFileTools = projectFileTools ?? new ProjectFileToolService();
        _instructionsService = instructionsService ?? new AgentInstructionsService();
        _subagentDefinitions = subagentDefinitions ?? new SubagentDefinitionService();
        _subagentCoordinator = subagentCoordinator ?? new SubagentCoordinator(_llmClient, _projectFileTools);
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        LuckyState state,
        LuckyProject? project,
        ChatSession session,
        string userMessage,
        CancellationToken cancellationToken = default,
        IProgress<AgentProgressEvent>? progress = null)
    {
        progress?.Report(new AgentProgressEvent("memory", "Checking memory"));
        var captured = _memoryService.CaptureFromUserMessage(userMessage, project?.Id, session.Id);
        _memoryService.MergeCapturedMemories(state.Memories, captured);

        var recalled = _memoryService.RetrieveRelevant(
            state.Memories,
            userMessage,
            project?.Id,
            state.Settings.MemorySearchLimit);

        var trace = new List<ToolTraceEntry>();
        if (state.Settings.AccessLevel != HarnessAccessLevel.FullAccess && LooksLikeProjectMutationRequest(userMessage))
        {
            return new AgentTurnResult(
                "This looks like a project-changing request. Switch Lucky to Full access for the selected project and resend it, then I can create or edit files visibly.",
                trace,
                recalled,
                captured,
                UsedModel: false);
        }

        progress?.Report(new AgentProgressEvent("instructions", "Reading project instructions"));
        var instructions = await _instructionsService.LoadAsync(project, cancellationToken).ConfigureAwait(false);
        if (instructions.HasInstructions)
        {
            trace.Add(new ToolTraceEntry("instructions", instructions.SourceSummary, "Loaded project instructions."));
        }

        var availableSubagents = await _subagentDefinitions.LoadAsync(state, project, cancellationToken).ConfigureAwait(false);
        var subagentsAvailable = state.Settings.Subagents.Enabled &&
                                 availableSubagents.Count > 0 &&
                                 (state.Settings.Subagents.AutoDelegateEnabled || LooksLikeExplicitSubagentRequest(userMessage));

        var searchResults = await MaybeSearchAsync(state.Settings, userMessage, trace, cancellationToken, progress).ConfigureAwait(false);
        var provider = state.Settings.ActiveProviderSettings;
        var apiKey = CredentialProtector.Unprotect(provider.EncryptedApiKey);

        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            return new AgentTurnResult(
                $"Lucky is ready, but {provider.DisplayName} needs an API key in Settings before I can call the model. " +
                "LM Studio can run without a key if its local server is listening.",
                trace,
                recalled,
                captured,
                UsedModel: false);
        }

        if (searchResults.Count > 0 && IsExplicitWebOnly(userMessage) && string.IsNullOrWhiteSpace(provider.Model))
        {
            return new AgentTurnResult(RenderSearchOnly(searchResults), trace, recalled, captured, UsedModel: false);
        }

        try
        {
            IReadOnlyList<SubagentDefinition> activeSubagents = subagentsAvailable
                ? availableSubagents
                : Array.Empty<SubagentDefinition>();
            var messages = BuildMessages(
                state,
                project,
                session,
                userMessage,
                recalled,
                searchResults,
                instructions,
                activeSubagents);
            var tools = BuildToolDefinitions(state.Settings, project, activeSubagents);
            var response = await RunToolLoopAsync(
                state.Settings,
                project,
                provider,
                apiKey,
                messages,
                tools,
                activeSubagents,
                instructions,
                trace,
                progress,
                cancellationToken).ConfigureAwait(false);
            var content = string.IsNullOrWhiteSpace(response.Content)
                ? "The model returned an empty response."
                : response.Content;

            var now = DateTimeOffset.UtcNow;
            foreach (var memory in recalled)
            {
                memory.LastUsedAt = now;
            }

            trace.Add(new ToolTraceEntry("llm", provider.DisplayName, $"Answered with {response.Model}."));
            return new AgentTurnResult(
                content,
                trace,
                recalled,
                captured,
                UsedModel: true,
                response.ReasoningContent,
                response.Usage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var fallback = new StringBuilder();
            fallback.AppendLine($"I could not reach {provider.DisplayName}: {ex.Message}");
            if (searchResults.Count > 0)
            {
                fallback.AppendLine();
                fallback.AppendLine(RenderSearchOnly(searchResults));
            }

            return new AgentTurnResult(fallback.ToString().Trim(), trace, recalled, captured, UsedModel: false);
        }
    }

    private async Task<IReadOnlyList<SearchResult>> MaybeSearchAsync(
        AppSettings settings,
        string userMessage,
        ICollection<ToolTraceEntry> trace,
        CancellationToken cancellationToken,
        IProgress<AgentProgressEvent>? progress)
    {
        var query = ExtractExplicitWebQuery(userMessage);
        if (query is null && settings.AutoWebSearch && LooksCurrent(userMessage))
        {
            query = userMessage;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (query == userMessage && LooksLikeOfflineBuildRequest(userMessage))
        {
            return [];
        }

        try
        {
            progress?.Report(new AgentProgressEvent("tool", "Searching web", query));
            var results = await _webSearchClient.SearchAsync(
                settings.SearxngUrl,
                query,
                settings.WebSearchMaxResults,
                cancellationToken).ConfigureAwait(false);

            trace.Add(new ToolTraceEntry("web.search", query, $"{results.Count} result(s) from SearXNG."));
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            trace.Add(new ToolTraceEntry("web.search", query, ex.Message, IsError: true));
            return [];
        }
    }

    private async Task<LlmResponse> RunToolLoopAsync(
        AppSettings settings,
        LuckyProject? project,
        ProviderSettings provider,
        string? apiKey,
        List<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        IReadOnlyList<SubagentDefinition> subagentDefinitions,
        AgentInstructionSet instructions,
        ICollection<ToolTraceEntry> trace,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        const int maxToolRounds = 8;
        LlmTokenUsage? aggregateUsage = null;
        var reasoningBlocks = new List<string>();
        var successfulToolSignatures = new HashSet<string>(StringComparer.Ordinal);
        var subagentBudget = new SubagentTurnBudget(Math.Clamp(settings.Subagents.MaxAgentsPerTurn, 0, 12));
        for (var round = 0; round < maxToolRounds; round++)
        {
            progress?.Report(new AgentProgressEvent("thinking", round == 0 ? "Thinking" : "Thinking with tool results"));
            var streamProgress = progress is null
                ? null
                : new DelegateProgress<LlmStreamDelta>(delta =>
                {
                    if (!string.IsNullOrEmpty(delta.ContentDelta))
                    {
                        progress.Report(new AgentProgressEvent("answer", "Writing answer", delta.ContentDelta));
                    }

                    if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                    {
                        progress.Report(new AgentProgressEvent("reasoning", "Thinking", delta.ReasoningDelta));
                    }
                });
            var response = await _llmClient.CompleteChatAsync(
                provider,
                apiKey,
                messages,
                tools,
                cancellationToken,
                streamProgress).ConfigureAwait(false);
            aggregateUsage = AddUsage(aggregateUsage, response.Usage);
            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                reasoningBlocks.Add(response.ReasoningContent.Trim());
            }

            var toolCalls = response.ToolCalls ?? [];
            if (toolCalls.Count == 0)
            {
                return response with
                {
                    ReasoningContent = JoinReasoningBlocks(reasoningBlocks),
                    Usage = aggregateUsage
                };
            }

            var repeatedToolCall = toolCalls.FirstOrDefault(toolCall => successfulToolSignatures.Contains(ToolSignature(toolCall)));
            if (repeatedToolCall is not null)
            {
                trace.Add(new ToolTraceEntry(
                    "agent.loop",
                    ToolSummaryFromJson(repeatedToolCall.Name, repeatedToolCall.ArgumentsJson),
                    "The model requested the same tool call after it had already completed successfully; asking the model to finalize without more tools."));
                return await FinalizeToolLoopAsync(
                    provider,
                    apiKey,
                    messages,
                    trace,
                    aggregateUsage,
                    reasoningBlocks,
                    "A repeated successful tool request was detected.",
                    maxToolRounds,
                    cancellationToken).ConfigureAwait(false);
            }

            messages.Add(new LlmChatMessage("assistant", response.Content, ToolCalls: toolCalls, ReasoningContent: response.ReasoningContent));
            var executions = await ExecuteToolCallsAsync(
                settings,
                project,
                provider,
                apiKey,
                toolCalls,
                subagentDefinitions,
                instructions,
                subagentBudget,
                progress,
                cancellationToken).ConfigureAwait(false);
            foreach (var bundle in executions)
            {
                aggregateUsage = AddUsage(aggregateUsage, bundle.Usage);
                foreach (var childTrace in bundle.AdditionalTrace)
                {
                    trace.Add(childTrace);
                }

                var execution = bundle.Execution;
                var completed = DateTimeOffset.UtcNow;
                trace.Add(new ToolTraceEntry(
                    execution.Tool,
                    execution.Input,
                    execution.Output,
                    execution.IsError,
                    StartedAt: completed,
                    CompletedAt: completed));
                messages.Add(new LlmChatMessage("tool", execution.Output, ToolCallId: bundle.ToolCall.Id));
                if (!execution.IsError)
                {
                    successfulToolSignatures.Add(ToolSignature(bundle.ToolCall));
                }
            }
        }

        trace.Add(new ToolTraceEntry("agent.loop", "tool rounds", $"Reached {maxToolRounds} tool rounds; asking the model to finalize without more tools."));
        return await FinalizeToolLoopAsync(
            provider,
            apiKey,
            messages,
            trace,
            aggregateUsage,
            reasoningBlocks,
            "The tool round limit has been reached.",
            maxToolRounds,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LlmResponse> FinalizeToolLoopAsync(
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<LlmChatMessage> messages,
        ICollection<ToolTraceEntry> trace,
        LlmTokenUsage? aggregateUsage,
        List<string> reasoningBlocks,
        string reason,
        int maxToolRounds,
        CancellationToken cancellationToken)
    {
        var finalizationPrompt = BuildToolLimitFinalizationPrompt(trace, reason);
        try
        {
            var finalMessages = messages
                .Append(new LlmChatMessage("system", finalizationPrompt))
                .ToArray();
            var finalResponse = await _llmClient.CompleteChatAsync(
                provider,
                apiKey,
                finalMessages,
                tools: null,
                cancellationToken,
                streamProgress: null).ConfigureAwait(false);
            aggregateUsage = AddUsage(aggregateUsage, finalResponse.Usage);
            if (!string.IsNullOrWhiteSpace(finalResponse.ReasoningContent))
            {
                reasoningBlocks.Add(finalResponse.ReasoningContent.Trim());
            }

            return finalResponse with
            {
                Content = string.IsNullOrWhiteSpace(finalResponse.Content)
                    ? BuildToolLimitFallback(trace, maxToolRounds)
                    : finalResponse.Content,
                ReasoningContent = JoinReasoningBlocks(reasoningBlocks),
                Usage = aggregateUsage
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            trace.Add(new ToolTraceEntry("agent.loop", "finalize", ex.Message, IsError: true));
            return new LlmResponse(
                BuildToolLimitFallback(trace, maxToolRounds),
                provider.Model,
                ReasoningContent: JoinReasoningBlocks(reasoningBlocks),
                Usage: aggregateUsage);
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

    private static string? JoinReasoningBlocks(IReadOnlyList<string> blocks)
    {
        return blocks.Count == 0
            ? null
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", blocks.Distinct());
    }

    private static string BuildToolLimitFinalizationPrompt(IEnumerable<ToolTraceEntry> trace, string reason)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{reason} Do not request more tools.");
        builder.AppendLine("Give the user a concise final answer based only on the completed tool results.");
        builder.AppendLine("If files were created or edited, report the relative path(s) and status. Do not paste large file contents unless the user explicitly asked to receive code in chat instead of a created file.");

        var completedFileWork = CompletedFileWork(trace).ToArray();
        if (completedFileWork.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Completed file operations:");
            foreach (var item in completedFileWork)
            {
                builder.AppendLine($"- {item}");
            }
        }

        return builder.ToString().Trim();
    }

    private static string BuildToolLimitFallback(IEnumerable<ToolTraceEntry> trace, int maxToolRounds)
    {
        var completedFileWork = CompletedFileWork(trace).ToArray();
        if (completedFileWork.Length == 0)
        {
            return $"I stopped after {maxToolRounds} tool rounds so the run would not continue indefinitely. The trace shows what I tried.";
        }

        var builder = new StringBuilder("I reached the tool-round safety limit while the model was still self-checking, but the completed file operations succeeded:");
        foreach (var item in completedFileWork)
        {
            builder.AppendLine();
            builder.AppendLine($"- {item}");
        }

        return builder.ToString();
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

    private static string ToolSummaryFromJson(string toolName, string argumentsJson)
    {
        return ToolSummary(toolName, ParseToolArguments(argumentsJson));
    }

    private static IEnumerable<string> CompletedFileWork(IEnumerable<ToolTraceEntry> trace)
    {
        foreach (var entry in trace.Where(entry => !entry.IsError))
        {
            if (entry.Tool is "project.write_file" or "project.edit_file")
            {
                yield return entry.Output;
            }
        }
    }

    private async Task<IReadOnlyList<ToolExecutionBundle>> ExecuteToolCallsAsync(
        AppSettings settings,
        LuckyProject? project,
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<ToolCallRequest> toolCalls,
        IReadOnlyList<SubagentDefinition> subagentDefinitions,
        AgentInstructionSet instructions,
        SubagentTurnBudget subagentBudget,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var results = new ToolExecutionBundle[toolCalls.Count];
        var subagentTasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(Math.Clamp(settings.Subagents.MaxParallelAgents, 1, 12));

        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            if (toolCall.Name == "subagent_run")
            {
                if (!subagentBudget.TryConsume())
                {
                    results[index] = new ToolExecutionBundle(
                        toolCall,
                        new ToolExecutionResult("subagent", toolCall.ArgumentsJson, $"Subagent limit reached for this turn ({subagentBudget.Limit}).", IsError: true),
                        [],
                        null);
                    continue;
                }

                var capturedIndex = index;
                subagentTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        results[capturedIndex] = await ExecuteSubagentToolCallAsync(
                            settings,
                            project,
                            provider,
                            apiKey,
                            toolCall,
                            subagentDefinitions,
                            instructions,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
                continue;
            }

            var execution = await ExecuteToolCallAsync(
                settings,
                project,
                toolCall,
                progress,
                cancellationToken).ConfigureAwait(false);
            results[index] = new ToolExecutionBundle(toolCall, execution, [], null);
        }

        await Task.WhenAll(subagentTasks).ConfigureAwait(false);
        return results;
    }

    private async Task<ToolExecutionBundle> ExecuteSubagentToolCallAsync(
        AppSettings settings,
        LuckyProject? project,
        ProviderSettings provider,
        string? apiKey,
        ToolCallRequest toolCall,
        IReadOnlyList<SubagentDefinition> subagentDefinitions,
        AgentInstructionSet instructions,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = ParseToolArguments(toolCall.ArgumentsJson);
            var agentName = RequiredStringArg(args, "agent");
            var task = RequiredStringArg(args, "task");
            var context = StringArg(args, "context", null);

            var result = await _subagentCoordinator.RunAsync(
                settings,
                project,
                provider,
                apiKey,
                subagentDefinitions,
                instructions,
                agentName,
                task,
                context,
                cancellationToken,
                progress).ConfigureAwait(false);
            var prefixedTrace = result.Trace
                .Select(entry => entry with { Tool = $"subagent.{result.AgentName}.{entry.Tool}" })
                .ToArray();

            return new ToolExecutionBundle(
                toolCall,
                new ToolExecutionResult(
                    $"subagent.{result.AgentName}",
                    result.Task,
                    RenderSubagentToolOutput(result),
                    result.IsError),
                prefixedTrace,
                result.TokenUsage);
        }
        catch (InvalidOperationException ex)
        {
            return new ToolExecutionBundle(
                toolCall,
                new ToolExecutionResult("subagent", toolCall.ArgumentsJson, ex.Message, IsError: true),
                [],
                null);
        }
    }

    private static string RenderSubagentToolOutput(SubagentRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Subagent: {result.AgentName}");
        builder.AppendLine($"Task: {result.Task}");
        builder.AppendLine(result.IsError ? "Status: failed" : "Status: completed");
        builder.AppendLine("Summary:");
        builder.AppendLine(result.Summary.Trim());
        return builder.ToString().Trim();
    }

    private async Task<ToolExecutionResult> ExecuteToolCallAsync(
        AppSettings settings,
        LuckyProject? project,
        ToolCallRequest toolCall,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsToolAllowed(settings.AccessLevel, toolCall.Name))
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, $"Denied by access level {settings.AccessLevel}.", IsError: true);
        }

        if (project is null)
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, "No project folder is selected.", IsError: true);
        }

        var args = ParseToolArguments(toolCall.ArgumentsJson);
        progress?.Report(new AgentProgressEvent("tool", ToolSummary(toolCall.Name, args), toolCall.ArgumentsJson));

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
                _ => new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, "Unknown tool.", IsError: true)
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, ex.Message, IsError: true);
        }
    }

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(
        AppSettings settings,
        LuckyProject? project,
        IReadOnlyList<SubagentDefinition> subagentDefinitions)
    {
        var accessLevel = settings.AccessLevel;
        var tools = new List<LlmToolDefinition>();
        if (settings.Subagents.Enabled && subagentDefinitions.Count > 0)
        {
            tools.Add(new LlmToolDefinition(
                "subagent_run",
                "Run a bounded Lucky subagent in an isolated context and return its compact summary. Use for parallelizable exploration, review, testing, or documentation work.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["agent"] = new("string", "Subagent name from the available subagent catalog.", Required: true),
                    ["task"] = new("string", "Concise, self-contained task for the subagent.", Required: true),
                    ["context"] = new("string", "Optional brief parent context or constraints.")
                },
                ["agent", "task"]));
        }

        if (accessLevel == HarnessAccessLevel.ChatOnly || project is null)
        {
            return tools;
        }

        tools.Add(new LlmToolDefinition(
                "project_list_files",
                "List files and folders inside the selected project. Use this before reading or editing unfamiliar paths.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "Project-relative folder or file path. Use '.' for the project root.")
                },
                []));
        tools.Add(new LlmToolDefinition(
                "project_read_file",
                "Read a UTF-8 text file inside the selected project.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["path"] = new("string", "Project-relative file path to read.", Required: true)
                },
                ["path"]));
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

        if (accessLevel == HarnessAccessLevel.FullAccess)
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

        return tools;
    }

    private static List<LlmChatMessage> BuildMessages(
        LuckyState state,
        LuckyProject? project,
        ChatSession session,
        string userMessage,
        IReadOnlyList<MemoryItem> memories,
        IReadOnlyList<SearchResult> searchResults,
        AgentInstructionSet instructions,
        IReadOnlyList<SubagentDefinition> subagentDefinitions)
    {
        var messages = new List<LlmChatMessage>
        {
            new("system", BuildSystemPrompt(state.Settings, project, memories, searchResults, instructions, subagentDefinitions))
        };

        foreach (var message in session.Messages
                     .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
                     .TakeLast(Math.Max(4, state.Settings.ContextMessageLimit)))
        {
            var role = message.Role == ChatRole.User ? "user" : "assistant";
            messages.Add(new LlmChatMessage(role, message.Content));
        }

        if (messages.LastOrDefault()?.Role != "user" || messages.Last().Content != userMessage)
        {
            messages.Add(new LlmChatMessage("user", userMessage));
        }

        return messages;
    }

    private static string BuildSystemPrompt(
        AppSettings settings,
        LuckyProject? project,
        IReadOnlyList<MemoryItem> memories,
        IReadOnlyList<SearchResult> searchResults,
        AgentInstructionSet instructions,
        IReadOnlyList<SubagentDefinition> subagentDefinitions)
    {
        var builder = new StringBuilder();
        builder.AppendLine(settings.Persona.Trim());
        builder.AppendLine();
        builder.AppendLine($"Harness access level: {settings.AccessLevel}.");
        builder.AppendLine("Respect this access level. Use available tools for project filesystem work instead of guessing. Do not imply you performed filesystem, shell, or external actions unless Lucky explicitly supplies tool results.");
        builder.AppendLine("When tools are available, inspect files before describing them and prefer small exact edits over whole-file rewrites.");
        builder.AppendLine("For standalone file creation or artifact-generation requests, create the requested file directly in the selected project instead of inspecting unrelated examples or benchmark files. After a successful write/edit and any targeted verification, stop using tools and provide a concise final status with the relative path.");
        builder.AppendLine("Present answers in clean chat prose for a plain-text UI: avoid decorative Markdown separators, heading hashes, excessive bold markers, and filler preambles. Use short labels, bullets, or tables only when they make the answer easier to scan.");

        if (project is not null)
        {
            builder.AppendLine($"Selected project: {project.Name}");
            builder.AppendLine($"Project path: {project.Path}");
        }

        var renderedInstructions = AgentInstructionsService.RenderForPrompt(instructions);
        if (!string.IsNullOrWhiteSpace(renderedInstructions))
        {
            builder.AppendLine();
            builder.AppendLine(renderedInstructions);
        }

        var subagentCatalog = SubagentDefinitionService.RenderCatalogForPrompt(
            subagentDefinitions,
            settings.Subagents.AutoDelegateEnabled);
        if (!string.IsNullOrWhiteSpace(subagentCatalog))
        {
            builder.AppendLine();
            builder.AppendLine(subagentCatalog);
            builder.AppendLine($"Subagent limits: at most {settings.Subagents.MaxAgentsPerTurn} per turn, {settings.Subagents.MaxParallelAgents} in parallel. Child agents inherit the current provider and cannot exceed the current access level.");
        }

        if (memories.Count > 0)
        {
            builder.AppendLine();
            var profile = memories.Where(memory => memory.Kind == MemoryKind.UserProfile).ToArray();
            var durable = memories.Where(memory => memory.Kind == MemoryKind.Memory).ToArray();
            if (profile.Length > 0)
            {
                builder.AppendLine("USER profile notes:");
                foreach (var memory in profile)
                {
                    builder.AppendLine($"- {memory.Summary}");
                }
            }

            if (durable.Length > 0)
            {
                builder.AppendLine("Relevant durable memories:");
                foreach (var memory in durable)
                {
                    builder.AppendLine($"- {memory.Summary}");
                }
            }
        }

        if (searchResults.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Web search results from the user's SearXNG instance:");
            foreach (var result in searchResults)
            {
                builder.AppendLine($"- {result.Title} | {result.Url} | {result.Snippet}");
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsToolAllowed(HarnessAccessLevel accessLevel, string toolName)
    {
        if (accessLevel == HarnessAccessLevel.ChatOnly)
        {
            return toolName == "subagent_run";
        }

        if (toolName == "subagent_run")
        {
            return true;
        }

        if (toolName is "project_list_files" or "project_read_file" or "project_search")
        {
            return accessLevel is HarnessAccessLevel.Workspace or HarnessAccessLevel.FullAccess;
        }

        if (toolName is "project_write_file" or "project_edit_file")
        {
            return accessLevel == HarnessAccessLevel.FullAccess;
        }

        return false;
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

    private static string ToolSummary(string toolName, IReadOnlyDictionary<string, JsonElement> args)
    {
        var path = StringArg(args, "path", ".");
        return toolName switch
        {
            "project_list_files" => $"Listing {path}",
            "project_read_file" => $"Reading {path}",
            "project_search" => $"Searching {path}",
            "project_write_file" => $"Writing {path}",
            "project_edit_file" => $"Editing {path}",
            "subagent_run" => $"Running subagent {StringArg(args, "agent", "subagent")}",
            _ => $"Running {toolName}"
        };
    }

    private static bool LooksCurrent(string text)
    {
        return Regex.IsMatch(
            text,
            "\\b(latest|today|current|now|news|price|release|schedule|weather|verify|look\\s*up|search\\s+for)\\b",
            RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeOfflineBuildRequest(string text)
    {
        return Regex.IsMatch(
            text,
            "\\b(create|build|implement|write|generate|make)\\b[\\s\\S]{0,180}\\b(file|html|css|javascript|standalone|canvas|game|app|website|component|script)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeProjectMutationRequest(string text)
    {
        return Regex.IsMatch(
            text,
            "\\b(create|build|implement|write|generate|make|edit|modify|fix|save)\\b[\\s\\S]{0,240}\\b(file|html|css|javascript|standalone|canvas|game|app|website|component|script|readme|document|project)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ExtractExplicitWebQuery(string text)
    {
        var match = Regex.Match(text.Trim(), "^/web\\s+(.+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool IsExplicitWebOnly(string text) => text.TrimStart().StartsWith("/web ", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeExplicitSubagentRequest(string text)
    {
        return Regex.IsMatch(
            text,
            "\\b(subagent|subagents|spawn\\s+agents?|delegate|parallel\\s+agents?|use\\s+agents?)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string RenderSearchOnly(IEnumerable<SearchResult> results)
    {
        var builder = new StringBuilder("SearXNG results:");
        foreach (var result in results)
        {
            builder.AppendLine();
            builder.AppendLine($"- {result.Title}");
            builder.AppendLine($"  {result.Url}");
            if (!string.IsNullOrWhiteSpace(result.Snippet))
            {
                builder.AppendLine($"  {result.Snippet}");
            }
        }

        return builder.ToString();
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record ToolExecutionBundle(
        ToolCallRequest ToolCall,
        ToolExecutionResult Execution,
        IReadOnlyList<ToolTraceEntry> AdditionalTrace,
        LlmTokenUsage? Usage);

    private sealed class SubagentTurnBudget(int limit)
    {
        private int _used;

        public int Limit { get; } = limit;

        public bool TryConsume()
        {
            if (Limit <= 0)
            {
                return false;
            }

            return Interlocked.Increment(ref _used) <= Limit;
        }
    }
}
