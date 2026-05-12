# Native WinUI Chat Control — Implementation Plan (v2)

_Revised after 10-expert adversarial review (see `files/expert-synthesis.md` for full critique)._

## Problem

The current chat window (`ChatWindow.xaml`) is a WebView2 wrapper loading the gateway's web-based chat UI. We want a native WinUI chat control built on lightweight primitives for better performance, native look-and-feel, and full UX control.

## Approach

**Staged rollout:** Build the chat control as a standalone, gateway-independent visual component. Iterate in a dev app until solid. Then integrate into the tray app behind a feature flag as an alternative to WebView2 chat.

**Architecture:** Simple, flat, follow existing codebase conventions (A2UI pattern). One interface, feature folder, no extra layers. MVVM via CommunityToolkit.Mvvm. Two-phase rendering (plain text while streaming, markdown on completion).

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  ChatPanel (XAML control)                                     │
│  Owns: ChatViewModel, scroll behavior, templates              │
│  Consumer sets: IChatService via dependency property           │
├──────────────────────────────────────────────────────────────┤
│  ChatViewModel (ObservableObject)                             │
│  Owns: message collection, streaming state, send/abort logic  │
│  Depends on: IChatService (injected)                          │
├──────────────────────────────────────────────────────────────┤
│  IChatService (1 interface)                                   │
│  LoadHistory + Send + Abort + C# events for streaming         │
│  Implementations: MockChatService (dev), GatewayChatService   │
├──────────────────────────────────────────────────────────────┤
│  ChatMessage (ObservableObject)                               │
│  Id, Role, Content, Status, IsStreaming                        │
│  Mutable streaming state, immutable identity fields            │
└──────────────────────────────────────────────────────────────┘
```

### Design Principles (from expert review)

| Principle | Application |
|-----------|-------------|
| **YAGNI** | 1 interface, no factories, no extra layers. Extract abstractions only when a second real consumer appears. |
| **Codebase consistency** | Follow A2UI pattern (feature folder, C# events, single seam interface). Match `IOperatorGatewayClient`'s event-based style. |
| **Two-phase rendering** | Plain `TextBlock` during streaming (batched at ~10 Hz). Full markdown via Markdig+RichTextBlock on completion. Avoids O(n²) re-parse. |
| **Security by default** | No external image loads. Link scheme allowlist (https only). Content size limits. Parse markdown off UI thread. |
| **Testability** | ViewModel testable without UI thread via `Action<Action>` dispatcher injection. Hand-written fakes, no mocking frameworks needed. |

---

## Project Structure

```
src/
  OpenClaw.ChatControl/                    # WinUI class library
    OpenClaw.ChatControl.csproj
    IChatService.cs                        # Single interface: load, send, abort, events
    ChatMessage.cs                         # ObservableObject: role, content, status, streaming
    ChatViewModel.cs                       # Session orchestrator: message list, send command, stream dispatch
    ChatPanel.xaml/.cs                     # Main control: ScrollViewer + ListView + ChatInputBox
    ChatInputBox.xaml/.cs                  # Input area: multiline TextBox + Send/Stop button
    MessageTemplateSelector.cs             # DataTemplateSelector for user/assistant/system
    MarkdownRenderer.cs                    # Markdig → RichTextBlock Inlines (static helper)
    ChatStyles.xaml                        # All visual styling (bubble colors, spacing, fonts)

  OpenClaw.ChatControl.DevApp/             # Standalone WinUI app for iteration
    OpenClaw.ChatControl.DevApp.csproj
    App.xaml/.cs
    MainWindow.xaml/.cs                    # Hosts ChatPanel + dev test controls
    MockChatService.cs                     # Implements IChatService with fake streaming

tests/
  OpenClaw.ChatControl.Tests/              # Unit tests (net10.0-windows10.0.19041.0)
    OpenClaw.ChatControl.Tests.csproj
    ChatViewModelTests.cs                  # Streaming lifecycle, message ordering, errors
    ChatMessageTests.cs                    # Status transitions, delta accumulation
    MarkdownRendererTests.cs               # Markdig parse correctness (AST-level, no UI)
```

**~10 source files. 1 interface. 0 factories. 0 extra abstraction layers.**

---

## Key Abstractions

### IChatService — The single seam
```csharp
public interface IChatService
{
    /// Load conversation history for the current session.
    Task<IReadOnlyList<ChatMessage>> LoadHistoryAsync(CancellationToken ct = default);

