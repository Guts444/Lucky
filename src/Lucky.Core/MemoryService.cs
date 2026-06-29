using System.Text.RegularExpressions;

namespace Lucky.Core;

public sealed class MemoryService
{
    private static readonly Regex TokenRegex = new("[a-z0-9][a-z0-9_'-]{1,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretRegex = new("(api[_ -]?key|password|token|secret|bearer\\s+[a-z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] StopWords =
    [
        "about", "after", "again", "also", "because", "been", "being", "chat", "could", "from", "have",
        "into", "just", "like", "more", "that", "their", "them", "then", "there", "this", "want", "what",
        "when", "where", "which", "with", "would", "your"
    ];

    public IReadOnlyList<MemoryItem> RetrieveRelevant(
        IEnumerable<MemoryItem> memories,
        string query,
        string? projectId,
        int limit)
    {
        var queryVector = Vectorize(query);
        if (queryVector.Count == 0)
        {
            return memories
                .Where(memory => memory.Enabled && (memory.Pinned || memory.ProjectId == projectId || memory.ProjectId is null))
                .OrderByDescending(memory => memory.Pinned)
                .ThenByDescending(memory => memory.UpdatedAt)
                .Take(Math.Max(1, limit))
                .ToArray();
        }

        var now = DateTimeOffset.UtcNow;
        return memories
            .Where(memory => memory.Enabled)
            .Select(memory => new
            {
                Memory = memory,
                Score = Score(memory, queryVector, projectId, now)
            })
            .Where(item => item.Score > 0.08 || item.Memory.Pinned)
            .OrderByDescending(item => item.Score)
            .Take(Math.Max(1, limit))
            .Select(item => item.Memory)
            .ToArray();
    }

    public IReadOnlyList<MemoryItem> CaptureFromUserMessage(string userMessage, string? projectId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || SecretRegex.IsMatch(userMessage))
        {
            return [];
        }

        var trimmed = userMessage.Trim();
        var explicitRemember = MatchPhrase(trimmed, "^/remember\\s+(.+)$");
        var rememberThat = MatchPhrase(trimmed, "\\bremember\\s+(?:that\\s+)?(.+)$");
        var preference = MatchPhrase(trimmed, "\\b(i\\s+(?:prefer|like|want|need|usually|always|never)\\b.+)$");
        var identity = MatchPhrase(trimmed, "\\b(my\\s+(?:name|handle|timezone|project|machine|pc|setup)\\s+is\\b.+)$");
        var correction = MatchPhrase(trimmed, "\\b(?:call\\s+me|don't\\s+call\\s+me|do\\s+not\\s+call\\s+me)\\b.+$");

        var summary = FirstNonEmpty(explicitRemember, rememberThat, preference, identity, correction);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return [];
        }

        summary = NormalizeSummary(summary);
        if (summary.Length < 8)
        {
            return [];
        }

        return
        [
            new MemoryItem
            {
                Kind = identity is not null || preference is not null || correction is not null
                    ? MemoryKind.UserProfile
                    : MemoryKind.Memory,
                Summary = summary,
                Evidence = trimmed,
                ProjectId = projectId,
                SourceSessionId = sessionId,
                Tags = ExtractTags(summary),
                Confidence = explicitRemember is not null || rememberThat is not null ? 0.85 : 0.68
            }
        ];
    }

    public void MergeCapturedMemories(ICollection<MemoryItem> target, IEnumerable<MemoryItem> captured)
    {
        foreach (var memory in captured)
        {
            var duplicate = target.FirstOrDefault(existing =>
                existing.Enabled &&
                existing.Kind == memory.Kind &&
                string.Equals(NormalizeSummary(existing.Summary), NormalizeSummary(memory.Summary), StringComparison.OrdinalIgnoreCase));

            if (duplicate is not null)
            {
                duplicate.Evidence = memory.Evidence;
                duplicate.ProjectId ??= memory.ProjectId;
                duplicate.SourceSessionId = memory.SourceSessionId ?? duplicate.SourceSessionId;
                duplicate.UpdatedAt = DateTimeOffset.UtcNow;
                duplicate.Confidence = Math.Max(duplicate.Confidence, memory.Confidence);
                duplicate.Tags = duplicate.Tags.Union(memory.Tags, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
                continue;
            }

            target.Add(memory);
        }
    }

    private static double Score(MemoryItem memory, Dictionary<string, double> queryVector, string? projectId, DateTimeOffset now)
    {
        var memoryText = $"{memory.Summary} {string.Join(' ', memory.Tags)} {memory.Evidence}";
        var memoryVector = Vectorize(memoryText);
        var cosine = Cosine(queryVector, memoryVector);
        var projectBoost = memory.ProjectId == projectId ? 0.18 : memory.ProjectId is null ? 0.04 : 0;
        var pinBoost = memory.Pinned ? 0.35 : 0;
        var confidenceBoost = Math.Clamp(memory.Confidence, 0, 1) * 0.12;
        var ageDays = Math.Max(0, (now - memory.UpdatedAt).TotalDays);
        var recencyBoost = Math.Max(0, 0.08 - ageDays / 365);
        return cosine + projectBoost + pinBoost + confidenceBoost + recencyBoost;
    }

    private static Dictionary<string, double> Vectorize(string text)
    {
        var stopWords = StopWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex.Matches(text.ToLowerInvariant()))
        {
            var token = match.Value.Trim('\'', '-');
            if (token.Length < 3 || stopWords.Contains(token))
            {
                continue;
            }

            vector[token] = vector.GetValueOrDefault(token) + 1;
        }

        return vector;
    }

    private static double Cosine(Dictionary<string, double> left, Dictionary<string, double> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var dot = left.Sum(pair => pair.Value * right.GetValueOrDefault(pair.Key));
        var leftMag = Math.Sqrt(left.Values.Sum(value => value * value));
        var rightMag = Math.Sqrt(right.Values.Sum(value => value * value));
        return leftMag == 0 || rightMag == 0 ? 0 : dot / (leftMag * rightMag);
    }

    private static string? MatchPhrase(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : match.Value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeSummary(string summary)
    {
        summary = Regex.Replace(summary.Trim(), "\\s+", " ");
        if (summary.EndsWith(".", StringComparison.Ordinal))
        {
            summary = summary[..^1];
        }

        return summary;
    }

    private static List<string> ExtractTags(string summary)
    {
        return Vectorize(summary)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .Take(8)
            .ToList();
    }
}
