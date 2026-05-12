namespace OpenClaw.ChatControl;

public static class ToolResultFormatter
{
    public static string Format(string? text, string? toolName = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";

        // If it looks like JSON, try to pretty-print
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                return System.Text.Json.JsonSerializer.Serialize(
                    doc.RootElement,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch { /* not valid JSON, return as-is */ }
        }

        // If it looks like an error, sanitize to one line
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        {
            var firstLine = text.Split('\n', 2)[0].Trim();
            return firstLine.Length > 200 ? firstLine[..200] + "..." : firstLine;
        }

        return text;
    }
}
