using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucky.Core;

public sealed class AgentRunner
{
    private const int MaxToolCallsPerResponse = 12;

    private readonly ILlmClient _llmClient;
    private readonly IWebSearchClient _webSearchClient;
    private readonly MemoryService _memoryService;
    private readonly IProjectFileToolService _projectFileTools;
    private readonly IProjectTerminalToolService _projectTerminalTools;
    private readonly IWebPageReader _webPageReader;
    private readonly IMcpToolService _mcpToolService;
    private readonly ICodeExecutionSandboxService _codeExecutionSandbox;
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
        SubagentCoordinator? subagentCoordinator = null,
        IProjectTerminalToolService? projectTerminalTools = null,
        IWebPageReader? webPageReader = null,
        IMcpToolService? mcpToolService = null,
        ICodeExecutionSandboxService? codeExecutionSandbox = null)
    {
        _llmClient = llmClient ?? new LuckyLlmClient();
        _webSearchClient = webSearchClient ?? new SearxngSearchClient();
        _memoryService = memoryService ?? new MemoryService();
        _projectFileTools = projectFileTools ?? new ProjectFileToolService();
        _projectTerminalTools = projectTerminalTools ?? new ProjectTerminalToolService();
        _webPageReader = webPageReader ?? new WebPageReader();
        _mcpToolService = mcpToolService ?? new McpToolService();
        _codeExecutionSandbox = codeExecutionSandbox ?? new DockerCodeExecutionSandboxService();
        _instructionsService = instructionsService ?? new AgentInstructionsService();
        _subagentDefinitions = subagentDefinitions ?? new SubagentDefinitionService();
        _subagentCoordinator = subagentCoordinator ?? new SubagentCoordinator(
            _llmClient,
            _projectFileTools,
            _projectTerminalTools);
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        LuckyState state,
        LuckyProject? project,
        ChatSession session,
        string userMessage,
        CancellationToken cancellationToken = default,
        IProgress<AgentProgressEvent>? progress = null)
    {
        var workspaceProject = state.Settings.AccessLevel == HarnessAccessLevel.ChatOnly ? null : project;
        var explicitSubagentRequest = LooksLikeExplicitSubagentRequest(userMessage);

        IReadOnlyList<MemoryItem> captured = [];
        IReadOnlyList<MemoryItem> recalled = [];
        if (state.Settings.MemoriesEnabled)
        {
            progress?.Report(new AgentProgressEvent("memory", "Checking memory"));
            captured = _memoryService.CaptureFromUserMessage(userMessage, workspaceProject?.Id, session.Id);
            _memoryService.MergeCapturedMemories(state.Memories, captured);

            recalled = _memoryService.RetrieveRelevant(
                state.Memories,
                userMessage,
                workspaceProject?.Id,
                state.Settings.MemorySearchLimit);
        }

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
        var instructions = await _instructionsService.LoadAsync(workspaceProject, cancellationToken).ConfigureAwait(false);
        if (instructions.HasInstructions)
        {
            trace.Add(new ToolTraceEntry("instructions", instructions.SourceSummary, "Loaded project instructions."));
        }

        var availableSubagents = await _subagentDefinitions.LoadAsync(state, workspaceProject, cancellationToken).ConfigureAwait(false);
        var activeSubagents = SelectActiveSubagents(
            availableSubagents,
            state.Settings.Subagents,
            explicitSubagentRequest);

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

        IMcpToolSession? mcpSession = null;
        try
        {
            if (state.Settings.AccessLevel == HarnessAccessLevel.FullAccess && state.Settings.Mcp.Enabled)
            {
                progress?.Report(new AgentProgressEvent("mcp", "Connecting MCP servers"));
                mcpSession = await _mcpToolService
                    .OpenSessionAsync(state.Settings.Mcp, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var startupTrace in mcpSession.StartupTrace)
                {
                    trace.Add(startupTrace);
                }
            }

            var messages = BuildMessages(
                state,
                workspaceProject,
                session,
                userMessage,
                recalled,
                searchResults,
                instructions,
                activeSubagents);
            var tools = BuildToolDefinitions(
                state.Settings,
                workspaceProject,
                activeSubagents,
                mcpSession?.Tools ?? []);
            using var conversationScope = _llmClient is IConversationScopedLlmClient scopedClient
                ? scopedClient.BeginConversationScope()
                : null;
            var response = await RunToolLoopAsync(
                state.Settings,
                workspaceProject,
                provider,
                apiKey,
                messages,
                tools,
                mcpSession,
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
                ReasoningContent: response.ReasoningContent,
                TokenUsage: response.Usage,
                Model: response.Model);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        finally
        {
            if (mcpSession is not null)
            {
                try
                {
                    await mcpSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    trace.Add(new ToolTraceEntry("mcp.disconnect", "MCP servers", ex.Message, IsError: true));
                }
            }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        IMcpToolSession? mcpSession,
        IReadOnlyList<SubagentDefinition> subagentDefinitions,
        AgentInstructionSet instructions,
        ICollection<ToolTraceEntry> trace,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        const int maxToolRounds = 12;
        LlmTokenUsage? aggregateUsage = null;
        var reasoningBlocks = new List<string>();
        var successfulToolSignatures = new HashSet<string>(StringComparer.Ordinal);
        var workspaceRevision = 0;
        var consecutiveFailedToolRounds = 0;
        var subagentBudget = new SubagentTurnBudget(Math.Clamp(settings.Subagents.MaxAgentsPerTurn, 0, 12));
        var contextBoundToolCount = BoundToolDefinitionsForModel(
            tools,
            InputTokenBudget(provider.ContextWindowTokens) / 3).Count;
        if (contextBoundToolCount < tools.Count)
        {
            trace.Add(new ToolTraceEntry(
                "agent.context",
                "tool schemas",
                $"Exposed {contextBoundToolCount} of {tools.Count} tools for this turn so the provider request stays within its configured context budget."));
        }

        for (var round = 0; round < maxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var request = PrepareModelRequest(messages, tools, provider.ContextWindowTokens);
            var response = await _llmClient.CompleteChatAsync(
                provider,
                apiKey,
                request.Messages,
                request.Tools,
                cancellationToken,
                streamProgress).ConfigureAwait(false);
            aggregateUsage = AddUsage(aggregateUsage, response.Usage);
            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                reasoningBlocks.Add(response.ReasoningContent.Trim());
            }

            var toolCalls = NormalizeToolCalls(response.ToolCalls, round);
            if (toolCalls.Count == 0)
            {
                return response with
                {
                    ReasoningContent = JoinReasoningBlocks(reasoningBlocks),
                    Usage = aggregateUsage
                };
            }

            var repeatedToolCall = toolCalls.Take(MaxToolCallsPerResponse).FirstOrDefault(toolCall =>
                successfulToolSignatures.Contains(ToolLoopSignature(toolCall, workspaceRevision)));
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
                MaxToolCallsPerResponse,
                mcpSession,
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
                var completed = bundle.CompletedAt ?? DateTimeOffset.UtcNow;
                trace.Add(new ToolTraceEntry(
                    execution.Tool,
                    execution.Input,
                    execution.Output,
                    execution.IsError,
                    StartedAt: bundle.StartedAt ?? completed,
                    CompletedAt: completed));
                messages.Add(new LlmChatMessage(
                    "tool",
                    BoundToolOutputForModel(execution.Output, provider.ContextWindowTokens),
                    ToolCallId: bundle.ToolCall.Id));
                if (!execution.IsError)
                {
                    if (ChangesWorkspace(execution.Tool))
                    {
                        workspaceRevision++;
                    }

                    successfulToolSignatures.Add(ToolLoopSignature(bundle.ToolCall, workspaceRevision));
                }
            }

            if (executions.Count > 0 && executions.All(bundle => bundle.Execution.IsError))
            {
                consecutiveFailedToolRounds++;
                if (consecutiveFailedToolRounds >= 3)
                {
                    trace.Add(new ToolTraceEntry(
                        "agent.loop",
                        "tool errors",
                        "Three consecutive tool rounds failed; asking the model to summarize the completed work instead of continuing the same failing approach."));
                    return await FinalizeToolLoopAsync(
                        provider,
                        apiKey,
                        messages,
                        trace,
                        aggregateUsage,
                        reasoningBlocks,
                        "Three consecutive tool rounds failed.",
                        maxToolRounds,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                consecutiveFailedToolRounds = 0;
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
            var finalRequest = PrepareModelRequest(
                finalMessages,
                tools: null,
                contextWindowTokens: provider.ContextWindowTokens);
            var finalResponse = await _llmClient.CompleteChatAsync(
                provider,
                apiKey,
                finalRequest.Messages,
                finalRequest.Tools,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            AddNullable(left.TotalTokens, right.TotalTokens),
            right.ContextTokens ?? left.ContextTokens,
            right.ContextWindowTokens ?? left.ContextWindowTokens);

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
                ? toolCall with { Id = $"lucky_tool_{round + 1}_{index + 1}" }
                : toolCall)
            .ToArray();
    }

    private static string ToolLoopSignature(ToolCallRequest toolCall, int workspaceRevision)
    {
        var signature = ToolSignature(toolCall);
        return toolCall.Name is "project_list_files" or "project_read_file" or "project_search" or "project_run_command"
            ? $"workspace-{workspaceRevision}:{signature}"
            : signature;
    }

    private static bool ChangesWorkspace(string executionTool) => executionTool is
        "project.write_file" or
        "project.edit_file" or
        "project.apply_patch" or
        "project.run_command";

    private static string BoundToolOutputForModel(string output, int contextWindowTokens)
    {
        var maxCharacters = Math.Clamp(Math.Max(512, contextWindowTokens / 2), 512, 8000);
        return BoundTextForModel(
            output,
            maxCharacters,
            "\n\n[... Lucky truncated this tool output for context safety; the visible trace retains the result summary ...]\n\n");
    }

    private static PreparedModelRequest PrepareModelRequest(
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        int contextWindowTokens)
    {
        var inputBudget = InputTokenBudget(contextWindowTokens);
        var preparedTools = tools is null
            ? null
            : BoundToolDefinitionsForModel(tools, inputBudget / 3);
        var toolTokens = preparedTools?.Sum(EstimateToolTokens) ?? 0;
        var messageBudget = Math.Max(128, inputBudget - toolTokens);
        return new PreparedModelRequest(
            CompactMessagesForModel(messages, messageBudget),
            preparedTools);
    }

    private static IReadOnlyList<LlmToolDefinition> BoundToolDefinitionsForModel(
        IReadOnlyList<LlmToolDefinition> tools,
        int budgetTokens)
    {
        var remaining = Math.Max(128, budgetTokens);
        var bounded = new List<LlmToolDefinition>();
        foreach (var tool in tools)
        {
            var candidate = CompactToolDefinition(tool, Math.Min(4096, Math.Max(512, remaining * 4)));
            var estimate = EstimateToolTokens(candidate);
            if (estimate > remaining)
            {
                candidate = CompactToolDefinition(tool, Math.Min(1024, Math.Max(256, remaining * 4 / 2)), forceFlatSchema: true);
                estimate = EstimateToolTokens(candidate);
            }

            if (estimate > remaining && bounded.Count > 0)
            {
                continue;
            }

            // Preserve at least one function definition even for a very small configured context.
            // The request-message compactor reserves the rest of the input budget around it.
            bounded.Add(candidate);
            remaining = Math.Max(0, remaining - estimate);
            if (remaining <= 0)
            {
                break;
            }
        }

        return bounded;
    }

    private static LlmToolDefinition CompactToolDefinition(
        LlmToolDefinition tool,
        int maximumCharacters,
        bool forceFlatSchema = false)
    {
        var description = BoundTextForModel(
            tool.Description,
            Math.Clamp(maximumCharacters / 3, 160, 1200),
            "\n[tool description truncated by Lucky]");
        var parameterLimit = Math.Clamp(maximumCharacters / 120, 1, 48);
        var parameters = tool.Parameters
            .OrderByDescending(entry => entry.Value.Required)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(parameterLimit)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value with
                {
                    Description = BoundTextForModel(
                        entry.Value.Description,
                        Math.Clamp(maximumCharacters / Math.Max(2, parameterLimit), 80, 480),
                        " [parameter description truncated by Lucky]")
                },
                StringComparer.Ordinal);
        var required = tool.Required.Where(parameters.ContainsKey).ToArray();
        JsonElement? inputSchema = null;
        if (!forceFlatSchema && tool.InputSchema is JsonElement schema)
        {
            var raw = schema.GetRawText();
            if (raw.Length <= Math.Clamp(maximumCharacters / 2, 256, 4096))
            {
                inputSchema = schema;
            }
        }

        return new LlmToolDefinition(tool.Name, description, parameters, required, inputSchema);
    }

    private static int EstimateToolTokens(LlmToolDefinition tool)
    {
        var characters = tool.Name.Length + tool.Description.Length + 24;
        characters += tool.Parameters.Sum(parameter =>
            parameter.Key.Length + parameter.Value.Type.Length + parameter.Value.Description.Length + 16);
        characters += tool.Required.Sum(required => required.Length + 4);
        if (tool.InputSchema is JsonElement schema)
        {
            characters += schema.GetRawText().Length;
        }

        return EstimateTokensForCharacterCount(characters);
    }

    private static IReadOnlyList<LlmChatMessage> CompactMessagesForModel(
        IReadOnlyList<LlmChatMessage> source,
        int budgetTokens)
    {
        var messages = source.Select(CloneMessageForModel).ToList();
        while (EstimateMessagesTokens(messages) > budgetTokens && DropOldestCompleteTurn(messages))
        {
            // Keep the latest user turn and its most recent tool context whenever possible.
        }

        if (EstimateMessagesTokens(messages) > budgetTokens)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                if (messages[index].Role == "tool")
                {
                    messages[index] = messages[index] with
                    {
                        Content = BoundTextForModel(
                            messages[index].Content,
                            384,
                            "\n[tool result compacted by Lucky]")
                    };
                }
                else if (messages[index].ToolCalls is { Count: > 0 })
                {
                    messages[index] = messages[index] with
                    {
                        ToolCalls = messages[index].ToolCalls!
                            .Select(call => call.ArgumentsJson.Length <= 512
                                ? call
                                : call with { ArgumentsJson = "{\"_lucky_compacted\":true}" })
                            .ToArray(),
                        ReasoningContent = string.IsNullOrWhiteSpace(messages[index].ReasoningContent)
                            ? messages[index].ReasoningContent
                            : BoundTextForModel(messages[index].ReasoningContent!, 384, "\n[reasoning compacted by Lucky]")
                    };
                }
            }
        }

        if (EstimateMessagesTokens(messages) > budgetTokens)
        {
            var contentBudget = Math.Max(256, budgetTokens * 4 - messages.Count * 48);
            var latestUserIndex = messages.FindLastIndex(message => message.Role == "user");
            var totalWeight = messages
                .Select((message, index) => index == latestUserIndex ? 4 : message.Role == "system" ? 3 : 1)
                .Sum();
            for (var index = 0; index < messages.Count; index++)
            {
                var weight = index == latestUserIndex ? 4 : messages[index].Role == "system" ? 3 : 1;
                var limit = Math.Max(96, contentBudget * weight / Math.Max(1, totalWeight));
                messages[index] = messages[index] with
                {
                    Content = BoundTextForModel(
                        messages[index].Content,
                        limit,
                        messages[index].Role == "system"
                            ? "\n[... Lucky truncated older system context to stay within the configured model context window ...]\n"
                            : "\n[message compacted by Lucky to fit the configured context]")
                };
            }
        }

        return messages;
    }

    private static LlmChatMessage CloneMessageForModel(LlmChatMessage message) => message with
    {
        ToolCalls = message.ToolCalls?.Select(call => call with { }).ToArray()
    };

    private static bool DropOldestCompleteTurn(IList<LlmChatMessage> messages)
    {
        var firstTurn = messages
            .Select((message, index) => (message, index))
            .FirstOrDefault(entry => entry.message.Role != "system");
        if (firstTurn.message is null ||
            (firstTurn.message.Role == "user" && messages.Count(message => message.Role == "user") <= 1))
        {
            return false;
        }

        var nextUser = -1;
        for (var index = firstTurn.index + 1; index < messages.Count; index++)
        {
            if (messages[index].Role == "user")
            {
                nextUser = index;
                break;
            }
        }

        var count = nextUser < 0 ? messages.Count - firstTurn.index : nextUser - firstTurn.index;
        if (count <= 0)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            messages.RemoveAt(firstTurn.index);
        }

        return true;
    }

    private static int EstimateMessagesTokens(IEnumerable<LlmChatMessage> messages) => messages.Sum(message =>
    {
        var characters = message.Content.Length + message.Role.Length + (message.ToolCallId?.Length ?? 0) + (message.ReasoningContent?.Length ?? 0) + 16;
        characters += message.ToolCalls?.Sum(call => call.Id.Length + call.Name.Length + call.ArgumentsJson.Length + 12) ?? 0;
        return EstimateTokensForCharacterCount(characters);
    });

    private static int EstimateTokensForCharacterCount(int characters) => characters <= 0
        ? 0
        : Math.Max(1, (int)Math.Ceiling(characters / 4.0));

    private sealed record PreparedModelRequest(
        IReadOnlyList<LlmChatMessage> Messages,
        IReadOnlyList<LlmToolDefinition>? Tools);

    private static string BoundTextForModel(string text, int maxCharacters, string marker)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var remaining = Math.Max(0, maxCharacters - marker.Length);
        var headLength = remaining * 2 / 3;
        var tailLength = remaining - headLength;
        return text[..headLength] + marker + text[(text.Length - tailLength)..];
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
            if (entry.Tool is "project.write_file" or "project.edit_file" or "project.apply_patch")
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
        int maxToolCallsPerResponse,
        IMcpToolSession? mcpSession,
        IReadOnlyList<SubagentDefinition> subagentDefinitions,
        AgentInstructionSet instructions,
        SubagentTurnBudget subagentBudget,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var results = new ToolExecutionBundle[toolCalls.Count];
        var subagentTasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(Math.Clamp(settings.Subagents.MaxParallelAgents, 1, 12));
        using var subagentMutationGate = new SemaphoreSlim(1, 1);

        for (var index = 0; index < toolCalls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toolCall = toolCalls[index];
            if (index >= maxToolCallsPerResponse)
            {
                results[index] = new ToolExecutionBundle(
                    toolCall,
                    ToolCallLimitError(toolCall, maxToolCallsPerResponse),
                    [],
                    null,
                    StartedAt: DateTimeOffset.UtcNow,
                    CompletedAt: DateTimeOffset.UtcNow);
                continue;
            }

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
                var requiresMutationGate = SubagentCanMutateWorkspace(
                    settings.AccessLevel,
                    subagentDefinitions,
                    toolCall);
                subagentTasks.Add(Task.Run(async () =>
                {
                    var mutationGateHeld = false;
                    var concurrencyGateHeld = false;
                    try
                    {
                        if (requiresMutationGate)
                        {
                            await subagentMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            mutationGateHeld = true;
                        }

                        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        concurrencyGateHeld = true;
                        var started = DateTimeOffset.UtcNow;
                        var bundle = await ExecuteSubagentToolCallAsync(
                            settings,
                            project,
                            provider,
                            apiKey,
                            toolCall,
                            subagentDefinitions,
                            instructions,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                        results[capturedIndex] = bundle with
                        {
                            StartedAt = started,
                            CompletedAt = DateTimeOffset.UtcNow
                        };
                    }
                    finally
                    {
                        if (concurrencyGateHeld)
                        {
                            semaphore.Release();
                        }

                        if (mutationGateHeld)
                        {
                            subagentMutationGate.Release();
                        }
                    }
                }, cancellationToken));
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            var execution = await ExecuteToolCallAsync(
                settings,
                project,
                toolCall,
                mcpSession,
                progress,
                cancellationToken).ConfigureAwait(false);
            results[index] = new ToolExecutionBundle(
                toolCall,
                execution,
                [],
                null,
                StartedAt: started,
                CompletedAt: DateTimeOffset.UtcNow);
        }

        await Task.WhenAll(subagentTasks).ConfigureAwait(false);
        return results;
    }

    private static ToolExecutionResult ToolCallLimitError(ToolCallRequest toolCall, int limit) => new(
        TraceToolName(toolCall.Name),
        toolCall.ArgumentsJson,
        $"Lucky accepts at most {limit} tool calls in one model response. This call was not run; review the completed tool results and return a final answer or send a focused follow-up.",
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
        "sandbox_execute" => "sandbox.execute",
        "web_open" => "web.open",
        "subagent_run" => "subagent",
        _ => toolName
    };

    private static bool SubagentCanMutateWorkspace(
        HarnessAccessLevel parentAccessLevel,
        IReadOnlyList<SubagentDefinition> definitions,
        ToolCallRequest toolCall)
    {
        if (parentAccessLevel != HarnessAccessLevel.FullAccess)
        {
            return false;
        }

        var agentName = StringArg(ParseToolArguments(toolCall.ArgumentsJson), "agent", null);
        var definition = definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, agentName, StringComparison.OrdinalIgnoreCase));
        return definition is { AccessLevel: HarnessAccessLevel.FullAccess } &&
               definition.Tools.Any(IsWorkspaceMutationTool);
    }

    private static bool IsWorkspaceMutationTool(string toolName) => toolName is
        "project_write_file" or
        "project_edit_file" or
        "project_apply_patch" or
        "project_run_command";

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
        IMcpToolSession? mcpSession,
        IProgress<AgentProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsToolAllowed(settings.AccessLevel, toolCall.Name))
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, $"Denied by access level {settings.AccessLevel}.", IsError: true);
        }

        var args = ParseToolArguments(toolCall.ArgumentsJson);
        progress?.Report(new AgentProgressEvent("tool", ToolSummary(toolCall.Name, args), toolCall.ArgumentsJson));

        if (toolCall.Name == "web_search")
        {
            return await ExecuteWebSearchToolAsync(settings, args, cancellationToken).ConfigureAwait(false);
        }

        if (toolCall.Name == "web_open")
        {
            return await ExecuteWebOpenToolAsync(settings, args, cancellationToken).ConfigureAwait(false);
        }

        if (toolCall.Name.StartsWith("mcp_", StringComparison.Ordinal))
        {
            return mcpSession is null
                ? new ToolExecutionResult(toolCall.Name, toolCall.Name, "No MCP session is available for this turn.", IsError: true)
                : await mcpSession.ExecuteAsync(toolCall.Name, toolCall.ArgumentsJson, cancellationToken).ConfigureAwait(false);
        }

        if (toolCall.Name == "sandbox_execute")
        {
            return await _codeExecutionSandbox.ExecuteAsync(
                settings.Sandbox,
                RequiredStringArg(args, "command"),
                NullableIntArg(args, "timeoutSeconds"),
                cancellationToken).ConfigureAwait(false);
        }

        if (project is null)
        {
            return new ToolExecutionResult(toolCall.Name, toolCall.ArgumentsJson, "No project folder is selected.", IsError: true);
        }

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

    private async Task<ToolExecutionResult> ExecuteWebSearchToolAsync(
        AppSettings settings,
        IReadOnlyDictionary<string, JsonElement> args,
        CancellationToken cancellationToken)
    {
        var query = StringArg(args, "query", null);
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new InvalidOperationException("query is required.");
            }

            var results = await _webSearchClient.SearchAsync(
                settings.SearxngUrl,
                query,
                settings.WebSearchMaxResults,
                cancellationToken).ConfigureAwait(false);

            return new ToolExecutionResult(
                "web.search",
                query,
                RenderSearchOnly(results));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new ToolExecutionResult("web.search", query ?? JsonSerializer.Serialize(args), ex.Message, IsError: true);
        }
    }

    private async Task<ToolExecutionResult> ExecuteWebOpenToolAsync(
        AppSettings settings,
        IReadOnlyDictionary<string, JsonElement> args,
        CancellationToken cancellationToken)
    {
        var url = StringArg(args, "url", null);
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolExecutionResult("web.open", "", "url is required.", IsError: true);
        }

        return await _webPageReader
            .OpenAsync(settings.Browser, url, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(
        AppSettings settings,
        LuckyProject? project,
        IReadOnlyList<SubagentDefinition> subagentDefinitions,
        IReadOnlyList<McpDiscoveredTool> mcpTools)
    {
        var accessLevel = settings.AccessLevel;
        var tools = new List<LlmToolDefinition>();
        if (!string.IsNullOrWhiteSpace(settings.SearxngUrl))
        {
            tools.Add(new LlmToolDefinition(
                "web_search",
                "Search the web through the user's configured SearXNG endpoint and return the top results. Use for live/current information or when the user asks to search the web.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["query"] = new("string", "Search query to send to SearXNG.", Required: true)
                },
                ["query"]));
        }

        if (accessLevel == HarnessAccessLevel.FullAccess &&
            settings.Browser.Enabled &&
            settings.Browser.AllowedDomains.Count > 0)
        {
            tools.Add(new LlmToolDefinition(
                "web_open",
                "Read a static web page from the user's configured trusted-domain list. The reader has no browser login, cookies, or form interaction. Use only for a URL relevant to the user's request.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["url"] = new("string", "Absolute http or https URL on a configured trusted domain.", Required: true)
                },
                ["url"]));
        }

        if (accessLevel == HarnessAccessLevel.FullAccess)
        {
            foreach (var mcpTool in mcpTools)
            {
                tools.Add(new LlmToolDefinition(
                    mcpTool.ModelToolName,
                    $"Run the '{mcpTool.ToolName}' tool exposed by the user's configured MCP server '{mcpTool.ServerName}'. Use it only when it directly serves the user's request; all results are visible in Lucky's trace.",
                    mcpTool.Parameters,
                    mcpTool.Required,
                    mcpTool.InputSchema));
            }
        }

        if (accessLevel == HarnessAccessLevel.FullAccess &&
            settings.Sandbox.Enabled &&
            !string.IsNullOrWhiteSpace(settings.Sandbox.Image))
        {
            tools.Add(new LlmToolDefinition(
                "sandbox_execute",
                "Run a Unix shell command in Lucky's explicitly configured Docker code sandbox. The image must already exist locally; Lucky never pulls it. Lucky requires its local Windows named-pipe Docker daemon and rejects remote Docker contexts. The container has no network, a read-only root filesystem, bounded CPU/memory/PIDs, disposable /scratch storage, and no host-folder mounts. This is separate from project_run_command, which is unsandboxed host PowerShell.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["command"] = new("string", "Unix shell command to run inside the configured local Docker image.", Required: true),
                    ["timeoutSeconds"] = new("integer", "Optional timeout from 1 second up to the sandbox limit configured in Settings.")
                },
                ["command"]));
        }

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
            tools.Add(new LlmToolDefinition(
                "project_apply_patch",
                "Apply a standard unified diff to text files inside the selected project. Validate all hunks first; use this for multi-line or multi-file edits. Git-style a/ and b/ paths are accepted, but do not use rename patches.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["patch"] = new("string", "Complete unified diff beginning with --- and +++ file headers, followed by @@ hunks.", Required: true)
                },
                ["patch"]));
            tools.Add(new LlmToolDefinition(
                "project_run_command",
                "Run a PowerShell command from the verified selected-project root. Use it to build, test, inspect diagnostics, or run project tooling. The output, exit code, and timeout are visible to the user. Commands execute with the user's Full access Windows account, so keep them relevant to the selected project.",
                new Dictionary<string, ToolParameterDefinition>
                {
                    ["command"] = new("string", "PowerShell command to run from the selected project root.", Required: true),
                    ["timeoutSeconds"] = new("integer", "Optional timeout from 1 to 300 seconds; defaults to 60.")
                },
                ["command"]));
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
        var inputBudget = InputTokenBudget(state.Settings.ActiveProviderSettings.ContextWindowTokens);
        var systemPrompt = BoundSystemPromptForModel(
            BuildSystemPrompt(state.Settings, project, memories, searchResults, instructions, subagentDefinitions),
            inputBudget);
        var history = session.Messages
            .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
            .TakeLast(Math.Max(4, state.Settings.ContextMessageLimit))
            .Select(message => new LlmChatMessage(
                message.Role == ChatRole.User ? "user" : "assistant",
                message.Content))
            .ToList();

        var latest = history.LastOrDefault();
        if (latest is null ||
            latest.Role != "user" ||
            !string.Equals(latest.Content, userMessage, StringComparison.Ordinal))
        {
            history.Add(new LlmChatMessage("user", userMessage));
        }

        while (history.Count > 1 && ContextEstimator.EstimateTokens(systemPrompt) + history.Sum(message => ContextEstimator.EstimateTokens(message.Content)) > inputBudget)
        {
            DropOldestConversationTurn(history);
        }

        var messages = new List<LlmChatMessage>(history.Count + 1)
        {
            new("system", systemPrompt)
        };
        messages.AddRange(history);
        return messages;
    }

    private static int InputTokenBudget(int contextWindowTokens)
    {
        var context = Math.Max(1024, contextWindowTokens);
        var reservedForCompletionAndTools = Math.Clamp(context / 4, 512, 8192);
        return Math.Max(512, context - reservedForCompletionAndTools);
    }

    private static string BoundSystemPromptForModel(string systemPrompt, int inputTokenBudget)
    {
        var systemBudgetTokens = Math.Max(256, inputTokenBudget * 70 / 100);
        var maxCharacters = Math.Max(1024, systemBudgetTokens * 4);
        return BoundTextForModel(
            systemPrompt,
            maxCharacters,
            "\n\n[... Lucky truncated older system context to stay within the configured model context window ...]\n\n");
    }

    private static void DropOldestConversationTurn(IList<LlmChatMessage> history)
    {
        if (history.Count == 0)
        {
            return;
        }

        var removed = history[0];
        history.RemoveAt(0);
        if (removed.Role == "user" && history.Count > 1 && history[0].Role == "assistant")
        {
            history.RemoveAt(0);
        }

        while (history.Count > 1 && history[0].Role == "assistant")
        {
            history.RemoveAt(0);
        }
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
        if (settings.AccessLevel == HarnessAccessLevel.FullAccess)
        {
            builder.AppendLine("Full access can expose project-scoped writes, unified patches, PowerShell execution from the verified selected-project root, trusted static page reading, user-configured MCP tools, and an explicitly configured Docker code sandbox. Host PowerShell is not sandboxed. Inspect first, use focused actions, and report only actions confirmed by tool results.");
        }
        builder.AppendLine("Lucky web access is through the user's configured SearXNG endpoint. If web_search is available, use it for live/current web information or when the user asks to search. If SearXNG results are already included below, Lucky fetched them for this turn; treat them as real web results and do not claim you lack internet access merely because the search was prefetched.");
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
            var memoryBuilder = new StringBuilder();
            var profile = memories.Where(memory => memory.Kind == MemoryKind.UserProfile).ToArray();
            var durable = memories.Where(memory => memory.Kind == MemoryKind.Memory).ToArray();
            AppendMemorySection(memoryBuilder, "USER profile notes:", profile, settings.UserProfileCharLimit);
            AppendMemorySection(memoryBuilder, "Relevant durable memories:", durable, settings.MemoryCharLimit);
            if (memoryBuilder.Length > 0)
            {
                builder.AppendLine();
                builder.Append(memoryBuilder);
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

    private static void AppendMemorySection(
        StringBuilder builder,
        string heading,
        IEnumerable<MemoryItem> memories,
        int charLimit)
    {
        var remaining = Math.Max(0, charLimit);
        if (remaining == 0)
        {
            return;
        }

        var lines = new List<string>();
        foreach (var memory in memories)
        {
            var summary = memory.Summary.Trim();
            if (summary.Length == 0)
            {
                continue;
            }

            var line = $"- {summary}";
            if (line.Length > remaining)
            {
                if (remaining < 8)
                {
                    break;
                }

                var prefixLength = Math.Max(0, remaining - 3);
                line = line[..Math.Min(line.Length, prefixLength)].TrimEnd() + "...";
            }

            lines.Add(line);
            remaining -= line.Length + Environment.NewLine.Length;
            if (remaining <= 0 || line.EndsWith("...", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        builder.AppendLine(heading);
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }
    }

    private static bool IsToolAllowed(HarnessAccessLevel accessLevel, string toolName)
    {
        if (accessLevel == HarnessAccessLevel.ChatOnly)
        {
            return toolName is "web_search" or "subagent_run";
        }

        if (toolName is "web_search" or "subagent_run")
        {
            return true;
        }

        if (toolName is "project_list_files" or "project_read_file" or "project_search")
        {
            return accessLevel is HarnessAccessLevel.Workspace or HarnessAccessLevel.FullAccess;
        }

        if (toolName is "project_write_file" or "project_edit_file" or "project_apply_patch" or "project_run_command" or "sandbox_execute" or "web_open" ||
            toolName.StartsWith("mcp_", StringComparison.Ordinal))
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
            "project_apply_patch" => "Applying unified diff patch",
            "project_run_command" => "Running PowerShell command",
            "sandbox_execute" => "Running isolated Docker sandbox",
            "web_search" => $"Searching web for {StringArg(args, "query", "query")}",
            "web_open" => $"Reading trusted page {StringArg(args, "url", "page")}",
            "subagent_run" => $"Running subagent {StringArg(args, "agent", "subagent")}",
            _ when toolName.StartsWith("mcp_", StringComparison.Ordinal) => "Running MCP tool",
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

    private static IReadOnlyList<SubagentDefinition> SelectActiveSubagents(
        IReadOnlyList<SubagentDefinition> definitions,
        SubagentSettings settings,
        bool explicitSubagentRequest)
    {
        if (!settings.Enabled || definitions.Count == 0)
        {
            return [];
        }

        if (explicitSubagentRequest)
        {
            return definitions;
        }

        if (!settings.AutoDelegateEnabled)
        {
            return [];
        }

        return definitions.Where(definition => definition.AutoActivate).ToArray();
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
        LlmTokenUsage? Usage,
        DateTimeOffset? StartedAt = null,
        DateTimeOffset? CompletedAt = null);

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
