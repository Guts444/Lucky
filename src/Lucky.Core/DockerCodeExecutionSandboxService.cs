using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lucky.Core;

/// <summary>
/// Executes a command through the local Docker CLI without invoking a host shell.
/// The result is deliberately small so callers can report Docker availability and execution
/// failures without coupling the sandbox policy to <see cref="Process"/>.
/// </summary>
public sealed record DockerCliResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    string? StartError = null,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false)
{
    public bool Started => StartError is null;
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Small seam around the Docker CLI. It keeps the production implementation shell-free and lets
/// focused tests assert the exact container policy without requiring Docker Desktop to be running.
/// </summary>
public interface IDockerCliRunner
{
    Task<DockerCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps sandbox execution on the local Windows Docker named pipe. The regular Docker CLI can use
/// a remote endpoint or a selected context; that is intentionally not a valid Lucky sandbox host.
/// </summary>
public static class DockerSandboxDaemonPolicy
{
    public const string LocalWindowsNpipeHost = "npipe:////./pipe/docker_engine";

    public static string? ValidateCurrentEnvironment() => Validate(
        Environment.GetEnvironmentVariable("DOCKER_HOST"),
        Environment.GetEnvironmentVariable("DOCKER_CONTEXT"),
        OperatingSystem.IsWindows());

    public static string? Validate(string? dockerHost, string? dockerContext, bool isWindows)
    {
        if (!isWindows)
        {
            return "Lucky's Docker sandbox is available only with a local Windows named-pipe Docker daemon; this runtime is not Windows.";
        }

        if (!string.IsNullOrWhiteSpace(dockerContext) &&
            !string.Equals(dockerContext.Trim(), "default", StringComparison.OrdinalIgnoreCase))
        {
            return "Lucky's Docker sandbox refuses an explicit DOCKER_CONTEXT. Clear it or use the default local Windows named-pipe daemon; remote Docker contexts are not allowed.";
        }

        if (string.IsNullOrWhiteSpace(dockerHost))
        {
            return null;
        }

        var normalizedHost = dockerHost.Trim().Replace('\\', '/');
        return normalizedHost.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Lucky's Docker sandbox refuses DOCKER_HOST because it is not a local Windows named-pipe endpoint. Clear it and use the local Docker Desktop daemon; remote Docker hosts are not allowed.";
    }
}

/// <summary>
/// Runs Docker through <see cref="ProcessStartInfo.ArgumentList"/>. No command string is handed to
/// cmd.exe or PowerShell, so Docker arguments cannot be changed by shell parsing.
/// </summary>
public sealed class DockerCliRunner : IDockerCliRunner
{
    private const int MaximumCapturedCharactersPerStream = 48 * 1024;

    public async Task<DockerCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Docker command timeout must be positive.");
        }

        if (HasUnsupportedConnectionOverride(arguments))
        {
            return new DockerCliResult(
                null,
                "",
                "",
                StartError: "Docker command rejected because sandbox execution requires Lucky's local Windows named-pipe daemon.");
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(arguments),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return new DockerCliResult(null, "", "", StartError: "Docker did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return new DockerCliResult(null, "", "", StartError: $"Docker could not be started: {ex.Message}");
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput);
        var stderrTask = ReadCappedAsync(process.StandardError);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await StopAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            var stdout = await ObserveOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ObserveOutputAsync(stderrTask).ConfigureAwait(false);
            return new DockerCliResult(
                null,
                stdout.Text,
                stderr.Text,
                TimedOut: true,
                StandardOutputTruncated: stdout.WasTruncated,
                StandardErrorTruncated: stderr.WasTruncated);
        }

        try
        {
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new DockerCliResult(
                process.ExitCode,
                stdout.Text,
                stderr.Text,
                StandardOutputTruncated: stdout.WasTruncated,
                StandardErrorTruncated: stderr.WasTruncated);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new DockerCliResult(process.HasExited ? process.ExitCode : null, "", "", StartError: $"Docker exited, but Lucky could not collect its output: {ex.Message}");
        }
    }

