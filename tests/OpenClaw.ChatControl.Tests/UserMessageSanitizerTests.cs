using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.Tests;

public class UserMessageSanitizerTests
{
    [Fact]
    public void Sanitize_Null_ReturnsEmpty()
    {
        Assert.Equal("", UserMessageSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_Empty_ReturnsEmpty()
    {
        Assert.Equal("", UserMessageSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_PlainText_Unchanged()
    {
        Assert.Equal("Hello world", UserMessageSanitizer.Sanitize("Hello world"));
    }

    [Fact]
    public void Sanitize_StripsChannelEnvelope()
    {
        var input = "[Channel 2024-01-15T10:30Z some-channel]\nActual message";
        var result = UserMessageSanitizer.Sanitize(input);
        Assert.Equal("Actual message", result);
    }

    [Fact]
    public void Sanitize_StripsMessageId()
    {
        var input = "[message_id: abc-123]\nHello";
        var result = UserMessageSanitizer.Sanitize(input);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Sanitize_StripsTimestampPrefix()
    {
        var input = "[Mon 2024-01-15 10:30 UTC] Hello";
        var result = UserMessageSanitizer.Sanitize(input);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Sanitize_NormalizesExcessiveNewlines()
    {
        var input = "Line1\n\n\n\n\nLine2";
        var result = UserMessageSanitizer.Sanitize(input);
        Assert.Equal("Line1\n\nLine2", result);
    }

    [Fact]
    public void Sanitize_StripsMultiplePrefixes()
    {
        var input = "[Channel 2024-01-15T10:30Z ch1]\n[message_id: xyz]\n[Mon 2024-01-15 10:30 UTC] Hello there";
        var result = UserMessageSanitizer.Sanitize(input);
        Assert.Equal("Hello there", result);
    }

    [Fact]
    public void Sanitize_TrimsWhitespace()
    {
        Assert.Equal("Hello", UserMessageSanitizer.Sanitize("  Hello  "));
    }
}
