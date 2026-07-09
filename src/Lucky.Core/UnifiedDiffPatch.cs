using System.Text.RegularExpressions;

namespace Lucky.Core;

internal sealed record UnifiedDiffFile(
    string? OldPath,
    string? NewPath,
    IReadOnlyList<UnifiedDiffHunk> Hunks);

internal sealed class UnifiedDiffHunk
{
    public UnifiedDiffHunk(
        int oldStart,
        int oldCount,
        int newStart,
        int newCount,
        IReadOnlyList<UnifiedDiffLine> lines)
    {
        OldStart = oldStart;
        OldCount = oldCount;
        NewStart = newStart;
        NewCount = newCount;
        Lines = lines;
    }

    public int OldStart { get; }

    public int OldCount { get; }

    public int NewStart { get; }

    public int NewCount { get; }

    public IReadOnlyList<UnifiedDiffLine> Lines { get; }

    public bool OldEndsWithoutNewline { get; set; }

    public bool NewEndsWithoutNewline { get; set; }
}

internal sealed record UnifiedDiffLine(char Kind, string Text);

internal sealed record UnifiedDiffApplyResult(string Content, int HunkCount, int OffsetMatchedHunks);

/// <summary>
/// Parses and applies a deliberately narrow, text-only subset of unified diff. The caller owns
/// filesystem authorization and transaction handling; this type only operates on text.
/// </summary>
internal static class UnifiedDiffPatch
{
    private const int FuzzySearchLineWindow = 120;

    private static readonly Regex HunkHeader = new(
        "^@@ -(?<oldStart>\\d+)(,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(,(?<newCount>\\d+))? @@(?:.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public static IReadOnlyList<UnifiedDiffFile> Parse(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            throw new InvalidOperationException("A unified diff patch is required.");
        }

        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var files = new List<UnifiedDiffFile>();
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (IsIgnorableOuterLine(line))
            {
                index++;
                continue;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Binary patches are not supported.");
            }

            if (!line.StartsWith("--- ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a '---' file header on patch line {index + 1}.");
            }

            var oldPath = ParseFilePath(line[4..], index + 1);
            index++;
            if (index >= lines.Length || !lines[index].StartsWith("+++ ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a '+++' file header after patch line {index}.");
            }

            var newPath = ParseFilePath(lines[index][4..], index + 1);
            index++;
            if (oldPath is null && newPath is null)
            {
                throw new InvalidOperationException("A patch cannot use /dev/null for both file paths.");
            }

            var hunks = new List<UnifiedDiffHunk>();
            while (index < lines.Length && lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                hunks.Add(ParseHunk(lines, ref index));
            }

            if (oldPath is not null && newPath is not null && hunks.Count == 0)
            {
                throw new InvalidOperationException($"The patch for '{newPath}' does not contain a hunk.");
            }