    private static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "docker.exe" : "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Do not inherit a remote Docker endpoint/context from the desktop process. The service
        // also passes this exact host as a visible policy argument; this fallback protects direct
        // uses of DockerCliRunner as well.
        startInfo.Environment.Remove("DOCKER_HOST");
        startInfo.Environment.Remove("DOCKER_CONTEXT");
        startInfo.Environment.Remove("DOCKER_TLS_VERIFY");
        startInfo.Environment.Remove("DOCKER_CERT_PATH");
        if (!arguments.Any(argument => argument.StartsWith("--host=", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.ArgumentList.Add($"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}");
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool HasUnsupportedConnectionOverride(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Equals("--context", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--context=", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--host", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-H", StringComparison.Ordinal))
            {
                return true;
            }

            if (argument.StartsWith("--host=", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    argument["--host=".Length..],
                    DockerSandboxDaemonPolicy.LocalWindowsNpipeHost,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<CapturedOutput> ReadCappedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var totalCharacters = 0L;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalCharacters += read;
            var remaining = MaximumCapturedCharactersPerStream - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, read));
            }
        }

        return new CapturedOutput(builder.ToString(), totalCharacters > MaximumCapturedCharactersPerStream);
    }

    private static async Task StopAndDrainAsync(
        Process process,
        Task<CapturedOutput> stdoutTask,
        Task<CapturedOutput> stderrTask)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Docker exited between HasExited and Kill.
        }
        catch (Win32Exception)
        {
            // The sandbox caller still gets a bounded timeout/cancellation outcome.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Do not let a broken Docker client keep the agent loop alive indefinitely.
        }
        catch (InvalidOperationException)
        {
            // Process handles may be released during a forced stop.
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Output is best-effort after a forced stop.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Preserve the original timeout/cancellation result.
        }
    }

    private static async Task<CapturedOutput> ObserveOutputAsync(Task<CapturedOutput> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return await task.ConfigureAwait(false);
        }

        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new CapturedOutput("", WasTruncated: false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new CapturedOutput($"[Output could not be collected: {ex.Message}]", WasTruncated: false);
        }
    }

    private sealed record CapturedOutput(string Text, bool WasTruncated);
}