    /// Send a user message. Returns the run ID for abort tracking.
    Task<string> SendAsync(string text, string idempotencyKey, CancellationToken ct = default);

    /// Abort an active agent run.
    Task AbortAsync(string runId, CancellationToken ct = default);

    /// Streaming events — fired on any thread; consumer must marshal to UI.
    event EventHandler<ChatStreamDelta>? DeltaReceived;
    event EventHandler<ChatLifecycleEvent>? LifecycleChanged;
}
```

- **One interface** — matches codebase convention (`IOperatorGatewayClient` style)
- **C# events** — idiomatic, testable, no custom observer boilerplate
- **Thread contract:** Events fire on any thread. `ChatViewModel` captures `DispatcherQueue` at construction and marshals internally.

### ChatMessage — Observable data
```csharp
public partial class ChatMessage : ObservableObject
{
    public string Id { get; init; }
    public MessageRole Role { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    [ObservableProperty] private string _content = "";
    [ObservableProperty] private MessageStatus _status = MessageStatus.Complete;
    [ObservableProperty] private bool _isStreaming;

    // For streaming: accumulate deltas efficiently
    private readonly StringBuilder _contentBuffer = new();
    public void AppendDelta(string delta) { _contentBuffer.Append(delta); Content = _contentBuffer.ToString(); }
    public void FinalizeContent() { Content = _contentBuffer.ToString(); IsStreaming = false; }
}

public enum MessageRole { User, Assistant, System }
public enum MessageStatus { Sending, Thinking, Streaming, Complete, Error, Aborted }
```

- **Single object** — no immutable record + mutable wrapper duplication
- **`{get; init;}` for identity** — Role, Id, Timestamp are set once
- **`[ObservableProperty]` for mutable state** — Content, Status, IsStreaming change during lifecycle
- **`Thinking` status** — covers the gap before first delta (expert #6 finding)

### ChatViewModel — Session orchestrator
```csharp
public partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IChatService _service;
    private readonly Action<Action> _dispatchToUI;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    [ObservableProperty] private bool _isRunActive;
    [ObservableProperty] private string? _activeRunId;
    [ObservableProperty] private string? _errorMessage;

    public ChatViewModel(IChatService service, Action<Action> dispatchToUI) { ... }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(string text) { ... }

    [RelayCommand(CanExecute = nameof(IsRunActive))]
    private async Task AbortAsync() { ... }

    // Event handlers marshal to UI thread, batch deltas, update ChatMessage
}
```

- **`Action<Action> dispatchToUI`** — testable without DispatcherQueue (tests pass `action => action()`)
- **No DI container** — constructor injection of concrete dependencies
- **Implements IDisposable** — unsubscribes from IChatService events on dispose

---

## Key Design Decisions

### 1. ListView (not ItemsRepeater)
- Built-in scroll anchoring via `ItemsUpdatingScrollMode.KeepLastItemInView`
- Built-in accessibility (screen reader, keyboard navigation)
- `DataTemplateSelector` works directly
- Strip selection chrome via `ItemContainerStyle` (no hover, no selection highlight)
- Chat has ~100 messages, not thousands — ListView's overhead is negligible

### 2. Two-phase rendering
- **During streaming:** `TextBlock` displays accumulated plain text. UI updates batched via `DispatcherQueueTimer` at ~10 Hz (not per-token). `StringBuilder` accumulates off-thread.
- **On completion:** `MarkdownRenderer.Render(content)` parses via Markdig and produces `RichTextBlock` `Inline`/`Block` elements. The DataTemplate swaps from streaming view to rendered view based on `IsStreaming` property.
- **Why:** Avoids O(n²) re-parse, layout thrash, and broken partial markdown. All 3 rendering experts agreed this is the right approach.

### 3. Markdig + custom RichTextBlock renderer (not CommunityToolkit MarkdownTextBlock)
- **Markdig** is actively maintained, widely used, pure parser (no UI deps)
- Custom renderer targets only the subset we need: paragraphs, emphasis, inline code, fenced code blocks, links, lists, blockquotes
- No stale dependency (CommunityToolkit markdown is archived, last release 2021)
- Renderer is a static helper, not an interface — extract interface if a second renderer appears

### 4. Bubble-style visual design (swappable)
- All visual properties in `ChatStyles.xaml` — colors, corner radius, padding, max-width, fonts
- User bubbles right-aligned (accent), assistant bubbles left-aligned (surface)
- Swap to flat cards or other styles by merging a different resource dictionary
- Timestamps hidden by default (show on hover)

### 5. Smart auto-scroll (private methods, no interface)
- Auto-scroll while user is at/near bottom
- Disengage when user scrolls up manually
- Show floating "↓ New messages" button when disengaged and new content arrives
- Re-engage when user clicks button or scrolls back to bottom
- ~30 lines of code in ChatPanel code-behind

### 6. Security defaults
- **Images disabled** in markdown renderer (strip `![](...)` syntax, show placeholder)
- **Link clicks intercepted** — allowlist: `https://` only. Block `file://`, `ms-settings:`, `shell:`, etc.
- **Content size limits** — max 256KB per message, max 10,000 messages in memory
- **Stream timeout** — mark as truncated/error if no `lifecycle.end` within 5 minutes
- **Parse off UI thread** — markdown parsing in `Task.Run`, time-budgeted

