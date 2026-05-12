using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.DevApp;

/// <summary>
/// Mock implementation of IChatService for dev/testing.
/// Simulates streaming responses word-by-word with configurable latency.
/// </summary>
public sealed class MockChatService : IChatService
{
    private int _runCounter;

    /// <summary>Delay between words during streaming (ms).</summary>
    public int StreamDelayMs { get; set; } = 50;

    /// <summary>Delay before first delta (simulates "thinking" time).</summary>
    public int ThinkingDelayMs { get; set; } = 500;

    /// <summary>If set, the next send will throw this exception.</summary>
    public Exception? NextSendException { get; set; }

    /// <summary>If true, the next stream will error mid-way.</summary>
    public bool NextStreamErrors { get; set; }

    public event EventHandler<ChatStreamDelta>? DeltaReceived;
    public event EventHandler<ChatLifecycleEvent>? LifecycleChanged;
    public event EventHandler<ChatToolCallEvent>? ToolCallReceived;
    public event EventHandler<ChatReasoningEvent>? ReasoningReceived;
#pragma warning disable CS0067 // Event never used — required by IChatService interface
    public event EventHandler<ChatStatusEvent>? StatusReceived;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler? Reconnected;
#pragma warning restore CS0067

    public bool IsConnected => true;

    public Task<IReadOnlyList<ChatMessage>> LoadHistoryAsync(CancellationToken ct = default)
    {
        var history = new List<ChatMessage>
        {
            new("hist-1", MessageRole.Assistant,
                "Hello! I'm your AI assistant. How can I help you today?",
                DateTimeOffset.UtcNow.AddMinutes(-5)),
            new("hist-2", MessageRole.User,
                "Can you explain how this chat control works?",
                DateTimeOffset.UtcNow.AddMinutes(-4)),
            new("hist-3", MessageRole.Assistant,
                "This is a **native WinUI chat control** built with:\n\n" +
                "- `ListView` for the message list\n" +
                "- `Markdig` for markdown rendering\n" +
                "- Two-phase rendering (plain text during streaming, markdown on completion)\n\n" +
                "```csharp\n// Example: sending a message\nawait chatService.SendAsync(\"Hello!\", idempotencyKey);\n```\n\n" +
                "It's designed to be simple, performant, and gateway-independent.",
                DateTimeOffset.UtcNow.AddMinutes(-3)),
        };

        return Task.FromResult<IReadOnlyList<ChatMessage>>(history);
    }

    public async Task<string> SendAsync(string text, string idempotencyKey, CancellationToken ct = default)
    {
        if (NextSendException is { } ex)
        {
            NextSendException = null;
            throw ex;
        }

        var runId = $"run-{Interlocked.Increment(ref _runCounter)}";

        // Fire lifecycle start and stream response on background thread
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ThinkingDelayMs, ct);

                LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
                {
                    RunId = runId,
                    Phase = ChatLifecyclePhase.Start,
                    Model = "gpt-5.5"
                });

                // Simulate reasoning before the response
                ReasoningReceived?.Invoke(this, new ChatReasoningEvent
                {
                    RunId = runId,
                    Delta = "Let me think about this... I should consider the user's question carefully and provide a helpful response."
                });

                // Simulate a tool call with args and output
                var toolCallId = $"tool-{runId}-1";
                ToolCallReceived?.Invoke(this, new ChatToolCallEvent
                {
                    RunId = runId,
                    ToolCallId = toolCallId,
                    ToolName = "read",
                    Phase = ToolCallPhase.Running,
                    ArgsJson = "{\n  \"path\": \"src/main.cs\",\n  \"lines\": \"1-42\"\n}"
                });

                await Task.Delay(Math.Max(ThinkingDelayMs / 2, 10), ct);

                ToolCallReceived?.Invoke(this, new ChatToolCallEvent
                {
                    RunId = runId,
                    ToolCallId = toolCallId,
                    ToolName = "read",
                    Phase = ToolCallPhase.Done,
                    ResultSummary = "Read 42 lines from main.cs",
                    ToolOutput = "using System;\nnamespace MyApp;\n\npublic class Program\n{\n    static void Main() => Console.WriteLine(\"Hello\");\n}"
                });

                var response = GenerateResponse(text);
                var words = response.Split(' ');

                for (int i = 0; i < words.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (NextStreamErrors && i == words.Length / 2)
                    {
                        NextStreamErrors = false;
                        LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
                        {
                            RunId = runId,
                            Phase = ChatLifecyclePhase.Error,
                            ErrorMessage = "Simulated stream error"
                        });
                        return;
                    }

                    var delta = (i == 0 ? "" : " ") + words[i];
                    DeltaReceived?.Invoke(this, new ChatStreamDelta
                    {
                        RunId = runId,
                        Delta = delta
                    });

                    await Task.Delay(StreamDelayMs, ct);
                }

                LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
                {
                    RunId = runId,
                    Phase = ChatLifecyclePhase.End,
                    Model = "gpt-5.5",
                    InputTokens = 1475,
                    OutputTokens = 42,
                    ContextPercent = 23
                });
            }
            catch (OperationCanceledException) { }
        }, ct);

        return runId;
    }

    public Task AbortAsync(string runId, CancellationToken ct = default)
    {
        // In a real implementation, this would cancel the active run.
        // The lifecycle end/error event would be fired by the server.
        return Task.CompletedTask;
    }

    /// <summary>Send a specific response with custom streaming.</summary>
    public async Task SimulateResponseAsync(string response, int delayMs = 50, bool errorMidway = false)
    {
        var runId = $"run-{Interlocked.Increment(ref _runCounter)}";

        LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
        {
            RunId = runId,
            Phase = ChatLifecyclePhase.Start
        });

        var words = response.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (errorMidway && i == words.Length / 2)
            {
                LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
                {
                    RunId = runId,
                    Phase = ChatLifecyclePhase.Error,
                    ErrorMessage = "Simulated error"
                });
                return;
            }

            var delta = (i == 0 ? "" : " ") + words[i];
            DeltaReceived?.Invoke(this, new ChatStreamDelta
            {
                RunId = runId,
                Delta = delta
            });

            await Task.Delay(delayMs);
        }

        LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
        {
            RunId = runId,
            Phase = ChatLifecyclePhase.End
        });
    }

    private static string GenerateResponse(string userText)
    {
        var lower = userText.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("example"))
            return "Here's a code example:\n\n```csharp\npublic class HelloWorld\n{\n    public static void Main()\n    {\n        Console.WriteLine(\"Hello, World!\");\n    }\n}\n```\n\nThis demonstrates a simple C# program.";

        if (lower.Contains("markdown") || lower.Contains("format"))
            return "I support **bold**, *italic*, `inline code`, and:\n\n- Bullet lists\n- [Links](https://example.com)\n- Blockquotes\n\n> This is a blockquote.\n\nAnd fenced code blocks with language tags.";

        if (lower.Contains("long"))
            return "This is a longer response to test scrolling behavior. " +
                   "The chat control should handle messages of varying lengths gracefully. " +
                   "When a message is longer than the visible area, the scroll should remain stable. " +
                   "During streaming, auto-scroll should keep the latest content visible, " +
                   "but if the user has scrolled up to read earlier messages, " +
                   "the auto-scroll should disengage and not fight the user's scroll position. " +
                   "A floating scroll-to-bottom button should appear when new content arrives offscreen. " +
                   "This behavior is critical for a good chat UX.";

        if (lower.Contains("error"))
            return "This response will simulate an error condition.";

        return $"You said: \"{userText}\"\n\nI'm a mock assistant running in the dev app. " +
               "Try asking about **code**, **markdown**, or send a **long** message to test different rendering.";
    }
}
