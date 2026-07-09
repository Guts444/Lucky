using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lucky.Core;

public interface IMcpToolService
{
    Task<IMcpToolSession> OpenSessionAsync(
        McpSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IMcpToolSession : IAsyncDisposable
{
    IReadOnlyList<McpDiscoveredTool> Tools { get; }

    IReadOnlyList<ToolTraceEntry> StartupTrace { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        string modelToolName,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Local stdio MCP host. Every configured server is launched only for an active Full Access
/// turn, receives the standard initialize/tools/list/tools/call lifecycle, and is stopped when
/// that turn ends. Server commands and argument values are user configuration, never model input.
/// </summary>
public sealed class McpToolService : IMcpToolService
{
    private const string ProtocolVersion = "2025-11-25";
    private const int MaxToolsPerServer = 128;
    private const int MaxToolListPages = 8;
    private const int MaxProtocolFrameCharacters = 256 * 1024;
    private const int MaxStderrLineCharacters = 8 * 1024;
    private const int MaxToolNameCharacters = 128;
    private const int MaxToolDescriptionCharacters = 4 * 1024;
    private const int MaxToolParameterDescriptionCharacters = 1024;
    private const int MaxToolSchemaCharacters = 12 * 1024;
    private const int MaxTotalToolSchemaCharactersPerServer = 64 * 1024;
    private const int MaxToolCursorCharacters = 2048;
    private const int MaxToolRequiredProperties = 128;
    private const int MaxToolContentItems = 64;
    private const int MaxToolTextContentCharacters = 8 * 1024;
    private const int MaxStructuredContentCharacters = 12 * 1024;
    private const int MaxResourceReferenceCharacters = 2048;

    public async Task<IMcpToolSession> OpenSessionAsync(
        McpSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return EmptyMcpToolSession.Instance;
        }

        var timeoutSeconds = Math.Clamp(settings.RequestTimeoutSeconds, 5, 300);
        var outputLimit = Math.Clamp(settings.MaxToolOutputChars, 1000, 64000);
        var tools = new List<McpDiscoveredTool>();
        var connections = new List<StdioMcpConnection>();
        var startupTrace = new List<ToolTraceEntry>();
        var modelToolNames = new HashSet<string>(StringComparer.Ordinal);
        var serverIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in (settings.Servers ?? []).Where(server => server is { Enabled: true }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                definition.Id = IdFactory.NewId("mcp");
            }

            if (!serverIds.Add(definition.Id))
            {
                startupTrace.Add(new ToolTraceEntry(
                    "mcp.connect",
                    ServerDisplayName(definition),
                    "Two configured MCP servers have the same id. Remove and re-add one of them in Settings.",
                    IsError: true));
                continue;
            }

            if (definition.Transport != McpTransportKind.Stdio)
            {
                startupTrace.Add(new ToolTraceEntry(
                    "mcp.connect",
                    ServerDisplayName(definition),
                    "Lucky currently supports local stdio MCP servers only.",
                    IsError: true));
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.Command))
            {
                startupTrace.Add(new ToolTraceEntry(
                    "mcp.connect",
                    ServerDisplayName(definition),
                    "No command is configured for this MCP server.",
                    IsError: true));
                continue;
            }

            StdioMcpConnection? connection = null;
            try
            {
                connection = new StdioMcpConnection(definition, TimeSpan.FromSeconds(timeoutSeconds));
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
                var serverTools = await ListToolsAsync(connection, cancellationToken).ConfigureAwait(false);
                if (serverTools.Count == 0)
                {
                    startupTrace.Add(new ToolTraceEntry(
                        "mcp.connect",
                        ServerDisplayName(definition),
                        "Connected, but the server did not expose any tools."));
                    await connection.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                foreach (var tool in serverTools.Take(MaxToolsPerServer))
                {
                    var modelToolName = UniqueModelToolName(definition, tool.Name, modelToolNames);
                    tools.Add(new McpDiscoveredTool(
                        modelToolName,
                        definition.Id,
                        ServerDisplayName(definition),
                        tool.Name,
                        tool.Description,
                        ParseParameterDefinitions(tool.InputSchema),
                        ParseRequiredProperties(tool.InputSchema),
                        tool.InputSchema));
                }

                connections.Add(connection);
                startupTrace.Add(new ToolTraceEntry(
                    "mcp.connect",
                    ServerDisplayName(definition),
                    $"Connected over stdio; discovered {serverTools.Count} tool(s)."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or TaskCanceledException or JsonException)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                startupTrace.Add(new ToolTraceEntry(
                    "mcp.connect",
                    ServerDisplayName(definition),
                    ex.Message,
                    IsError: true));
            }
        }

        return new StdioMcpToolSession(tools, connections, startupTrace, outputLimit);
    }

    private static async Task InitializeAsync(StdioMcpConnection connection, CancellationToken cancellationToken)
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new
            {
                name = "Lucky",
                version = "0.1"
            }
        });
        var response = await connection.RequestAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
        var negotiatedVersion = StringProperty(response, "protocolVersion");
        if (!IsSupportedProtocolVersion(negotiatedVersion))
        {
            throw new InvalidOperationException($"The server selected unsupported MCP protocol version '{negotiatedVersion ?? "unknown"}'.");
        }

        await connection.NotifyAsync(
            "notifications/initialized",
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<McpRawTool>> ListToolsAsync(
        StdioMcpConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<McpRawTool>();
        string? cursor = null;
        var totalSchemaCharacters = 0;
        for (var page = 0; page < MaxToolListPages; page++)
        {
            var parameters = cursor is null
                ? JsonSerializer.SerializeToElement(new { })
                : JsonSerializer.SerializeToElement(new { cursor });
            var response = await connection.RequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);
            if (!response.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The MCP server returned an invalid tools/list response.");
            }

            foreach (var toolElement in toolsElement.EnumerateArray())
            {
                var name = StringProperty(toolElement, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.Length > MaxToolNameCharacters)
                {
                    throw new InvalidOperationException($"The MCP server exposed a tool name longer than Lucky's {MaxToolNameCharacters}-character limit.");
                }

                var description = CapMetadata(
                    StringProperty(toolElement, "description") ?? $"Run MCP tool '{name}'.",
                    MaxToolDescriptionCharacters,
                    "tool description");
                var inputSchema = toolElement.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object
                    ? CloneBoundedSchema(schema, ref totalSchemaCharacters)
                    : JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { }
                    });
                result.Add(new McpRawTool(name, description, inputSchema));
                if (result.Count >= MaxToolsPerServer)
                {
                    return result;
                }
            }

            cursor = StringProperty(response, "nextCursor");
            if (cursor is { Length: > MaxToolCursorCharacters })
            {
                throw new InvalidOperationException($"The MCP server returned a tools/list cursor longer than Lucky's {MaxToolCursorCharacters}-character limit.");
            }

            if (string.IsNullOrWhiteSpace(cursor))
            {
                return result;
            }
        }