### 7. Input bar behavior
- Multiline `TextBox` — grows up to ~4 lines, then internal scroll
- `Enter` sends, `Shift+Enter` inserts newline
- Placeholder text: "Message..." (configurable)
- Send button disabled when input is empty/whitespace
- While run active: Send becomes Stop (abort)
- Preserve unsent input on send failure

---

## Implementation Phases

### Phase 1: Project Scaffolding + Models ✅
- Created `OpenClaw.ChatControl` class library, `OpenClaw.ChatControl.DevApp` WinUI app, `OpenClaw.ChatControl.Tests`
- Added to solution, NuGet refs (CommunityToolkit.Mvvm 8.4.2, Markdig 0.40.0, WindowsAppSDK 2.0.1)
- Created `IChatService.cs`, `ChatMessage.cs`, `ChatStyles.xaml`
- 8 passing ChatMessage tests

### Phase 2: ViewModel + Mock Service ✅
- Implemented `ChatViewModel.cs` with send/abort commands, streaming event dispatch, `Action<Action>` dispatcher
- Implemented `MockChatService.cs` with fake history, word-by-word streaming, error simulation
- 13 passing ChatViewModel tests (streaming, errors, abort, disposal, sequential sends)

### Phase 3: Core Controls + Dev App ✅
- Built `ChatPanel.xaml` — ListView with user/assistant DataTemplates, auto-scroll, empty state, error bar
- Built `ChatInputBox.xaml` — multiline TextBox, Enter/Shift+Enter, Send/Stop toggle
- Built `MessageTemplateSelector.cs`, wired dev app with sidebar controls
- **Milestone achieved:** Working chat with plain text, streaming, send/receive in dev app

### Phase 4: Markdown Rendering ✅
- Implemented `MarkdownRenderer.cs` using Markdig (paragraphs, headings, bold/italic, inline code, fenced code blocks, links, lists, blockquotes, thematic breaks)
- Security: images stripped (show `[image]` placeholder), link scheme allowlist (https/http only), unsafe schemes blocked
- Created `AssistantMessageControl.xaml/.cs` — two-phase rendering:
  - Streaming: plain TextBlock (fast updates, no re-parse)
  - Complete: switches to rendered markdown via Markdig → RichTextBlock
- Updated assistant DataTemplate in ChatPanel to use AssistantMessageControl
- 18 passing MarkdownRenderer tests (AST-level, no UI thread required)
- **Milestone achieved:** Full streaming + markdown chat in dev app

### Phase 5: Polish + Tray Wire-up ✅
- Added `EnableNativeChatDev` feature flag to `SettingsData` + `SettingsManager` (default false)
- Added "Native Chat (Dev)" tray menu item (shown only when flag is enabled, icon: 🧪)
- Created `NativeChatWindow.xaml/.cs` with full Win32 tray integration (tool window, DPI-aware positioning, auto-hide, hide-instead-of-close, collapsed title bar)
- When `EnableNativeChatDev=true`, left-clicking tray icon and "Chat" menu item both route to native chat
- **Milestone achieved:** Native chat accessible from tray, wired to real gateway

