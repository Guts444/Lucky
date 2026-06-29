using System.Text;

namespace Lucky.Core;

public sealed class AgentInstructionsService
{
    private const int MaxInstructionBytes = 32 * 1024;

    public async Task<AgentInstructionSet> LoadAsync(
        LuckyProject? project,
        CancellationToken cancellationToken = default)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Path))
        {
            return new AgentInstructionSet([]);
        }

        try
        {
            var root = Path.GetFullPath(project.Path);
            if (!Directory.Exists(root) || HasReparsePoint(root))
            {
                return new AgentInstructionSet([]);
            }

            foreach (var fileName in new[] { "AGENTS.override.md", "AGENTS.md" })
            {
                var candidate = Path.GetFullPath(Path.Combine(root, fileName));
                if (!IsWithinRoot(root, candidate) || !File.Exists(candidate) || HasReparsePoint(candidate))
                {
                    continue;
                }

                var content = await ReadInstructionFileAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return new AgentInstructionSet([new AgentInstructionDocument(fileName, content)]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new AgentInstructionSet([]);
        }

        return new AgentInstructionSet([]);
    }

    public static string RenderForPrompt(AgentInstructionSet instructions)
    {
        if (!instructions.HasInstructions)
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Project instructions from AGENTS.md:");
        foreach (var document in instructions.Documents)
        {
            builder.AppendLine($"--- {document.RelativePath} ---");
            builder.AppendLine(document.Content.Trim());
        }

        return builder.ToString().Trim();
    }

    private static async Task<string> ReadInstructionFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var bytesToRead = (int)Math.Min(info.Length, MaxInstructionBytes);
        var bytes = new byte[bytesToRead];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(bytes.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
        if (read == 0 || bytes.AsSpan(0, read).Contains((byte)0))
        {
            return "";
        }

        var content = Encoding.UTF8.GetString(bytes, 0, read);
        if (info.Length > MaxInstructionBytes)
        {
            content += $"{Environment.NewLine}{Environment.NewLine}[AGENTS.md truncated at {MaxInstructionBytes} bytes.]";
        }

        return content;
    }

    private static bool HasReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    private static bool IsWithinRoot(string root, string target)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedRoot, target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
               target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
               target.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
