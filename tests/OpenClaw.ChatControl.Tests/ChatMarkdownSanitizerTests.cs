using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.Tests;

public class ChatMarkdownSanitizerTests
{
    [Fact]
    public void Sanitize_Null_ReturnsEmpty()
    {
        Assert.Equal("", ChatMarkdownSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_Empty_ReturnsEmpty()
    {
        Assert.Equal("", ChatMarkdownSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_PlainText_Unchanged()
    {
        Assert.Equal("Hello world", ChatMarkdownSanitizer.Sanitize("Hello world"));
    }

    [Fact]
    public void Sanitize_Image_ReplacedWithPlaceholder()
    {
        var result = ChatMarkdownSanitizer.Sanitize("Check this ![photo](http://evil.com/track.png) out");
        Assert.Contains("[Image: photo]", result);
        Assert.DoesNotContain("http://evil.com", result);
    }

    [Fact]
    public void Sanitize_ImageNoAlt_ReplacedWithGenericPlaceholder()
    {
        var result = ChatMarkdownSanitizer.Sanitize("![](http://evil.com/track.png)");
        Assert.Equal("[Image]", result);
    }

    [Fact]
    public void Sanitize_Link_FlattenedToInertText()
    {
        var result = ChatMarkdownSanitizer.Sanitize("Visit [Google](https://google.com) now");
        Assert.Contains("Google (https://google.com)", result);
        Assert.DoesNotContain("[Google]", result);
    }

    [Fact]
    public void Sanitize_CodeBlock_Preserved()
    {
        var input = "Before\n```python\nprint('![hack](evil)')\n```\nAfter";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("![hack](evil)", result); // Inside code block, should be preserved
    }

    [Fact]
    public void Sanitize_InlineCode_Preserved()
    {
        var result = ChatMarkdownSanitizer.Sanitize("Use `[link](url)` syntax");
        Assert.Contains("`[link](url)`", result);
    }

    [Fact]
    public void Sanitize_HtmlTags_Stripped()
    {
        var result = ChatMarkdownSanitizer.Sanitize("Hello <script>alert('xss')</script> world");
        Assert.DoesNotContain("<script>", result);
        Assert.Contains("Hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void FlattenLinkToInertText_TextAndUrl()
    {
        Assert.Equal("Google (https://google.com)",
            ChatMarkdownSanitizer.FlattenLinkToInertText("Google", "https://google.com"));
    }

    [Fact]
    public void FlattenLinkToInertText_SameTextAndUrl()
    {
        Assert.Equal("https://google.com",
            ChatMarkdownSanitizer.FlattenLinkToInertText("https://google.com", "https://google.com"));
    }

    [Fact]
    public void FlattenLinkToInertText_UrlOnly()
    {
        Assert.Equal("https://google.com",
            ChatMarkdownSanitizer.FlattenLinkToInertText(null, "https://google.com"));
    }
}
