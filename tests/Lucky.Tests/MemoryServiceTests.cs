using Lucky.Core;

namespace Lucky.Tests;

public sealed class MemoryServiceTests
{
    [Fact]
    public void CaptureFromUserMessage_CapturesExplicitMemoryWithMetadata()
    {
        var service = new MemoryService();

        var captured = service.CaptureFromUserMessage(
            "/remember I prefer concise answers in code reviews.",
            "project_1",
            "chat_1");

        var memory = Assert.Single(captured);
        Assert.Equal("I prefer concise answers in code reviews", memory.Summary);
        Assert.Equal("/remember I prefer concise answers in code reviews.", memory.Evidence);
        Assert.Equal("project_1", memory.ProjectId);
        Assert.Equal("chat_1", memory.SourceSessionId);
        Assert.Equal(MemoryKind.UserProfile, memory.Kind);
        Assert.True(memory.Confidence >= 0.85);
        Assert.Contains("concise", memory.Tags);
        Assert.Contains("reviews", memory.Tags);
    }

    [Fact]
    public void CaptureFromUserMessage_ClassifiesPreferencesAsUserProfile()
    {
        var service = new MemoryService();

        var captured = service.CaptureFromUserMessage(
            "I prefer Extra High reasoning for coding tasks.",
            "project_1",
            "chat_1");

        var memory = Assert.Single(captured);
        Assert.Equal(MemoryKind.UserProfile, memory.Kind);
        Assert.Equal("I prefer Extra High reasoning for coding tasks", memory.Summary);
    }

    [Fact]
    public void CaptureFromUserMessage_IgnoresMessagesThatLookLikeSecrets()
    {
        var service = new MemoryService();

        var captured = service.CaptureFromUserMessage(
            "remember that my api key is abc123",
            "project_1",
            "chat_1");

        Assert.Empty(captured);
    }

    [Fact]
    public void RetrieveRelevant_RanksProjectMemoryWithoutMutatingUsageMetadata()
    {
        var service = new MemoryService();
        var now = DateTimeOffset.UtcNow;
        var projectMemory = new MemoryItem
        {
            Summary = "User prefers vim keybindings for editing code",
            Tags = ["vim", "keybindings", "editing"],
            Evidence = "I always use vim keybindings.",
            ProjectId = "project_a",
            Confidence = 1,
            UpdatedAt = now.AddDays(-20)
        };
        var otherProjectMemory = new MemoryItem
        {
            Summary = "User prefers vim keybindings for editing code",
            Tags = ["vim", "keybindings", "editing"],
            Evidence = "I always use vim keybindings.",
            ProjectId = "project_b",
            Confidence = 1,
            UpdatedAt = now.AddDays(-1)
        };
        var pinnedUnrelated = new MemoryItem
        {
            Summary = "Keep the weekly planning note visible",
            Tags = ["planning"],
            Evidence = "Remember the planning note.",
            ProjectId = "project_b",
            Pinned = true,
            Confidence = 0.7,
            UpdatedAt = now
        };
        var disabledMemory = new MemoryItem
        {
            Summary = "User prefers vim keybindings",
            Tags = ["vim", "keybindings"],
            Evidence = "Disabled memory should not be recalled.",
            ProjectId = "project_a",
            Enabled = false,
            Confidence = 1,
            UpdatedAt = now
        };

        var results = service.RetrieveRelevant(
            [otherProjectMemory, disabledMemory, pinnedUnrelated, projectMemory],
            "vim keybindings in the editor",
            "project_a",
            limit: 3);

        Assert.Equal(projectMemory.Id, results[0].Id);
        Assert.Contains(results, memory => memory.Id == otherProjectMemory.Id);
        Assert.Contains(results, memory => memory.Id == pinnedUnrelated.Id);
        Assert.DoesNotContain(results, memory => memory.Id == disabledMemory.Id);
        Assert.Null(results[0].LastUsedAt);
    }

    [Fact]
    public void MergeCapturedMemories_DeduplicatesNormalizedSummariesAndMergesMetadata()
    {
        var service = new MemoryService();
        var existing = new MemoryItem
        {
            Summary = "I prefer concise answers",
            Evidence = "old evidence",
            ProjectId = null,
            SourceSessionId = "old_chat",
            Tags = ["concise"],
            Confidence = 0.5,
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var captured = new MemoryItem
        {
            Summary = "I prefer concise answers.",
            Evidence = "new evidence",
            ProjectId = "project_1",
            SourceSessionId = "new_chat",
            Tags = ["answers", "concise"],
            Confidence = 0.85
        };
        var target = new List<MemoryItem> { existing };

        service.MergeCapturedMemories(target, [captured]);

        var merged = Assert.Single(target);
        Assert.Same(existing, merged);
        Assert.Equal("new evidence", merged.Evidence);
        Assert.Equal("project_1", merged.ProjectId);
        Assert.Equal("new_chat", merged.SourceSessionId);
        Assert.Equal(0.85, merged.Confidence);
        Assert.Contains("concise", merged.Tags);
        Assert.Contains("answers", merged.Tags);
    }
}
