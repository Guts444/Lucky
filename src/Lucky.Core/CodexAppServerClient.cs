using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lucky.Core;

/// <summary>
/// A deliberately narrow bridge to the local, official Codex app-server. Codex owns the
/// ChatGPT OAuth browser flow and refresh tokens; Lucky only receives account state, the model
/// catalog, streamed output, and token metadata over a local stdio JSON-RPC connection.
/// </summary>
public sealed class CodexAppServerClient : ILlmClient, ICodexSubscriptionService, IConversationScopedLlmClient
{
    private readonly AsyncLocal<CodexConversation?> _conversation = new();
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CodexLoginCompletion>> _loginAttempts = new(StringComparer.Ordinal);
    private CodexAppServerConnection? _accountConnection;

    public IDisposable BeginConversationScope()
    {
        var previous = _conversation.Value;
        var current = new CodexConversation();
        _conversation.Value = current;
        return new ConversationScope(this, previous, current);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        ProviderSettings provider,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var models = await GetModelCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(model => model.Id).ToArray();
    }

    public async Task<LlmResponse> CompleteChatAsync(
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default,
        IProgress<LlmStreamDelta>? streamProgress = null)
    {
        var conversation = _conversation.Value;
        var ownsScope = conversation is null;
        if (conversation is null)
        {
            conversation = new CodexConversation();
            _conversation.Value = conversation;
        }

        try
        {
            var response = await conversation
                .CompleteAsync(provider, messages, tools, streamProgress, cancellationToken)
                .ConfigureAwait(false);
            if (response.Usage?.ContextWindowTokens is > 0)
            {
                provider.ContextWindowTokens = response.Usage.ContextWindowTokens.Value;
            }

            return response;
        }
        finally
        {
            if (ownsScope)
            {
                _conversation.Value = null;
                await conversation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<CodexAccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            CodexAppServerConnection? connection = null;
            try
            {
                connection = await GetAccountConnectionAsync(cancellationToken).ConfigureAwait(false);
                var result = await connection
                    .SendRequestAsync("account/read", new { refreshToken = true }, cancellationToken)
                    .ConfigureAwait(false);
                await CodexAppHomeDirectory.ProtectAuthFileAsync(cancellationToken).ConfigureAwait(false);
                return ParseAccountStatus(result);
            }
            catch (Exception ex) when (attempt == 0 && IsBrokenAccountPipe(ex))
            {
                await ResetAccountConnectionAsync(connection).ConfigureAwait(false);
            }
        }
    }

    public async Task<CodexLoginStart> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetAccountConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = await connection
            .SendRequestAsync("account/login/start", new { type = "chatgpt" }, cancellationToken)
            .ConfigureAwait(false);

        var loginId = StringProperty(result, "loginId");
        var authorizationUrl = StringProperty(result, "authUrl");
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(authorizationUrl))
        {
            throw new InvalidOperationException("Codex did not return a ChatGPT authorization URL.");
        }

