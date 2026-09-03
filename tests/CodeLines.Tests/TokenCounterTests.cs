using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class TokenCounterTests
{
    [Theory]
    [InlineData("o200k_base")]
    [InlineData("cl100k_base")]
    public void Counts_known_single_token_text(string encoding)
    {
        var counter = new TokenCounter();
        Assert.Equal(1, counter.CountTokens("hello", encoding));
    }

    [Fact]
    public void Rejects_unknown_encoding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenCounter().CountTokens("hello", "unknown"));
    }
}

