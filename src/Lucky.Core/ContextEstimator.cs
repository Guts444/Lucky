namespace Lucky.Core;

public static class ContextEstimator
{
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    public static int EstimateSessionTokens(ChatSession? session)
    {
        if (session is null)
        {
            return 0;
        }

        // Chat-completions providers add message-role framing in addition to the visible text.
        // This remains intentionally an estimate; Lucky switches to the provider-reported input
        // count as soon as a response supplies one.
        return session.Messages.Sum(message => EstimateTokens(message.Content) + 4);
    }

    public static int EstimateMemoryChars(IEnumerable<MemoryItem> memories, MemoryKind kind)
    {
        return memories
            .Where(memory => memory.Enabled && memory.Kind == kind)
            .Sum(memory => memory.Summary.Length);
    }
}
