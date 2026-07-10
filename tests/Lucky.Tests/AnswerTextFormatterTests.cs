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

    [Fact]
    public void ForPlainChat_HidesLegacyDsmlProtocolMarkup()
    {
        var formatted = AnswerTextFormatter.ForPlainChat(
            """
            <| | DSML | | tool_calls>
            <| | DSML | | invoke name="project_search">
            <| | DSML | | parameter name="query" string="true">TODO</| | DSML | | parameter>
            </| | DSML | | invoke>
            </| | DSML | | tool_calls>
            """);

        Assert.DoesNotContain("DSML", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project_search", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal protocol markup", formatted, StringComparison.OrdinalIgnoreCase);
    }
}
