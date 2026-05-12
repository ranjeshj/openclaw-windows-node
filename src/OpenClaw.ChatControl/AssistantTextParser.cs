using System;
using System.Collections.Generic;

namespace OpenClaw.ChatControl;

public record TextSegment(string Text, bool IsThinking);

public static class AssistantTextParser
{
    public static IReadOnlyList<TextSegment> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<TextSegment>();

        var segments = new List<TextSegment>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            var thinkStart = remaining.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (thinkStart < 0)
            {
                if (remaining.Length > 0) segments.Add(new(remaining, false));
                break;
            }

            if (thinkStart > 0)
                segments.Add(new(remaining[..thinkStart], false));

            remaining = remaining[(thinkStart + 7)..];
            var thinkEnd = remaining.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd < 0)
            {
                // Unclosed think tag — treat rest as thinking
                segments.Add(new(remaining, true));
                break;
            }

            segments.Add(new(remaining[..thinkEnd], true));
            remaining = remaining[(thinkEnd + 8)..];
        }

        return segments;
    }
}
