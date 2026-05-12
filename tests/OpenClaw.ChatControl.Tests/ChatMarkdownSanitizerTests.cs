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

    [Fact]
    public void ReplaceEmbedDirectives_WithTitle_ShowsPlaceholder()
    {
        var input = "Here is content [embed ref=\"img123\" title=\"My Chart\" height=\"300\" /] and more";
        var result = ChatMarkdownSanitizer.ReplaceEmbedDirectives(input);
        Assert.Contains("\U0001F4CE My Chart", result);
        Assert.DoesNotContain("[embed", result);
    }

    [Fact]
    public void ReplaceEmbedDirectives_WithoutTitle_ShowsDefaultPlaceholder()
    {
        var input = "[embed ref=\"img456\" /]";
        var result = ChatMarkdownSanitizer.ReplaceEmbedDirectives(input);
        Assert.Equal("\U0001F4CE Embedded content", result);
    }

    [Fact]
    public void ReplaceEmbedDirectives_NoDirective_Unchanged()
    {
        var input = "Just plain text";
        var result = ChatMarkdownSanitizer.ReplaceEmbedDirectives(input);
        Assert.Equal("Just plain text", result);
    }

    [Fact]
    public void ReplaceEmbedDirectives_NullOrEmpty_ReturnsInput()
    {
        Assert.Null(ChatMarkdownSanitizer.ReplaceEmbedDirectives(null!));
        Assert.Equal("", ChatMarkdownSanitizer.ReplaceEmbedDirectives(""));
    }

    [Fact]
    public void ReplaceEmbedDirectives_CaseInsensitive()
    {
        var input = "[EMBED ref=\"x\" title=\"Upper\" /]";
        var result = ChatMarkdownSanitizer.ReplaceEmbedDirectives(input);
        Assert.Contains("\U0001F4CE Upper", result);
    }

    [Fact]
    public void Sanitize_EmbedDirective_ReplacedWithPlaceholder()
    {
        var input = "See this [embed ref=\"chart1\" title=\"Revenue\" /] below";
        var result = ChatMarkdownSanitizer.Sanitize(input);
        Assert.Contains("\U0001F4CE Revenue", result);
        Assert.DoesNotContain("[embed", result);
    }
}
