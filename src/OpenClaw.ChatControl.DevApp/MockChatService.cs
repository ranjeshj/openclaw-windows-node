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
    public event EventHandler<ChatInjectEvent>? MessageInjected;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler? Reconnected;
#pragma warning restore CS0067

    public bool IsConnected => true;

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<ChatMessage>> LoadHistoryAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var history = new List<ChatMessage>();
        int id = 0;
        string Id() => $"demo-{++id}";

        // ── 1. Plain text ──
        history.Add(new(Id(), MessageRole.User, "Hello!", now.AddMinutes(-30)));
        var plain = new ChatMessage(Id(), MessageRole.Assistant,
            "Hi there! I'm your AI assistant. How can I help you today?",
            now.AddMinutes(-29)) { SenderLabel = "Assistant", ModelName = "gpt-5.5" };
        history.Add(plain);

        // ── 2. Markdown: bold, italic, inline code, lists, blockquote ──
        history.Add(new(Id(), MessageRole.User, "Show me markdown formatting", now.AddMinutes(-28)));
        var md = new ChatMessage(Id(), MessageRole.Assistant,
            "Here's what I can render:\n\n" +
            "**Bold text**, *italic text*, and `inline code`.\n\n" +
            "### Lists\n" +
            "- Bullet one\n" +
            "- Bullet two\n" +
            "  - Nested bullet\n\n" +
            "1. Numbered one\n" +
            "2. Numbered two\n\n" +
            "### Blockquote\n" +
            "> This is a blockquote with *emphasis*.\n\n" +
            "### Thematic break\n" +
            "---\n\n" +
            "And [links](https://example.com) are rendered as inert text for security.",
            now.AddMinutes(-27)) { SenderLabel = "Assistant", ModelName = "gpt-5.5" };
        history.Add(md);

        // ── 3. Code blocks (multiple languages) ──
        history.Add(new(Id(), MessageRole.User, "Show me code blocks", now.AddMinutes(-26)));
        var code = new ChatMessage(Id(), MessageRole.Assistant,
            "Here's C#:\n\n" +
            "```csharp\npublic class HelloWorld\n{\n    public static void Main()\n    {\n        Console.WriteLine(\"Hello, World!\");\n    }\n}\n```\n\n" +
            "And Python:\n\n" +
            "```python\ndef greet(name: str) -> str:\n    return f\"Hello, {name}!\"\n\nprint(greet(\"World\"))\n```\n\n" +
            "And JSON:\n\n" +
            "```json\n{\n  \"name\": \"OpenClaw\",\n  \"version\": \"1.0\",\n  \"features\": [\"chat\", \"tools\", \"voice\"]\n}\n```",
            now.AddMinutes(-25)) { SenderLabel = "Assistant", ModelName = "gpt-5.5" };
        history.Add(code);

        // ── 4. Tool calls (multiple tools with args + output) ──
        history.Add(new(Id(), MessageRole.User, "Search the codebase for TODOs", now.AddMinutes(-24)));
        var tools = new ChatMessage(Id(), MessageRole.Assistant,
            "I found 3 TODOs. Here's what I did to fix them.",
            now.AddMinutes(-23))
        { SenderLabel = "Assistant", ModelName = "gpt-5.5", InputTokens = 2340, OutputTokens = 156, ContextPercent = 34 };
        tools.ToolCalls.Add(new ToolCallInfo("tc-grep", "grep")
        {
            Phase = ToolCallPhase.Done, ArgsJson = "{\n  \"pattern\": \"TODO\",\n  \"path\": \"src/\"\n}",
            ResultSummary = "Found 3 matches", ToolOutput = "src/App.cs:42: // TODO: refactor\nsrc/Main.cs:15: // TODO: logging\nsrc/Utils.cs:8: // TODO: edge case"
        });
        tools.ToolCalls.Add(new ToolCallInfo("tc-read", "read")
        {
            Phase = ToolCallPhase.Done, ArgsJson = "{\n  \"path\": \"src/App.cs\",\n  \"view_range\": [40, 50]\n}",
            ResultSummary = "Read 10 lines", ToolOutput = "40. namespace MyApp;\n41. \n42. // TODO: refactor this class\n43. public class App\n44. {\n45.     public void Run() { }\n46. }"
        });
        tools.ToolCalls.Add(new ToolCallInfo("tc-edit", "edit")
        {
            Phase = ToolCallPhase.Done, ArgsJson = "{\n  \"path\": \"src/App.cs\",\n  \"old_str\": \"// TODO: refactor\",\n  \"new_str\": \"// Refactored\"\n}",
            ResultSummary = "Applied edit"
        });
        tools.ToolCalls.Add(new ToolCallInfo("tc-exec", "exec")
        {
            Phase = ToolCallPhase.Done, ArgsJson = "{\n  \"command\": \"dotnet build\"\n}",
            ResultSummary = "Build succeeded", ToolOutput = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\nTime Elapsed 00:00:03.42"
        });
        tools.ToolCalls.Add(new ToolCallInfo("tc-glob", "glob")
        {
            Phase = ToolCallPhase.Done, ArgsJson = "{\n  \"pattern\": \"**/*.cs\"\n}",
            ResultSummary = "5 files", ToolOutput = "src/App.cs\nsrc/Main.cs\nsrc/Utils.cs\ntests/AppTests.cs\ntests/UtilsTests.cs"
        });
        tools.ToolCalls.Add(new ToolCallInfo("tc-web", "web_search")
        {
            Phase = ToolCallPhase.Done, ArgsJson = "{\n  \"query\": \"WinUI ScrollViewer best practices\"\n}",
            ResultSummary = "3 results", ToolOutput = "1. Microsoft Docs: ScrollViewer\n2. SO: Auto-scroll in ItemsRepeater\n3. GitHub: WinUI scroll issues"
        });
        history.Add(tools);

        // ── 5. Tool call error ──
        history.Add(new(Id(), MessageRole.User, "Run a dangerous command", now.AddMinutes(-22)));
        var toolErr = new ChatMessage(Id(), MessageRole.Assistant,
            "⚠️ The command failed with a permission error. I can't execute destructive operations.",
            now.AddMinutes(-21)) { SenderLabel = "Assistant", ModelName = "gpt-5.5" };
        toolErr.ToolCalls.Add(new ToolCallInfo("tc-fail", "exec")
        {
            Phase = ToolCallPhase.Error, ArgsJson = "{\n  \"command\": \"rm -rf /\"\n}",
            ResultSummary = "Permission denied: operation not permitted"
        });
        history.Add(toolErr);

        // ── 6. Reasoning/thinking block ──
        history.Add(new(Id(), MessageRole.User, "What's the best approach to fix this bug?", now.AddMinutes(-20)));
        var reasoning = new ChatMessage(Id(), MessageRole.Assistant,
            "# Recommended Approach\n\n" +
            "Based on my analysis, here's the best fix:\n\n" +
            "1. Add a null check before the dereference\n" +
            "2. Use `TryGetValue` instead of direct indexer access\n" +
            "3. Add a unit test for the edge case\n\n" +
            "This is the safest approach with minimal risk of regression.",
            now.AddMinutes(-19)) { SenderLabel = "Assistant", ModelName = "claude-sonnet-4.5", InputTokens = 890, OutputTokens = 67 };
        reasoning.AppendReasoning(
            "Let me analyze this bug carefully.\n\n" +
            "The issue is a NullReferenceException at line 42. The variable `_activeMessage` " +
            "can be null when events arrive before initialization. There are three approaches:\n\n" +
            "1. Add a null check — simplest, lowest risk\n" +
            "2. Use a Nullable pattern — more idiomatic but bigger change\n" +
            "3. Restructure initialization order — fixes root cause but high risk\n\n" +
            "I'll recommend option 1 since it's the safest.");
        history.Add(reasoning);

        // ── 7. Status messages (different tones) ──
        history.Add(new(Id(), MessageRole.Status, "ℹ️ Connected to gateway at localhost:18789") { Tone = ChatTone.Info });
        history.Add(new(Id(), MessageRole.Status, "✅ Session reset successfully") { Tone = ChatTone.Success });
        history.Add(new(Id(), MessageRole.Status, "⚠️ Context window at 85% — consider using /compact") { Tone = ChatTone.Warning });

        // ── 8. Long response (scroll test) ──
        history.Add(new(Id(), MessageRole.User, "Give me a long response", now.AddMinutes(-16)));
        var longResp = new ChatMessage(Id(), MessageRole.Assistant,
            "# Long Response Test\n\n" +
            "This is a longer response to test scrolling behavior.\n\n" +
            "## Section 1: Scroll Behavior\n\n" +
            "The chat control should handle messages of varying lengths gracefully. " +
            "When a message is longer than the visible area, the scroll should remain stable. " +
            "During streaming, auto-scroll should keep the latest content visible.\n\n" +
            "## Section 2: User Scroll\n\n" +
            "If the user has scrolled up to read earlier messages, " +
            "the auto-scroll should disengage and not fight the user's scroll position. " +
            "A floating \"↓ New messages\" button should appear when new content arrives offscreen.\n\n" +
            "## Section 3: Code in Long Response\n\n" +
            "```csharp\n// This code block should render properly even in a long response\npublic class LongExample\n{\n    public void Process()\n    {\n        for (int i = 0; i < 100; i++)\n        {\n            Console.WriteLine($\"Processing item {i}\");\n        }\n    }\n}\n```\n\n" +
            "## Section 4: Lists\n\n" +
            "- First item with some explanation text\n" +
            "- Second item with **bold** and `code`\n" +
            "- Third item with a longer description that wraps across multiple lines to test how the layout handles it\n" +
            "- Fourth item\n" +
            "- Fifth item\n\n" +
            "## Section 5: Conclusion\n\n" +
            "This demonstrates that long responses with mixed content render correctly.",
            now.AddMinutes(-15)) { SenderLabel = "Assistant", ModelName = "gpt-5.5", InputTokens = 500, OutputTokens = 342 };
        history.Add(longResp);

        // ── 9. Image/embed placeholder ──
        history.Add(new(Id(), MessageRole.User, "Show me a weather card", now.AddMinutes(-14)));
        var embed = new ChatMessage(Id(), MessageRole.Assistant,
            "Here's the current weather:\n\n" +
            "[embed ref=\"weather-card-2026\" title=\"Weather Card\" height=\"400\" /]\n\n" +
            "The temperature is 72°F with clear skies. ![weather icon](https://example.com/sun.png)\n\n" +
            "Note: embedded cards and images show as placeholders in the native chat.",
            now.AddMinutes(-13)) { SenderLabel = "Assistant", ModelName = "gpt-5.5" };
        history.Add(embed);

        // ── 10. System message ──
        history.Add(new(Id(), MessageRole.System, "Session started · Model: gpt-5.5 · Agent: main",
            now.AddMinutes(-10)));

        // ── 11. Metadata footer showcase ──
        history.Add(new(Id(), MessageRole.User, "Show full metadata", now.AddMinutes(-8)));
        var meta = new ChatMessage(Id(), MessageRole.Assistant,
            "Look at the footer below this message — it shows the full metadata: " +
            "sender, timestamp, input/output token counts, context usage, and model name.",
            now.AddMinutes(-7))
        {
            SenderLabel = "Field",
            ModelName = "claude-opus-4.7",
            InputTokens = 45200,
            OutputTokens = 1230,
            ContextPercent = 67
        };
        history.Add(meta);

        // ── 12. Tool in-progress (spinner) ──
        history.Add(new(Id(), MessageRole.User, "Start a long running task", now.AddMinutes(-5)));
        var running = new ChatMessage(Id(), MessageRole.Assistant, "",
            now.AddMinutes(-4)) { SenderLabel = "Assistant", ModelName = "gpt-5.5" };
        running.Status = MessageStatus.Streaming;
        running.IsStreaming = true;
        running.ToolCalls.Add(new ToolCallInfo("tc-running", "task")
        {
            Phase = ToolCallPhase.Running, ArgsJson = "{\n  \"name\": \"long-analysis\",\n  \"prompt\": \"Analyze the entire codebase...\"\n}"
        });
        running.ToolCalls.Add(new ToolCallInfo("tc-bash-running", "bash")
        {
            Phase = ToolCallPhase.Running, ArgsJson = "{\n  \"command\": \"npm run build\"\n}"
        });
        history.Add(running);

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
        var lower = text.Trim().ToLowerInvariant();

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

                // ── Vary the response scenario based on keywords ──

                if (lower.Contains("tools") || lower.Contains("multi"))
                    await SimulateMultiToolResponse(runId, ct);
                else if (lower.Contains("error"))
                    await SimulateErrorResponse(runId, ct);
                else if (lower.Contains("reason") || lower.Contains("think"))
                    await SimulateReasoningResponse(runId, ct);
                else if (lower.Contains("status"))
                    await SimulateStatusResponse(runId, ct);
                else
                    await SimulateStandardResponse(runId, text, ct);

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

    private async Task SimulateStandardResponse(string runId, string userText, CancellationToken ct)
    {
        // Single tool call + reasoning + text
        ReasoningReceived?.Invoke(this, new ChatReasoningEvent
        {
            RunId = runId,
            Delta = "Let me think about this... I should consider the user's question carefully and provide a helpful response."
        });

        var toolCallId = $"tool-{runId}-1";
        ToolCallReceived?.Invoke(this, new ChatToolCallEvent
        {
            RunId = runId, ToolCallId = toolCallId, ToolName = "read",
            Phase = ToolCallPhase.Running,
            ArgsJson = "{\n  \"path\": \"src/main.cs\",\n  \"lines\": \"1-42\"\n}"
        });
        await Task.Delay(200, ct);
        ToolCallReceived?.Invoke(this, new ChatToolCallEvent
        {
            RunId = runId, ToolCallId = toolCallId, ToolName = "read",
            Phase = ToolCallPhase.Done,
            ResultSummary = "Read 42 lines from main.cs",
            ToolOutput = "using System;\nnamespace MyApp;\n\npublic class Program\n{\n    static void Main() => Console.WriteLine(\"Hello\");\n}"
        });

        await StreamText(runId, GenerateResponse(userText), ct);
    }

    private async Task SimulateMultiToolResponse(string runId, CancellationToken ct)
    {
        // Multiple different tool types
        var tools = new[]
        {
            ("grep", "🔍 Search", "{\n  \"pattern\": \"TODO\",\n  \"path\": \"src/\"\n}", "Found 7 matches across 3 files:\n  src/App.cs:42: // TODO: refactor\n  src/Main.cs:15: // TODO: add logging\n  src/Utils.cs:8: // TODO: handle edge case"),
            ("read", "📄 Read", "{\n  \"path\": \"src/App.cs\",\n  \"view_range\": [40, 50]\n}", "40. namespace MyApp;\n41. \n42. // TODO: refactor this class\n43. public class App\n44. {\n45.     public void Run() { }\n46. }"),
            ("glob", "📂 Files", "{\n  \"pattern\": \"**/*.cs\"\n}", "src/App.cs\nsrc/Main.cs\nsrc/Utils.cs\ntests/AppTests.cs\ntests/UtilsTests.cs"),
            ("edit", "✏️ Edit", "{\n  \"path\": \"src/App.cs\",\n  \"old_str\": \"// TODO: refactor\",\n  \"new_str\": \"// Refactored\"\n}", "Applied edit to src/App.cs"),
            ("exec", "⚡ Exec", "{\n  \"command\": \"dotnet build\"\n}", "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:03.42"),
            ("web_search", "🔎 Search", "{\n  \"query\": \"WinUI 3 ScrollViewer best practices\"\n}", "Found 5 results:\n1. Microsoft Docs: ScrollViewer in WinUI 3\n2. Stack Overflow: Auto-scroll in ItemsRepeater\n3. GitHub: WinUI scroll issues"),
        };

        foreach (var (name, _, args, output) in tools)
        {
            ct.ThrowIfCancellationRequested();
            var tcId = $"tool-{runId}-{name}";
            ToolCallReceived?.Invoke(this, new ChatToolCallEvent
            {
                RunId = runId, ToolCallId = tcId, ToolName = name,
                Phase = ToolCallPhase.Running, ArgsJson = args
            });
            await Task.Delay(150, ct);
            ToolCallReceived?.Invoke(this, new ChatToolCallEvent
            {
                RunId = runId, ToolCallId = tcId, ToolName = name,
                Phase = ToolCallPhase.Done,
                ResultSummary = output.Length > 80 ? output[..80] + "..." : output,
                ToolOutput = output
            });
        }

        await StreamText(runId,
            "I've searched the codebase, found the TODOs, read the file, applied the fix, " +
            "ran the build (all green ✅), and looked up best practices. " +
            "Here's what I did:\n\n" +
            "1. Found 7 TODO comments across 3 files\n" +
            "2. Read `src/App.cs` around the TODO\n" +
            "3. Replaced the TODO with a proper comment\n" +
            "4. Build succeeded with 0 errors\n\n" +
            "The refactoring is complete.", ct);
    }

    private async Task SimulateErrorResponse(string runId, CancellationToken ct)
    {
        // Tool call that errors
        var tcId = $"tool-{runId}-fail";
        ToolCallReceived?.Invoke(this, new ChatToolCallEvent
        {
            RunId = runId, ToolCallId = tcId, ToolName = "exec",
            Phase = ToolCallPhase.Running,
            ArgsJson = "{\n  \"command\": \"rm -rf /\"\n}"
        });
        await Task.Delay(300, ct);
        ToolCallReceived?.Invoke(this, new ChatToolCallEvent
        {
            RunId = runId, ToolCallId = tcId, ToolName = "exec",
            Phase = ToolCallPhase.Error,
            ResultSummary = "Permission denied: operation not permitted"
        });

        // Status event
        StatusReceived?.Invoke(this, new ChatStatusEvent
        {
            RunId = runId,
            Text = "Tool execution failed — permission denied",
            Tone = ChatTone.Error
        });

        // Still provide a text response after the error
        await StreamText(runId,
            "⚠️ The command failed with a permission error. " +
            "I don't have permission to execute destructive commands. " +
            "Would you like me to try a safer approach?", ct);
    }

    private async Task SimulateReasoningResponse(string runId, CancellationToken ct)
    {
        // Extended reasoning with multiple segments
        ReasoningReceived?.Invoke(this, new ChatReasoningEvent
        {
            RunId = runId,
            Delta = "The user is asking about reasoning capabilities. "
        });
        await Task.Delay(100, ct);
        ReasoningReceived?.Invoke(this, new ChatReasoningEvent
        {
            RunId = runId,
            Delta = "I should demonstrate the thinking/reasoning block feature. " +
                    "This is the internal chain-of-thought that models like o1 or Claude produce. " +
                    "It should appear as a collapsible dimmed block above the response. " +
                    "The user can expand it to see what the model was thinking.\n\n" +
                    "Key considerations:\n" +
                    "- Reasoning should be clearly distinguished from the final response\n" +
                    "- It should be collapsible so it doesn't clutter the chat\n" +
                    "- The <think> tags are stripped from the final display\n" +
                    "- Multiple reasoning blocks can appear in sequence"
        });
        await Task.Delay(200, ct);

        await StreamText(runId,
            "# Reasoning Blocks\n\n" +
            "I just demonstrated the **reasoning/thinking** feature. " +
            "You should see a collapsible \"💭 Reasoning\" block above this text.\n\n" +
            "This is how models like GPT-o1 and Claude show their chain-of-thought:\n\n" +
            "- The `<think>` tags in the model output are parsed by `AssistantTextParser`\n" +
            "- Reasoning text appears in a dimmed, italic, collapsible block\n" +
            "- The final response appears as normal markdown below\n\n" +
            "Try clicking the reasoning block to expand/collapse it.", ct);
    }

    private async Task SimulateStatusResponse(string runId, CancellationToken ct)
    {
        // Fire various status events
        StatusReceived?.Invoke(this, new ChatStatusEvent
        {
            RunId = runId, Text = "ℹ️ Connected to gateway at localhost:18789", Tone = ChatTone.Info
        });
        await Task.Delay(200, ct);
        StatusReceived?.Invoke(this, new ChatStatusEvent
        {
            RunId = runId, Text = "✅ Model loaded: gpt-5.5", Tone = ChatTone.Success
        });
        await Task.Delay(200, ct);
        StatusReceived?.Invoke(this, new ChatStatusEvent
        {
            RunId = runId, Text = "⚠️ Context window at 85% — consider compacting", Tone = ChatTone.Warning
        });
        await Task.Delay(200, ct);

        await StreamText(runId,
            "I've demonstrated **status messages** with different tones:\n\n" +
            "- **Info** (ℹ️): General informational messages\n" +
            "- **Success** (✅): Confirmation of completed actions\n" +
            "- **Warning** (⚠️): Non-critical alerts\n" +
            "- **Error** (❌): Critical failures\n\n" +
            "Status messages appear as centered timeline entries between user/assistant bubbles.", ct);
    }

    private async Task StreamText(string runId, string response, CancellationToken ct)
    {
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
                RunId = runId, Delta = delta
            });
            await Task.Delay(StreamDelayMs, ct);
        }
    }

    public Task AbortAsync(string runId, CancellationToken ct = default)
    {
        // In a real implementation, this would cancel the active run.
        // The lifecycle end/error event would be fired by the server.
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatSessionInfo>> ListSessionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatSessionInfo>>(Array.Empty<ChatSessionInfo>());

    public Task SwitchSessionAsync(string sessionKey, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ResetSessionAsync(string? sessionKey, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CompactSessionAsync(string? sessionKey, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new[] { "gpt-5.5", "claude-sonnet-4.5", "gpt-5-mini" });

    public Task SetModelAsync(string model, CancellationToken ct = default)
        => Task.CompletedTask;

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