            files.Add(new UnifiedDiffFile(oldPath, newPath, hunks));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("No unified-diff file sections were found.");
        }

        return files;
    }

    public static UnifiedDiffApplyResult Apply(string originalContent, IReadOnlyList<UnifiedDiffHunk> hunks)
    {
        var text = PatchText.Parse(originalContent);
        var lineOffset = 0;
        var offsetMatchedHunks = 0;

        for (var hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
        {
            var hunk = hunks[hunkIndex];
            var expectedOldLines = hunk.Lines
                .Where(line => line.Kind is ' ' or '-')
                .Select(line => line.Text)
                .ToArray();
            var replacementLines = hunk.Lines
                .Where(line => line.Kind is ' ' or '+')
                .Select(line => line.Text)
                .ToArray();
            var expectedIndex = hunk.OldStart == 0
                ? 0
                : hunk.OldStart - 1 + lineOffset;
            var match = FindMatch(text.Lines, expectedOldLines, expectedIndex, hunkIndex + 1, hunk.OldStart);
            if (match.UsedOffsetMatch)
            {
                offsetMatchedHunks++;
            }

            if (hunk.OldEndsWithoutNewline &&
                match.Index + expectedOldLines.Length == text.Lines.Count &&
                text.EndsWithNewline)
            {
                throw new InvalidOperationException($"Hunk {hunkIndex + 1} expected the old file to end without a newline.");
            }

            text.Lines.RemoveRange(match.Index, expectedOldLines.Length);
            text.Lines.InsertRange(match.Index, replacementLines);
            lineOffset += replacementLines.Length - expectedOldLines.Length;

            if (match.Index + replacementLines.Length == text.Lines.Count && text.Lines.Count > 0)
            {
                text.EndsWithNewline = !hunk.NewEndsWithoutNewline;
            }
        }

        return new UnifiedDiffApplyResult(text.Render(), hunks.Count, offsetMatchedHunks);
    }

    private static UnifiedDiffHunk ParseHunk(string[] lines, ref int index)
    {
        var headerLine = index + 1;
        var match = HunkHeader.Match(lines[index]);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Invalid hunk header on patch line {headerLine}.");
        }

        var oldStart = ParseNumber(match.Groups["oldStart"].Value, "old start", headerLine);
        var oldCount = match.Groups["oldCount"].Success
            ? ParseNumber(match.Groups["oldCount"].Value, "old count", headerLine)
            : 1;
        var newStart = ParseNumber(match.Groups["newStart"].Value, "new start", headerLine);
        var newCount = match.Groups["newCount"].Success
            ? ParseNumber(match.Groups["newCount"].Value, "new count", headerLine)
            : 1;
        index++;

        var hunkLines = new List<UnifiedDiffLine>();
        UnifiedDiffHunk? hunk = null;
        var oldSeen = 0;
        var newSeen = 0;
        char? previousKind = null;
        while (oldSeen < oldCount || newSeen < newCount)
        {
            if (index >= lines.Length)
            {
                throw new InvalidOperationException($"Hunk starting on patch line {headerLine} ended early.");
            }

            var line = lines[index];
            if (line == "\\ No newline at end of file")
            {
                if (previousKind is null)
                {
                    throw new InvalidOperationException($"A no-newline marker on patch line {index + 1} has no preceding patch line.");
                }

                hunk ??= new UnifiedDiffHunk(oldStart, oldCount, newStart, newCount, hunkLines);
                MarkNoNewline(hunk, previousKind.Value);
                index++;
                continue;
            }

            if (line.Length == 0 || line[0] is not (' ' or '+' or '-'))
            {
                throw new InvalidOperationException($"Invalid hunk line on patch line {index + 1}.");
            }

            var kind = line[0];
            if (kind is ' ' or '-')
            {
                oldSeen++;
            }

            if (kind is ' ' or '+')
            {
                newSeen++;
            }

            if (oldSeen > oldCount || newSeen > newCount)
            {
                throw new InvalidOperationException($"Hunk starting on patch line {headerLine} exceeds its declared line counts.");
            }

            hunkLines.Add(new UnifiedDiffLine(kind, line[1..]));
            previousKind = kind;
            index++;
        }

        hunk ??= new UnifiedDiffHunk(oldStart, oldCount, newStart, newCount, hunkLines);
        while (index < lines.Length && lines[index] == "\\ No newline at end of file")
        {
            if (previousKind is null)
            {
                throw new InvalidOperationException($"A no-newline marker on patch line {index + 1} has no preceding patch line.");
            }

            MarkNoNewline(hunk, previousKind.Value);
            index++;
        }

        return hunk;
    }

    private static (int Index, bool UsedOffsetMatch) FindMatch(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> expected,
        int expectedIndex,
        int hunkNumber,
        int sourceLine)
    {
        if (MatchesAt(lines, expected, expectedIndex))
        {
            return (expectedIndex, false);
        }

        if (expected.Count == 0)
        {
            throw new InvalidOperationException(
                $"Hunk {hunkNumber} is an insertion and its target line {sourceLine} is no longer valid. Re-read the file and send an updated patch.");
        }

        var first = Math.Max(0, expectedIndex - FuzzySearchLineWindow);
        var last = Math.Min(lines.Count - expected.Count, expectedIndex + FuzzySearchLineWindow);
        var found = -1;
        for (var candidate = first; candidate <= last; candidate++)
        {
            if (!MatchesAt(lines, expected, candidate))
            {
                continue;
            }

            if (found >= 0)
            {
                throw new InvalidOperationException(
                    $"Hunk {hunkNumber} matched more than once near line {sourceLine}; the patch is ambiguous and was not applied.");
            }

            found = candidate;
        }

        if (found < 0)
        {
            throw new InvalidOperationException(
                $"Hunk {hunkNumber} could not find its expected context near line {sourceLine}. Re-read the file and send an updated patch.");
        }

        return (found, true);
    }

    private static bool MatchesAt(IReadOnlyList<string> lines, IReadOnlyList<string> expected, int index)
    {
        if (index < 0 || index + expected.Count > lines.Count)
        {
            return false;
        }

        for (var lineIndex = 0; lineIndex < expected.Count; lineIndex++)
        {
            if (!string.Equals(lines[index + lineIndex], expected[lineIndex], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void MarkNoNewline(UnifiedDiffHunk hunk, char kind)
    {
        if (kind is ' ' or '-')
        {
            hunk.OldEndsWithoutNewline = true;
        }

        if (kind is ' ' or '+')
        {
            hunk.NewEndsWithoutNewline = true;
        }
    }

    private static string? ParseFilePath(string rawValue, int lineNumber)
    {
        var tabIndex = rawValue.IndexOf('\t');
        var value = (tabIndex >= 0 ? rawValue[..tabIndex] : rawValue).Trim();
        if (string.Equals(value, "/dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        if (value.Length == 0 || value.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException($"Invalid file path on patch line {lineNumber}.");
        }

        if (value.StartsWith('"') || value.EndsWith('"'))
        {
            throw new InvalidOperationException($"Quoted file paths are not supported (patch line {lineNumber}).");
        }

        return value;
    }

    private static int ParseNumber(string value, string description, int lineNumber)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new InvalidOperationException($"Invalid {description} in hunk header on patch line {lineNumber}.");
        }

        return parsed;
    }

    private static bool IsIgnorableOuterLine(string line)
    {
        return string.IsNullOrWhiteSpace(line) ||
               line == "```" ||
               line.StartsWith("```diff", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("```patch", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("diff --git ", StringComparison.Ordinal) ||
               line.StartsWith("index ", StringComparison.Ordinal) ||
               line.StartsWith("new file mode ", StringComparison.Ordinal) ||
               line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
               line.StartsWith("similarity index ", StringComparison.Ordinal) ||
               line.StartsWith("dissimilarity index ", StringComparison.Ordinal) ||
               line.StartsWith("old mode ", StringComparison.Ordinal) ||
               line.StartsWith("new mode ", StringComparison.Ordinal);
    }

    private sealed class PatchText
    {
        private PatchText(List<string> lines, bool endsWithNewline, string newline)
        {
            Lines = lines;
            EndsWithNewline = endsWithNewline;
            Newline = newline;
        }

        public List<string> Lines { get; }

        public bool EndsWithNewline { get; set; }

        public string Newline { get; }

        public static PatchText Parse(string content)
        {
            if (content.Length == 0)
            {
                return new PatchText([], false, Environment.NewLine);
            }

            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var endsWithNewline = normalized.EndsWith('\n');
            var body = endsWithNewline ? normalized[..^1] : normalized;
            var lines = body.Split('\n').ToList();
            return new PatchText(lines, endsWithNewline, newline);
        }

        public string Render()
        {
            if (Lines.Count == 0)
            {
                return "";
            }

            var text = string.Join(Newline, Lines);
            return EndsWithNewline ? text + Newline : text;
        }
    }
}
