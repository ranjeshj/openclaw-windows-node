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
        Assert.Equal("🔎 Search", tc.DisplayLabel);
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

    [Fact]
    public void DisplayLabel_ToolSpecificIcons()
    {
        Assert.Equal("🔍 Search", new ToolCallInfo("t1", "grep").DisplayLabel);
        Assert.Equal("📂 Files", new ToolCallInfo("t2", "glob").DisplayLabel);
        Assert.Equal("🌐 Fetch", new ToolCallInfo("t3", "web_fetch").DisplayLabel);
        Assert.Equal("$ Shell", new ToolCallInfo("t4", "bash").DisplayLabel);
        Assert.Equal("⚡ Exec", new ToolCallInfo("t5", "exec").DisplayLabel);
        Assert.Equal("📄 Read", new ToolCallInfo("t6", "read").DisplayLabel);
        Assert.Equal("📄 Read", new ToolCallInfo("t7", "view").DisplayLabel);
        Assert.Equal("✏️ Edit", new ToolCallInfo("t8", "edit").DisplayLabel);
        Assert.Equal("✏️ Edit", new ToolCallInfo("t9", "create").DisplayLabel);
        Assert.Equal("🤖 Task", new ToolCallInfo("t10", "task").DisplayLabel);
        Assert.Equal("💬 Intent", new ToolCallInfo("t11", "report_intent").DisplayLabel);
        Assert.Equal("$ Shell", new ToolCallInfo("t12", "powershell").DisplayLabel);
    }

    [Fact]
    public void Resolve_UnknownTool_ReturnsCapitalized()
    {
        var (icon, title) = ToolCallInfo.Resolve("custom_tool");
        Assert.Equal("⚡", icon);
        Assert.Equal("Custom_tool", title);
    }

    [Fact]
    public void ArgsJson_StoredCorrectly()
    {
        var tc = new ToolCallInfo("tc-1", "read");
        tc.ArgsJson = "{ \"path\": \"main.cs\" }";

        Assert.Equal("{ \"path\": \"main.cs\" }", tc.ArgsJson);
    }

    [Fact]
    public void ToolOutput_StoredCorrectly()
    {
        var tc = new ToolCallInfo("tc-1", "read");
        tc.ToolOutput = "file contents here";

        Assert.Equal("file contents here", tc.ToolOutput);
    }
}
