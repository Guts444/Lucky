using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lucky.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Lucky_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly LuckyStore _store;
    private readonly AgentRunner _agentRunner;
    private readonly LuckyLlmClient _modelClient;
    private LuckyState _state = new();
    private bool _isHydratingSettings;
    private CancellationTokenSource? _activeTurnCancellation;

    public MainPageViewModel()
    {
        _store = new LuckyStore();
        _modelClient = new LuckyLlmClient();
        _agentRunner = new AgentRunner(_modelClient);
        (ProfileDisplayName, ProfileInitials) = BuildLocalProfileLabels();
    }

    public MainPageViewModel(LuckyStore store, AgentRunner agentRunner, LuckyLlmClient modelClient)
    {
        _store = store;
        _agentRunner = agentRunner;
        _modelClient = modelClient;
        (ProfileDisplayName, ProfileInitials) = BuildLocalProfileLabels();
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<MemoryItemViewModel> Memories { get; } = [];
    public ObservableCollection<ToolTraceItemViewModel> Trace { get; } = [];
    public ObservableCollection<string> AccessLevels { get; } = ["Chat only", "Workspace", "Full access"];
    public ObservableCollection<string> ProviderSetupOptions { get; } =
        ["DeepSeek API", "OpenRouter", "LM Studio", "Custom OpenAI-compatible"];
    public ObservableCollection<ModelOptionViewModel> ModelOptions { get; } = [];
    public ObservableCollection<string> ModelCatalog { get; } = [];
    public ObservableCollection<McpServerItemViewModel> McpServers { get; } = [];

    /// <summary>Windows account name shown on the local profile row (not a cloud identity).</summary>
    public string ProfileDisplayName { get; }

    /// <summary>Short initials for the local profile avatar badge.</summary>
    public string ProfileInitials { get; }

    [ObservableProperty]
    public partial ProjectItemViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial SessionItemViewModel? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string MessageInput { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsInitialized { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial Visibility ChatVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility SettingsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility EmptyStateVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility MessageListVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string Status { get; set; } = "Loading Lucky...";

    [ObservableProperty]
    public partial string Persona { get; set; } = "";

    [ObservableProperty]
    public partial string ProviderEndpoint { get; set; } = "";

    [ObservableProperty]
    public partial string ApiKeyInput { get; set; } = "";

    [ObservableProperty]
    public partial Visibility ProviderEndpointVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility ApiKeyVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility CodexConnectionVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string CodexConnectionStatus { get; set; } = "Connect your ChatGPT account to use the Codex models included with your plan.";

    [ObservableProperty]
    public partial bool IsCodexSignInInProgress { get; set; }

    [ObservableProperty]
    public partial bool IsCodexConnected { get; set; }

    [ObservableProperty]
    public partial string SelectedProviderSetup { get; set; } = "DeepSeek API";

    [ObservableProperty]
    public partial bool IsProviderEndpointEditable { get; set; }

    [ObservableProperty]
    public partial string SelectedAccessLevel { get; set; } = "Workspace";

    [ObservableProperty]
    public partial string SearxngUrl { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoWebSearch { get; set; }

    [ObservableProperty]
    public partial bool BrowserEnabled { get; set; }

    [ObservableProperty]
    public partial string BrowserAllowedDomains { get; set; } = "";

    [ObservableProperty]
    public partial bool McpEnabled { get; set; }

    [ObservableProperty]
    public partial string McpServerName { get; set; } = "";

    [ObservableProperty]
    public partial string McpServerCommand { get; set; } = "";

    [ObservableProperty]
    public partial string McpServerArguments { get; set; } = "";

    [ObservableProperty]
    public partial string McpServerWorkingDirectory { get; set; } = "";

    [ObservableProperty]
    public partial bool SandboxEnabled { get; set; }

    [ObservableProperty]
    public partial string SandboxImage { get; set; } = "";

    [ObservableProperty]
    public partial int SandboxTimeoutSeconds { get; set; } = 60;

    [ObservableProperty]
    public partial int SandboxMemoryMiB { get; set; } = 512;

    [ObservableProperty]
    public partial double SandboxCpuLimit { get; set; } = 1.0;

    [ObservableProperty]
    public partial int SandboxPidsLimit { get; set; } = 128;

    [ObservableProperty]
    public partial int SandboxScratchMiB { get; set; } = 128;

    [ObservableProperty]
    public partial bool MemoriesEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SubagentsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoDelegateEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int MaxParallelSubagents { get; set; } = 3;

    [ObservableProperty]
    public partial int MaxSubagentsPerTurn { get; set; } = 3;

    [ObservableProperty]
    public partial ModelOptionViewModel? SelectedModelOption { get; set; }

    [ObservableProperty]
    public partial int ContextWindowTokens { get; set; } = 32768;

    [ObservableProperty]
    public partial bool IsContextWindowEditable { get; set; } = true;

    [ObservableProperty]
    public partial string WorkspaceHint { get; set; } = "Choose a folder to start project-scoped chats.";

    [ObservableProperty]
    public partial string ChatHeading { get; set; } = "What should we work on?";

    [ObservableProperty]
    public partial string ChatSubheading { get; set; } = "Choose a folder, start a chat, and Lucky will keep the thread under that project.";

    [ObservableProperty]
    public partial string ContextUsageLabel { get; set; } = "0/32K";

    [ObservableProperty]
    public partial string ContextUsagePercentText { get; set; } = "0%";

    [ObservableProperty]
    public partial double ContextUsagePercent { get; set; }

    [ObservableProperty]
    public partial string MemoryUsageLabel { get; set; } = "Memory 0/2200";

    [ObservableProperty]
    public partial string UserProfileUsageLabel { get; set; } = "User 0/1375";

    private static (string DisplayName, string Initials) BuildLocalProfileLabels()
    {
        var userName = Environment.UserName?.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return ("Local user", "LU");
        }

        var parts = userName.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = parts.Length >= 2
            ? string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]))
            : userName.Length >= 2
                ? userName[..2].ToUpperInvariant()
                : userName.ToUpperInvariant();

        return (userName, initials);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            return;
        }

        _state = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(_state.Settings.OpenAiCodex.ConnectedAccountPlan))
        {
            await RefreshCodexAccountStatusAsync(cancellationToken).ConfigureAwait(true);
        }
        HydrateSettings();
        RefreshModelOptions();
        ReloadProjects();
        ReloadMemories();
        ReloadMcpServers();

        var selectedProject = Projects.FirstOrDefault(project => project.Id == _state.Settings.SelectedProjectId)
            ?? Projects.FirstOrDefault();
        SelectedProject = selectedProject;

        if (SelectedProject is null)
        {
            Status = "Choose a project folder to begin.";
        }

        IsInitialized = true;
        RefreshDerivedState();
    }

    public async Task AddProjectAsync(string folderPath)
    {
        var project = LuckyStore.EnsureProject(_state, folderPath);
        await _store.SaveAsync(_state).ConfigureAwait(true);
        ReloadProjects();
        SelectedProject = Projects.FirstOrDefault(item => item.Id == project.Id);
        Status = $"Opened {project.Name}.";
    }

    [RelayCommand]
    private async Task NewChatAsync()
    {
        var project = CurrentProject();
        if (project is null)
        {
            Status = "Choose a folder before starting a chat.";
            return;
        }

        var session = LuckyStore.CreateSession(project.Id);
        _state.Sessions.Insert(0, session);
        await _store.SaveAsync(_state).ConfigureAwait(true);
        ReloadSessions(project.Id);
        SelectedSession = Sessions.FirstOrDefault(item => item.Id == session.Id);
        MessageInput = "";
        Trace.Clear();
        Status = "Started a new chat.";
        RefreshDerivedState();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = MessageInput.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var project = CurrentProject();
        if (project is null)
        {
            Status = "Choose a folder before sending a message.";
            return;
        }

        var session = CurrentSession(project.Id);
        if (session is null)
        {
            session = LuckyStore.CreateSession(project.Id);
            _state.Sessions.Insert(0, session);
            ReloadSessions(project.Id);
            SelectedSession = Sessions.FirstOrDefault(item => item.Id == session.Id);
        }

        MessageInput = "";
        var userMessage = new ChatMessage { Role = ChatRole.User, Content = text };
        session.Messages.Add(userMessage);
        TouchSession(session, text);
        Messages.Add(MessageItemViewModel.From(userMessage));
        // Persist the user's turn before any provider/network work. A crash or malformed
        // response must not silently lose the prompt the user already sent.
        await _store.SaveAsync(_state).ConfigureAwait(true);
        var assistantItem = MessageItemViewModel.PendingAssistant();
        assistantItem.IsThinking = true;
        assistantItem.ThinkingVisibility = Visibility.Visible;
        assistantItem.ThinkingText = "Preparing request...";
        Messages.Add(assistantItem);
        RefreshDerivedState();
        Trace.Clear();
        IsBusy = true;
        Status = "Lucky is thinking...";
        using var turnCancellation = new CancellationTokenSource();
        _activeTurnCancellation = turnCancellation;
        var turnProvider = _state.Settings.ActiveProvider;
        var turnModel = _state.Settings.ActiveProviderSettings.Model;

        try
        {
            var progress = new Progress<AgentProgressEvent>(progressEvent =>
            {
                if (progressEvent.Stage == "answer" && progressEvent.Detail is not null)
                {
                    assistantItem.Content += progressEvent.Detail;
                    Status = progressEvent.Summary;
                    return;
                }

                assistantItem.IsThinking = true;
                assistantItem.ThinkingVisibility = Visibility.Visible;
                assistantItem.AppendThinking(progressEvent);
                Status = progressEvent.Summary;
            });
            var result = await _agentRunner.RunTurnAsync(
                _state,
                project,
                session,
                text,
                turnCancellation.Token,
                progress).ConfigureAwait(true);
            foreach (var trace in result.Trace)
            {
                Trace.Add(ToolTraceItemViewModel.From(trace));
            }

            assistantItem.IsThinking = false;
            assistantItem.ThinkingText = RenderThinkingText(assistantItem.ThinkingText, result.Trace, result.ReasoningContent);
            assistantItem.ThinkingVisibility = string.IsNullOrWhiteSpace(assistantItem.ThinkingText)
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (string.IsNullOrEmpty(assistantItem.Content))
            {
                await RevealTextAsync(assistantItem, result.AssistantMessage).ConfigureAwait(true);
            }
            else
            {
                assistantItem.Content = result.AssistantMessage;
            }

            var assistant = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = result.AssistantMessage,
                Trace = assistantItem.ThinkingText,
                PromptTokens = result.TokenUsage?.PromptTokens,
                CompletionTokens = result.TokenUsage?.CompletionTokens,
                TotalTokens = result.TokenUsage?.TotalTokens,
                ContextTokens = result.TokenUsage?.ContextTokens,
                ContextWindowTokens = result.TokenUsage?.ContextWindowTokens,
                ProviderKind = turnProvider,
                ModelId = result.Model
            };
            session.Messages.Add(assistant);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            ReloadMemories();
            await _store.SaveAsync(_state).ConfigureAwait(true);
            RefreshDerivedState();

            if (result.UsedModel)
            {
                Status = _state.Settings.MemoriesEnabled
                    ? $"Answered using {MemoryCountLabel(result.RecalledMemories.Count)}."
                    : "Answered with memories disabled.";
            }
            else
            {
                Status = "Answered without a model call.";
            }
        }
        catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested)
        {
            assistantItem.IsThinking = false;
            assistantItem.ThinkingText = RenderThinkingText(
                assistantItem.ThinkingText,
                [],
                reasoningContent: null);
            assistantItem.ThinkingVisibility = string.IsNullOrWhiteSpace(assistantItem.ThinkingText)
                ? Visibility.Collapsed
                : Visibility.Visible;
            assistantItem.Content = string.IsNullOrWhiteSpace(assistantItem.Content)
                ? "Stopped."
                : $"{assistantItem.Content.TrimEnd()}{Environment.NewLine}{Environment.NewLine}Stopped.";

            session.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = assistantItem.Content,
                Trace = assistantItem.ThinkingText
            });
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(_state).ConfigureAwait(true);
            RefreshDerivedState();
            Status = "Stopped the current turn.";
        }
        catch (Exception ex)
        {
            assistantItem.IsThinking = false;
            assistantItem.ThinkingText = RenderThinkingText(
                assistantItem.ThinkingText,
                [],
                reasoningContent: null);
            assistantItem.ThinkingVisibility = string.IsNullOrWhiteSpace(assistantItem.ThinkingText)
                ? Visibility.Collapsed
                : Visibility.Visible;
            assistantItem.Content = $"I couldn't finish that turn: {ex.Message}";
            session.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = assistantItem.Content,
                Trace = assistantItem.ThinkingText,
                ProviderKind = turnProvider,
                ModelId = turnModel
            });
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(_state).ConfigureAwait(true);
            RefreshDerivedState();
            Status = "The turn failed. Check the provider settings and try again.";
        }
        finally
        {
            if (ReferenceEquals(_activeTurnCancellation, turnCancellation))
            {
                _activeTurnCancellation = null;
            }

            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_activeTurnCancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        Status = "Stopping the current turn...";
        cancellation.Cancel();
    }

    private bool CanStop() => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static async Task RevealTextAsync(MessageItemViewModel message, string content)
    {
        message.Content = "";
        foreach (var segment in RevealSegments(content))
        {
            message.Content += segment;
            await Task.Delay(18).ConfigureAwait(true);
        }
    }

    private static string MemoryCountLabel(int count) => count == 1
        ? "1 memory"
        : $"{count} memories";

    private static IEnumerable<string> RevealSegments(string content)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var rune in content.EnumerateRunes())
        {
            builder.Append(rune);
            if (Rune.IsWhiteSpace(rune) || builder.Length >= 14)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string RenderThinkingText(string current, IReadOnlyList<ToolTraceEntry> trace, string? reasoningContent)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            lines.Add(reasoningContent.Trim());
        }
        else
        {
            lines.AddRange(
                current.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.Equals("Preparing request...", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.Equals("Thinking", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.Equals("Thinking...", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var entry in trace)
        {
            if (entry.Tool == "llm")
            {
                continue;
            }

            lines.Add(FormatToolTraceLine(entry));
        }

        return string.Join(Environment.NewLine, lines.Distinct());
    }

    public static string FormatToolTraceLine(ToolTraceEntry entry)
    {
        var marker = entry.IsError ? "failed" : "done";
        return $"{entry.Tool} {marker}: {SummarizeToolOutput(entry)}";
    }

    public static string SummarizeToolOutput(ToolTraceEntry entry)
    {
        if (entry.Tool == "project.read_file" && !entry.IsError)
        {
            var preview = FirstNonEmptyLine(entry.Output);
            return string.IsNullOrWhiteSpace(preview)
                ? $"Read {entry.Input} ({entry.Output.Length:N0} characters)."
                : $"Read {entry.Input} ({entry.Output.Length:N0} characters). Preview: {TrimForTrace(preview, 180)}";
        }

        return TrimForTrace(CollapseWhitespace(entry.Output), 900);
    }

    private static string FirstNonEmptyLine(string text)
    {
        return text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "";
    }

    private static string CollapseWhitespace(string text)
    {
        return string.Join(
            Environment.NewLine,
            text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()));
    }

    private static string TrimForTrace(string text, int maxLength)
    {
        return text.Length <= maxLength
            ? text
            : $"{text[..maxLength]}... ({text.Length:N0} chars total)";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!IsSandboxImageValueValid(SandboxImage))
        {
            Status = "Sandbox image names can contain letters, digits, '.', '_', '-', '/', ':', and '@'.";
            return;
        }

        if (ConfiguredProviderKind() != LlmProviderKind.DeepSeek &&
            !IsSafeProviderEndpoint(ProviderEndpoint, ConfiguredProviderKind()))
        {
            Status = "Use HTTPS for remote providers. Plain HTTP is allowed only for loopback local endpoints.";
            return;
        }

        ApplySettings();
        await _store.SaveAsync(_state).ConfigureAwait(true);
        ApiKeyInput = "";
        Status = "Settings saved.";
        RefreshModelOptions();
        RefreshDerivedState();
    }

    [RelayCommand]
    private void AddMcpServer()
    {
        var command = McpServerCommand.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            Status = "Enter an MCP server command before adding it.";
            return;
        }

        if (command.Contains('\r') || command.Contains('\n'))
        {
            Status = "An MCP command cannot contain a line break.";
            return;
        }

        IReadOnlyList<string> arguments;
        try
        {
            arguments = CommandLineArgumentParser.Parse(McpServerArguments);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }

        var name = string.IsNullOrWhiteSpace(McpServerName)
            ? Path.GetFileNameWithoutExtension(command)
            : McpServerName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "MCP server";
        }

        _state.Settings.Mcp.Servers.Add(new McpServerDefinition
        {
            Name = name,
            Command = command,
            Arguments = arguments.ToList(),
            WorkingDirectory = string.IsNullOrWhiteSpace(McpServerWorkingDirectory)
                ? null
                : McpServerWorkingDirectory.Trim()
        });
        ReloadMcpServers();
        McpServerName = "";
        McpServerCommand = "";
        McpServerArguments = "";
        McpServerWorkingDirectory = "";
        Status = $"Added MCP server '{name}'. Save Settings to persist it.";
    }

    [RelayCommand]
    private void RemoveMcpServer(McpServerItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var server = _state.Settings.Mcp.Servers.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (server is null)
        {
            return;
        }

        _state.Settings.Mcp.Servers.Remove(server);
        ReloadMcpServers();
        Status = $"Removed MCP server '{item.Name}'. Save Settings to persist it.";
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        RefreshModeVisibility();
        Status = "Settings opened.";
        await RefreshCodexAccountStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BackToApp()
    {
        IsSettingsOpen = false;
        RefreshModeVisibility();
        RefreshDerivedState();
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        ApplySettings();
        var provider = ConfiguredProvider();
        var providerKind = ConfiguredProviderKind();

        var apiKey = CredentialProtector.Unprotect(provider.EncryptedApiKey);
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            Status = $"{provider.DisplayName} needs an API key before refreshing models.";
            return;
        }

        IsBusy = true;
        ModelCatalog.Clear();
        try
        {
            var models = await _modelClient.ListModelsAsync(provider, apiKey).ConfigureAwait(true);
            foreach (var model in models)
            {
                ModelCatalog.Add(model);
            }

            provider.ModelCapabilities = models.Select(model => new ProviderModelCapability
            {
                Id = model,
                DisplayName = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model,
                Description = $"{provider.DisplayName} model",
                ReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
                ContextWindowTokens = provider.ContextWindowTokens
            }).ToList();
            if (models.Count > 0 && !models.Contains(provider.Model, StringComparer.OrdinalIgnoreCase) &&
                providerKind is not (LlmProviderKind.DeepSeek or LlmProviderKind.OpenRouter))
            {
                provider.Model = models[0];
            }

            if (providerKind == LlmProviderKind.OpenRouter &&
                models.Count > 0 &&
                !models.Contains(provider.Model, StringComparer.OrdinalIgnoreCase))
            {
                // Prefer a known seed default when the saved model is no longer listed.
                provider.Model = models.Contains("openai/gpt-4o-mini", StringComparer.OrdinalIgnoreCase)
                    ? "openai/gpt-4o-mini"
                    : models[0];
            }

            RefreshModelOptions();
            HydrateProviderFields(provider);
            await _store.SaveAsync(_state).ConfigureAwait(true);

            Status = models.Count == 0
                ? "Connection ok, but no models were returned."
                : $"Connection ok. Loaded {models.Count} model(s), context {CompactNumber(provider.ContextWindowTokens)}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Status = $"Could not refresh models: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<CodexLoginStart?> StartCodexSignInAsync(CancellationToken cancellationToken = default)
    {
        if (IsCodexSignInInProgress)
        {
            return null;
        }

        IsCodexSignInInProgress = true;
        CodexConnectionStatus = "Opening the official ChatGPT sign-in page...";
        try
        {
            return await _modelClient.StartLoginAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            IsCodexSignInInProgress = false;
            CodexConnectionStatus = $"Could not start ChatGPT sign-in: {ex.Message}";
            Status = CodexConnectionStatus;
            return null;
        }
    }

    public async Task FinishCodexSignInAsync(CodexLoginStart login, CancellationToken cancellationToken = default)
    {
        try
        {
            CodexConnectionStatus = "Waiting for ChatGPT sign-in to finish...";
            var account = await _modelClient.WaitForLoginAsync(login.LoginId, cancellationToken).ConfigureAwait(true);
            var provider = _state.Settings.OpenAiCodex;
            provider.ConnectedAccountPlan = account.Plan;
            IsCodexConnected = account.IsChatGptConnected;
            CodexConnectionStatus = account.Detail;
            await RefreshCodexModelsAsync(saveState: true).ConfigureAwait(true);
            Status = $"{account.Detail} Codex models are ready.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CodexConnectionStatus = "ChatGPT sign-in was cancelled.";
            Status = CodexConnectionStatus;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            CodexConnectionStatus = $"ChatGPT sign-in did not complete: {ex.Message}";
            Status = CodexConnectionStatus;
        }
        finally
        {
            IsCodexSignInInProgress = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectCodexAsync()
    {
        if (!IsCodexConnected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var account = await _modelClient.LogoutAsync().ConfigureAwait(true);
            var provider = _state.Settings.OpenAiCodex;
            provider.ConnectedAccountPlan = null;
            provider.ModelCapabilities.Clear();
            if (_state.Settings.ActiveProvider == LlmProviderKind.OpenAiCodex)
            {
                _state.Settings.ActiveProvider = LlmProviderKind.DeepSeek;
            }
            IsCodexConnected = account.IsChatGptConnected;
            CodexConnectionStatus = "Disconnected. Lucky's ChatGPT sign-in is separate from Codex and other apps.";
            RefreshModelOptions();
            await _store.SaveAsync(_state).ConfigureAwait(true);
            Status = "Disconnected ChatGPT from Lucky.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or IOException)
        {
            Status = $"Could not disconnect ChatGPT: {ex.Message}";
            CodexConnectionStatus = Status;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshCodexSubscriptionAsync() => RefreshCodexModelsAsync(saveState: true);

    private async Task RefreshCodexAccountStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await _modelClient.GetAccountStatusAsync(cancellationToken).ConfigureAwait(true);
            IsCodexConnected = account.IsChatGptConnected;
            var provider = _state.Settings.OpenAiCodex;
            provider.ConnectedAccountPlan = account.IsChatGptConnected ? account.Plan : null;
            CodexConnectionStatus = account.IsChatGptConnected
                ? $"{account.Detail} Credentials are protected with Windows DPAPI and isolated from Codex CLI."
                : "Not connected. Sign in in your browser; Lucky keeps this account separate from Codex and other apps.";
            if (!account.IsChatGptConnected)
            {
                provider.ModelCapabilities.Clear();
                if (_state.Settings.ActiveProvider == LlmProviderKind.OpenAiCodex)
                {
                    _state.Settings.ActiveProvider = LlmProviderKind.DeepSeek;
                }
            }

            RefreshModelOptions();
            await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or IOException)
        {
            IsCodexConnected = false;
            _state.Settings.OpenAiCodex.ConnectedAccountPlan = null;
            CodexConnectionStatus = $"ChatGPT connection unavailable: {ex.Message}";
        }
    }

    private async Task RefreshCodexModelsAsync(bool saveState)
    {
        IsBusy = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var account = await _modelClient.GetAccountStatusAsync(timeout.Token).ConfigureAwait(true);
            var provider = _state.Settings.OpenAiCodex;
            provider.ConnectedAccountPlan = account.Plan;
            IsCodexConnected = account.IsChatGptConnected;
            CodexConnectionStatus = account.Detail;
            if (!account.IsChatGptConnected)
            {
                provider.ModelCapabilities.Clear();
                if (_state.Settings.ActiveProvider == LlmProviderKind.OpenAiCodex)
                {
                    _state.Settings.ActiveProvider = LlmProviderKind.DeepSeek;
                }

                RefreshModelOptions();
                HydrateProviderFields(_state.Settings.ActiveProviderSettings);
                RefreshProviderConfigurationVisibility();
                if (saveState)
                {
                    await _store.SaveAsync(_state, timeout.Token).ConfigureAwait(true);
                }

                Status = "Connect ChatGPT in Settings before loading Codex subscription models.";
                return;
            }

            var capabilities = await _modelClient.GetModelCapabilitiesAsync(timeout.Token).ConfigureAwait(true);
            var refreshedCapabilities = capabilities.Select(CloneModelCapability).ToList();
            if (refreshedCapabilities.Count == 0)
            {
                Status = provider.ModelCapabilities.Count == 0
                    ? "ChatGPT is connected, but Codex did not return any models for this account."
                    : "Codex returned an empty model catalog; Lucky kept the last working catalog.";
                return;
            }

            provider.ModelCapabilities = refreshedCapabilities;

            var selected = provider.ModelCapabilities.FirstOrDefault(model =>
                string.Equals(model.Id, provider.Model, StringComparison.OrdinalIgnoreCase))
                ?? provider.ModelCapabilities.FirstOrDefault(model => model.IsDefault)
                ?? provider.ModelCapabilities[0];
            provider.Model = selected.Id;
            provider.ContextWindowTokens = selected.ContextWindowTokens;
            if (!selected.ReasoningEfforts.Contains(provider.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
            {
                provider.ReasoningEffort = selected.DefaultReasoningEffort;
            }

            RefreshModelOptions();
            HydrateProviderFields(_state.Settings.ActiveProviderSettings);
            RefreshProviderConfigurationVisibility();
            if (saveState)
            {
                await _store.SaveAsync(_state).ConfigureAwait(true);
            }

            CodexConnectionStatus = $"{account.Detail} Loaded {provider.ModelCapabilities.Count} model(s) advertised for this Lucky sign-in.";
            Status = $"Loaded {provider.ModelCapabilities.Count} Codex model(s), context {CompactNumber(provider.ContextWindowTokens)}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or IOException)
        {
            Status = ex is TaskCanceledException
                ? "Could not refresh Codex models within 30 seconds. The previous catalog was kept."
                : "Could not refresh Codex models. The previous catalog was kept; reconnect the account if this continues.";
            CodexConnectionStatus = Status;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisableSelectedMemoryAsync()
    {
        var enabled = _state.Memories.Where(memory => memory.Enabled).OrderByDescending(memory => memory.UpdatedAt).FirstOrDefault();
        if (enabled is null)
        {
            return;
        }

        enabled.Enabled = false;
        enabled.UpdatedAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync(_state).ConfigureAwait(true);
        ReloadMemories();
        Status = "Disabled the latest memory.";
        RefreshDerivedState();
    }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        if (value is null)
        {
            Sessions.Clear();
            Messages.Clear();
            WorkspaceHint = "Choose a folder to start project-scoped chats.";
            RefreshDerivedState();
            return;
        }

        _state.Settings.SelectedProjectId = value.Id;
        var project = CurrentProject();
        if (project is not null)
        {
            project.LastOpenedAt = DateTimeOffset.UtcNow;
            WorkspaceHint = project.Path;
            ReloadSessions(project.Id);
            SelectedSession = Sessions.FirstOrDefault();
            _ = _store.SaveAsync(_state);
            RefreshDerivedState();
        }
    }

    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        Messages.Clear();
        Trace.Clear();
        if (value is null)
        {
            return;
        }

        var session = _state.Sessions.FirstOrDefault(candidate => candidate.Id == value.Id);
        if (session is null)
        {
            return;
        }

        foreach (var message in session.Messages)
        {
            Messages.Add(MessageItemViewModel.From(message));
        }

        Status = $"{session.Title} is open.";
        RefreshDerivedState();
    }

    partial void OnSelectedModelOptionChanged(ModelOptionViewModel? value)
    {
        if (_isHydratingSettings || value is null)
        {
            return;
        }

        ApplyModelOption(value);
        HydrateSettings();
        RefreshDerivedState();
    }

    partial void OnSelectedProviderSetupChanged(string value)
    {
        if (_isHydratingSettings || !IsInitialized)
        {
            return;
        }

        ApiKeyInput = "";
        HydrateProviderFields(ConfiguredProvider());
    }

    partial void OnContextWindowTokensChanged(int value)
    {
        if (_isHydratingSettings)
        {
            return;
        }

        if (!IsContextWindowEditable)
        {
            return;
        }

        ConfiguredProvider().ContextWindowTokens = Math.Max(1024, value);
        RefreshDerivedState();
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(MessageInput);

    partial void OnMessageInputChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
    }

    private void HydrateSettings()
    {
        _isHydratingSettings = true;
        try
        {
            Persona = _state.Settings.Persona;
            SelectedAccessLevel = AccessName(_state.Settings.AccessLevel);
            SearxngUrl = _state.Settings.SearxngUrl;
            AutoWebSearch = _state.Settings.AutoWebSearch;
            BrowserEnabled = _state.Settings.Browser.Enabled;
            BrowserAllowedDomains = string.Join(", ", _state.Settings.Browser.AllowedDomains);
            McpEnabled = _state.Settings.Mcp.Enabled;
            SandboxEnabled = _state.Settings.Sandbox.Enabled;
            SandboxImage = _state.Settings.Sandbox.Image;
            SandboxTimeoutSeconds = _state.Settings.Sandbox.TimeoutSeconds;
            SandboxMemoryMiB = _state.Settings.Sandbox.MemoryMiB;
            SandboxCpuLimit = _state.Settings.Sandbox.CpuLimit;
            SandboxPidsLimit = _state.Settings.Sandbox.PidsLimit;
            SandboxScratchMiB = _state.Settings.Sandbox.ScratchMiB;
            MemoriesEnabled = _state.Settings.MemoriesEnabled;
            SubagentsEnabled = _state.Settings.Subagents.Enabled;
            AutoDelegateEnabled = _state.Settings.Subagents.AutoDelegateEnabled;
            MaxParallelSubagents = _state.Settings.Subagents.MaxParallelAgents;
            MaxSubagentsPerTurn = _state.Settings.Subagents.MaxAgentsPerTurn;
            SelectedProviderSetup = ProviderSetupName(_state.Settings.ActiveProvider);
            HydrateProviderFields(ConfiguredProvider());
        }
        finally
        {
            _isHydratingSettings = false;
        }
    }

    private static string ProviderSetupName(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.OpenRouter => "OpenRouter",
        LlmProviderKind.LmStudio => "LM Studio",
        LlmProviderKind.CustomOpenAiCompatible => "Custom OpenAI-compatible",
        _ => "DeepSeek API"
    };

    private void HydrateProviderFields(ProviderSettings provider)
    {
        ProviderEndpoint = provider.Transport == ProviderTransport.CodexAppServer
            ? "Managed by the local Codex app-server"
            : provider.BaseUrl;
        ContextWindowTokens = provider.ContextWindowTokens;
        ModelCatalog.Clear();
        if (!string.IsNullOrWhiteSpace(provider.Model))
        {
            ModelCatalog.Add(provider.Model);
        }

        RefreshProviderConfigurationVisibility();
    }

    private void RefreshProviderConfigurationVisibility()
    {
        var kind = ConfiguredProviderKind();
        ProviderEndpointVisibility = Visibility.Visible;
        ApiKeyVisibility = kind is LlmProviderKind.DeepSeek or LlmProviderKind.OpenRouter or LlmProviderKind.CustomOpenAiCompatible
            ? Visibility.Visible
            : Visibility.Collapsed;
        CodexConnectionVisibility = Visibility.Collapsed;
        IsContextWindowEditable = true;
        IsProviderEndpointEditable = kind is not (LlmProviderKind.DeepSeek or LlmProviderKind.OpenRouter);
    }

    private void RefreshModelOptions()
    {
        var previousKey = SelectedModelOption?.Key;
        ModelOptions.Clear();

        ModelOptions.Add(new ModelOptionViewModel(
            "deepseek-v4-flash|max",
            "DeepSeek V4 Flash · Extra High",
            "Fast hosted reasoning",
            LlmProviderKind.DeepSeek,
            "deepseek-v4-flash",
            "max",
            true,
            1000000));
        ModelOptions.Add(new ModelOptionViewModel(
            "deepseek-v4-flash|high",
            "DeepSeek V4 Flash · High",
            "Fast hosted reasoning",
            LlmProviderKind.DeepSeek,
            "deepseek-v4-flash",
            "high",
            true,
            1000000));
        ModelOptions.Add(new ModelOptionViewModel(
            "deepseek-v4-pro|max",
            "DeepSeek V4 Pro · Extra High",
            "Most capable hosted reasoning",
            LlmProviderKind.DeepSeek,
            "deepseek-v4-pro",
            "max",
            true,
            1000000));
        ModelOptions.Add(new ModelOptionViewModel(
            "deepseek-v4-pro|high",
            "DeepSeek V4 Pro · High",
            "Most capable hosted reasoning",
            LlmProviderKind.DeepSeek,
            "deepseek-v4-pro",
            "high",
            true,
            1000000));

        AddOpenRouterModelOptions();
        AddDiscoveredProviderOptions(_state.Settings.LmStudio, LlmProviderKind.LmStudio, "LM Studio", "lmstudio");
        AddDiscoveredProviderOptions(_state.Settings.Custom, LlmProviderKind.CustomOpenAiCompatible, "Custom", "custom");

        if (!string.IsNullOrWhiteSpace(_state.Settings.OpenAiCodex.ConnectedAccountPlan))
        {
            foreach (var capability in _state.Settings.OpenAiCodex.ModelCapabilities
                         .OrderByDescending(model => model.IsDefault)
                         .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var efforts = capability.ReasoningEfforts.Count == 0
                    ? [capability.DefaultReasoningEffort]
                    : capability.ReasoningEfforts;
                foreach (var effort in efforts)
                {
                    var displayEffort = FormatReasoningEffort(effort);
                    ModelOptions.Add(new ModelOptionViewModel(
                        $"codex|{capability.Id}|{effort}",
                        $"ChatGPT · {capability.DisplayName} · {displayEffort}",
                        $"{capability.Description} · {CompactNumber(capability.ContextWindowTokens)} input context",
                        LlmProviderKind.OpenAiCodex,
                        capability.Id,
                        effort,
                        true,
                        capability.ContextWindowTokens));
                }
            }
        }

        var active = _state.Settings.ActiveProviderSettings;
        var activeKey = _state.Settings.ActiveProvider switch
        {
            LlmProviderKind.DeepSeek => $"{active.Model}|{active.ReasoningEffort}",
            LlmProviderKind.LmStudio => $"lmstudio|{active.Model}",
            LlmProviderKind.OpenRouter => $"openrouter|{active.Model}",
            LlmProviderKind.OpenAiCodex => $"codex|{active.Model}|{active.ReasoningEffort}",
            _ => $"custom|{active.Model}"
        };

        _isHydratingSettings = true;
        try
        {
            SelectedModelOption = ModelOptions.FirstOrDefault(option => option.Key == activeKey)
                ?? ModelOptions.FirstOrDefault(option => option.Key == previousKey)
                ?? ModelOptions.FirstOrDefault();
        }
        finally
        {
            _isHydratingSettings = false;
        }
    }

    private void AddOpenRouterModelOptions()
    {
        var provider = _state.Settings.OpenRouter;
        var seedIds = new AppSettings().OpenRouter.ModelCapabilities
            .Select(model => model.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilities = provider.ModelCapabilities.Count > 0
            ? provider.ModelCapabilities
            : new AppSettings().OpenRouter.ModelCapabilities;

        // OpenRouter catalogs are huge; keep the composer usable by prioritizing seeds + selection.
        const int maxComposerModels = 40;
        var ordered = capabilities
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(model =>
                string.Equals(model.Id, provider.Model, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => seedIds.Contains(model.Id) || model.IsDefault)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(maxComposerModels)
            .ToList();

        foreach (var capability in ordered)
        {
            var contextTokens = capability.ContextWindowTokens > 0
                ? capability.ContextWindowTokens
                : provider.ContextWindowTokens;
            var description = string.IsNullOrWhiteSpace(capability.Description)
                ? $"OpenRouter · {CompactNumber(contextTokens)} context"
                : $"{capability.Description} · {CompactNumber(contextTokens)} context";
            ModelOptions.Add(new ModelOptionViewModel(
                $"openrouter|{capability.Id}",
                $"OpenRouter · {capability.DisplayName}",
                description,
                LlmProviderKind.OpenRouter,
                capability.Id,
                "none",
                false,
                contextTokens));
        }

        // Keep a selected model visible even if it is not in the seed/catalog yet.
        if (!string.IsNullOrWhiteSpace(provider.Model) &&
            !ModelOptions.Any(option =>
                option.Provider == LlmProviderKind.OpenRouter &&
                string.Equals(option.Model, provider.Model, StringComparison.OrdinalIgnoreCase)))
        {
            ModelOptions.Add(new ModelOptionViewModel(
                $"openrouter|{provider.Model}",
                $"OpenRouter · {provider.Model}",
                $"OpenRouter · {CompactNumber(provider.ContextWindowTokens)} context",
                LlmProviderKind.OpenRouter,
                provider.Model,
                "none",
                false,
                provider.ContextWindowTokens));
        }
    }

    private void AddDiscoveredProviderOptions(
        ProviderSettings provider,
        LlmProviderKind kind,
        string label,
        string keyPrefix)
    {
        var models = provider.ModelCapabilities.Count > 0
            ? provider.ModelCapabilities.Select(capability => capability.Id)
            : [provider.Model];
        foreach (var model in models.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ModelOptions.Add(new ModelOptionViewModel(
                $"{keyPrefix}|{model}",
                $"{label} · {model}",
                kind == LlmProviderKind.LmStudio ? "Local model" : "Custom OpenAI-compatible model",
                kind,
                model,
                "none",
                false,
                provider.ContextWindowTokens));
        }
    }

    private void ApplyModelOption(ModelOptionViewModel option)
    {
        _state.Settings.ActiveProvider = option.Provider;
        var provider = _state.Settings.ActiveProviderSettings;
        provider.Model = option.Model;
        provider.SupportsThinking = option.SupportsThinking;
        provider.ThinkingEnabled = option.SupportsThinking;
        provider.ReasoningEffort = option.ReasoningEffort == "none" && option.Provider != LlmProviderKind.OpenAiCodex
            ? "medium"
            : option.ReasoningEffort;
        provider.ContextWindowTokens = option.ContextWindowTokens;
        ApplyKnownProviderCapabilities();
    }

    private void ApplySettings()
    {
        if (SelectedModelOption is not null)
        {
            ApplyModelOption(SelectedModelOption);
        }

        _state.Settings.Persona = Persona.Trim();
        _state.Settings.AccessLevel = AccessFromName(SelectedAccessLevel);
        _state.Settings.SearxngUrl = string.IsNullOrWhiteSpace(SearxngUrl) ? "http://127.0.0.1:8080" : SearxngUrl.Trim();
        _state.Settings.AutoWebSearch = AutoWebSearch;
        _state.Settings.Browser.Enabled = BrowserEnabled;
        _state.Settings.Browser.AllowedDomains = NormalizeBrowserDomains(BrowserAllowedDomains);
        _state.Settings.Mcp.Enabled = McpEnabled;
        _state.Settings.Sandbox.Enabled = SandboxEnabled;
        _state.Settings.Sandbox.Image = SandboxImage.Trim();
        _state.Settings.Sandbox.AllowReadOnlyProjectMount = false;
        _state.Settings.Sandbox.TimeoutSeconds = Math.Clamp(SandboxTimeoutSeconds, 5, 120);
        _state.Settings.Sandbox.MemoryMiB = Math.Clamp(SandboxMemoryMiB, 64, 2048);
        _state.Settings.Sandbox.CpuLimit = double.IsFinite(SandboxCpuLimit)
            ? Math.Clamp(SandboxCpuLimit, 0.25, 2.0)
            : 1.0;
        _state.Settings.Sandbox.PidsLimit = Math.Clamp(SandboxPidsLimit, 16, 256);
        _state.Settings.Sandbox.ScratchMiB = Math.Clamp(SandboxScratchMiB, 16, 512);
        _state.Settings.MemoriesEnabled = MemoriesEnabled;
        _state.Settings.Subagents.Enabled = SubagentsEnabled;
        _state.Settings.Subagents.AutoDelegateEnabled = AutoDelegateEnabled;
        _state.Settings.Subagents.MaxParallelAgents = Math.Clamp(MaxParallelSubagents, 1, 12);
        _state.Settings.Subagents.MaxAgentsPerTurn = Math.Clamp(MaxSubagentsPerTurn, 0, 12);

        var provider = ConfiguredProvider();
        if (provider.Transport != ProviderTransport.CodexAppServer)
        {
            provider.BaseUrl = ConfiguredProviderKind() switch
            {
                LlmProviderKind.DeepSeek => "https://api.deepseek.com",
                LlmProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
                _ => ProviderEndpoint.Trim()
            };
        }

        provider.ContextWindowTokens = Math.Max(1024, ContextWindowTokens);
        ApplyKnownProviderCapabilities();
        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            provider.EncryptedApiKey = CredentialProtector.Protect(ApiKeyInput.Trim());
        }
    }

    private LlmProviderKind ConfiguredProviderKind() => SelectedProviderSetup switch
    {
        "OpenRouter" => LlmProviderKind.OpenRouter,
        "LM Studio" => LlmProviderKind.LmStudio,
        "Custom OpenAI-compatible" => LlmProviderKind.CustomOpenAiCompatible,
        _ => LlmProviderKind.DeepSeek
    };

    private ProviderSettings ConfiguredProvider() => ConfiguredProviderKind() switch
    {
        LlmProviderKind.OpenRouter => _state.Settings.OpenRouter,
        LlmProviderKind.LmStudio => _state.Settings.LmStudio,
        LlmProviderKind.CustomOpenAiCompatible => _state.Settings.Custom,
        _ => _state.Settings.DeepSeek
    };

    private static bool IsSafeProviderEndpoint(string value, LlmProviderKind kind)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp &&
               (uri.IsLoopback ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                kind == LlmProviderKind.LmStudio && uri.Host == "127.0.0.1");
    }

    private void ApplyKnownProviderCapabilities()
    {
        var provider = _state.Settings.ActiveProviderSettings;
        switch (_state.Settings.ActiveProvider)
        {
            case LlmProviderKind.DeepSeek:
                if (string.IsNullOrWhiteSpace(provider.BaseUrl))
                {
                    provider.BaseUrl = "https://api.deepseek.com";
                }

                provider.SupportsThinking = true;
                provider.ThinkingEnabled = true;
                if (provider.Model.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase))
                {
                    provider.ContextWindowTokens = 1_000_000;
                }

                break;
            case LlmProviderKind.OpenRouter:
                provider.BaseUrl = "https://openrouter.ai/api/v1";
                provider.RequiresApiKey = true;
                provider.Transport = ProviderTransport.OpenAiCompatible;
                provider.SupportsThinking = false;
                provider.ThinkingEnabled = false;
                provider.ReasoningEffort = "medium";
                if (string.IsNullOrWhiteSpace(provider.Model))
                {
                    provider.Model = "openai/gpt-4o-mini";
                }

                break;
            case LlmProviderKind.LmStudio:
                provider.SupportsThinking = false;
                provider.ThinkingEnabled = false;
                provider.ReasoningEffort = "medium";
                break;
            case LlmProviderKind.OpenAiCodex:
                provider.Transport = ProviderTransport.CodexAppServer;
                provider.RequiresApiKey = false;
                provider.SupportsThinking = true;
                provider.ThinkingEnabled = true;
                var capability = provider.ModelCapabilities.FirstOrDefault(model =>
                    string.Equals(model.Id, provider.Model, StringComparison.OrdinalIgnoreCase));
                if (capability is not null)
                {
                    provider.ContextWindowTokens = capability.ContextWindowTokens;
                    if (!capability.ReasoningEfforts.Contains(provider.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
                    {
                        provider.ReasoningEffort = capability.DefaultReasoningEffort;
                    }
                }

                break;
        }
    }

    private void ReloadProjects()
    {
        Projects.Clear();
        foreach (var project in _state.Projects.OrderByDescending(project => project.LastOpenedAt))
        {
            Projects.Add(ProjectItemViewModel.From(project));
        }
    }

    private void ReloadSessions(string projectId)
    {
        Sessions.Clear();
        foreach (var session in _state.Sessions
                     .Where(session => session.ProjectId == projectId)
                     .OrderByDescending(session => session.UpdatedAt))
        {
            Sessions.Add(SessionItemViewModel.From(session));
        }

        if (Sessions.Count == 0)
        {
            var session = LuckyStore.CreateSession(projectId);
            _state.Sessions.Insert(0, session);
            Sessions.Add(SessionItemViewModel.From(session));
        }
    }

    private void ReloadMemories()
    {
        Memories.Clear();
        foreach (var memory in _state.Memories
                     .OrderByDescending(memory => memory.Pinned)
                     .ThenByDescending(memory => memory.UpdatedAt)
                     .Take(64))
        {
            Memories.Add(MemoryItemViewModel.From(memory));
        }
    }

    private void ReloadMcpServers()
    {
        McpServers.Clear();
        foreach (var server in _state.Settings.Mcp.Servers)
        {
            McpServers.Add(McpServerItemViewModel.From(server));
        }
    }

    private static List<string> NormalizeBrowserDomains(string value) => value
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(domain => domain.Trim().TrimStart('.'))
        .Where(domain => domain.Length > 0 &&
                         domain.Length <= 253 &&
                         domain.All(character => char.IsLetterOrDigit(character) || character is '-' or '.'))
        .Select(domain => domain.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(32)
        .ToList();

    private static bool IsSandboxImageValueValid(string? value)
    {
        var image = value?.Trim() ?? "";
        return image.Length <= 255 &&
               (image.Length == 0 || char.IsLetterOrDigit(image[0])) &&
               image.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or ':' or '@');
    }

    private void RefreshModeVisibility()
    {
        ChatVisibility = IsSettingsOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsVisibility = IsSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDerivedState()
    {
        RefreshModeVisibility();

        var project = CurrentProject();
        var session = project is null ? null : CurrentSession(project.Id);
        var projectName = project?.Name ?? "Projects";
        ChatHeading = session?.Messages.Count > 0
            ? session.Title
            : $"What should we work on in {projectName}?";
        ChatSubheading = project is null
            ? "Choose a folder to start project-scoped chats."
            : project.Path;

        var messageCount = session?.Messages.Count ?? 0;
        EmptyStateVisibility = messageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        MessageListVisibility = messageCount == 0 ? Visibility.Collapsed : Visibility.Visible;

        var activeProvider = _state.Settings.ActiveProvider;
        var activeModel = _state.Settings.ActiveProviderSettings.Model;
        var actualContextTokens = LastActualContextUsage(session, activeProvider, activeModel);
        var usedTokens = actualContextTokens ?? ContextEstimator.EstimateSessionTokens(session);
        var maxTokens = Math.Max(1024, _state.Settings.ActiveProviderSettings.ContextWindowTokens);
        ContextUsagePercent = Math.Clamp(usedTokens * 100.0 / maxTokens, 0, 100);
        ContextUsagePercentText = $"{ContextUsagePercent:0}%";
        ContextUsageLabel = actualContextTokens.HasValue
            ? $"{CompactNumber(usedTokens)}/{CompactNumber(maxTokens)}"
            : $"~{CompactNumber(usedTokens)}/{CompactNumber(maxTokens)}";

        var profileChars = ContextEstimator.EstimateMemoryChars(_state.Memories, MemoryKind.UserProfile);
        var memoryChars = ContextEstimator.EstimateMemoryChars(_state.Memories, MemoryKind.Memory);
        UserProfileUsageLabel = $"User {profileChars}/{_state.Settings.UserProfileCharLimit}";
        MemoryUsageLabel = $"Memory {memoryChars}/{_state.Settings.MemoryCharLimit}";
    }

    private LuckyProject? CurrentProject()
    {
        return SelectedProject is null
            ? null
            : _state.Projects.FirstOrDefault(project => project.Id == SelectedProject.Id);
    }

    private ChatSession? CurrentSession(string projectId)
    {
        if (SelectedSession is not null)
        {
            var selected = _state.Sessions.FirstOrDefault(session => session.Id == SelectedSession.Id);
            if (selected is not null)
            {
                return selected;
            }
        }

        return _state.Sessions.FirstOrDefault(session => session.ProjectId == projectId);
    }

    private void TouchSession(ChatSession session, string userMessage)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        if (session.Title == "New chat")
        {
            session.Title = userMessage.Length <= 42 ? userMessage : $"{userMessage[..42]}...";
            var sessionItem = Sessions.FirstOrDefault(item => item.Id == session.Id);
            if (sessionItem is not null)
            {
                sessionItem.Title = session.Title;
            }
        }
    }

    private static HarnessAccessLevel AccessFromName(string name) => name switch
    {
        "Chat only" => HarnessAccessLevel.ChatOnly,
        "Full access" => HarnessAccessLevel.FullAccess,
        _ => HarnessAccessLevel.Workspace
    };

    private static string AccessName(HarnessAccessLevel level) => level switch
    {
        HarnessAccessLevel.ChatOnly => "Chat only",
        HarnessAccessLevel.FullAccess => "Full access",
        _ => "Workspace"
    };

    private static string CompactNumber(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1000)
        {
            return $"{value / 1000d:0.#}K";
        }

        return value.ToString();
    }

    private static string FormatReasoningEffort(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "none" => "No reasoning",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra High",
        "max" => "Max",
        _ => effort
    };

    private static ProviderModelCapability CloneModelCapability(ProviderModelCapability source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Description = source.Description,
        ReasoningEfforts = [.. source.ReasoningEfforts],
        DefaultReasoningEffort = source.DefaultReasoningEffort,
        ContextWindowTokens = source.ContextWindowTokens,
        IsDefault = source.IsDefault
    };

    private static int? LastActualContextUsage(
        ChatSession? session,
        LlmProviderKind activeProvider,
        string activeModel)
    {
        return session?.Messages
            .Where(message => message.Role == ChatRole.Assistant &&
                              message.ContextTokens is > 0 &&
                              message.ProviderKind == activeProvider &&
                              string.Equals(message.ModelId, activeModel, StringComparison.OrdinalIgnoreCase))
            .Select(message => message.ContextTokens)
            .LastOrDefault();
    }
}

public sealed class ProjectItemViewModel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Detail { get; init; } = "";

    public static ProjectItemViewModel From(LuckyProject project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Path = project.Path,
        Detail = project.Path
    };
}

public sealed partial class SessionItemViewModel : ObservableObject
{
    public string Id { get; init; } = "";

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    public string Detail { get; init; } = "";

    public static SessionItemViewModel From(ChatSession session) => new()
    {
        Id = session.Id,
        Title = session.Title,
        Detail = session.UpdatedAt.LocalDateTime.ToString("g")
    };
}

public sealed partial class MessageItemViewModel : ObservableObject
{
    private bool _isAppendingReasoning;

    public ChatRole RoleKind { get; init; }

    [ObservableProperty]
    public partial string Content { get; set; } = "";

    public string DisplayContent => AnswerTextFormatter.ForPlainChat(Content);

    [ObservableProperty]
    public partial string ThinkingText { get; set; } = "";

    [ObservableProperty]
    public partial Visibility ThinkingVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool IsThinking { get; set; }

    [ObservableProperty]
    public partial bool IsThinkingExpanded { get; set; }

    public string Role => RoleKind.ToString();
    public string Timestamp { get; init; } = "";
    public string Tone => RoleKind == ChatRole.User ? "" : "Lucky";
    public Visibility HeaderVisibility => RoleKind == ChatRole.User ? Visibility.Collapsed : Visibility.Visible;
    public HorizontalAlignment TimestampAlignment => RoleKind == ChatRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public string ThinkingLabel => "Thinking";
    public HorizontalAlignment BubbleAlignment => RoleKind == ChatRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public double BubbleMaxWidth => RoleKind == ChatRole.User ? 560 : 780;
    public Thickness BubblePadding => RoleKind == ChatRole.User ? new Thickness(16, 14, 16, 10) : new Thickness(4, 2, 4, 2);
    public CornerRadius BubbleCornerRadius => RoleKind == ChatRole.User ? new CornerRadius(16) : new CornerRadius(0);
    public Thickness BubbleBorderThickness => RoleKind == ChatRole.User ? new Thickness(1) : new Thickness(0);
    public Brush BubbleBrush => new SolidColorBrush(RoleKind == ChatRole.User ? Color.FromArgb(210, 36, 36, 36) : Colors.Transparent);
    public Brush BubbleBorderBrush => new SolidColorBrush(RoleKind == ChatRole.User ? Color.FromArgb(255, 58, 58, 58) : Colors.Transparent);

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayContent));
    }

    partial void OnIsThinkingChanged(bool value)
    {
        OnPropertyChanged(nameof(ThinkingLabel));
    }

    public void AppendThinking(AgentProgressEvent progressEvent)
    {
        if (progressEvent.Stage == "memory")
        {
            return;
        }

        if (progressEvent.Stage == "reasoning" && !string.IsNullOrEmpty(progressEvent.Detail))
        {
            var existing = RemovePlaceholderThinking(ThinkingText);
            if (!_isAppendingReasoning && !string.IsNullOrWhiteSpace(existing))
            {
                existing = $"{existing.TrimEnd()}{Environment.NewLine}";
            }

            ThinkingText = $"{existing}{progressEvent.Detail}";
            _isAppendingReasoning = true;
            return;
        }

        _isAppendingReasoning = false;
        var detail = string.IsNullOrWhiteSpace(progressEvent.Detail) ? "" : $" - {progressEvent.Detail}";
        var line = progressEvent.Stage == "tool"
            ? $"{progressEvent.Summary}{detail}"
            : progressEvent.Summary;
        if (!ThinkingText.Contains(line, StringComparison.OrdinalIgnoreCase))
        {
            ThinkingText = string.IsNullOrWhiteSpace(ThinkingText)
                ? line
                : $"{ThinkingText}{Environment.NewLine}{line}";
        }
    }

    private static string RemovePlaceholderThinking(string value)
    {
        var lines = value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Equals("Preparing request...", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("Thinking", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("Thinking...", StringComparison.OrdinalIgnoreCase));
        return string.Join(Environment.NewLine, lines);
    }

    public static MessageItemViewModel PendingAssistant() => new()
    {
        RoleKind = ChatRole.Assistant,
        Timestamp = DateTimeOffset.Now.ToString("t"),
        IsThinkingExpanded = false
    };

    public static MessageItemViewModel From(ChatMessage message) => new()
    {
        RoleKind = message.Role,
        Content = message.Content,
        Timestamp = message.CreatedAt.LocalDateTime.ToString("t"),
        ThinkingText = CleanStoredThinkingText(message.Trace),
        ThinkingVisibility = string.IsNullOrWhiteSpace(CleanStoredThinkingText(message.Trace)) ? Visibility.Collapsed : Visibility.Visible
    };

    private static string CleanStoredThinkingText(string? trace)
    {
        if (string.IsNullOrWhiteSpace(trace))
        {
            return "";
        }

        var lines = trace.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !line.Equals("Preparing request...", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("memory: Checking memory", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("thinking: Thinking", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("Thinking", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("reasoning: Model reasoning in progress", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("llm done:", StringComparison.OrdinalIgnoreCase));
        var cleaned = string.Join(Environment.NewLine, lines);
        const int maxLegacyTraceChars = 9000;
        if (cleaned.Length <= maxLegacyTraceChars)
        {
            return cleaned;
        }

        const int headChars = 5200;
        const int tailChars = 2600;
        return string.Concat(
            cleaned.AsSpan(0, Math.Min(headChars, cleaned.Length)).ToString().TrimEnd(),
            Environment.NewLine,
            Environment.NewLine,
            "... large legacy Thinking trace truncated; full file/tool payload omitted ...",
            Environment.NewLine,
            Environment.NewLine,
            cleaned.AsSpan(Math.Max(0, cleaned.Length - tailChars)).ToString().TrimStart());
    }
}

public sealed class MemoryItemViewModel
{
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public string State { get; init; } = "";
    public string Kind { get; init; } = "";

    public static MemoryItemViewModel From(MemoryItem memory) => new()
    {
        Summary = memory.Summary,
        Detail = string.Join(", ", memory.Tags.DefaultIfEmpty("memory")),
        State = memory.Enabled ? memory.Pinned ? "Pinned" : "Enabled" : "Disabled",
        Kind = memory.Kind == MemoryKind.UserProfile ? "USER" : "MEMORY"
    };
}

public sealed class ToolTraceItemViewModel
{
    public string Tool { get; init; } = "";
    public string Input { get; init; } = "";
    public string Output { get; init; } = "";

    public static ToolTraceItemViewModel From(ToolTraceEntry trace) => new()
    {
        Tool = trace.IsError ? $"{trace.Tool} failed" : trace.Tool,
        Input = trace.Input,
        Output = MainPageViewModel.SummarizeToolOutput(trace)
    };
}

public sealed class McpServerItemViewModel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";

    public static McpServerItemViewModel From(McpServerDefinition server) => new()
    {
        Id = server.Id,
        Name = server.Name,
        Detail = "stdio · launch configuration protected for this Windows user"
    };
}

public sealed record ModelOptionViewModel(
    string Key,
    string Label,
    string Detail,
    LlmProviderKind Provider,
    string Model,
    string ReasoningEffort,
    bool SupportsThinking,
    int ContextWindowTokens);