        _loginAttempts.TryAdd(loginId, NewLoginCompletionSource());
        return new CodexLoginStart(loginId, authorizationUrl);
    }

    public async Task<CodexAccountStatus> WaitForLoginAsync(string loginId, CancellationToken cancellationToken = default)
    {
        if (!_loginAttempts.TryGetValue(loginId, out var completion))
        {
            throw new InvalidOperationException("That ChatGPT sign-in attempt is no longer active.");
        }

        try
        {
            var result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? "ChatGPT sign-in did not complete."
                    : result.Error);
            }

            await CodexAppHomeDirectory.ProtectAuthFileAsync(cancellationToken).ConfigureAwait(false);
            return await GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loginAttempts.TryRemove(loginId, out _);
        }
    }

    public async Task<CodexAccountStatus> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetAccountConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.SendRequestAsync("account/logout", new { }, cancellationToken).ConfigureAwait(false);
        await CodexAppHomeDirectory.ForgetAuthAsync(cancellationToken).ConfigureAwait(false);
        return await GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderModelCapability>> GetModelCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var pages = new List<JsonElement>();
        string? cursor = null;
        do
        {
            JsonElement result;
            for (var attempt = 0; ; attempt++)
            {
                CodexAppServerConnection? connection = null;
                try
                {
                    // Reuse the authenticated account app-server. Starting a second packaged child
                    // just to list models can trigger unrelated repository/git initialization and
                    // also duplicates the OAuth/runtime state Lucky already has open.
                    connection = await GetAccountConnectionAsync(cancellationToken).ConfigureAwait(false);
                    result = await connection
                        .SendRequestAsync(
                            "model/list",
                            new { limit = 100, cursor, includeHidden = false },
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt == 0 && IsBrokenAccountPipe(ex))
                {
                    await ResetAccountConnectionAsync(connection).ConfigureAwait(false);
                }
            }

            if (!result.TryGetProperty("data", out var page) || page.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            pages.Add(page.Clone());
            cursor = StringProperty(result, "nextCursor");
        } while (!string.IsNullOrWhiteSpace(cursor));

        var contexts = ReadCachedModelContexts();

        var models = new List<ProviderModelCapability>();
        foreach (var item in pages.SelectMany(page => page.EnumerateArray()).GroupBy(
                     item => StringProperty(item, "id") ?? StringProperty(item, "model"),
                     StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            var id = StringProperty(item, "id") ?? StringProperty(item, "model");
            if (string.IsNullOrWhiteSpace(id) || BoolProperty(item, "hidden"))
            {
                continue;
            }

            var efforts = item.TryGetProperty("supportedReasoningEfforts", out var supportedEfforts) &&
                          supportedEfforts.ValueKind == JsonValueKind.Array
                ? supportedEfforts.EnumerateArray()
                    .Select(entry => StringProperty(entry, "reasoningEffort"))
                    .Where(effort => !string.IsNullOrWhiteSpace(effort))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            var defaultEffort = StringProperty(item, "defaultReasoningEffort")
                ?? efforts.FirstOrDefault()
                ?? "medium";
            if (efforts.Count == 0)
            {
                efforts.Add(defaultEffort);
            }

            var contextWindow = contexts.TryGetValue(id, out var cached)
                ? cached.EffectiveInputTokens
                : CodexModelContextDefaults.For(id);
            models.Add(new ProviderModelCapability
            {
                Id = id,
                DisplayName = StringProperty(item, "displayName") ?? id,
                Description = StringProperty(item, "description") ?? "Codex subscription model",
                ReasoningEfforts = efforts,
                DefaultReasoningEffort = defaultEffort,
                ContextWindowTokens = contextWindow,
                IsDefault = BoolProperty(item, "isDefault")
            });
        }

        return models
            .OrderByDescending(model => model.IsDefault)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<CodexAppServerConnection> GetAccountConnectionAsync(CancellationToken cancellationToken)
    {
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accountConnection is { IsClosed: false })
            {
                return _accountConnection;
            }

            if (_accountConnection is not null)
            {
                await _accountConnection.DisposeAsync().ConfigureAwait(false);
            }

            _accountConnection = await CodexAppServerConnection.StartAsync(cancellationToken).ConfigureAwait(false);
            _accountConnection.NotificationReceived = HandleAccountNotificationAsync;
            _accountConnection.Closed = error =>
            {
                foreach (var attempt in _loginAttempts.Values)
                {
                    attempt.TrySetResult(new CodexLoginCompletion(false, error?.Message));
                }
            };
            return _accountConnection;
        }
        finally
        {
            _accountGate.Release();
        }
    }

    private async Task ResetAccountConnectionAsync(CodexAppServerConnection? failedConnection)
    {
        await _accountGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (failedConnection is not null && ReferenceEquals(_accountConnection, failedConnection))
            {
                await _accountConnection.DisposeAsync().ConfigureAwait(false);
                _accountConnection = null;
            }
        }
        finally
        {
            _accountGate.Release();
        }
    }

    private static bool IsBrokenAccountPipe(Exception exception) =>
        exception is IOException ||
        exception is InvalidOperationException &&
        (exception.Message.Contains("pipe", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("app-server ended", StringComparison.OrdinalIgnoreCase));

    private Task HandleAccountNotificationAsync(string method, JsonElement parameters)
    {
        if (!string.Equals(method, "account/login/completed", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var loginId = StringProperty(parameters, "loginId");
        if (string.IsNullOrWhiteSpace(loginId) || !_loginAttempts.TryGetValue(loginId, out var attempt))
        {
            return Task.CompletedTask;
        }

        attempt.TrySetResult(new CodexLoginCompletion(
            BoolProperty(parameters, "success"),
            StringProperty(parameters, "error")));
        return Task.CompletedTask;
    }

    private static TaskCompletionSource<CodexLoginCompletion> NewLoginCompletionSource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static CodexAccountStatus ParseAccountStatus(JsonElement result)
    {
        var requiresAuth = BoolProperty(result, "requiresOpenaiAuth");
        if (!result.TryGetProperty("account", out var account) || account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new CodexAccountStatus(false, null, requiresAuth, "Not connected to ChatGPT.");
        }

        var type = StringProperty(account, "type");
        var plan = StringProperty(account, "planType");
        var isChatGpt = string.Equals(type, "chatgpt", StringComparison.OrdinalIgnoreCase);
        var detail = isChatGpt
            ? string.IsNullOrWhiteSpace(plan) ? "Connected to ChatGPT." : $"Connected to ChatGPT {plan}."
            : string.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase)
                ? "Codex is connected with an API key, not a ChatGPT subscription."
                : "Codex has an active account connection.";
        return new CodexAccountStatus(isChatGpt, plan, requiresAuth, detail);
    }

    private static IReadOnlyDictionary<string, CachedCodexModelContext> ReadCachedModelContexts()
    {
        var path = Path.Combine(CodexAppHomeDirectory.Ensure(), "models_cache.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, CachedCodexModelContext>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, CachedCodexModelContext>(StringComparer.OrdinalIgnoreCase);
            }

            var contexts = new Dictionary<string, CachedCodexModelContext>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models.EnumerateArray())
            {
                var slug = StringProperty(model, "slug");
                var contextWindow = IntProperty(model, "context_window");
                if (string.IsNullOrWhiteSpace(slug) || contextWindow is not > 0)
                {
                    continue;
                }

                var percent = DoubleProperty(model, "effective_context_window_percent") ?? 100d;
                var effective = Math.Max(1024, (int)Math.Floor(contextWindow.Value * Math.Clamp(percent, 1d, 100d) / 100d));
                contexts[slug] = new CachedCodexModelContext(effective);
            }

            return contexts;
        }
        catch (IOException)
        {
            return new Dictionary<string, CachedCodexModelContext>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, CachedCodexModelContext>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, CachedCodexModelContext>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? IntProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static double? DoubleProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;

    private static bool BoolProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private sealed class ConversationScope : IDisposable
    {
        private readonly CodexAppServerClient _owner;
        private readonly CodexConversation? _previous;
        private CodexConversation? _current;

        public ConversationScope(CodexAppServerClient owner, CodexConversation? previous, CodexConversation current)
        {
            _owner = owner;
            _previous = previous;
            _current = current;
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _current, null);
            if (current is null)
            {
                return;
            }

            _owner._conversation.Value = _previous;
            current.Dispose();
        }
    }

    private sealed record CodexLoginCompletion(bool Success, string? Error);
    private sealed record CachedCodexModelContext(int EffectiveInputTokens);
}

