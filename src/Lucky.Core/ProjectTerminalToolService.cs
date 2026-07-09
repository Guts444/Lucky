using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Lucky.Core;

public interface IProjectTerminalToolService
{
    Task<ToolExecutionResult> RunCommandAsync(
        LuckyProject project,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a Windows PowerShell command from the verified root of the selected project.
/// This service constrains the initial working directory and reports bounded stdout/stderr, but it
/// is not an operating-system sandbox: callers must expose it only behind an explicit FullAccess
/// user choice.
/// </summary>
public sealed class ProjectTerminalToolService : IProjectTerminalToolService
{
    private const int DefaultTimeoutSeconds = 60;
    private const int MaximumTimeoutSeconds = 300;
    private const int MaximumCommandCharacters = 64 * 1024;
    private const int MaximumCapturedCharactersPerStream = 48 * 1024;

    public async Task<ToolExecutionResult> RunCommandAsync(
        LuckyProject project,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var input = $"project root | {command}";

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

        var normalizedTimeout = timeoutSeconds ?? DefaultTimeoutSeconds;
        if (normalizedTimeout is < 1 or > MaximumTimeoutSeconds)
        {
            return Error(input, $"timeoutSeconds must be between 1 and {MaximumTimeoutSeconds}.");
        }

        string workingDirectory;
        try
        {
            workingDirectory = ResolveProjectRoot(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Error(input, ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        using var process = new Process
        {
            StartInfo = CreatePowerShellStartInfo(command, workingDirectory),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return Error(input, "PowerShell did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return Error(input, $"PowerShell could not be started: {ex.Message}");
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput);
        var stderrTask = ReadCappedAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(normalizedTimeout));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var stop = await StopAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            if (!stop.HostExitConfirmed || !stop.ProcessTreeTerminationRequested)
            {
                throw new OperationCanceledException(RenderCancellationMessage(stop), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var stop = await StopAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            var stdout = await ObserveOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ObserveOutputAsync(stderrTask).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            return Error(input, RenderTimeoutOutput(normalizedTimeout, elapsed, stop, stdout, stderr));
        }

        CapturedOutput completedStdout;
        CapturedOutput completedStderr;
        try
        {
            completedStdout = await stdoutTask.ConfigureAwait(false);
            completedStderr = await stderrTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return Error(input, $"PowerShell exited, but Lucky could not collect its output: {ex.Message}");
        }
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var output = RenderCompletedOutput(process.ExitCode, duration, completedStdout, completedStderr);
        return new ToolExecutionResult("project.run_command", input, output, IsError: process.ExitCode != 0);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        return startInfo;
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

    private static async Task<StopAndDrainResult> StopAndDrainAsync(
        Process process,
        Task<CapturedOutput> stdoutTask,
        Task<CapturedOutput> stderrTask)
    {
        var processTreeTerminationRequested = false;
        var hostExitConfirmed = false;
        string? terminationDetail = null;
        try
        {
            if (process.HasExited)
            {
                hostExitConfirmed = true;
            }
            else
            {
                process.Kill(entireProcessTree: true);
                processTreeTerminationRequested = true;
            }
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between HasExited and Kill. Confirm that before reporting it.
            hostExitConfirmed = HasExited(process);
            if (!hostExitConfirmed)
            {
                terminationDetail = "Lucky could not request process-tree termination because the PowerShell process handle was no longer available.";
            }
        }
        catch (Win32Exception ex)
        {
            terminationDetail = $"Windows did not accept Lucky's process-tree termination request: {ex.Message}";
        }

        if (!hostExitConfirmed)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                hostExitConfirmed = true;
            }
            catch (TimeoutException)
            {
                terminationDetail ??= "Lucky did not observe the PowerShell host exit within five seconds of the termination request.";
            }
            catch (InvalidOperationException)
            {
                hostExitConfirmed = HasExited(process);
                if (!hostExitConfirmed)
                {
                    terminationDetail ??= "Lucky could not confirm whether the PowerShell host exited.";
                }
            }
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Output collection is bounded and best-effort after a forced stop.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The timeout/cancellation result remains more useful than a late stream-read failure.
        }

        return new StopAndDrainResult(
            processTreeTerminationRequested,
            hostExitConfirmed,
            terminationDetail);
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

    private static string RenderCompletedOutput(
        int exitCode,
        TimeSpan elapsed,
        CapturedOutput stdout,
        CapturedOutput stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"PowerShell exited with code {exitCode} in {FormatElapsed(elapsed)}.");
        AppendStreams(builder, stdout, stderr);
        return builder.ToString().Trim();
    }

    private static string RenderTimeoutOutput(
        int timeoutSeconds,
        TimeSpan elapsed,
        StopAndDrainResult stop,
        CapturedOutput stdout,
        CapturedOutput stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"PowerShell timed out after {timeoutSeconds} second(s); Lucky began termination handling after {FormatElapsed(elapsed)}.");
        AppendTerminationOutcome(builder, stop);
        AppendStreams(builder, stdout, stderr);
        return builder.ToString().Trim();
    }

    private static void AppendTerminationOutcome(StringBuilder builder, StopAndDrainResult stop)
    {
        if (stop.ProcessTreeTerminationRequested && stop.HostExitConfirmed)
        {
            builder.AppendLine("Lucky requested process-tree termination and confirmed that the PowerShell host exited. Detached child processes are not independently verified.");
        }
        else if (stop.HostExitConfirmed)
        {
            builder.AppendLine("Lucky confirmed that the PowerShell host exited, but could not confirm process-tree termination; child processes may remain.");
        }
        else
        {
            builder.AppendLine("Lucky could not confirm that the PowerShell host exited; the process or its children may still be running.");
        }

        if (!string.IsNullOrWhiteSpace(stop.TerminationDetail))
        {
            builder.AppendLine($"Termination detail: {stop.TerminationDetail}");
        }
    }

    private static string RenderCancellationMessage(StopAndDrainResult stop)
    {
        var builder = new StringBuilder("The PowerShell command was cancelled. ");
        if (stop.ProcessTreeTerminationRequested && stop.HostExitConfirmed)
        {
            builder.Append("Lucky requested process-tree termination and confirmed that the PowerShell host exited. Detached child processes are not independently verified.");
        }
        else if (stop.HostExitConfirmed)
        {
            builder.Append("Lucky confirmed that the PowerShell host exited, but could not confirm process-tree termination; child processes may remain.");
        }
        else
        {
            builder.Append("Lucky could not confirm that the PowerShell host exited; the process or its children may still be running.");
        }

        if (!string.IsNullOrWhiteSpace(stop.TerminationDetail))
        {
            builder.Append($" Termination detail: {stop.TerminationDetail}");
        }

        return builder.ToString();
    }

    private static void AppendStreams(StringBuilder builder, CapturedOutput stdout, CapturedOutput stderr)
    {
        if (stdout.Text.Length == 0 && stderr.Text.Length == 0)
        {
            builder.Append("No output.");
            return;
        }

        if (stdout.Text.Length > 0)
        {
            builder.AppendLine("stdout:");
            builder.AppendLine(stdout.Text.TrimEnd());
            if (stdout.WasTruncated)
            {
                builder.AppendLine($"[stdout capped at {MaximumCapturedCharactersPerStream:N0} characters]");
            }
        }

        if (stderr.Text.Length > 0)
        {
            builder.AppendLine("stderr:");
            builder.AppendLine(stderr.Text.TrimEnd());
            if (stderr.WasTruncated)
            {
                builder.AppendLine($"[stderr capped at {MaximumCapturedCharactersPerStream:N0} characters]");
            }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds >= 1
        ? $"{elapsed.TotalSeconds:0.0}s"
        : $"{elapsed.TotalMilliseconds:0}ms";

    private static string ResolveProjectRoot(LuckyProject project)
    {
        var root = NormalizeRoot(project.Path);
        EnsureNoReparseEscape(root);
        return root;
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("No project folder is selected.");
        }

        var normalized = Path.GetFullPath(root);
        if (!Directory.Exists(normalized))
        {
            throw new InvalidOperationException("Selected project folder does not exist.");
        }

        return Path.TrimEndingDirectorySeparator(normalized);
    }

    private static void EnsureNoReparseEscape(string root)
    {
        if (HasReparsePoint(root))
        {
            throw new InvalidOperationException("Project folder is a reparse point and cannot be used safely.");
        }
    }

    private static bool HasReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    private static ToolExecutionResult Error(string input, string output) => new("project.run_command", input, output, IsError: true);

    private sealed record CapturedOutput(string Text, bool WasTruncated);

    private sealed record StopAndDrainResult(
        bool ProcessTreeTerminationRequested,
        bool HostExitConfirmed,
        string? TerminationDetail);

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
