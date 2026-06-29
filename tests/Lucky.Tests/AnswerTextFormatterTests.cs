using Lucky.Core;

namespace Lucky.Tests;

public sealed class AnswerTextFormatterTests
{
    [Fact]
    public void ForPlainChat_RemovesDecorativeMarkdownArtifacts()
    {
        var formatted = AnswerTextFormatter.ForPlainChat(
            """
            ---

            ## 🎮 Latest Xbox Headlines

            ### 1. **Updated Xbox Console Prices**
            Xbox has announced **updated console pricing**.

            ***
            """);

        Assert.DoesNotContain("---", formatted);
        Assert.DoesNotContain("***", formatted);
        Assert.DoesNotContain("##", formatted);
        Assert.DoesNotContain("**", formatted);
        Assert.Contains("Latest Xbox Headlines", formatted);
        Assert.Contains("1. Updated Xbox Console Prices", formatted);
    }
}