### Phase 6: GatewayChatService + Gateway RPCs ✅
- Added `RequestChatHistoryAsync(sessionKey)` and `AbortRunAsync(runId)` RPCs to `OpenClawGatewayClient` + `IOperatorGatewayClient`
- Added `ChatHistoryMessage` model
- Built `GatewayChatService` adapter mapping `AgentEventReceived` (assistant/lifecycle/job streams) to `IChatService` events
- `NativeChatWindow.Initialize(operatorClient)` wires to real gateway — no mocks in production

### Phase 7: Cosmetic Parity ✅
- Header bar: 🦞 "Chat" title + pop-out (red border, not wired) + close (wired)
- Timestamps under each message ("8:20 PM" format)
- 🦞 avatar left of assistant bubbles
- Input bar redesigned: "Message Assistant (Enter to send)", toolbar with 📎 attach, 🎙 mic, ··· more (red border = not wired), send ▷
- Footer: "OpenClaw Native Chat" label
- All colors theme-aware (light + dark)

---

## Deferred (Post-MVP, in priority order)

1. ~~`GatewayChatService` adapter~~ ✅ Done
2. ~~Win32 window behaviors~~ ✅ Done
3. **Tool call cards** — expandable "⚡ Tool call `read`" UI (see web chat screenshot in session)
4. **Scroll behavior bugs** — jitter during streaming, auto-scroll doesn't always engage properly
5. **Session switching** — new chat (`/new`), switch between sessions
6. **Wire placeholder buttons** — attach 📎, mic 🎙, more ··· (currently red-bordered)
7. **Slash command hints** — `/new`, `/model`, `/status` autocomplete in input bar
8. **Media/image rendering** — with security controls
9. **Reasoning/thinking blocks** — collapsible
10. **Message copy / context menu** — right-click copy on messages
11. **Pre-warm** — create NativeChatWindow at app startup for instant open (like WebView2 ChatWindow)
12. **BootstrapMessageInjector** — native overload (call `SendAsync` directly instead of JS injection)
13. **Replace WebView2 as default** — flip feature flag after stabilization

---

## Known Bugs (from testing 2026-05-11)

1. **Scroll jitter during streaming** — improved with coalescing + programmatic scroll suppression, but still not perfect. Possible cause: `ChangeView` called before ItemsRepeater finishes layout for the growing content.
2. **Auto-scroll doesn't always engage** — after sending a message, the viewport sometimes stays put instead of following new content down.
3. **Markdown rendering during streaming shows raw markdown** — this is by design (two-phase rendering: plain text during streaming, markdown on completion), but the transition from raw text to rendered markdown can be visually jarring.
4. **`chat.history` RPC response format** — needs live testing with different gateway versions; content can be string or array of content blocks, both are handled but edge cases may exist.
5. **`GatewayChatService` run ID correlation** — `SendChatMessageAsync` doesn't return a run ID; the adapter uses the idempotency key as correlation and picks up the real run ID from the first `AgentEventReceived`. This works for single-active-run but may need refinement for concurrent runs.

---

## Dependencies

| Package | Purpose | Risk |
|---------|---------|------|
| `Markdig` | Markdown parser (actively maintained, ~200M downloads) | Low |
| `CommunityToolkit.Mvvm` | ObservableObject, RelayCommand, source generators | Low |
| `Microsoft.WindowsAppSDK` | WinUI 3 framework | Low (already used) |

No stale or archived packages. No transitive UI framework pulls.

---

## Integration Prep (documented, not implemented yet)

When we build `GatewayChatService`, these gateway client changes are prerequisites:
- Add `ChatTurnReceived` event to `OpenClawGatewayClient` (all streaming states, full content)
- Add `RequestChatHistoryAsync(sessionKey, limit)` RPC
- Adapter receives `GatewayConnectionManager.OperatorClient` — no new WS connections
- `BootstrapMessageInjector` needs a native overload (call `SendAsync` directly instead of JS injection)
- Feature flag gates the cutover; explicit go-criteria before flipping default

---

## Notes

- The chat control targets `net10.0-windows10.0.19041.0` (same as tray app)
- `OpenClaw.ChatControl` has **zero references** to `OpenClaw.Shared`
- Consumer experience: `<chat:ChatPanel ChatService="{x:Bind ...}" />` — one property
- All gateway protocol knowledge stays outside the control
- Tests use `Action<Action>` dispatcher injection — `action => action()` for sync test execution
- Feature flag ensures safe rollback if native chat has issues