public interface ICodeExecutionSandboxService
{
    Task<ToolExecutionResult> ExecuteAsync(
        CodeExecutionSandboxSettings settings,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An opt-in Docker-backed code execution boundary. The service intentionally has no image pull,
/// host shell, writable host mount, network, device, capability, or elevated-container path.
/// It is only meaningful when Docker Desktop is healthy and the explicitly configured image already
/// exists in Docker's local image cache.
/// </summary>
public sealed class DockerCodeExecutionSandboxService : ICodeExecutionSandboxService
{
    private const int DefaultTimeoutSeconds = 60;
    private const int MinimumTimeoutSeconds = 5;
    private const int MaximumConfiguredTimeoutSeconds = 120;
    private const int MinimumMemoryMiB = 64;
    private const int MaximumMemoryMiB = 2048;
    private const int MinimumScratchMiB = 16;
    private const int MaximumScratchMiB = 512;
    private const int MinimumPids = 16;
    private const int MaximumPids = 256;
    private const int MaximumCommandCharacters = 64 * 1024;
    private static readonly TimeSpan ImageInspectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly IDockerCliRunner _docker;

    public DockerCodeExecutionSandboxService(IDockerCliRunner? docker = null)
    {
        _docker = docker ?? new DockerCliRunner();
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        CodeExecutionSandboxSettings settings,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuredImage = settings.Image?.Trim() ?? "";
        var input = $"Docker sandbox | {configuredImage} | {command}";
        if (!settings.Enabled)
        {
            return Error(input, "Code execution sandbox is disabled in Settings.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return Error(input, "command is required.");
        }

        if (command.IndexOf('\0') >= 0)
        {
            return Error(input, "command contains a null character.");
        }

        if (command.Length > MaximumCommandCharacters)
        {
            return Error(input, $"Command is larger than {MaximumCommandCharacters:N0} characters.");
        }

        var image = NormalizeImage(settings.Image);
        if (image is null)
        {
            return Error(input, "Configure a Docker image name before enabling sandbox execution.");
        }

        var policy = SandboxPolicy.FromSettings(settings);
        var effectiveTimeoutSeconds = timeoutSeconds ?? policy.MaximumTimeoutSeconds;
        if (effectiveTimeoutSeconds < 1 || effectiveTimeoutSeconds > policy.MaximumTimeoutSeconds)
        {
            return Error(input, $"timeoutSeconds must be between 1 and {policy.MaximumTimeoutSeconds} for this sandbox.");
        }

        var daemonError = DockerSandboxDaemonPolicy.ValidateCurrentEnvironment();
        if (daemonError is not null)
        {
            return Error(input, daemonError);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var inspection = await _docker.RunAsync(
            [
                $"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}",
                "image",
                "inspect",
                "--format",
                "{{json .Config.Volumes}}",
                image
            ],
            ImageInspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!inspection.Succeeded)
        {
            return Error(input, RenderImageInspectionFailure(image, inspection));
        }

        var imageVolumeError = ValidateNoDeclaredVolumes(inspection.StandardOutput);
        if (imageVolumeError is not null)
        {
            return Error(input, imageVolumeError);
        }

        var containerName = $"lucky-sandbox-{Guid.NewGuid():N}";
        var runArguments = BuildRunArguments(image, command, containerName, policy);
        DockerCliResult execution;
        try
        {
            execution = await _docker.RunAsync(
                runArguments,
                TimeSpan.FromSeconds(effectiveTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RemoveContainerAsync(containerName).ConfigureAwait(false);
            throw;
        }

        if (execution.TimedOut)
        {
            var cleanup = await RemoveContainerAsync(containerName).ConfigureAwait(false);
            return Error(input, RenderTimeoutOutput(effectiveTimeoutSeconds, policy, execution, cleanup));
        }

        if (!execution.Started)
        {
            var cleanup = await RemoveContainerAsync(containerName).ConfigureAwait(false);
            return Error(input, RenderDockerFailure("Docker could not start the sandbox container.", policy, execution, cleanup));
        }

        return new ToolExecutionResult(
            "sandbox.execute",
            input,
            RenderCompletedOutput(execution, policy),
            IsError: execution.ExitCode != 0);
    }

    private static IReadOnlyList<string> BuildRunArguments(
        string image,
        string command,
        string containerName,
        SandboxPolicy policy)
    {
        var arguments = new List<string>
        {
            $"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}",
            "run",
            "--rm",
            $"--name={containerName}",
            "--pull=never",
            "--network=none",
            "--read-only",
            "--cap-drop=ALL",
            "--security-opt=no-new-privileges=true",
            $"--pids-limit={policy.PidsLimit}",
            $"--memory={policy.MemoryMiB}m",
            $"--memory-swap={policy.MemoryMiB}m",
            $"--cpus={policy.CpuLimit.ToString("0.###", CultureInfo.InvariantCulture)}",
            "--user=65532:65532",
            "--workdir=/scratch",
            "--no-healthcheck",
            "--shm-size=64m",
            "--ulimit",
            $"nofile={policy.OpenFileLimit}:{policy.OpenFileLimit}",
            "--tmpfs",
            $"/tmp:rw,noexec,nosuid,nodev,size={policy.ScratchMiB}m,mode=1777",
            "--tmpfs",
            $"/scratch:rw,nosuid,nodev,size={policy.ScratchMiB}m,mode=1777",
            "--env=HOME=/scratch",
            "--env=TMPDIR=/tmp",
            "--env=DOTNET_CLI_HOME=/scratch/.dotnet",
            "--entrypoint=sh"
        };

        arguments.Add(image);
        arguments.Add("-lc");
        arguments.Add(command);
        return arguments;
    }

    private async Task<string> RemoveContainerAsync(string containerName)
    {
        try
        {
            var cleanup = await _docker.RunAsync(
                [
                    $"--host={DockerSandboxDaemonPolicy.LocalWindowsNpipeHost}",
                    "container",
                    "rm",
                    "--force",
                    containerName
                ],
                CleanupTimeout,
                CancellationToken.None).ConfigureAwait(false);
            return cleanup.Succeeded
                ? "Forced cleanup completed."
                : "Lucky attempted forced cleanup; Docker did not confirm removal.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"Lucky attempted forced cleanup, but Docker reported: {ex.Message}";
        }
    }

    private static string RenderImageInspectionFailure(string image, DockerCliResult result)
    {
        if (result.TimedOut)
        {
            return "Docker did not verify the configured image before the 15-second safety timeout. Make sure Docker Desktop is running; Lucky did not start a container or pull an image.";
        }

        if (!result.Started)
        {
            return $"Docker is unavailable, so Lucky cannot verify the configured local image. {result.StartError}";
        }

        var diagnostic = Diagnostic(result);
        return string.IsNullOrWhiteSpace(diagnostic)
            ? $"Configured image '{image}' is not available locally. Lucky never pulls sandbox images automatically; build or load it locally, then retry."
            : $"Docker could not verify configured image '{image}'. Lucky never pulls sandbox images automatically. Docker said: {diagnostic}";
    }

    private static string RenderCompletedOutput(
        DockerCliResult result,
        SandboxPolicy policy)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Docker sandbox exited with code {result.ExitCode ?? -1}.");
        AppendBoundarySummary(builder, policy);
        AppendStreams(builder, result);
        return builder.ToString().Trim();
    }

    private static string RenderTimeoutOutput(
        int timeoutSeconds,
        SandboxPolicy policy,
        DockerCliResult result,
        string cleanup)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Docker sandbox timed out after {timeoutSeconds} second(s); Lucky stopped the Docker client and requested container removal.");
        builder.AppendLine(cleanup);
        AppendBoundarySummary(builder, policy);
        AppendStreams(builder, result);
        return builder.ToString().Trim();
    }

    private static string RenderDockerFailure(
        string heading,
        SandboxPolicy policy,
        DockerCliResult result,
        string cleanup)
    {
        var builder = new StringBuilder();
        builder.AppendLine(heading);
        builder.AppendLine(cleanup);
        AppendBoundarySummary(builder, policy);
        if (!string.IsNullOrWhiteSpace(result.StartError))
        {
            builder.AppendLine(result.StartError);
        }

        AppendStreams(builder, result);
        return builder.ToString().Trim();
    }

    private static void AppendBoundarySummary(StringBuilder builder, SandboxPolicy policy)
    {
        builder.AppendLine($"Boundary: local Windows Docker daemon and local image only; network disabled; read-only root; non-root process; CPU {policy.CpuLimit.ToString("0.###", CultureInfo.InvariantCulture)}, memory {policy.MemoryMiB} MiB, PID limit {policy.PidsLimit}.");
        builder.AppendLine("Host files: no host paths are mounted. Scratch: disposable in-container tmpfs at /scratch.");
    }

    private static void AppendStreams(StringBuilder builder, DockerCliResult result)
    {
        if (result.StandardOutput.Length == 0 && result.StandardError.Length == 0)
        {
            builder.Append("No output.");
            return;
        }

        if (result.StandardOutput.Length > 0)
        {
            builder.AppendLine("stdout:");
            builder.AppendLine(result.StandardOutput.TrimEnd());
            if (result.StandardOutputTruncated)
            {
                builder.AppendLine("[stdout capped at 48 KiB]");
            }
        }

        if (result.StandardError.Length > 0)
        {
            builder.AppendLine("stderr:");
            builder.AppendLine(result.StandardError.TrimEnd());
            if (result.StandardErrorTruncated)
            {
                builder.AppendLine("[stderr capped at 48 KiB]");
            }
        }
    }

    private static string Diagnostic(DockerCliResult result)
    {
        var diagnostic = result.StandardError.Length > 0
            ? result.StandardError
            : result.StandardOutput;
        return diagnostic.Trim().Replace(Environment.NewLine, " ");
    }

    private static string? NormalizeImage(string? value)
    {
        var image = value?.Trim();
        if (string.IsNullOrWhiteSpace(image) || image.Length > 255 || image.IndexOf('\0') >= 0)
        {
            return null;
        }

        return char.IsLetterOrDigit(image[0]) &&
               image.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or ':' or '@')
            ? image
            : null;
    }

    private static string? ValidateNoDeclaredVolumes(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson.Trim());
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "Lucky could not validate the configured image's volume metadata, so it will not start the sandbox.";
            }

            var volumePaths = document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .Take(8)
                .ToArray();
            return volumePaths.Length == 0
                ? null
                : $"Configured image declares Docker volume(s) ({string.Join(", ", volumePaths)}). Lucky rejects images with declared volumes because Docker could create writable anonymous storage outside the disposable sandbox scratch.";
        }
        catch (JsonException)
        {
            return "Lucky could not validate the configured image's volume metadata, so it will not start the sandbox.";
        }
    }

    private static ToolExecutionResult Error(string input, string output) => new("sandbox.execute", input, output, IsError: true);

    private sealed record SandboxPolicy(
        int MaximumTimeoutSeconds,
        int MemoryMiB,
        double CpuLimit,
        int PidsLimit,
        int ScratchMiB,
        int OpenFileLimit)
    {
        public static SandboxPolicy FromSettings(CodeExecutionSandboxSettings settings)
        {
            var timeout = Math.Clamp(
                settings.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : settings.TimeoutSeconds,
                MinimumTimeoutSeconds,
                MaximumConfiguredTimeoutSeconds);
            var memory = Math.Clamp(
                settings.MemoryMiB <= 0 ? 512 : settings.MemoryMiB,
                MinimumMemoryMiB,
                MaximumMemoryMiB);
            var cpu = Math.Clamp(
                settings.CpuLimit <= 0 ? 1.0 : settings.CpuLimit,
                0.25,
                2.0);
            var pids = Math.Clamp(
                settings.PidsLimit <= 0 ? 128 : settings.PidsLimit,
                MinimumPids,
                MaximumPids);
            var scratch = Math.Clamp(
                settings.ScratchMiB <= 0 ? 128 : settings.ScratchMiB,
                MinimumScratchMiB,
                MaximumScratchMiB);
            return new SandboxPolicy(timeout, memory, cpu, pids, scratch, OpenFileLimit: 1024);
        }
    }
}
