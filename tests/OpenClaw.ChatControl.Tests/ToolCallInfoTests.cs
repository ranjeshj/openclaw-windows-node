using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.Tests;

public class ToolCallInfoTests
{
    [Fact]
    public void Constructor_SetsInitialState()
    {
        var tc = new ToolCallInfo("tc-1", "read");

        Assert.Equal("tc-1", tc.ToolCallId);
        Assert.Equal("read", tc.Name);
        Assert.Equal(ToolCallPhase.Running, tc.Phase);
        Assert.Null(tc.ResultSummary);
    }

    [Fact]
    public void DisplayLabel_FormatCorrect()
    {
        var tc = new ToolCallInfo("tc-1", "web_search");
        Assert.Equal("⚡ web_search", tc.DisplayLabel);
    }

    [Fact]
    public void StatusIcon_Running()
    {
        var tc = new ToolCallInfo("tc-1", "read");
        Assert.Equal("⏳", tc.StatusIcon);
    }

    [Fact]
    public void StatusIcon_Done()
    {
        var tc = new ToolCallInfo("tc-1", "read") { Phase = ToolCallPhase.Done };
        Assert.Equal("✅", tc.StatusIcon);
    }

    [Fact]
    public void StatusIcon_Error()
    {
        var tc = new ToolCallInfo("tc-1", "read") { Phase = ToolCallPhase.Error };
        Assert.Equal("❌", tc.StatusIcon);
    }

    [Fact]
    public void PhaseTransition_RunningToDone()
    {
        var tc = new ToolCallInfo("tc-1", "exec");
        var changed = new List<string>();
        tc.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        tc.Phase = ToolCallPhase.Done;
        tc.ResultSummary = "Success";

        Assert.Contains("Phase", changed);
        Assert.Contains("ResultSummary", changed);
        Assert.Equal(ToolCallPhase.Done, tc.Phase);
        Assert.Equal("Success", tc.ResultSummary);
    }

    [Fact]
    public void PhaseTransition_RunningToError()
    {
        var tc = new ToolCallInfo("tc-1", "exec");

        tc.Phase = ToolCallPhase.Error;

        Assert.Equal(ToolCallPhase.Error, tc.Phase);
    }
}
