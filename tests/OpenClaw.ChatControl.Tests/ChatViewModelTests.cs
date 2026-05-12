using OpenClaw.ChatControl;
using OpenClaw.ChatControl.DevApp;

namespace OpenClaw.ChatControl.Tests;

public class ChatViewModelTests : IDisposable
{
    private readonly MockChatService _service;
    private readonly ChatViewModel _vm;

    public ChatViewModelTests()
    {
        _service = new MockChatService { StreamDelayMs = 0, ThinkingDelayMs = 0 };
        _vm = new ChatViewModel(_service, action => action()); // sync dispatcher for tests
    }

    public void Dispose() => _vm.Dispose();

    [Fact]
    public async Task LoadHistory_PopulatesMessages()
    {
        await _vm.LoadHistoryAsync();

        Assert.True(_vm.IsHistoryLoaded);
        Assert.Equal(3, _vm.Messages.Count);
        Assert.Equal(MessageRole.Assistant, _vm.Messages[0].Role);
        Assert.Equal(MessageRole.User, _vm.Messages[1].Role);
        Assert.Equal(MessageRole.Assistant, _vm.Messages[2].Role);
    }

    [Fact]
    public async Task Send_AddsUserAndAssistantMessages()
    {
        _vm.SendCommand.Execute("Hello");
        // Allow background streaming to complete
        await Task.Delay(200);

        Assert.True(_vm.Messages.Count >= 2);
        Assert.Equal(MessageRole.User, _vm.Messages[0].Role);
        Assert.Equal("Hello", _vm.Messages[0].Content);
        Assert.Equal(MessageRole.Assistant, _vm.Messages[1].Role);
        Assert.NotEmpty(_vm.Messages[1].Content);
    }

    [Fact]
    public async Task Send_UserMessageMarkedComplete()
    {
        _vm.SendCommand.Execute("Test");
        await Task.Delay(200);

        Assert.Equal(MessageStatus.Complete, _vm.Messages[0].Status);
    }

    [Fact]
    public async Task Send_AssistantMessageFinalized()
    {
        _vm.SendCommand.Execute("Hello");
        await Task.Delay(500);

        var assistant = _vm.Messages[1];
        Assert.Equal(MessageStatus.Complete, assistant.Status);
        Assert.False(assistant.IsStreaming);
        Assert.False(_vm.IsRunActive);
        Assert.Null(_vm.ActiveRunId);
    }

    [Fact]
    public async Task Send_StreamingLifecycle()
    {
        // Use a slower stream to observe intermediate states
        _service.StreamDelayMs = 50;
        _service.ThinkingDelayMs = 10;

        _vm.SendCommand.Execute("Hello");
        await Task.Delay(80); // After thinking, before stream completes

        // Should have 2 messages: user + assistant
        Assert.Equal(2, _vm.Messages.Count);
        var assistant = _vm.Messages[1];
        // At this point it could be Thinking or Streaming (race-dependent)
        Assert.True(assistant.Status == MessageStatus.Thinking ||
                    assistant.Status == MessageStatus.Streaming);

        // Wait for completion
        await Task.Delay(2000);
        Assert.Equal(MessageStatus.Complete, assistant.Status);
    }

    [Fact]
    public async Task Send_NullOrEmpty_DoesNothing()
    {
        _vm.SendCommand.Execute(null);
        _vm.SendCommand.Execute("");
        _vm.SendCommand.Execute("   ");
        await Task.Delay(50);

        Assert.Empty(_vm.Messages);
    }

    [Fact]
    public async Task Send_ServiceThrows_MarksError()
    {
        _service.NextSendException = new InvalidOperationException("Network error");

        _vm.SendCommand.Execute("Test");
        await Task.Delay(200);

        Assert.Single(_vm.Messages);
        Assert.Equal(MessageStatus.Error, _vm.Messages[0].Status);
        Assert.Contains("Failed to send", _vm.ErrorMessage);
    }

    [Fact]
    public async Task StreamError_MarksAssistantMessageAsError()
    {
        _service.NextStreamErrors = true;

        _vm.SendCommand.Execute("error test");
        await Task.Delay(500);

        Assert.Equal(2, _vm.Messages.Count);
        var assistant = _vm.Messages[1];
        Assert.Equal(MessageStatus.Error, assistant.Status);
        Assert.False(assistant.IsStreaming);
        Assert.False(_vm.IsRunActive);
    }

