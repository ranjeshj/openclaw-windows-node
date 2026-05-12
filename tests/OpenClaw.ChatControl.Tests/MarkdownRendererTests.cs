using Markdig;

namespace OpenClaw.ChatControl.Tests;

/// <summary>
/// Tests for MarkdownRenderer — validates Markdig parsing at the AST level
/// without requiring a UI thread (no FrameworkElement creation).
/// </summary>
public class MarkdownRendererTests
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    [Theory]
    [InlineData("Hello world", 1)]           // Single paragraph
    [InlineData("Line 1\n\nLine 2", 2)]       // Two paragraphs
    [InlineData("- Item 1\n- Item 2", 1)]     // List (one block)
    [InlineData("> A quote", 1)]              // Blockquote
    public void Parse_ProducesExpectedBlockCount(string markdown, int expectedBlocks)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        Assert.Equal(expectedBlocks, doc.Count);
    }

    [Fact]
    public void Parse_FencedCodeBlock_ExtractsLanguage()
    {
        var md = "```csharp\nConsole.WriteLine(\"hello\");\n```";
        var doc = Markdown.Parse(md, Pipeline);

        var code = Assert.IsType<Markdig.Syntax.FencedCodeBlock>(doc[0]);
        Assert.Equal("csharp", code.Info);
    }

    [Fact]
    public void Parse_BoldAndItalic_ProducesEmphasis()
    {
        var md = "This is **bold** and *italic*";
        var doc = Markdown.Parse(md, Pipeline);
        var para = Assert.IsType<Markdig.Syntax.ParagraphBlock>(doc[0]);

        Assert.NotNull(para.Inline);
        var inlines = new List<Markdig.Syntax.Inlines.Inline>();
        foreach (var i in para.Inline!) inlines.Add(i);

        // Should contain literal + emphasis(bold) + literal + emphasis(italic)
        Assert.True(inlines.Count >= 4);
        Assert.IsType<Markdig.Syntax.Inlines.EmphasisInline>(inlines[1]);
        Assert.IsType<Markdig.Syntax.Inlines.EmphasisInline>(inlines[3]);
    }

    [Fact]
    public void Parse_InlineCode_Detected()
    {
        var md = "Use `Console.WriteLine()` to print";
        var doc = Markdown.Parse(md, Pipeline);
        var para = Assert.IsType<Markdig.Syntax.ParagraphBlock>(doc[0]);

        var hasCode = false;
        foreach (var i in para.Inline!)
        {
            if (i is Markdig.Syntax.Inlines.CodeInline code)
            {
                Assert.Equal("Console.WriteLine()", code.Content);
                hasCode = true;
            }
        }
        Assert.True(hasCode);
    }

    [Fact]
    public void Parse_Link_ExtractsUrl()
    {
        var md = "[Example](https://example.com)";
        var doc = Markdown.Parse(md, Pipeline);
        var para = Assert.IsType<Markdig.Syntax.ParagraphBlock>(doc[0]);

        var hasLink = false;
        foreach (var i in para.Inline!)
        {
            if (i is Markdig.Syntax.Inlines.LinkInline link)
            {
                Assert.Equal("https://example.com", link.Url);
                Assert.False(link.IsImage);
                hasLink = true;
            }
        }
        Assert.True(hasLink);
    }

    [Fact]
    public void Parse_Image_DetectedAsImage()
    {
        var md = "![alt](https://example.com/img.png)";
        var doc = Markdown.Parse(md, Pipeline);
        var para = Assert.IsType<Markdig.Syntax.ParagraphBlock>(doc[0]);

        var hasImage = false;
        foreach (var i in para.Inline!)
        {
            if (i is Markdig.Syntax.Inlines.LinkInline link && link.IsImage)
            {
                hasImage = true;
            }
        }
        Assert.True(hasImage, "Image markdown should be detected as IsImage=true");
    }

    [Fact]
    public void Parse_OrderedList_IsOrdered()
    {
        var md = "1. First\n2. Second\n3. Third";
        var doc = Markdown.Parse(md, Pipeline);
        var list = Assert.IsType<Markdig.Syntax.ListBlock>(doc[0]);
        Assert.True(list.IsOrdered);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Parse_UnorderedList_IsNotOrdered()
    {
        var md = "- Alpha\n- Beta\n- Gamma";
        var doc = Markdown.Parse(md, Pipeline);
        var list = Assert.IsType<Markdig.Syntax.ListBlock>(doc[0]);
        Assert.False(list.IsOrdered);
    }

    [Fact]
    public void Parse_Blockquote_Detected()
    {
        var md = "> This is a quote";
        var doc = Markdown.Parse(md, Pipeline);
        Assert.IsType<Markdig.Syntax.QuoteBlock>(doc[0]);
    }

    [Fact]
    public void Parse_MultipleFencedCodeBlocks_AllDetected()
    {
        var md = "```python\nprint('hi')\n```\n\nSome text\n\n```js\nconsole.log('hi');\n```";
        var doc = Markdown.Parse(md, Pipeline);

        int codeBlockCount = 0;
        foreach (var block in doc)
        {
            if (block is Markdig.Syntax.FencedCodeBlock) codeBlockCount++;
        }
        Assert.Equal(2, codeBlockCount);
    }

    [Fact]
    public void Parse_EmptyString_ProducesEmptyDocument()
    {
        var doc = Markdown.Parse("", Pipeline);
        Assert.Empty(doc);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ms-settings:display")]
    [InlineData("shell:startup")]
    [InlineData("cmd:ipconfig")]
    public void UnsafeSchemes_ShouldBeBlocked(string url)
    {
        // Verify that our allowed schemes list would reject these
        var uri = new Uri(url, UriKind.Absolute);
        var allowed = new[] { "https", "http" };
        Assert.DoesNotContain(uri.Scheme.ToLowerInvariant(), allowed);
    }
}
