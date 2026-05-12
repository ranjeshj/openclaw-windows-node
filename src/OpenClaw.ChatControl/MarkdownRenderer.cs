using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace OpenClaw.ChatControl;

/// <summary>
/// Renders markdown content into WinUI RichTextBlock elements using Markdig.
/// Supports: paragraphs, bold, italic, inline code, fenced code blocks, links, lists, blockquotes.
/// Security: images are stripped, link clicks are intercepted.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly string[] s_allowedLinkSchemes = ["https", "http"];

    /// <summary>
    /// Render markdown content into a UIElement suitable for display.
    /// Returns a StackPanel containing RichTextBlocks for text and
    /// Border-wrapped code blocks for fenced code.
    /// </summary>
    public static FrameworkElement Render(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new TextBlock { Text = "" };
        }

        try
        {
            // Security: pre-sanitize to neutralize images, links, and HTML
            var sanitized = ChatMarkdownSanitizer.Sanitize(content);
            var document = Markdown.Parse(sanitized, s_pipeline);

            // Check if there are any fenced code blocks — if so, we need a StackPanel
            // container to interleave RichTextBlocks with styled code block Borders.
            bool hasFencedCode = false;
            foreach (var block in document)
            {
                if (block is FencedCodeBlock) { hasFencedCode = true; break; }
            }

            if (!hasFencedCode)
            {
                // Simple case: all inline content, single RichTextBlock
                var richTextBlock = new RichTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };
                foreach (var block in document)
                    RenderBlock(block, richTextBlock);
                return richTextBlock;
            }

            // Complex case: interleave text and code block cards
            var container = new StackPanel { Spacing = 4 };
            RichTextBlock? currentRtb = null;

            void FlushCurrentRtb()
            {
                if (currentRtb != null && currentRtb.Blocks.Count > 0)
                {
                    container.Children.Add(currentRtb);
                    currentRtb = null;
                }
            }

            RichTextBlock EnsureRtb()
            {
                if (currentRtb == null)
                {
                    currentRtb = new RichTextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    };
                }
                return currentRtb;
            }

            foreach (var block in document)
            {
                if (block is FencedCodeBlock fenced)
                {
                    FlushCurrentRtb();
                    container.Children.Add(RenderCodeBlockCard(fenced));
                }
                else
                {
                    RenderBlock(block, EnsureRtb());
                }
            }
            FlushCurrentRtb();

            return container;
        }
        catch
        {
            return new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
        }
    }

    private static void RenderBlock(Markdig.Syntax.Block block, RichTextBlock target)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                RenderInlines(paragraph.Inline, para.Inlines);
                target.Blocks.Add(para);
                break;

            case HeadingBlock heading:
                var headingPara = new Paragraph { Margin = new Thickness(0, 4, 0, 6) };
                headingPara.FontSize = heading.Level switch
                {
                    1 => 24,
                    2 => 20,
                    3 => 17,
                    _ => 15,
                };
                var boldRun = new Run();
                boldRun.FontWeight = FontWeights.SemiBold;
                if (heading.Inline != null)
                {
                    RenderInlines(heading.Inline, headingPara.Inlines);
                    foreach (var inline in headingPara.Inlines)
                    {
                        if (inline is Run r) r.FontWeight = FontWeights.SemiBold;
                    }
                }
                target.Blocks.Add(headingPara);
                break;

            case FencedCodeBlock fencedCode:
                RenderCodeBlock(fencedCode, target);
                break;

            case CodeBlock code:
                RenderCodeBlock(code, target);
                break;

            case ListBlock list:
                RenderList(list, target, 0);
                break;

            case QuoteBlock quote:
                RenderQuoteBlock(quote, target);
                break;

            case ThematicBreakBlock:
                var hrPara = new Paragraph();
                hrPara.Inlines.Add(new Run { Text = "───────────────────────────", Foreground = GetSecondaryBrush() });
                target.Blocks.Add(hrPara);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    RenderBlock(child, target);
                }
                break;
        }
    }

    private static void RenderInlines(ContainerInline? inlineContainer, InlineCollection target)
    {
        if (inlineContainer == null) return;

        foreach (var inline in inlineContainer)
        {
            RenderInline(inline, target);
        }
    }

    private static void RenderInline(Markdig.Syntax.Inlines.Inline inline, InlineCollection target)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run { Text = literal.Content.ToString() });
                break;

            case EmphasisInline emphasis:
                var emphSpan = new Span();
                if (emphasis.DelimiterCount == 2 || emphasis.DelimiterChar == '*' && emphasis.DelimiterCount >= 2)
                {
                    emphSpan.FontWeight = FontWeights.Bold;
                }
                else
                {
                    emphSpan.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                RenderInlines(emphasis, emphSpan.Inlines);
                target.Add(emphSpan);
                break;

            case CodeInline code:
                var codeSpan = new Span();
                codeSpan.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, monospace");
                codeSpan.Inlines.Add(new Run { Text = code.Content });
                target.Add(codeSpan);
                break;

            case LinkInline link:
                if (link.IsImage)
                {
                    // Security: strip images, show placeholder
                    target.Add(new Run
                    {
                        Text = "[image]",
                        Foreground = GetSecondaryBrush(),
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                    });
                }
                else
                {
                    RenderLink(link, target);
                }
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case HtmlInline html:
                // Strip HTML — show as text if non-empty
                var htmlText = html.Tag;
                if (!string.IsNullOrWhiteSpace(htmlText))
                {
                    target.Add(new Run
                    {
                        Text = htmlText,
                        Foreground = GetSecondaryBrush(),
                        FontSize = 11,
                    });
                }
                break;

            case ContainerInline container:
                RenderInlines(container, target);
                break;
        }
    }

    private static void RenderLink(LinkInline link, InlineCollection target)
    {
        var url = link.Url;

        // Security: only allow safe schemes
        if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var isSafe = false;
            foreach (var scheme in s_allowedLinkSchemes)
            {
                if (string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                {
                    isSafe = true;
                    break;
                }
            }

            if (isSafe)
            {
                var hyperlink = new Hyperlink { NavigateUri = uri };
                RenderInlines(link, hyperlink.Inlines);
                if (hyperlink.Inlines.Count == 0)
                {
                    hyperlink.Inlines.Add(new Run { Text = url });
                }
                target.Add(hyperlink);
                return;
            }
        }

        // Unsafe or invalid URL — render as plain text
        var span = new Span { Foreground = GetSecondaryBrush() };
        RenderInlines(link, span.Inlines);
        target.Add(span);
    }

    private static void RenderCodeBlock(LeafBlock codeBlock, RichTextBlock target)
    {
        var language = (codeBlock as FencedCodeBlock)?.Info ?? "";
        var code = ExtractText(codeBlock);

        var codePara = new Paragraph
        {
            FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, monospace"),
            FontSize = 13,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };

        if (!string.IsNullOrEmpty(language))
        {
            codePara.Inlines.Add(new Run
            {
                Text = language,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = GetSecondaryBrush(),
            });
            codePara.Inlines.Add(new LineBreak());
        }

        codePara.Inlines.Add(new Run { Text = code.TrimEnd() });
        target.Blocks.Add(codePara);
    }

    /// <summary>
    /// Renders a fenced code block as a styled Border card with language header,
    /// rounded corners, and card background — matching Fluent design.
    /// </summary>
    private static FrameworkElement RenderCodeBlockCard(FencedCodeBlock fenced)
    {
        var language = fenced.Info ?? "";
        var code = ExtractText(fenced).TrimEnd();

        var stack = new StackPanel();

        // Language label header
        if (!string.IsNullOrEmpty(language))
        {
            stack.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetSecondaryBrush(),
                Padding = new Thickness(12, 6, 12, 0),
            });
        }

        // Code text
        stack.Children.Add(new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(12, 8, 12, 12),
        });

        return new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 4),
            // Use theme-aware resources when available, fall back to reasonable defaults
            Background = GetCodeBlockBackground(),
            BorderBrush = GetSecondaryBrush(),
        };
    }

    private static SolidColorBrush GetCodeBlockBackground()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var brush) && brush is SolidColorBrush scb)
                return scb;
        }
        catch { }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128));
    }

    private static void RenderList(ListBlock list, RichTextBlock target, int depth)
    {
        int index = 1;
        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                var bullet = list.IsOrdered ? $"{index}. " : "• ";
                var indent = new string(' ', depth * 3);

                foreach (var child in listItem)
                {
                    if (child is ParagraphBlock para)
                    {
                        var listPara = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                        listPara.Inlines.Add(new Run { Text = indent + bullet });
                        RenderInlines(para.Inline, listPara.Inlines);
                        target.Blocks.Add(listPara);
                    }
                    else if (child is ListBlock nestedList)
                    {
                        RenderList(nestedList, target, depth + 1);
                    }
                    else
                    {
                        RenderBlock(child, target);
                    }
                }
                index++;
            }
        }
    }

    private static void RenderQuoteBlock(QuoteBlock quote, RichTextBlock target)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock para)
            {
                var quotePara = new Paragraph();
                quotePara.Inlines.Add(new Run
                {
                    Text = "│ ",
                    Foreground = GetSecondaryBrush(),
                    FontWeight = FontWeights.Bold,
                });
                var quoteSpan = new Span { FontStyle = Windows.UI.Text.FontStyle.Italic };
                RenderInlines(para.Inline, quoteSpan.Inlines);
                quotePara.Inlines.Add(quoteSpan);
                target.Blocks.Add(quotePara);
            }
            else
            {
                RenderBlock(child, target);
            }
        }
    }

    private static string ExtractText(LeafBlock block)
    {
        if (block.Lines.Lines == null) return "";

        var lines = new List<string>();
        foreach (var line in block.Lines.Lines)
        {
            if (line.Slice.Text == null) continue;
            lines.Add(line.Slice.ToString());
        }
        return string.Join("\n", lines);
    }

    private static SolidColorBrush GetSecondaryBrush()
    {
        // Use a neutral gray that works in both light and dark themes
        return new SolidColorBrush(Colors.Gray);
    }
}
