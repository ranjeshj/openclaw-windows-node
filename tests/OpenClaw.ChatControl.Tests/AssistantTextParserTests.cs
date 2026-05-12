using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.Tests;

public class AssistantTextParserTests
{
    [Fact]
    public void Parse_PlainText_ReturnsSingleNonThinkingSegment()
    {
        var result = AssistantTextParser.Parse("Hello, world!");

        Assert.Single(result);
        Assert.Equal("Hello, world!", result[0].Text);
        Assert.False(result[0].IsThinking);
    }

    [Fact]
    public void Parse_ThinkThenResponse_ReturnsTwoSegments()
    {
        var result = AssistantTextParser.Parse("<think>reasoning</think>response");

        Assert.Equal(2, result.Count);
        Assert.Equal("reasoning", result[0].Text);
        Assert.True(result[0].IsThinking);
        Assert.Equal("response", result[1].Text);
        Assert.False(result[1].IsThinking);
    }

    [Fact]
    public void Parse_UnclosedThinkTag_TreatsRestAsThinking()
    {
        var result = AssistantTextParser.Parse("<think>still thinking...");

        Assert.Single(result);
        Assert.Equal("still thinking...", result[0].Text);
        Assert.True(result[0].IsThinking);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyList()
    {
        var result = AssistantTextParser.Parse("");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NullString_ReturnsEmptyList()
    {
        var result = AssistantTextParser.Parse(null!);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_MultipleThinkBlocks_ReturnsAllSegments()
    {
        var result = AssistantTextParser.Parse("intro<think>thought1</think>middle<think>thought2</think>end");

        Assert.Equal(5, result.Count);
        Assert.Equal("intro", result[0].Text);
        Assert.False(result[0].IsThinking);
        Assert.Equal("thought1", result[1].Text);
        Assert.True(result[1].IsThinking);
        Assert.Equal("middle", result[2].Text);
        Assert.False(result[2].IsThinking);
        Assert.Equal("thought2", result[3].Text);
        Assert.True(result[3].IsThinking);
        Assert.Equal("end", result[4].Text);
        Assert.False(result[4].IsThinking);
    }

    [Fact]
    public void Parse_CaseInsensitiveTags()
    {
        var result = AssistantTextParser.Parse("<THINK>reasoning</THINK>response");

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsThinking);
        Assert.False(result[1].IsThinking);
    }
}