/// <summary>
/// The one interface the WinUI layer needs for ChatGPT subscription management. It deliberately
/// omits any raw OAuth or refresh token surface.
/// </summary>
public interface ICodexSubscriptionService
{
    Task<CodexAccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default);
    Task<CodexLoginStart> StartLoginAsync(CancellationToken cancellationToken = default);
    Task<CodexAccountStatus> WaitForLoginAsync(string loginId, CancellationToken cancellationToken = default);
    Task<CodexAccountStatus> LogoutAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderModelCapability>> GetModelCapabilitiesAsync(CancellationToken cancellationToken = default);
}

public interface IConversationScopedLlmClient
{
    IDisposable BeginConversationScope();
}

public sealed record CodexAccountStatus(bool IsChatGptConnected, string? Plan, bool RequiresOpenAiAuth, string Detail);
public sealed record CodexLoginStart(string LoginId, string AuthorizationUrl);

/// <summary>
/// Routes normal HTTP providers and Codex subscriptions through the correct transport while
/// preserving the existing ILlmClient contract for Lucky's tool loop and subagents.
/// </summary>
public sealed class LuckyLlmClient : ILlmClient, ICodexSubscriptionService, IConversationScopedLlmClient
{
    private readonly OpenAiCompatibleClient _openAiCompatible;
    private readonly CodexAppServerClient _codex;

    public LuckyLlmClient(
        OpenAiCompatibleClient? openAiCompatible = null,
        CodexAppServerClient? codex = null)
    {
        _openAiCompatible = openAiCompatible ?? new OpenAiCompatibleClient();
        _codex = codex ?? new CodexAppServerClient();
    }

    public IDisposable BeginConversationScope() => _codex.BeginConversationScope();

    public Task<IReadOnlyList<string>> ListModelsAsync(
        ProviderSettings provider,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        provider.Transport == ProviderTransport.CodexAppServer
            ? _codex.ListModelsAsync(provider, apiKey, cancellationToken)
            : _openAiCompatible.ListModelsAsync(provider, apiKey, cancellationToken);

    public Task<LlmResponse> CompleteChatAsync(
        ProviderSettings provider,
        string? apiKey,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default,
        IProgress<LlmStreamDelta>? streamProgress = null) =>
        provider.Transport == ProviderTransport.CodexAppServer
            ? _codex.CompleteChatAsync(provider, apiKey, messages, tools, cancellationToken, streamProgress)
            : _openAiCompatible.CompleteChatAsync(provider, apiKey, messages, tools, cancellationToken, streamProgress);

    public Task<CodexAccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
        _codex.GetAccountStatusAsync(cancellationToken);

    public Task<CodexLoginStart> StartLoginAsync(CancellationToken cancellationToken = default) =>
        _codex.StartLoginAsync(cancellationToken);

    public Task<CodexAccountStatus> WaitForLoginAsync(string loginId, CancellationToken cancellationToken = default) =>
        _codex.WaitForLoginAsync(loginId, cancellationToken);

    public Task<CodexAccountStatus> LogoutAsync(CancellationToken cancellationToken = default) =>
        _codex.LogoutAsync(cancellationToken);

    public Task<IReadOnlyList<ProviderModelCapability>> GetModelCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _codex.GetModelCapabilitiesAsync(cancellationToken);
}

internal static class CodexModelContextDefaults
{
    // Codex applies a safety/compaction margin to these default input budgets. A live
    // thread/tokenUsage/updated event replaces this value as soon as a model serves a turn.
    public static int For(string model) => model switch
    {
        "gpt-5.6-terra" or "gpt-5.6-luna" or "gpt-5.6-sol" => 353400,
        "gpt-5.5" or "gpt-5.4" or "gpt-5.4-mini" => 258400,
        _ => 128000
    };
}

internal sealed class CodexConversation : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly object _sync = new();
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private CodexAppServerConnection? _connection;
    private TaskCompletionSource<CodexConversationOutcome>? _outcome;
    private PendingDynamicToolCall? _pendingTool;
    private string? _threadId;
    private string? _turnId;
    private string? _actualModel;
    private bool _started;
    private bool _toolsDisabled;
    private int? _totalInputTokens;
    private int? _totalOutputTokens;
    private int? _totalTokens;
    private int? _consumedInputTokens;
    private int? _consumedOutputTokens;
    private int? _consumedTotalTokens;
    private int? _latestContextTokens;
    private int? _latestContextWindowTokens;
    private bool _disposed;