        throw new InvalidOperationException($"The MCP server exceeded Lucky's {MaxToolListPages}-page tool discovery limit.");
    }

    private static bool IsSupportedProtocolVersion(string? version) => version is
        "2024-11-05" or
        "2025-03-26" or
        "2025-06-18" or
        "2025-11-25";

    private static string UniqueModelToolName(
        McpServerDefinition server,
        string toolName,
        ISet<string> knownNames)
    {
        var baseName = $"mcp_{NormalizeFunctionSegment(ServerDisplayName(server))}_{NormalizeFunctionSegment(toolName)}";
        baseName = baseName.Length > 56 ? baseName[..56].TrimEnd('_') : baseName;
        if (baseName.Length == 0 || baseName == "mcp")
        {
            baseName = "mcp_tool";
        }

        var candidate = baseName;
        var suffix = 2;
        while (!knownNames.Add(candidate))
        {
            var suffixText = $"_{suffix++}";
            candidate = $"{baseName[..Math.Min(baseName.Length, 64 - suffixText.Length)]}{suffixText}";
        }

        return candidate;
    }

    private static string NormalizeFunctionSegment(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        var normalized = builder.ToString().Trim('_');
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        return normalized.Length == 0 ? "server" : normalized;
    }

    private static JsonElement CloneBoundedSchema(JsonElement schema, ref int totalSchemaCharacters)
    {
        var rawSchema = schema.GetRawText();
        if (rawSchema.Length > MaxToolSchemaCharacters)
        {
            throw new InvalidOperationException($"The MCP server exposed an input schema larger than Lucky's {MaxToolSchemaCharacters:N0}-character limit.");
        }

        totalSchemaCharacters += rawSchema.Length;
        if (totalSchemaCharacters > MaxTotalToolSchemaCharactersPerServer)
        {
            throw new InvalidOperationException($"The MCP server exposed more than {MaxTotalToolSchemaCharactersPerServer:N0} characters of tool schemas.");
        }

        return schema.Clone();
    }

    private static string CapMetadata(string value, int maxLength, string label) => value.Length <= maxLength
        ? value
        : $"{value[..maxLength].TrimEnd()}\n\n... {label} capped by Lucky";

    private static IReadOnlyDictionary<string, ToolParameterDefinition> ParseParameterDefinitions(JsonElement schema)
    {
        var required = ParseRequiredProperties(schema).ToHashSet(StringComparer.Ordinal);
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, ToolParameterDefinition>();
        }

        var result = new Dictionary<string, ToolParameterDefinition>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject().Take(MaxToolRequiredProperties))
        {
            var propertySchema = property.Value;
            var type = StringProperty(propertySchema, "type") ?? "string";
            var description = CapMetadata(
                StringProperty(propertySchema, "description") ?? property.Name,
                MaxToolParameterDescriptionCharacters,
                "parameter description");
            result[property.Name] = new ToolParameterDefinition(type, description, required.Contains(property.Name));
        }

        return result;
    }

    private static IReadOnlyList<string> ParseRequiredProperties(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return required.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item) && item!.Length <= MaxToolNameCharacters)
            .Cast<string>()
            .Take(MaxToolRequiredProperties)
            .ToArray();
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string ServerDisplayName(McpServerDefinition server) =>
        string.IsNullOrWhiteSpace(server.Name) ? "MCP server" : server.Name.Trim();

    private sealed record McpRawTool(string Name, string Description, JsonElement InputSchema);

    private sealed record McpToolBinding(McpDiscoveredTool Tool, StdioMcpConnection Connection);

    private sealed class StdioMcpToolSession : IMcpToolSession
    {
        private readonly Dictionary<string, McpToolBinding> _bindings;
        private readonly IReadOnlyList<StdioMcpConnection> _connections;
        private readonly int _outputLimit;

        public StdioMcpToolSession(
            IReadOnlyList<McpDiscoveredTool> tools,
            IReadOnlyList<StdioMcpConnection> connections,
            IReadOnlyList<ToolTraceEntry> startupTrace,
            int outputLimit)
        {
            Tools = tools;
            StartupTrace = startupTrace;
            _connections = connections;
            _outputLimit = outputLimit;
            _bindings = new Dictionary<string, McpToolBinding>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                var connection = connections.First(connection => string.Equals(connection.ServerId, tool.ServerId, StringComparison.Ordinal));
                _bindings[tool.ModelToolName] = new McpToolBinding(tool, connection);
            }
        }

        public IReadOnlyList<McpDiscoveredTool> Tools { get; }

        public IReadOnlyList<ToolTraceEntry> StartupTrace { get; }

        public async Task<ToolExecutionResult> ExecuteAsync(
            string modelToolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            if (!_bindings.TryGetValue(modelToolName, out var binding))
            {
                return Error(modelToolName, "Lucky did not expose this MCP tool for the current turn.");
            }

            try
            {
                if (argumentsJson.Length > MaxProtocolFrameCharacters)
                {
                    return Error(binding.Tool, $"MCP tool arguments exceed Lucky's {MaxProtocolFrameCharacters:N0}-character protocol-frame limit.");
                }

                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Error(binding.Tool, "MCP tool arguments must be a JSON object.");
                }

                var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["name"] = binding.Tool.ToolName,
                    ["arguments"] = document.RootElement.Clone()
                });
                var response = await binding.Connection
                    .RequestAsync("tools/call", parameters, cancellationToken)
                    .ConfigureAwait(false);
                return RenderToolResult(binding.Tool, response, _outputLimit);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or TaskCanceledException)
            {
                return Error(binding.Tool, ex.Message);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static ToolExecutionResult RenderToolResult(McpDiscoveredTool tool, JsonElement response, int outputLimit)
        {
            var isError = response.TryGetProperty("isError", out var isErrorElement) &&
                          isErrorElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                          isErrorElement.GetBoolean();
            var output = new StringBuilder();
            if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray().Take(MaxToolContentItems))
                {
                    var type = StringProperty(item, "type") ?? "unknown";
                    switch (type)
                    {
                        case "text":
                            output.AppendLine(Trim(StringProperty(item, "text") ?? "", MaxToolTextContentCharacters));
                            break;
                        case "resource":
                            output.AppendLine($"[MCP resource: {Trim(ResourceReference(item), MaxResourceReferenceCharacters)}]");
                            break;
                        case "image":
                        case "audio":
                            output.AppendLine($"[MCP returned {type} content; binary payload is omitted from the model context.]");
                            break;
                        default:
                            output.AppendLine($"[MCP {Trim(type, 128)} content: {Trim(item.GetRawText(), 1200)}]");
                            break;
                    }
                }

                if (content.GetArrayLength() > MaxToolContentItems)
                {
                    output.AppendLine($"[MCP content capped at {MaxToolContentItems} items]");
                }
            }

            if (response.TryGetProperty("structuredContent", out var structuredContent))
            {
                output.AppendLine("Structured result:");
                output.AppendLine(Trim(structuredContent.GetRawText(), MaxStructuredContentCharacters));
            }

            var rendered = output.ToString().Trim();
            if (string.IsNullOrWhiteSpace(rendered))
            {
                rendered = "The MCP server returned no content.";
            }

            rendered = Trim(rendered, outputLimit);
            return new ToolExecutionResult(
                $"mcp.{NormalizeFunctionSegment(tool.ServerName)}.{NormalizeFunctionSegment(tool.ToolName)}",
                $"{tool.ServerName}.{tool.ToolName}",
                rendered,
                isError);
        }

        private static string ResourceReference(JsonElement item)
        {
            if (item.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
            {
                return StringProperty(resource, "uri") ?? "embedded resource";
            }

            return StringProperty(item, "uri") ?? "embedded resource";
        }

        private static ToolExecutionResult Error(McpDiscoveredTool tool, string message) =>
            new(
                $"mcp.{NormalizeFunctionSegment(tool.ServerName)}.{NormalizeFunctionSegment(tool.ToolName)}",
                $"{tool.ServerName}.{tool.ToolName}",
                message,
                IsError: true);

        private static ToolExecutionResult Error(string input, string message) =>
            new("mcp", input, message, IsError: true);
    }

    private sealed class EmptyMcpToolSession : IMcpToolSession
    {
        public static EmptyMcpToolSession Instance { get; } = new();

        public IReadOnlyList<McpDiscoveredTool> Tools { get; } = [];

        public IReadOnlyList<ToolTraceEntry> StartupTrace { get; } = [];

        public Task<ToolExecutionResult> ExecuteAsync(string modelToolName, string argumentsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolExecutionResult("mcp", modelToolName, "MCP is disabled in Settings.", IsError: true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StdioMcpConnection : IAsyncDisposable
    {
        private readonly McpServerDefinition _server;
        private readonly TimeSpan _requestTimeout;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly ConcurrentQueue<string> _stderrLines = new();
        private Process? _process;
        private StreamWriter? _writer;
        private StreamReader? _reader;
        private CappedLineReader? _stdoutLines;
        private Task? _stderrDrainTask;
        private int _nextRequestId;

        public StdioMcpConnection(McpServerDefinition server, TimeSpan requestTimeout)
        {
            _server = server;
            _requestTimeout = requestTimeout;
        }

        public string ServerId => _server.Id;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_server.Command.Contains('\r') || _server.Command.Contains('\n'))
            {
                throw new InvalidOperationException("The MCP command cannot contain a line break.");
            }

            if (!string.IsNullOrWhiteSpace(_server.WorkingDirectory) && !Directory.Exists(_server.WorkingDirectory))
            {
                throw new InvalidOperationException("The MCP server working directory does not exist.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _server.Command,
                WorkingDirectory = string.IsNullOrWhiteSpace(_server.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : _server.WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in _server.Arguments ?? [])
            {
                if (argument.Contains('\r') || argument.Contains('\n'))
                {
                    throw new InvalidOperationException("An MCP server argument cannot contain a line break.");
                }

                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Lucky could not start the MCP server process.");
            _writer = _process.StandardInput;
            _writer.AutoFlush = true;
            _reader = _process.StandardOutput;
            _stdoutLines = new CappedLineReader(_reader);
            _stderrDrainTask = DrainStderrAsync(new CappedLineReader(_process.StandardError));
            return Task.CompletedTask;
        }

        public async Task<JsonElement> RequestAsync(
            string method,
            JsonElement parameters,
            CancellationToken cancellationToken)
        {
            EnsureStarted();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_requestTimeout);
            var token = timeoutSource.Token;
            await _requestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var requestId = Interlocked.Increment(ref _nextRequestId);
                var request = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = requestId,
                    ["method"] = method,
                    ["params"] = parameters
                });
                await _writer!.WriteLineAsync(request).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);

                while (true)
                {
                    var line = await _stdoutLines!.ReadLineAsync(MaxProtocolFrameCharacters, token).ConfigureAwait(false);
                    if (line is null)
                    {
                        throw new InvalidOperationException(ServerStoppedMessage());
                    }

                    if (line.WasTruncated)
                    {
                        await StopForProtocolViolationAsync().ConfigureAwait(false);
                        throw new InvalidOperationException($"The MCP server wrote a stdout protocol frame larger than Lucky's {MaxProtocolFrameCharacters:N0}-character limit and was stopped.");
                    }

                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(line.Text);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException("The MCP server wrote a non-JSON message to stdout.", ex);
                    }

                    using (document)
                    {
                        if (!IsMatchingResponse(document.RootElement, requestId))
                        {
                            // Notifications and server-initiated requests are intentionally not model-visible.
                            // Lucky currently acts as a tool client and ignores unrelated protocol messages.
                            continue;
                        }

                        if (document.RootElement.TryGetProperty("error", out var error))
                        {
                            throw new InvalidOperationException($"MCP {method} failed: {ErrorMessage(error)}");
                        }

                        if (!document.RootElement.TryGetProperty("result", out var result))
                        {
                            throw new InvalidOperationException($"MCP {method} returned neither result nor error.");
                        }

                        return result.Clone();
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"MCP {method} timed out after {(int)_requestTimeout.TotalSeconds} seconds.");
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task NotifyAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
        {
            EnsureStarted();
            cancellationToken.ThrowIfCancellationRequested();
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var notification = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = method,
                    ["params"] = parameters
                });
                await _writer!.WriteLineAsync(notification).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _writer?.Close();
                await StopProcessAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process exited between checks.
            }
            finally
            {
                _writer?.Dispose();
                _reader?.Dispose();
                _process?.Dispose();
                _requestGate.Dispose();
            }
        }

        private void EnsureStarted()
        {
            if (_process is null || _writer is null || _reader is null)
            {
                throw new InvalidOperationException("The MCP server process was not started.");
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException(ServerStoppedMessage());
            }
        }

        private string ServerStoppedMessage()
        {
            var diagnosticCount = _stderrLines.Count;
            return diagnosticCount == 0
                ? "The MCP server process stopped unexpectedly."
                : $"The MCP server process stopped unexpectedly after writing {diagnosticCount} diagnostic line(s).";
        }

        private async Task DrainStderrAsync(CappedLineReader stderr)
        {
            try
            {
                while (await stderr.ReadLineAsync(MaxStderrLineCharacters, CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    _stderrLines.Enqueue(line.WasTruncated
                        ? $"[MCP stderr line exceeded Lucky's {MaxStderrLineCharacters:N0}-character limit and was truncated.]"
                        : line.Text);
                    while (_stderrLines.Count > 8)
                    {
                        _stderrLines.TryDequeue(out _);
                    }
                }
            }
            catch (IOException)
            {
                // Stderr closes with the process; no action is needed.
            }
        }

        private async Task StopForProtocolViolationAsync()
        {
            await StopProcessAsync().ConfigureAwait(false);
        }

        private async Task StopProcessAsync()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The process is already being torn down; do not stall Lucky's chat turn.
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Process exited between checks.
            }
            catch (Win32Exception)
            {
                // Windows denied a late kill request while the process was already exiting.
            }
        }

        private sealed class CappedLineReader
        {
            private readonly StreamReader _reader;
            private readonly char[] _buffer = new char[4096];
            private int _offset;
            private int _count;

            public CappedLineReader(StreamReader reader)
            {
                _reader = reader;
            }

            public async Task<CappedLine?> ReadLineAsync(int maximumCharacters, CancellationToken cancellationToken)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
                var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
                var wasTruncated = false;
                var sawAnyCharacter = false;

                while (true)
                {
                    if (_offset >= _count)
                    {
                        _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        _offset = 0;
                        if (_count == 0)
                        {
                            return sawAnyCharacter ? new CappedLine(builder.ToString(), wasTruncated) : null;
                        }
                    }

                    var character = _buffer[_offset++];
                    sawAnyCharacter = true;
                    if (character == '\n')
                    {
                        if (builder.Length > 0 && builder[^1] == '\r')
                        {
                            builder.Length--;
                        }

                        return new CappedLine(builder.ToString(), wasTruncated);
                    }

                    if (builder.Length < maximumCharacters)
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        wasTruncated = true;
                    }
                }
            }
        }

        private sealed record CappedLine(string Text, bool WasTruncated);

        private static bool IsMatchingResponse(JsonElement root, int requestId) =>
            root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value == requestId;

        private static string ErrorMessage(JsonElement error) =>
            StringProperty(error, "message") ?? Trim(error.GetRawText(), 700);
    }

    private static string Trim(string value, int maxLength) => value.Length <= maxLength
        ? value
        : $"{value[..maxLength].TrimEnd()}\n\n... output capped by Lucky";
}
