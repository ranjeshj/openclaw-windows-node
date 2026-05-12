using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.ChatControl;

/// <summary>
/// Security-first markdown pre-sanitizer. Runs before Markdig parsing to
/// neutralize potentially dangerous content:
/// - Images → inert "[Image: alt]" placeholders
/// - Links → "text (url)" plain text
/// - Reference definitions → stripped
/// - Raw HTML → stripped
/// Code blocks/spans are preserved during scanning.
/// </summary>
public static partial class ChatMarkdownSanitizer
{
    /// <summary>
    /// Sanitize markdown content before rendering. Makes all links and images
    /// inert so no network requests or navigation can be triggered by untrusted content.
    /// </summary>
    public static string Sanitize(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";

        var sb = new StringBuilder(content.Length);
        var i = 0;
        var inFencedCode = false;

        while (i < content.Length)
        {
            // Track fenced code blocks — don't modify content inside them
            if (i == 0 || (i > 0 && content[i - 1] == '\n'))
            {
                if (content.Length - i >= 3 && content[i] == '`' && content[i + 1] == '`' && content[i + 2] == '`')
                {
                    inFencedCode = !inFencedCode;
                    sb.Append("```");
                    i += 3;
                    // Consume rest of line
                    while (i < content.Length && content[i] != '\n')
                    {
                        sb.Append(content[i]);
                        i++;
                    }
                    continue;
                }
            }

            if (inFencedCode)
            {
                sb.Append(content[i]);
                i++;
                continue;
            }

            // Inline code spans — preserve content (handles multi-backtick spans like `` `code` `` or ``` ``code`` ```)
            if (content[i] == '`')
            {
                // Count opening backticks
                int backtickCount = 0;
                int start = i;
                while (i < content.Length && content[i] == '`') { backtickCount++; i++; }

                // Find matching closing backticks (same count)
                var closingPattern = new string('`', backtickCount);
                var end = content.IndexOf(closingPattern, i, StringComparison.Ordinal);
                if (end > 0)
                {
                    sb.Append(content, start, end + backtickCount - start);
                    i = end + backtickCount;
                }
                else
                {
                    // Unmatched backticks — emit as-is
                    sb.Append(content, start, backtickCount);
                }
                continue;
            }

            // Image: ![alt](url) → [Image: alt]
            if (content[i] == '!' && i + 1 < content.Length && content[i + 1] == '[')
            {
                var altEnd = content.IndexOf(']', i + 2);
                if (altEnd > 0 && altEnd + 1 < content.Length && content[altEnd + 1] == '(')
                {
                    var urlEnd = content.IndexOf(')', altEnd + 2);
                    if (urlEnd > 0)
                    {
                        var alt = content[(i + 2)..altEnd];
                        sb.Append(string.IsNullOrEmpty(alt) ? "[Image]" : $"[Image: {alt}]");
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            // Link: [text](url) → text (url)
            if (content[i] == '[')
            {
                var textEnd = content.IndexOf(']', i + 1);
                if (textEnd > 0 && textEnd + 1 < content.Length && content[textEnd + 1] == '(')
                {
                    var urlEnd = content.IndexOf(')', textEnd + 2);
                    if (urlEnd > 0)
                    {
                        var text = content[(i + 1)..textEnd];
                        var url = content[(textEnd + 2)..urlEnd];
                        sb.Append(FlattenLinkToInertText(text, url));
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            // Reference definitions: [label]: url — strip entire line
            if (i == 0 || (i > 0 && content[i - 1] == '\n'))
            {
                var match = RefDefRegex().Match(content, i);
                if (match.Success && match.Index == i)
                {
                    i += match.Length;
                    continue;
                }
            }

            // Raw HTML tags: <tag...> → strip
            if (content[i] == '<' && i + 1 < content.Length && (char.IsLetter(content[i + 1]) || content[i + 1] == '/'))
            {
                var tagEnd = content.IndexOf('>', i + 1);
                if (tagEnd > 0)
                {
                    i = tagEnd + 1;
                    continue;
                }
            }

            sb.Append(content[i]);
            i++;
        }

        return ReplaceEmbedDirectives(sb.ToString());
    }

    /// <summary>
    /// Replace [embed ref="..." title="..." /] directives with a visible placeholder.
    /// </summary>
    public static string ReplaceEmbedDirectives(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        return EmbedDirectiveRegex().Replace(content, match =>
        {
            var title = ExtractAttribute(match.Value, "title") ?? "Embedded content";
            return $"\U0001F4CE {title}";
        });
    }

    private static string? ExtractAttribute(string tag, string name)
    {
        var pattern = $"{name}=\"([^\"]*)\"";
        var m = System.Text.RegularExpressions.Regex.Match(tag, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Flatten a link into inert display text: "text (url)" or just "url".
    /// </summary>
    public static string FlattenLinkToInertText(string? text, string? url)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasUrl = !string.IsNullOrWhiteSpace(url);

        if (hasText && hasUrl && !string.Equals(text!.Trim(), url!.Trim(), System.StringComparison.OrdinalIgnoreCase))
            return $"{text} ({url})";
        if (hasUrl) return url!;
        if (hasText) return text!;
        return "";
    }

    /// <summary>
    /// Flatten raw HTML block content to inert selectable text.
    /// </summary>
    public static string FlattenRawHtmlBlockToInertText(string? rawHtml)
    {
        if (string.IsNullOrEmpty(rawHtml)) return "";
        return HtmlTagRegex().Replace(rawHtml, "").Trim();
    }

    [GeneratedRegex(@"^\[[^\]]+\]:\s+\S+[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex RefDefRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\[embed\s+[^\]]*\/?]", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedDirectiveRegex();
}
