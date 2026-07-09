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

    Task<ToolExecutionResult> ApplyPatchAsync(
        LuckyProject project,
        string patch,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectFileToolService : IProjectFileToolService
{
    private const long MaxReadBytes = 256 * 1024;
    private const long MaxWriteBytes = 512 * 1024;
    private const long MaxPatchBytes = 512 * 1024;
    private const int MaxListEntries = 200;
    private const int MaxSearchResults = 80;
    private const int MaxPatchFiles = 32;
    private const int MaxPatchHunks = 256;

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
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("project.search", query, "Search query is required.");
        }

        return await GuardAsync(project, relativePath ?? ".", "project.search", async (root, target, input) =>
        {
            var regex = TryCreateRegex(query);
            var results = new List<string>();
            var scannedFiles = 0;

            foreach (var file in EnumerateSearchFiles(target, glob))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scannedFiles++ >= 2000)
                {
                    break;
                }

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

    public async Task<ToolExecutionResult> ApplyPatchAsync(
        LuckyProject project,
        string patch,
        CancellationToken cancellationToken = default)
    {
        const string tool = "project.apply_patch";
        const string input = "unified diff";
        if (Encoding.UTF8.GetByteCount(patch) > MaxPatchBytes)
        {
            return Error(tool, input, $"Patch is larger than {MaxPatchBytes} bytes.");
        }

        try
        {
            var root = NormalizeRoot(project.Path);
            var patchFiles = UnifiedDiffPatch.Parse(patch);
            if (patchFiles.Count > MaxPatchFiles)
            {
                throw new InvalidOperationException($"Patch contains more than {MaxPatchFiles} files.");
            }

            var hunkCount = patchFiles.Sum(file => file.Hunks.Count);
            if (hunkCount > MaxPatchHunks)
            {
                throw new InvalidOperationException($"Patch contains more than {MaxPatchHunks} hunks.");
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var plannedTargets = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var plans = new List<PatchPlan>();

            foreach (var patchFile in patchFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldRelativePath = NormalizePatchPath(patchFile.OldPath, 'a');
                var newRelativePath = NormalizePatchPath(patchFile.NewPath, 'b');
                if (oldRelativePath is not null &&
                    newRelativePath is not null &&
                    !string.Equals(oldRelativePath, newRelativePath, comparison))
                {
                    throw new InvalidOperationException(
                        $"Rename patch from '{oldRelativePath}' to '{newRelativePath}' is not supported. Split it into a delete and an add.");
                }

                var relativePath = newRelativePath ?? oldRelativePath;
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    throw new InvalidOperationException("Patch file path is missing.");
                }

                var target = ResolveUnderRoot(root, relativePath);
                EnsureNoReparseEscape(root, target);
                if (!plannedTargets.Add(target))
                {
                    throw new InvalidOperationException($"Patch changes '{relativePath}' more than once. Combine its hunks into one file section.");
                }

                if (Directory.Exists(target))
                {
                    throw new InvalidOperationException($"Patch target '{relativePath}' is a directory.");
                }

                if (oldRelativePath is null)
                {
                    if (File.Exists(target))
                    {
                        throw new InvalidOperationException($"New patch target '{relativePath}' already exists.");
                    }

                    var applied = UnifiedDiffPatch.Apply("", patchFile.Hunks);
                    EnsurePatchOutputIsWithinLimit(relativePath, applied.Content);
                    plans.Add(new PatchPlan(
                        relativePath,
                        target,
                        PatchOperation.Create,
                        null,
                        applied.Content,
                        applied.HunkCount,
                        applied.OffsetMatchedHunks));
                    continue;
                }

                if (!File.Exists(target))
                {
                    throw new InvalidOperationException($"Patch source '{relativePath}' does not exist.");
                }

                var refusal = await ValidateReadableTextFileAsync(target, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                {
                    throw new InvalidOperationException($"Patch source '{relativePath}' cannot be read: {refusal}");
                }

                var original = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
                var result = UnifiedDiffPatch.Apply(original, patchFile.Hunks);
                if (newRelativePath is null)
                {
                    if (result.Content.Length != 0)
                    {
                        throw new InvalidOperationException($"Delete patch for '{relativePath}' did not remove all file content.");
                    }

                    plans.Add(new PatchPlan(
                        relativePath,
                        target,
                        PatchOperation.Delete,
                        original,
                        null,
                        result.HunkCount,
                        result.OffsetMatchedHunks));
                    continue;
                }

                if (string.Equals(original, result.Content, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Patch for '{relativePath}' does not change the file.");
                }

                EnsurePatchOutputIsWithinLimit(relativePath, result.Content);
                plans.Add(new PatchPlan(
                    relativePath,
                    target,
                    PatchOperation.Modify,
                    original,
                    result.Content,
                    result.HunkCount,
                    result.OffsetMatchedHunks));
            }

            await CommitPatchPlansAsync(root, plans, cancellationToken).ConfigureAwait(false);
            return Ok(tool, $"{plans.Count} file(s)", DescribePatchResult(plans));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Error(tool, input, $"Patch was not applied: {ex.Message}");
        }
    }

    private static string? NormalizePatchPath(string? patchPath, char gitPrefix)
    {
        if (patchPath is null)
        {
            return null;
        }

        var normalized = patchPath.Replace('/', Path.DirectorySeparatorChar);
        var prefix = gitPrefix + Path.DirectorySeparatorChar.ToString();
        if (normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            normalized = normalized[prefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Patch file path is empty.");
        }

        return normalized;
    }

    private static void EnsurePatchOutputIsWithinLimit(string relativePath, string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
        {
            throw new InvalidOperationException($"Patched file '{relativePath}' would be larger than {MaxWriteBytes} bytes.");
        }
    }

    private static async Task CommitPatchPlansAsync(
        string root,
        IReadOnlyList<PatchPlan> plans,
        CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var stagedFiles = new Dictionary<string, string>(comparison);
        var committedPlans = new List<PatchPlan>();
        try
        {
            foreach (var plan in plans.Where(plan => plan.Operation is PatchOperation.Create or PatchOperation.Modify))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(plan.TargetPath);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    throw new InvalidOperationException($"Could not resolve a parent directory for '{plan.RelativePath}'.");
                }

                EnsureNoReparseEscape(root, plan.TargetPath);
                Directory.CreateDirectory(parent);
                EnsureNoReparseEscape(root, plan.TargetPath);
                var temporaryPath = Path.Combine(parent, $".lucky-patch-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(temporaryPath, plan.UpdatedContent!, cancellationToken).ConfigureAwait(false);
                stagedFiles.Add(plan.TargetPath, temporaryPath);
            }

            await EnsurePlansAreUnchangedAsync(root, plans, cancellationToken).ConfigureAwait(false);
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoReparseEscape(root, plan.TargetPath);
                switch (plan.Operation)
                {
                    case PatchOperation.Create:
                        File.Move(stagedFiles[plan.TargetPath], plan.TargetPath);
                        break;
                    case PatchOperation.Modify:
                        File.Move(stagedFiles[plan.TargetPath], plan.TargetPath, overwrite: true);
                        break;
                    case PatchOperation.Delete:
                        File.Delete(plan.TargetPath);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown patch operation.");
                }

                committedPlans.Add(plan);
            }
        }
        catch (Exception commitException)
        {
            var rollbackError = await RestoreCommittedPlansAsync(root, committedPlans).ConfigureAwait(false);
            if (rollbackError is not null)
            {
                throw new IOException(
                    $"{commitException.Message} Lucky could not fully restore the original files: {rollbackError.Message}",
                    commitException);
            }

            throw;
        }
        finally
        {
            foreach (var temporaryPath in stagedFiles.Values)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch (IOException)
                {
                    // A temporary file is harmless if a concurrent process prevents cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the original patch result rather than hiding it behind cleanup failure.
                }
            }
        }
    }

    private static async Task EnsurePlansAreUnchangedAsync(
        string root,
        IEnumerable<PatchPlan> plans,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparseEscape(root, plan.TargetPath);
            if (plan.Operation == PatchOperation.Create)
            {
                if (File.Exists(plan.TargetPath) || Directory.Exists(plan.TargetPath))
                {
                    throw new IOException($"Patch target '{plan.RelativePath}' appeared while the patch was being prepared.");
                }

                continue;
            }

            if (!File.Exists(plan.TargetPath))
            {
                throw new IOException($"Patch source '{plan.RelativePath}' changed or disappeared while the patch was being prepared.");
            }

            var current = await File.ReadAllTextAsync(plan.TargetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, plan.OriginalContent, StringComparison.Ordinal))
            {
                throw new IOException($"Patch source '{plan.RelativePath}' changed while the patch was being prepared.");
            }
        }
    }

    private static async Task<Exception?> RestoreCommittedPlansAsync(
        string root,
        IEnumerable<PatchPlan> committedPlans)
    {
        Exception? firstError = null;
        foreach (var plan in committedPlans.Reverse())
        {
            try
            {
                EnsureNoReparseEscape(root, plan.TargetPath);
                if (plan.OriginalContent is null)
                {
                    if (File.Exists(plan.TargetPath))
                    {
                        File.Delete(plan.TargetPath);
                    }

                    continue;
                }

                var parent = Path.GetDirectoryName(plan.TargetPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                await File.WriteAllTextAsync(plan.TargetPath, plan.OriginalContent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                firstError ??= ex;
            }
        }

        return firstError;
    }

    private static string DescribePatchResult(IReadOnlyCollection<PatchPlan> plans)
    {
        var created = plans.Count(plan => plan.Operation == PatchOperation.Create);
        var modified = plans.Count(plan => plan.Operation == PatchOperation.Modify);
        var deleted = plans.Count(plan => plan.Operation == PatchOperation.Delete);
        var offsetMatchedHunks = plans.Sum(plan => plan.OffsetMatchedHunks);
        var actions = new List<string>();
        if (created > 0)
        {
            actions.Add($"created {created}");
        }

        if (modified > 0)
        {
            actions.Add($"modified {modified}");
        }

        if (deleted > 0)
        {
            actions.Add($"deleted {deleted}");
        }

        var files = string.Join(", ", plans.Select(plan => plan.RelativePath));
        var offsetNote = offsetMatchedHunks == 0
            ? ""
            : $" {offsetMatchedHunks} hunk(s) used a unique nearby context match.";
        return $"Applied unified diff: {string.Join(", ", actions)} file(s): {files}.{offsetNote}";
    }

    private enum PatchOperation
    {
        Create,
        Modify,
        Delete
    }

    private sealed record PatchPlan(
        string RelativePath,
        string TargetPath,
        PatchOperation Operation,
        string? OriginalContent,
        string? UpdatedContent,
        int HunkCount,
        int OffsetMatchedHunks);

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
            foreach (var childDirectory in Directory.EnumerateDirectories(directory)
                         .Where(path => !IsExcludedPath(path) && !HasReparsePoint(path)))
            {
                pending.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory, pattern).Where(path => !HasReparsePoint(path)))
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
