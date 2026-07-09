using Lucky.Core;

namespace Lucky.Tests;

public sealed class CommandLineArgumentParserTests
{
    [Fact]
    public void Parse_SupportsQuotedPathAndEscapedQuoteWithoutInvokingAShell()
    {
        var parsed = CommandLineArgumentParser.Parse("-y @modelcontextprotocol/server-filesystem \"C:\\Lucky Files\" \"say \\\"hello\\\"\"");

        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\Lucky Files", "say \"hello\""], parsed);
    }

    [Fact]
    public void Parse_RejectsUnmatchedQuotes()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CommandLineArgumentParser.Parse("--root \"C:\\Lucky"));

        Assert.Contains("unmatched", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
