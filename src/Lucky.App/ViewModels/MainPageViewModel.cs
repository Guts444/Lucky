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
    private readonly OpenAiCompatibleClient _modelClient;
    private LuckyState _state = new();
    private bool _isHydratingSettings;

    public MainPageViewModel()
        : this(new LuckyStore(), new AgentRunner(), new OpenAiCompatibleClient())
    {
    }

    public MainPageViewModel(LuckyStore store, AgentRunner agentRunner, OpenAiCompatibleClient modelClient)
    {
        _store = store;
        _agentRunner = agentRunner;
        _modelClient = modelClient;
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<MemoryItemViewModel> Memories { get; } = [];
    public ObservableCollection<ToolTraceItemViewModel> Trace { get; } = [];
    public ObservableCollection<string> AccessLevels { get; } = ["Chat only", "Workspace", "Full access"];
    public ObservableCollection<ModelOptionViewModel> ModelOptions { get; } = [];
    public ObservableCollection<string> ModelCatalog { get; } = [];

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
    public partial string SelectedAccessLevel { get; set; } = "Workspace";

    [ObservableProperty]
    public partial string SearxngUrl { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoWebSearch { get; set; }

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            return;
        }

        _state = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        HydrateSettings();
        RefreshModelOptions();
        ReloadProjects();
        ReloadMemories();

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
        var assistantItem = MessageItemViewModel.PendingAssistant();
        assistantItem.IsThinking = true;
        assistantItem.ThinkingVisibility = Visibility.Visible;
        assistantItem.ThinkingText = "Preparing request...";
        Messages.Add(assistantItem);
        RefreshDerivedState();
        Trace.Clear();
        IsBusy = true;
        SendCommand.NotifyCanExecuteChanged();
        Status = "Lucky is thinking...";

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
            var result = await _agentRunner.RunTurnAsync(_state, project, session, text, progress: progress).ConfigureAwait(true);
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
                TotalTokens = result.TokenUsage?.TotalTokens
            };
            session.Messages.Add(assistant);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            ReloadMemories();
            await _store.SaveAsync(_state).ConfigureAwait(true);
            RefreshDerivedState();

            var memoryLabel = result.RecalledMemories.Count == 1
                ? "1 memory"
                : $"{result.RecalledMemories.Count} memories";
            Status = result.UsedModel
                ? $"Answered using {memoryLabel}."
                : "Answered without a model call.";
        }
        finally
        {
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
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
        ApplySettings();
        await _store.SaveAsync(_state).ConfigureAwait(true);
        ApiKeyInput = "";
        Status = "Settings saved.";
        RefreshModelOptions();
        RefreshDerivedState();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        RefreshModeVisibility();
        Status = "Settings opened.";
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
        var provider = _state.Settings.ActiveProviderSettings;
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

            if (models.Count > 0 && !models.Contains(provider.Model, StringComparer.OrdinalIgnoreCase) &&
                _state.Settings.ActiveProvider != LlmProviderKind.DeepSeek)
            {
                provider.Model = models[0];
            }

            ApplyKnownProviderCapabilities();
            RefreshModelOptions();
            HydrateProviderFields(_state.Settings.ActiveProviderSettings);

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

    partial void OnContextWindowTokensChanged(int value)
    {
        if (_isHydratingSettings)
        {
            return;
        }

        _state.Settings.ActiveProviderSettings.ContextWindowTokens = Math.Max(1024, value);
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
            SubagentsEnabled = _state.Settings.Subagents.Enabled;
            AutoDelegateEnabled = _state.Settings.Subagents.AutoDelegateEnabled;
            MaxParallelSubagents = _state.Settings.Subagents.MaxParallelAgents;
            MaxSubagentsPerTurn = _state.Settings.Subagents.MaxAgentsPerTurn;
            HydrateProviderFields(_state.Settings.ActiveProviderSettings);
        }
        finally
        {
            _isHydratingSettings = false;
        }
    }

    private void HydrateProviderFields(ProviderSettings provider)
    {
        ProviderEndpoint = provider.BaseUrl;
        ContextWindowTokens = provider.ContextWindowTokens;
        ModelCatalog.Clear();
        if (!string.IsNullOrWhiteSpace(provider.Model))
        {
            ModelCatalog.Add(provider.Model);
        }
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

        var localLabel = string.IsNullOrWhiteSpace(_state.Settings.LmStudio.Model)
            ? "LM Studio · Local model"
            : $"LM Studio · {_state.Settings.LmStudio.Model}";
        ModelOptions.Add(new ModelOptionViewModel(
            $"lmstudio|{_state.Settings.LmStudio.Model}",
            localLabel,
            "Local OpenAI-compatible model",
            LlmProviderKind.LmStudio,
            _state.Settings.LmStudio.Model,
            "none",
            false,
            _state.Settings.LmStudio.ContextWindowTokens));

        var active = _state.Settings.ActiveProviderSettings;
        var activeKey = _state.Settings.ActiveProvider == LlmProviderKind.DeepSeek
            ? $"{active.Model}|{active.ReasoningEffort}"
            : _state.Settings.ActiveProvider == LlmProviderKind.LmStudio
                ? $"lmstudio|{active.Model}"
                : previousKey;

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

    private void ApplyModelOption(ModelOptionViewModel option)
    {
        _state.Settings.ActiveProvider = option.Provider;
        var provider = _state.Settings.ActiveProviderSettings;
        provider.Model = option.Model;
        provider.SupportsThinking = option.SupportsThinking;
        provider.ThinkingEnabled = option.SupportsThinking;
        provider.ReasoningEffort = option.ReasoningEffort == "none" ? "medium" : option.ReasoningEffort;
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
        _state.Settings.Subagents.Enabled = SubagentsEnabled;
        _state.Settings.Subagents.AutoDelegateEnabled = AutoDelegateEnabled;
        _state.Settings.Subagents.MaxParallelAgents = Math.Clamp(MaxParallelSubagents, 1, 12);
        _state.Settings.Subagents.MaxAgentsPerTurn = Math.Clamp(MaxSubagentsPerTurn, 0, 12);

        var provider = _state.Settings.ActiveProviderSettings;
        provider.BaseUrl = ProviderEndpoint.Trim();
        provider.ContextWindowTokens = Math.Max(1024, ContextWindowTokens);
        ApplyKnownProviderCapabilities();
        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            provider.EncryptedApiKey = CredentialProtector.Protect(ApiKeyInput.Trim());
        }
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
            case LlmProviderKind.LmStudio:
                provider.SupportsThinking = false;
                provider.ThinkingEnabled = false;
                provider.ReasoningEffort = "medium";
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

        var usedTokens = LastActualTokenUsage(session) ?? ContextEstimator.EstimateSessionTokens(session);
        var maxTokens = Math.Max(1024, _state.Settings.ActiveProviderSettings.ContextWindowTokens);
        ContextUsagePercent = Math.Clamp(usedTokens * 100.0 / maxTokens, 0, 100);
        ContextUsagePercentText = $"{ContextUsagePercent:0}%";
        ContextUsageLabel = $"{CompactNumber(usedTokens)}/{CompactNumber(maxTokens)}";

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

    private static int? LastActualTokenUsage(ChatSession? session)
    {
        return session?.Messages
            .Where(message => message.Role == ChatRole.Assistant && message.TotalTokens is > 0)
            .Select(message => message.TotalTokens)
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

public sealed record ModelOptionViewModel(
    string Key,
    string Label,
    string Detail,
    LlmProviderKind Provider,
    string Model,
    string ReasoningEffort,
    bool SupportsThinking,
    int ContextWindowTokens);
