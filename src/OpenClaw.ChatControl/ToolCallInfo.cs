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

    /// <summary>Structured details from the tool result (e.g. serialized JSON details).</summary>
    [ObservableProperty]
    public partial string? Details { get; set; }

    /// <summary>Resolves a tool name to its display icon and title.</summary>
    public static (string Icon, string Title) Resolve(string toolName) => toolName switch
    {
        "bash" or "powershell" => ("$", "Shell"),
        "read" or "view" => ("📄", "Read"),
        "edit" or "create" => ("✏️", "Edit"),
        "grep" => ("🔍", "Search"),
        "glob" => ("📂", "Files"),
        "web_fetch" => ("🌐", "Fetch"),
        "web_search" => ("🔎", "Search"),
        "exec" => ("⚡", "Exec"),
        "task" => ("🤖", "Task"),
        "report_intent" => ("💬", "Intent"),
        _ => ("⚡", CapitalizeFirst(toolName))
    };

    /// <summary>Display label with tool-specific icon prefix.</summary>
    public string DisplayLabel { get { var (icon, title) = Resolve(Name); return $"{icon} {title}"; } }

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
