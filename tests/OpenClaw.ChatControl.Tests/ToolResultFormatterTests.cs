using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.Tests;

public class ToolResultFormatterTests
{
    [Fact]
    public void Format_NullInput_ReturnsEmpty()
    {
        Assert.Equal("", ToolResultFormatter.Format(null));
    }

    [Fact]
    public void Format_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", ToolResultFormatter.Format(""));
    }

    [Fact]
    public void Format_WhitespaceInput_ReturnsOriginal()
    {
        Assert.Equal("   ", ToolResultFormatter.Format("   "));
    }

    [Fact]
    public void Format_ValidJson_PrettyPrints()
    {
        var input = """{"key":"value","num":42}""";
        var result = ToolResultFormatter.Format(input);
        Assert.Contains("\"key\": \"value\"", result);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void Format_JsonArray_PrettyPrints()
    {
        var input = """[1,2,3]""";
        var result = ToolResultFormatter.Format(input);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void Format_InvalidJson_ReturnsAsIs()
    {
        var input = "{not valid json";
        Assert.Equal(input, ToolResultFormatter.Format(input));
    }

    [Fact]
    public void Format_ErrorPrefix_TruncatesToFirstLine()
    {
        var input = "Error: something went wrong\nStack trace line 1\nStack trace line 2";
        var result = ToolResultFormatter.Format(input);
        Assert.Equal("Error: something went wrong", result);
    }

    [Fact]
    public void Format_LongError_TruncatesAt200()
    {
        var longMsg = "Error: " + new string('x', 300);
        var result = ToolResultFormatter.Format(longMsg);
        Assert.Equal(203, result.Length); // 200 + "..."
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void Format_PlainText_ReturnsAsIs()
    {
        var input = "Some regular output text";
        Assert.Equal(input, ToolResultFormatter.Format(input));
    }

    [Fact]
    public void Format_JsonWithLeadingWhitespace_StillPrettyPrints()
    {
        var input = "  {\"a\":1}";
        var result = ToolResultFormatter.Format(input);
        Assert.Contains("\"a\": 1", result);
    }

    [Fact]
    public void Format_ToolNameParam_DoesNotAffectBasicBehavior()
    {
        var input = "plain text";
        Assert.Equal(input, ToolResultFormatter.Format(input, "bash"));
    }
}