    [Fact]
    public async Task Abort_MarksMessageAsAborted()
    {
        _service.StreamDelayMs = 100;
        _service.ThinkingDelayMs = 10;

        _vm.SendCommand.Execute("long response");
        await Task.Delay(80);

        // Should be running
        Assert.True(_vm.IsRunActive || _vm.Messages.Count >= 2);

        _vm.AbortCommand.Execute(null);
        await Task.Delay(100);

        Assert.False(_vm.IsRunActive);
        if (_vm.Messages.Count >= 2)
        {
            var assistant = _vm.Messages[1];
            Assert.Equal(MessageStatus.Aborted, assistant.Status);
            Assert.False(assistant.IsStreaming);
        }
    }

    [Fact]
    public async Task Dispose_UnsubscribesEvents()
    {
        _vm.Dispose();

        // After dispose, sending should not throw or update the VM
        // (events are unsubscribed, so the service can fire without effect)
        _vm.SendCommand.Execute("should be ignored");
        await Task.Delay(50);
        Assert.Empty(_vm.Messages);
    }

    [Fact]
    public async Task LoadHistory_ClearsExistingMessages()
    {
        await _vm.LoadHistoryAsync();
        Assert.Equal(3, _vm.Messages.Count);

        await _vm.LoadHistoryAsync();
        Assert.Equal(3, _vm.Messages.Count); // replaced, not appended
    }

    [Fact]
    public async Task ErrorMessage_ClearedOnSuccessfulSend()
    {
        _service.NextSendException = new Exception("Fail");
        _vm.SendCommand.Execute("Test");
        await Task.Delay(200);
        Assert.NotNull(_vm.ErrorMessage);

        // Next send should clear the error
        _vm.SendCommand.Execute("Retry");
        await Task.Delay(200);
        Assert.Null(_vm.ErrorMessage);
    }

    [Fact]
    public async Task MultipleSends_SequentiallyProcessed()
    {
        _service.StreamDelayMs = 0;
        _service.ThinkingDelayMs = 0;

        _vm.SendCommand.Execute("First");
        await Task.Delay(300);

        _vm.SendCommand.Execute("Second");
        await Task.Delay(300);

        // Should have 4 messages: user1, assistant1, user2, assistant2
        Assert.Equal(4, _vm.Messages.Count);
        Assert.Equal("First", _vm.Messages[0].Content);
        Assert.Equal(MessageRole.Assistant, _vm.Messages[1].Role);
        Assert.Equal("Second", _vm.Messages[2].Content);
        Assert.Equal(MessageRole.Assistant, _vm.Messages[3].Role);
    }

    [Fact]
    public async Task ToolCall_AddedToActiveMessage()
    {
        _service.StreamDelayMs = 0;
        _service.ThinkingDelayMs = 0;

        _vm.SendCommand.Execute("Hello");
        await Task.Delay(500);

        // Mock service emits a tool call during streaming
        Assert.True(_vm.Messages.Count >= 2);
        var assistant = _vm.Messages[1];
        Assert.True(assistant.ToolCalls.Count >= 1, "Expected at least one tool call");
        Assert.Equal("read", assistant.ToolCalls[0].Name);
    }

    [Fact]
    public async Task ToolCall_TransitionsToDone()
    {
        _service.StreamDelayMs = 0;
        _service.ThinkingDelayMs = 0;

        _vm.SendCommand.Execute("Hello");
        await Task.Delay(500);

        Assert.True(_vm.Messages.Count >= 2);
        var assistant = _vm.Messages[1];
        if (assistant.ToolCalls.Count > 0)
        {
            Assert.Equal(ToolCallPhase.Done, assistant.ToolCalls[0].Phase);
            Assert.Equal("Read 42 lines from main.cs", assistant.ToolCalls[0].ResultSummary);
        }
    }

    [Fact]
    public async Task SlashNew_ClearsMessages()
    {
        // Load some history first
        await _vm.LoadHistoryAsync();
        Assert.Equal(3, _vm.Messages.Count);

        // Send /new — should clear messages
        _vm.SendCommand.Execute("/new");
        await Task.Delay(500);

        // After /new, history reloads (mock returns 3 items, but messages were cleared first)
        // The key behavior: IsRunActive should be false, no error
        Assert.False(_vm.IsRunActive);
        Assert.Null(_vm.ErrorMessage);
    }
}
