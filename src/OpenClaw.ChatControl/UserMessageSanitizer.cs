using System.Text.RegularExpressions;

namespace OpenClaw.ChatControl;

public static partial class UserMessageSanitizer
{
    public static string Sanitize(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";

        var result = content;
        // Strip [Channel YYYY-MM-DDTHH:MMZ ...] envelope prefixes
        result = ChannelEnvelopeRegex().Replace(result, "");
        // Strip [message_id: ...] lines
        result = MessageIdRegex().Replace(result, "");
        // Strip timestamp prefixes [Mon YYYY-MM-DD HH:MM...]
        result = TimestampPrefixRegex().Replace(result, "");
        // Normalize excessive newlines
        result = ExcessiveNewlinesRegex().Replace(result, "\n\n");
        return result.Trim();
    }

    [GeneratedRegex(@"^\[Channel\s+\d{4}-\d{2}-\d{2}T[^\]]*\][^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex ChannelEnvelopeRegex();

    [GeneratedRegex(@"^\[message_id:\s*[^\]]*\]\s*\n?", RegexOptions.Multiline)]
    private static partial Regex MessageIdRegex();

    [GeneratedRegex(@"^\[\w{3}\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}[^\]]*\]\s*", RegexOptions.Multiline)]
    private static partial Regex TimestampPrefixRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();
}