    public async Task<LlmResponse> CompleteAsync(
        ProviderSettings provider,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        IProgress<LlmStreamDelta>? streamProgress,
        CancellationToken cancellationToken)
    {
        await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return await StartAsync(provider, messages, tools, streamProgress, cancellationToken).ConfigureAwait(false);
            }

            return await ContinueAfterToolAsync(provider, messages, tools, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _callGate.Release();
        }
    }

    private async Task<LlmResponse> StartAsync(
        ProviderSettings provider,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        IProgress<LlmStreamDelta>? streamProgress,
        CancellationToken cancellationToken)
    {
        _connection = await CodexAppServerConnection.StartAsync(cancellationToken).ConfigureAwait(false);
        _connection.RequestHandler = HandleServerRequestAsync;
        _connection.NotificationReceived = (method, parameters) => HandleNotificationAsync(method, parameters, streamProgress);
        _connection.Closed = error => SignalOutcome(new CodexFailedOutcome(error?.Message ?? "The local Codex app-server stopped unexpectedly."));

        var threadStart = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["cwd"] = CodexRuntimeDirectory.Ensure(),
            ["sandbox"] = "read-only",
            ["approvalPolicy"] = "untrusted",
            ["personality"] = "friendly",
            ["ephemeral"] = true,
            ["serviceName"] = "lucky",
            ["developerInstructions"] = BuildDeveloperInstructions(messages),
            ["dynamicTools"] = BuildDynamicTools(tools)
        };
        var started = await _connection
            .SendRequestAsync("thread/start", threadStart, cancellationToken)
            .ConfigureAwait(false);
        if (!started.TryGetProperty("thread", out var thread))
        {
            throw new InvalidOperationException("Codex did not return a thread for the Lucky request.");
        }

        _threadId = StringProperty(thread, "id")
            ?? throw new InvalidOperationException("Codex returned a thread without an id.");
        _actualModel = StringProperty(started, "model") ?? provider.Model;
        _started = true;

        var waiter = PrepareOutcomeWaiter();
        try
        {
            var turn = await _connection.SendRequestAsync(
                "turn/start",
                new
                {
                    threadId = _threadId,
                    input = new[] { new { type = "text", text = BuildTurnInput(messages) } },
                    model = provider.Model,
                    effort = provider.ReasoningEffort,
                    summary = "concise",
                    approvalPolicy = "untrusted"
                },
                cancellationToken).ConfigureAwait(false);
            if (turn.TryGetProperty("turn", out var turnValue))
            {
                _turnId = StringProperty(turnValue, "id");
            }

            return await WaitForOutcomeAsync(waiter, provider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LlmResponse> ContinueAfterToolAsync(
        ProviderSettings provider,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        if (tools is null || tools.Count == 0)
        {
            _toolsDisabled = true;
        }

        PendingDynamicToolCall pending;
        lock (_sync)
        {
            pending = _pendingTool ?? throw new InvalidOperationException(
                "Codex was asked to continue, but it is not waiting on a Lucky tool result.");
            _pendingTool = null;
        }

        var toolResult = messages.LastOrDefault(message =>
            string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(message.ToolCallId, pending.Call.Id, StringComparison.Ordinal));
        if (toolResult is null)
        {
            throw new InvalidOperationException($"Lucky did not receive a result for Codex tool call '{pending.Call.Name}'.");
        }

        var waiter = PrepareOutcomeWaiter();
        pending.Completion.TrySetResult(new
        {
            contentItems = new[] { new { type = "inputText", text = toolResult.Content } },
            success = true
        });
        try
        {
            return await WaitForOutcomeAsync(waiter, provider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LlmResponse> WaitForOutcomeAsync(
        TaskCompletionSource<CodexConversationOutcome> waiter,
        ProviderSettings provider,
        CancellationToken cancellationToken)
    {
        var outcome = await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var usage = ConsumeUsage();
        if (usage?.ContextWindowTokens is > 0)
        {
            provider.ContextWindowTokens = usage.ContextWindowTokens.Value;
        }

        var content = _content.ToString().Trim();
        var reasoning = _reasoning.Length == 0 ? null : _reasoning.ToString().Trim();
        _content.Clear();
        _reasoning.Clear();

        return outcome switch
        {
            CodexToolOutcome tool => new LlmResponse(
                content,
                _actualModel ?? provider.Model,
                [tool.Call],
                reasoning,
                usage),
            CodexCompletedOutcome => new LlmResponse(
                content,
                _actualModel ?? provider.Model,
                ToolCalls: [],
                ReasoningContent: reasoning,
                Usage: usage),
            CodexFailedOutcome failed => throw new InvalidOperationException(failed.Message),
            _ => throw new InvalidOperationException("Codex returned an unsupported turn result.")
        };
    }

    private Task<object?> HandleServerRequestAsync(CodexServerRequest request)
    {
        if (string.Equals(request.Method, "item/tool/call", StringComparison.Ordinal))
        {
            return HandleDynamicToolCallAsync(request.Parameters);
        }

        if (request.Method.EndsWith("/requestApproval", StringComparison.Ordinal))
        {
            if (request.Method.Contains("permissions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<object?>(new { permissions = new Dictionary<string, object?>() });
            }

            // Native Codex command/file tools are never granted. Lucky's selected-project tools
            // are the only ones registered dynamically and remain visible in its own trace.
            return Task.FromResult<object?>(new { decision = "decline" });
        }

        if (string.Equals(request.Method, "item/tool/requestUserInput", StringComparison.Ordinal))
        {
            return Task.FromResult<object?>(new { answers = new Dictionary<string, string>() });
        }

        return Task.FromResult<object?>(new { });
    }

    private Task<object?> HandleDynamicToolCallAsync(JsonElement parameters)
    {
        var name = StringProperty(parameters, "tool");
        var callId = StringProperty(parameters, "callId");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(callId))
        {
            return Task.FromResult<object?>(new
            {
                contentItems = new[] { new { type = "inputText", text = "Lucky could not parse this tool request." } },
                success = false
            });
        }

        if (_toolsDisabled)
        {
            return Task.FromResult<object?>(new
            {
                contentItems = new[]
                {
                    new { type = "inputText", text = "Lucky has stopped accepting tool calls for this response. Return a final answer from the completed work." }
                },
                success = false
            });
        }

        var arguments = parameters.TryGetProperty("arguments", out var value)
            ? value.GetRawText()
            : "{}";
        var pending = new PendingDynamicToolCall(new ToolCallRequest(callId, name, arguments));
        lock (_sync)
        {
            if (_pendingTool is not null)
            {
                return Task.FromResult<object?>(new
                {
                    contentItems = new[] { new { type = "inputText", text = "Lucky can process one tool call at a time." } },
                    success = false
                });
            }

            _pendingTool = pending;
            _outcome?.TrySetResult(new CodexToolOutcome(pending.Call));
        }

        return pending.Completion.Task;
    }

    private Task HandleNotificationAsync(
        string method,
        JsonElement parameters,
        IProgress<LlmStreamDelta>? streamProgress)
    {
        if (string.Equals(method, "item/agentMessage/delta", StringComparison.Ordinal))
        {
            var delta = StringProperty(parameters, "delta");
            if (!string.IsNullOrEmpty(delta))
            {
                lock (_sync)
                {
                    _content.Append(delta);
                }

                streamProgress?.Report(new LlmStreamDelta(delta));
            }

            return Task.CompletedTask;
        }

        if (method.Contains("reasoning", StringComparison.OrdinalIgnoreCase) &&
            parameters.TryGetProperty("delta", out var reasoningDelta) &&
            reasoningDelta.ValueKind == JsonValueKind.String)
        {
            var delta = reasoningDelta.GetString();
            if (!string.IsNullOrEmpty(delta))
            {
                lock (_sync)
                {
                    _reasoning.Append(delta);
                }

                streamProgress?.Report(new LlmStreamDelta("", delta));
            }

            return Task.CompletedTask;
        }

        if (string.Equals(method, "thread/tokenUsage/updated", StringComparison.Ordinal))
        {
            UpdateUsage(parameters);
            return Task.CompletedTask;
        }

        if (string.Equals(method, "model/rerouted", StringComparison.Ordinal))
        {
            _actualModel = StringProperty(parameters, "toModel") ?? _actualModel;
            return Task.CompletedTask;
        }

        if (string.Equals(method, "item/completed", StringComparison.Ordinal))
        {
            AppendCompletedItem(parameters);
            return Task.CompletedTask;
        }

        if (string.Equals(method, "turn/completed", StringComparison.Ordinal))
        {
            CompleteTurn(parameters);
        }

        return Task.CompletedTask;
    }

    private void AppendCompletedItem(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = StringProperty(item, "type");
        if (string.Equals(type, "agentMessage", StringComparison.Ordinal))
        {
            var text = StringProperty(item, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                lock (_sync)
                {
                    if (_content.Length == 0)
                    {
                        _content.Append(text);
                    }
                }
            }
        }
        else if (string.Equals(type, "reasoning", StringComparison.Ordinal))
        {
            var text = StringProperty(item, "summary") ?? StringProperty(item, "content");
            if (!string.IsNullOrWhiteSpace(text))
            {
                lock (_sync)
                {
                    if (_reasoning.Length == 0)
                    {
                        _reasoning.Append(text);
                    }
                }
            }
        }
    }

    private void CompleteTurn(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("turn", out var turn) || turn.ValueKind != JsonValueKind.Object)
        {
            SignalOutcome(new CodexFailedOutcome("Codex completed a turn without a result."));
            return;
        }

        var status = StringProperty(turn, "status");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var message = turn.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? StringProperty(error, "message")
                : null;
            SignalOutcome(new CodexFailedOutcome(message ?? "Codex could not complete the response."));
            return;
        }

        if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase))
        {
            SignalOutcome(new CodexFailedOutcome("The Codex turn was interrupted."));
            return;
        }

        SignalOutcome(new CodexCompletedOutcome());
    }

    private void UpdateUsage(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        lock (_sync)
        {
            if (usage.TryGetProperty("total", out var total))
            {
                _totalInputTokens = IntProperty(total, "inputTokens") ?? _totalInputTokens;
                _totalOutputTokens = IntProperty(total, "outputTokens") ?? _totalOutputTokens;
                _totalTokens = IntProperty(total, "totalTokens") ?? _totalTokens;
            }

            if (usage.TryGetProperty("last", out var last))
            {
                _latestContextTokens = IntProperty(last, "inputTokens") ?? _latestContextTokens;
            }

            _latestContextWindowTokens = IntProperty(usage, "modelContextWindow") ?? _latestContextWindowTokens;
        }
    }

    private LlmTokenUsage? ConsumeUsage()
    {
        lock (_sync)
        {
            if (_totalTokens is null && _totalInputTokens is null && _totalOutputTokens is null)
            {
                return null;
            }

            var input = Difference(_totalInputTokens, _consumedInputTokens);
            var output = Difference(_totalOutputTokens, _consumedOutputTokens);
            var total = Difference(_totalTokens, _consumedTotalTokens);
            _consumedInputTokens = _totalInputTokens;
            _consumedOutputTokens = _totalOutputTokens;
            _consumedTotalTokens = _totalTokens;
            return new LlmTokenUsage(input, output, total, _latestContextTokens, _latestContextWindowTokens);
        }
    }

    private static int? Difference(int? current, int? previous)
    {
        if (current is null)
        {
            return null;
        }

        return previous is null ? current : Math.Max(0, current.Value - previous.Value);
    }

    private TaskCompletionSource<CodexConversationOutcome> PrepareOutcomeWaiter()
    {
        lock (_sync)
        {
            _outcome = new TaskCompletionSource<CodexConversationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _outcome;
        }
    }

    private void SignalOutcome(CodexConversationOutcome outcome)
    {
        lock (_sync)
        {
            _outcome?.TrySetResult(outcome);
        }
    }

    private async Task InterruptAsync()
    {
        if (_connection is null || string.IsNullOrWhiteSpace(_threadId) || string.IsNullOrWhiteSpace(_turnId))
        {
            return;
        }

        try
        {
            await _connection.SendRequestAsync(
                "turn/interrupt",
                new { threadId = _threadId, turnId = _turnId },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The server may have already completed the turn while cancellation was propagating.
        }
    }

    private static string BuildDeveloperInstructions(IReadOnlyList<LlmChatMessage> messages)
    {
        var system = string.Join(
            Environment.NewLine + Environment.NewLine,
            messages.Where(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(message => message.Content.Trim())
                .Where(content => content.Length > 0));
        var boundary = """
            
            You are operating inside Lucky, a local-first Windows assistant. Lucky owns all
            workspace and side-effect access. You have no direct authorization to read, change,
            run, upload, or browse anything through native Codex tools. Use only the dynamic Lucky
            tools supplied for this turn when a tool is necessary. Native command, patch, file,
            browser, network, app, plugin, and skill actions will be denied. Keep tool use visible
            through Lucky and never claim a tool result that Lucky did not return.
            """;
        return string.IsNullOrWhiteSpace(system)
            ? boundary.Trim()
            : $"{system.Trim()}{Environment.NewLine}{Environment.NewLine}{boundary.Trim()}";
    }

    private static string BuildTurnInput(IReadOnlyList<LlmChatMessage> messages)
    {
        var conversation = messages
            .Where(message => !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Where(message => !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (conversation.Length == 0)
        {
            return "Respond to the user's request.";
        }

        var latestUserIndex = Array.FindLastIndex(conversation, message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        var builder = new StringBuilder();
        if (latestUserIndex > 0)
        {
            builder.AppendLine("Prior Lucky conversation (for context):");
            foreach (var message in conversation.Take(latestUserIndex))
            {
                builder.Append('[').Append(message.Role).AppendLine("]");
                builder.AppendLine(message.Content.Trim());
                builder.AppendLine();
            }
        }

        var latest = latestUserIndex >= 0 ? conversation[latestUserIndex] : conversation[^1];
        builder.AppendLine("Current user request:");
        builder.Append(latest.Content.Trim());
        return builder.ToString().Trim();
    }

    private static object[] BuildDynamicTools(IReadOnlyList<LlmToolDefinition>? tools) =>
        tools?.Select(tool => new
        {
            type = "function",
            name = tool.Name,
            description = tool.Description,
            inputSchema = BuildInputSchema(tool)
        }).Cast<object>().ToArray() ?? [];

    private static JsonElement BuildInputSchema(LlmToolDefinition tool)
    {
        if (tool.InputSchema is JsonElement inputSchema)
        {
            return inputSchema;
        }

        var properties = tool.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => (object)new
            {
                type = parameter.Value.Type,
                description = parameter.Value.Description
            });
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            required = tool.Required.ToArray(),
            additionalProperties = false
        });
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _pendingTool?.Completion.TrySetCanceled();
            _outcome?.TrySetCanceled();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _callGate.Dispose();
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? IntProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private sealed class PendingDynamicToolCall
    {
        public PendingDynamicToolCall(ToolCallRequest call)
        {
            Call = call;
            Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ToolCallRequest Call { get; }
        public TaskCompletionSource<object?> Completion { get; }
    }

    private abstract record CodexConversationOutcome;
    private sealed record CodexToolOutcome(ToolCallRequest Call) : CodexConversationOutcome;
    private sealed record CodexCompletedOutcome : CodexConversationOutcome;
    private sealed record CodexFailedOutcome(string Message) : CodexConversationOutcome;
}

internal static class CodexRuntimeDirectory
{
    public static string Ensure()
    {
        var root = Path.Combine(CodexProcessDataDirectory.Ensure(), "CodexRuntime");
        Directory.CreateDirectory(root);
        return root;
    }
}

internal static class CodexAppHomeDirectory
{
    private const string ManagedConfig = """
        # Managed by Lucky. Chat and workspace tools are supplied explicitly by Lucky.
        cli_auth_credentials_store = "file"
        web_search = "disabled"
        check_for_update_on_startup = false
        history.persistence = "none"
        allow_login_shell = false

        [features]
        shell_tool = false
        apps = false
        multi_agent = false
        hooks = false
        memories = false
        remote_plugin = false
        """;

    public static string Ensure()
    {
        // Lucky intentionally owns a separate Codex home. Never inherit the user's global
        // CODEX_HOME/~/.codex account, plugins, MCP servers, hooks, or project configuration.
        var root = Path.Combine(CodexProcessDataDirectory.Ensure(), "CodexHome");
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.toml");
        try
        {
            if (!File.Exists(configPath) ||
                !string.Equals(File.ReadAllText(configPath), ManagedConfig, StringComparison.Ordinal))
            {
                File.WriteAllText(configPath, ManagedConfig);
            }

        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Lucky could not prepare its private Codex configuration: {ex.Message}", ex);
        }

        return root;
    }

    private static readonly byte[] AuthEntropy = Encoding.UTF8.GetBytes("Lucky.CodexOAuth.v1");
    private static readonly SemaphoreSlim AuthGate = new(1, 1);

    public static async Task<IAsyncDisposable> MaterializeAuthForProcessAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lucky protects ChatGPT credentials with Windows DPAPI.");
        }

        await AuthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = Ensure();
            var authPath = Path.Combine(root, "auth.json");
            var protectedPath = Path.Combine(root, "auth.json.dpapi");
            if (!File.Exists(authPath) && File.Exists(protectedPath))
            {
                var protectedBytes = await File.ReadAllBytesAsync(protectedPath, cancellationToken).ConfigureAwait(false);
                var clearBytes = ProtectedData.Unprotect(protectedBytes, AuthEntropy, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(authPath, clearBytes, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(clearBytes);
            }

            return new AuthMaterializationLease();
        }
        catch
        {
            AuthGate.Release();
            throw;
        }
    }

    public static async Task ProtectAuthFileAsync(CancellationToken cancellationToken = default)
    {
        await AuthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProtectAuthFileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AuthGate.Release();
        }
    }

    public static async Task ForgetAuthAsync(CancellationToken cancellationToken = default)
    {
        await AuthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = Ensure();
            File.Delete(Path.Combine(root, "auth.json"));
            File.Delete(Path.Combine(root, "auth.json.dpapi"));
        }
        finally
        {
            AuthGate.Release();
        }
    }

    private static async Task ProtectAuthFileCoreAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lucky protects ChatGPT credentials with Windows DPAPI.");
        }

        var root = Ensure();
        var authPath = Path.Combine(root, "auth.json");
        if (!File.Exists(authPath))
        {
            return;
        }

        var clearBytes = await File.ReadAllBytesAsync(authPath, cancellationToken).ConfigureAwait(false);
        try
        {
            var protectedBytes = ProtectedData.Protect(clearBytes, AuthEntropy, DataProtectionScope.CurrentUser);
            var protectedPath = Path.Combine(root, "auth.json.dpapi");
            var temporaryPath = protectedPath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, protectedPath, overwrite: true);
            File.Delete(authPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private sealed class AuthMaterializationLease : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await ProtectAuthFileCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                AuthGate.Release();
            }
        }
    }
}

