using System.Text;
using System.Text.RegularExpressions;

namespace Lucky.Core;

public interface IProjectFileToolService
{
    Task<ToolExecutionResult> ListAsync(LuckyProject project, string? relativePath, CancellationToken cancellationToken = default);

    Task<ToolExecutionResult> ReadAsync(LuckyProject project, string relativePath, CancellationToken cancellationToken = default);

    Task<ToolExecutionResult> SearchAsync(
        LuckyProject project,
        string query,
        string? relativePath = null,
        string? glob = null,
        CancellationToken cancellationToken = default);

    Task<ToolExecutionResult> WriteAsync(
        LuckyProject project,
        string relativePath,
        string content,
        bool overwrite,
        CancellationToken cancellationToken = default);

    Task<ToolExecutionResult> EditAsync(
        LuckyProject project,
        string relativePath,
        string oldText,
        string newText,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectFileToolService : IProjectFileToolService
{
    private const long MaxReadBytes = 256 * 1024;
    private const long MaxWriteBytes = 512 * 1024;
    private const int MaxListEntries = 200;
    private const int MaxSearchResults = 80;

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules"
    };

    public Task<ToolExecutionResult> ListAsync(
        LuckyProject project,
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Guard(project, relativePath ?? ".", "project.list_files", static (root, target, input) =>
        {
            if (File.Exists(target))
            {
                var info = new FileInfo(target);
                return Ok("project.list_files", input, $"{Path.GetRelativePath(root, target)} ({info.Length} bytes)");
            }

            if (!Directory.Exists(target))
            {
                return Error("project.list_files", input, "Path does not exist.");
            }

            var entries = Directory.EnumerateFileSystemEntries(target)
                .Where(path => !IsExcludedPath(path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxListEntries + 1)
                .ToArray();

            var builder = new StringBuilder();
            foreach (var entry in entries.Take(MaxListEntries))
            {
                var relative = Path.GetRelativePath(root, entry);
                if (Directory.Exists(entry))
                {
                    builder.AppendLine($"{relative}{Path.DirectorySeparatorChar}");
                }
                else
                {
                    builder.AppendLine($"{relative} ({new FileInfo(entry).Length} bytes)");
                }
            }

            if (entries.Length > MaxListEntries)
            {
                builder.AppendLine($"... capped at {MaxListEntries} entries");
            }

            return Ok("project.list_files", input, builder.ToString().Trim());
        }));
    }

    public async Task<ToolExecutionResult> ReadAsync(
        LuckyProject project,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        return await GuardAsync(project, relativePath, "project.read_file", async (root, target, input) =>
        {
            if (!File.Exists(target))
            {
                return Error("project.read_file", input, "File does not exist.");
            }

            var refusal = await ValidateReadableTextFileAsync(target, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                return Error("project.read_file", input, refusal);
            }

            var text = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            return Ok("project.read_file", input, text);
        }).ConfigureAwait(false);
    }

    public async Task<ToolExecutionResult> SearchAsync(
        LuckyProject project,
        string query,
        string? relativePath = null,
        string? glob = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("project.search", query, "Search query is required.");
        }

        return await GuardAsync(project, relativePath ?? ".", "project.search", async (root, target, input) =>
        {
            var files = EnumerateSearchFiles(target, glob).Take(2000).ToArray();
            var regex = TryCreateRegex(query);
            var results = new List<string>();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ValidateReadableTextFileAsync(file, cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    lineNumber++;
                    var matched = regex is not null
                        ? regex.IsMatch(line)
                        : line.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!matched)
                    {
                        continue;
                    }

                    results.Add($"{Path.GetRelativePath(root, file)}:{lineNumber}: {line.Trim()}");
                    if (results.Count >= MaxSearchResults)
                    {
                        break;
                    }
                }

                if (results.Count >= MaxSearchResults)
                {
                    break;
                }
            }

            var output = results.Count == 0
                ? "No matches."
                : string.Join(Environment.NewLine, results);
            if (results.Count >= MaxSearchResults)
            {
                output += Environment.NewLine + $"... capped at {MaxSearchResults} matches";
            }

            return Ok("project.search", $"{input} | {query}", output);
        }).ConfigureAwait(false);
    }

    public async Task<ToolExecutionResult> WriteAsync(
        LuckyProject project,
        string relativePath,
        string content,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
        {
            return Error("project.write_file", relativePath, $"Content is larger than {MaxWriteBytes} bytes.");
        }

        return await GuardAsync(project, relativePath, "project.write_file", async (root, target, input) =>
        {
            if (Directory.Exists(target))
            {
                return Error("project.write_file", input, "Target is a directory.");
            }

            if (File.Exists(target) && !overwrite)
            {
                return Error("project.write_file", input, "File already exists. Set overwrite=true to replace it.");
            }

            var parent = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return Error("project.write_file", input, "Could not resolve parent directory.");
            }

            Directory.CreateDirectory(parent);
            await File.WriteAllTextAsync(target, content, cancellationToken).ConfigureAwait(false);
            return Ok("project.write_file", input, $"Wrote {Path.GetRelativePath(root, target)} ({Encoding.UTF8.GetByteCount(content)} bytes).");
        }).ConfigureAwait(false);
    }

    public async Task<ToolExecutionResult> EditAsync(
        LuckyProject project,
        string relativePath,
        string oldText,
        string newText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return Error("project.edit_file", relativePath, "oldText is required.");
        }

        return await GuardAsync(project, relativePath, "project.edit_file", async (root, target, input) =>
        {
            if (!File.Exists(target))
            {
                return Error("project.edit_file", input, "File does not exist.");
            }

            var refusal = await ValidateReadableTextFileAsync(target, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                return Error("project.edit_file", input, refusal);
            }

            var text = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            var matches = CountOccurrences(text, oldText);
            if (matches == 0)
            {
                return Error("project.edit_file", input, "oldText was not found.");
            }

            if (matches > 1)
            {
                return Error("project.edit_file", input, $"oldText matched {matches} times. Provide a more specific edit.");
            }

            var updated = text.Replace(oldText, newText, StringComparison.Ordinal);
            if (Encoding.UTF8.GetByteCount(updated) > MaxWriteBytes)
            {
                return Error("project.edit_file", input, $"Edited file would be larger than {MaxWriteBytes} bytes.");
            }

            await File.WriteAllTextAsync(target, updated, cancellationToken).ConfigureAwait(false);
            return Ok("project.edit_file", input, $"Edited {Path.GetRelativePath(root, target)}.");
        }).ConfigureAwait(false);
    }

    private static ToolExecutionResult Guard(
        LuckyProject project,
        string relativePath,
        string tool,
        Func<string, string, string, ToolExecutionResult> action)
    {
        try
        {
            var root = NormalizeRoot(project.Path);
            var target = ResolveUnderRoot(root, relativePath);
            EnsureNoReparseEscape(root, target);
            return action(root, target, relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Error(tool, relativePath, ex.Message);
        }
    }

    private static async Task<ToolExecutionResult> GuardAsync(
        LuckyProject project,
        string relativePath,
        string tool,
        Func<string, string, string, Task<ToolExecutionResult>> action)
    {
        try
        {
            var root = NormalizeRoot(project.Path);
            var target = ResolveUnderRoot(root, relativePath);
            EnsureNoReparseEscape(root, target);
            return await action(root, target, relativePath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Error(tool, relativePath, ex.Message);
        }
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

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = ".";
        }

        var target = Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsWithinRoot(root, target))
        {
            throw new InvalidOperationException("Path is outside the selected project.");
        }

        return target;
    }

    private static bool IsWithinRoot(string root, string target)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(root, target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
               target.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
               target.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static void EnsureNoReparseEscape(string root, string target)
    {
        var current = root;
        if (HasReparsePoint(current))
        {
            throw new InvalidOperationException("Project folder is a reparse point and cannot be used safely.");
        }

        var relative = Path.GetRelativePath(root, target);
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (HasReparsePoint(current))
            {
                throw new InvalidOperationException("Path crosses a reparse point and was blocked.");
            }
        }
    }

    private static bool HasReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    private static bool IsExcludedPath(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Directory.Exists(path) && ExcludedDirectoryNames.Contains(name);
    }

    private static IEnumerable<string> EnumerateSearchFiles(string target, string? glob)
    {
        if (File.Exists(target))
        {
            yield return target;
            yield break;
        }

        if (!Directory.Exists(target))
        {
            yield break;
        }

        var pattern = string.IsNullOrWhiteSpace(glob) ? "*" : glob;
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory).Where(path => !IsExcludedPath(path)))
            {
                pending.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                yield return file;
            }
        }
    }

    private static async Task<string?> ValidateReadableTextFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxReadBytes)
        {
            return $"File is larger than {MaxReadBytes} bytes.";
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes.Any(value => value == 0) ? "File looks binary and was not read." : null;
    }

    private static Regex? TryCreateRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static ToolExecutionResult Ok(string tool, string input, string output) => new(tool, input, output);

    private static ToolExecutionResult Error(string tool, string input, string output) => new(tool, input, output, IsError: true);
}
