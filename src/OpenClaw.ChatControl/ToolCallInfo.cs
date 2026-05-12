using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenClaw.ChatControl;

/// <summary>
/// Represents a single tool call within an assistant message.
/// Phase transitions: Running → Done or Running → Error.
/// </summary>
public partial class ToolCallInfo : ObservableObject
{
    public ToolCallInfo(string toolCallId, string name)
    {
        ToolCallId = toolCallId;
        Name = name;
        Phase = ToolCallPhase.Running;
    }

    /// <summary>Unique identifier for this tool call.</summary>
    public string ToolCallId { get; }

    /// <summary>Tool name (e.g. "read", "exec", "web_search").</summary>
    public string Name { get; }

    /// <summary>Current phase of the tool call.</summary>
    [ObservableProperty]
    public partial ToolCallPhase Phase { get; set; }

    /// <summary>Summary of the tool result (shown when expanded).</summary>
    [ObservableProperty]
    public partial string? ResultSummary { get; set; }

    /// <summary>Pretty-printed JSON of the tool call arguments.</summary>
    [ObservableProperty]
    public partial string? ArgsJson { get; set; }

    /// <summary>Full tool output text.</summary>
    [ObservableProperty]
    public partial string? ToolOutput { get; set; }

    /// <summary>Display label with tool-specific icon prefix.</summary>
    public string DisplayLabel => Name switch
    {
        "bash" or "powershell" => $"$ {Name}",
        "grep" => $"🔍 {CapitalizeFirst(Name)}",
        "glob" => $"📂 {CapitalizeFirst(Name)}",
        "web_fetch" => $"🌐 {CapitalizeFirst(Name)}",
        "web_search" => $"🔎 {CapitalizeFirst(Name)}",
        _ => $"⚡ {CapitalizeFirst(Name)}"
    };

    /// <summary>Status icon for the current phase.</summary>
    public string StatusIcon => Phase switch
    {
        ToolCallPhase.Running => "⏳",
        ToolCallPhase.Done => "✅",
        ToolCallPhase.Error => "❌",
        _ => ""
    };

    private static string CapitalizeFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s[1..] : "");
}

public enum ToolCallPhase
{
    Running,
    Done,
    Error
}
