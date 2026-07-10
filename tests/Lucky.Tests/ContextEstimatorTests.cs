using Lucky.Core;

namespace Lucky.Tests;

public sealed class ContextEstimatorTests
{
    [Fact]
    public void EstimateSessionTokens_IncludesVisibleTextAndChatMessageFraming()
    {
        var session = new ChatSession
        {
            Messages =
            [
                new ChatMessage { Role = ChatRole.User, Content = "abcd" },
                new ChatMessage { Role = ChatRole.Assistant, Content = "efghijkl" }
            ]
        };

        // 1 token for four user characters, 2 for eight assistant characters, plus four
        // framing tokens per chat message.
        Assert.Equal(11, ContextEstimator.EstimateSessionTokens(session));
    }
}