internal static class CodexProcessDataDirectory
{
    private const int ErrorInsufficientBuffer = 122;

    public static string Ensure()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageFamily = TryGetCurrentPackageFamilyName();
        var root = string.IsNullOrWhiteSpace(packageFamily)
            ? Path.Combine(localAppData, "Lucky")
            : Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "Lucky");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? TryGetCurrentPackageFamilyName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        uint length = 0;
        var first = GetCurrentPackageFamilyName(ref length, null);
        if (first != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var value = new StringBuilder((int)length);
        return GetCurrentPackageFamilyName(ref length, value) == 0
            ? value.ToString()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}

internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new(StringComparer.Ordinal);
    private readonly Task _reader;
    private readonly Task _errorReader;
    private readonly StringBuilder _errors = new();
    private long _nextRequestId;
    private int _closed;
    private int _disposeStarted;

    private CodexAppServerConnection(Process process)
    {
        _process = process;
        _input = process.StandardInput;
        _input.AutoFlush = true;
        _reader = ReadLoopAsync();
        _errorReader = ReadErrorLoopAsync();
    }

    public Func<CodexServerRequest, Task<object?>>? RequestHandler { get; set; }
    public Func<string, JsonElement, Task>? NotificationReceived { get; set; }
    public Action<Exception?>? Closed { get; set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0 || _process.HasExited;

    public static async Task<CodexAppServerConnection> StartAsync(CancellationToken cancellationToken)
    {
        await using var authLease = await CodexAppHomeDirectory
            .MaterializeAuthForProcessAsync(cancellationToken)
            .ConfigureAwait(false);
        var executable = CodexExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException(
                "Lucky could not find the official Codex CLI. Install Codex, then return here to connect your ChatGPT subscription.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = CodexRuntimeDirectory.Ensure(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        startInfo.Environment["CODEX_HOME"] = CodexAppHomeDirectory.Ensure();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The Codex app-server did not start.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Lucky could not start the Codex app-server: {ex.Message}", ex);
        }

        var connection = new CodexAppServerConnection(process);
        try
        {
            await connection.SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new { name = "lucky", title = "Lucky", version = "0.1" },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken).ConfigureAwait(false);
            await connection.SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // Release and re-protect the startup credential before disposing the connection;
            // connection disposal also performs a final protection pass for refreshed tokens.
            await authLease.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var id = Interlocked.Increment(ref _nextRequestId);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(key, completion))
        {
            throw new InvalidOperationException("Lucky could not reserve a Codex JSON-RPC request id.");
        }

        try
        {
            await SendMessageAsync(new Dictionary<string, object?>
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = parameters
            }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(key, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        SendMessageAsync(new Dictionary<string, object?>
        {
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken);

    private async Task SendResponseAsync(JsonElement id, object? result)
    {
        await SendMessageAsync(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["result"] = result
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            var json = JsonSerializer.Serialize(message);
            await _input.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
                {
                    var method = methodElement.GetString() ?? "";
                    var parameters = root.TryGetProperty("params", out var paramsElement)
                        ? paramsElement.Clone()
                        : JsonSerializer.SerializeToElement(new { });
                    if (root.TryGetProperty("id", out var requestId))
                    {
                        _ = RespondToServerRequestAsync(new CodexServerRequest(method, requestId.Clone(), parameters));
                    }
                    else if (NotificationReceived is { } notification)
                    {
                        try
                        {
                            await notification(method, parameters).ConfigureAwait(false);
                        }
                        catch
                        {
                            // A display/stream callback must never stop the local RPC transport.
                        }
                    }

                    continue;
                }

                if (!root.TryGetProperty("id", out var responseId))
                {
                    continue;
                }

                var key = ResponseIdKey(responseId);
                if (!_requests.TryGetValue(key, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException(FormatRpcError(error)));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException("Codex returned a JSON-RPC response without a result."));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            failure = ex;
        }
        finally
        {
            CompleteClosed(failure);
        }
    }

    private async Task RespondToServerRequestAsync(CodexServerRequest request)
    {
        object? result;
        try
        {
            result = RequestHandler is null
                ? new { }
                : await RequestHandler(request).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new { decision = "cancel" };
        }
        catch (Exception ex)
        {
            result = new { decision = "decline", error = ex.Message };
        }

        try
        {
            await SendResponseAsync(request.Id, result).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The server can close after cancelling a turn before the response is written.
        }
    }

    private async Task ReadErrorLoopAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lock (_errors)
                {
                    if (_errors.Length > 6000)
                    {
                        _errors.Remove(0, _errors.Length - 4000);
                    }

                    _errors.AppendLine(line);
                }
            }
        }
        catch (IOException)
        {
            // Process shutdown closes this stream routinely.
        }
    }

    private void CompleteClosed(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        var details = ErrorDetails();
        var closed = failure ?? new InvalidOperationException(string.IsNullOrWhiteSpace(details)
            ? "The local Codex app-server ended."
            : $"The local Codex app-server ended: {details}");
        foreach (var request in _requests.Values)
        {
            request.TrySetException(closed);
        }

        Closed?.Invoke(closed);
    }

    private string ErrorDetails()
    {
        lock (_errors)
        {
            var text = _errors.ToString().Trim();
            return text.Length <= 900 ? text : text[^900..];
        }
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            var details = ErrorDetails();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? "The local Codex app-server is not running."
                : $"The local Codex app-server is not running: {details}");
        }
    }

    private static string ResponseIdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt64(out var numeric) => numeric.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.String => id.GetString() ?? "",
        _ => id.GetRawText()
    };

    private static string FormatRpcError(JsonElement error)
    {
        var message = error.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : error.GetRawText();
        return string.IsNullOrWhiteSpace(message) ? "Codex returned an unknown JSON-RPC error." : message;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            var closing = new ObjectDisposedException(nameof(CodexAppServerConnection));
            foreach (var request in _requests.Values)
            {
                request.TrySetException(closing);
            }

            Closed?.Invoke(closing);
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process completed between the state check and Kill.
        }

        await Task.WhenAll(IgnoreFailure(_reader), IgnoreFailure(_errorReader)).ConfigureAwait(false);
        await CodexAppHomeDirectory.ProtectAuthFileAsync().ConfigureAwait(false);
        _input.Dispose();
        _process.Dispose();
        _writeGate.Dispose();
    }

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Connection disposal deliberately terminates the local helper.
        }
    }
}

internal sealed record CodexServerRequest(string Method, JsonElement Id, JsonElement Parameters);

internal static class CodexExecutableLocator
{
    public static string? Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "codex";
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Codex", "codex.exe"),
            Path.Combine(localAppData, "Codex", "codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Codex", "codex.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // npm's Windows shim is a .cmd/.ps1 script, which cannot be launched as a direct stdio
        // child. Prefer the platform binary bundled by the official @openai/codex package.
        var npmVendor = Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor");
        string? bundled = null;
        try
        {
            bundled = Directory.Exists(npmVendor)
                ? Directory.EnumerateFiles(npmVendor, "codex.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (IOException)
        {
            bundled = null;
        }
        catch (UnauthorizedAccessException)
        {
            bundled = null;
        }

        if (!string.IsNullOrWhiteSpace(bundled))
        {
            return bundled;
        }

        return FindOnPath("codex.exe");
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH segments and continue searching known locations.
            }
        }

        return null;
    }
}
