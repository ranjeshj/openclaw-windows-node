using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.Tests;

public class ChatMessageTests
{
    [Fact]
    public void Constructor_SetsIdentityFields()
    {
        var ts = DateTimeOffset.UtcNow;
        var msg = new ChatMessage("msg-1", MessageRole.User, "Hello", ts);

        Assert.Equal("msg-1", msg.Id);
        Assert.Equal(MessageRole.User, msg.Role);
        Assert.Equal("Hello", msg.Content);
        Assert.Equal(ts, msg.Timestamp);
        Assert.Equal(MessageStatus.Complete, msg.Status);
        Assert.False(msg.IsStreaming);
    }

    [Fact]
    public void AppendDelta_AccumulatesContent()
    {
        var msg = new ChatMessage("msg-1", MessageRole.Assistant);
        msg.BeginStreaming("run-1");

        msg.AppendDelta("Hello ");
        Assert.Equal("Hello ", msg.Content);

        msg.AppendDelta("world!");
        Assert.Equal("Hello world!", msg.Content);
        Assert.True(msg.IsStreaming);
    }

    [Fact]
    public void FinalizeContent_ClearsStreamingState()
    {
        var msg = new ChatMessage("msg-1", MessageRole.Assistant);
        msg.BeginStreaming("run-1");
        msg.AppendDelta("Done");
        msg.FinalizeContent();

        Assert.Equal("Done", msg.Content);
        Assert.False(msg.IsStreaming);
        Assert.Equal(MessageStatus.Complete, msg.Status);
    }

    [Fact]
    public void BeginStreaming_ResetsContentAndSetsState()
    {
        var msg = new ChatMessage("msg-1", MessageRole.Assistant, "old content");
        msg.BeginStreaming("run-1");

        Assert.Equal("", msg.Content);
        Assert.True(msg.IsStreaming);
        Assert.Equal(MessageStatus.Streaming, msg.Status);
        Assert.Equal("run-1", msg.RunId);
    }

    [Fact]
    public void MarkError_SetsErrorState()
    {
        var msg = new ChatMessage("msg-1", MessageRole.Assistant);
        msg.BeginStreaming("run-1");
        msg.AppendDelta("Partial");
        msg.MarkError("Connection lost");

        Assert.False(msg.IsStreaming);
        Assert.Equal(MessageStatus.Error, msg.Status);
        Assert.Contains("Connection lost", msg.Content);
        Assert.Contains("Partial", msg.Content);
    }

    [Fact]
    public void PropertyChanged_FiresForContent()
    {
        var msg = new ChatMessage("msg-1", MessageRole.Assistant);
        var changedProps = new List<string>();
        msg.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        msg.BeginStreaming("run-1");
        msg.AppendDelta("Hi");

        Assert.Contains("Content", changedProps);
        Assert.Contains("IsStreaming", changedProps);
        Assert.Contains("Status", changedProps);
    }

    [Fact]
    public void DefaultTimestamp_IsReasonable()
    {
        var before = DateTimeOffset.UtcNow;
        var msg = new ChatMessage("msg-1", MessageRole.User, "test");
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(msg.Timestamp, before, after);
    }

    [Fact]
    public void StatusEnum_HasExpectedValues()
    {
        // Ensure all lifecycle states exist
        Assert.Equal(6, Enum.GetValues<MessageStatus>().Length);
        _ = MessageStatus.Sending;
        _ = MessageStatus.Thinking;
        _ = MessageStatus.Streaming;
        _ = MessageStatus.Complete;
        _ = MessageStatus.Error;
        _ = MessageStatus.Aborted;
    }
}
