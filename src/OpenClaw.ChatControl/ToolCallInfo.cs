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

    /// <summary>Display label: "⚡ tool_name".</summary>
    public string DisplayLabel => $"⚡ {Name}";

    /// <summary>Status icon for the current phase.</summary>
    public string StatusIcon => Phase switch
    {
        ToolCallPhase.Running => "⏳",
        ToolCallPhase.Done => "✅",
        ToolCallPhase.Error => "❌",
        _ => ""
    };
}

public enum ToolCallPhase
{
    Running,
    Done,
    Error
}
