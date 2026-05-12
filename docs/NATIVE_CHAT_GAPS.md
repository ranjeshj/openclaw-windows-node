# Native Chat Control — Gateway Feature Gaps

_Last updated: 2026-05-12. Generated from adversarial review (Opus/GPT-5.5/Sonnet) + gateway protocol deep analysis._

## Overview

The native WinUI chat control (`OpenClaw.ChatControl`) consumes gateway events via `GatewayChatService` → `IChatService`. This document tracks which gateway features are supported, partially supported, or missing.

## Feature Gap Table

| # | Gateway Feature | Event/RPC | Support | Gap Description |
|---|---|---|---|---|
| 1 | Assistant text deltas | `agent {stream:"assistant", delta}` | ✅ | Mapped to `DeltaReceived`. |
| 2 | Reasoning (`isReasoning`) | `agent {stream:"assistant", isReasoning:true}` | ✅ | Mapped to `ReasoningReceived`. Collapsible `ReasoningBlock` in UI. No `/reasoning on/off` toggle. |
| 3 | Lifecycle events | `agent {stream:"lifecycle", phase}` | ✅ | Start/End/Error with model + token metadata. |
| 4 | Job events | `agent {stream:"job", state}` | ✅ Partial | Mapped onto `LifecycleChanged`. Loses job-specific semantics (queueing, steer). |
| 5 | Tool start/result | `agent {stream:"tool", phase}` | ✅ | Args extracted as JSON, full output preserved. |
| 6 | Tool full output vs summary | `result.content[].text` | ✅ | Full text kept; summary truncated to 200 chars. |
| 7 | Tool `details` (media/artifacts) | `result.details` | ❌ | Dropped entirely. Details can contain media URLs, artifacts, file previews. |
| 8 | `item` stream events | `agent {stream:"item"}` | ❌ | Silently ignored in `OnAgentEvent` switch. |
| 9 | `error` stream events | `agent {stream:"error"}` | ❌ | Silently ignored. |
| 10 | Status/notification events | Gateway warning/info pushes | ⚠️ | `IChatService.StatusReceived` + `ChatStatusEvent` defined but `GatewayChatService` never raises it. Dead code. |
| 11 | Embedded cards | `[embed ref="..." title="..." height="..."]` | ❌ | Embed directives in assistant text rendered as raw text. No parsing or expansion. |
| 12 | Mermaid diagrams | ` ```mermaid ``` ` fenced blocks | ❌ | Rendered as code block, not as diagram. |
| 13 | LaTeX / math | `$...$`, `$$...$$` | ❌ | No MathJax/KaTeX renderer. |
| 14 | Syntax highlighting | Fenced code blocks with language tags | ⚠️ | Language label shown but no color syntax highlighting. |
| 15 | Media / image content | `MEDIA:` directives, image attachments | ❌ | No media model on `ChatMessage`. Images stripped by sanitizer. |
| 16 | Permission / approval events | `exec.approval.*` | ❌ | Not handled by `OpenClawGatewayClient.HandleAgentEvent`. No approve/deny UI. |
| 17 | `chat.inject` | Server-pushed assistant notes | ❌ | `GatewayChatService` only consumes `AgentEventReceived`. Injections won't appear until next `chat.history` reload. |
| 18 | Slash commands | `/new`, `/model`, `/status`, `/reasoning` | ⚠️ | Only `/new` intercepted (resets session). Others pass through as text. |
| 19 | Silent replies (`NO_REPLY`) | Model suppression token | ⚠️ | Gateway strips in `chat.history`, but no client-side filter on live stream. |
| 20 | Aborted run partial text | `chat.abort` + partial persistence | ⚠️ | Partial deltas preserved locally. History reconstruction doesn't propagate abort metadata. |
| 21 | Reconnect + history refresh | Connection state changes | ❌ | `GatewayChatService` doesn't subscribe to connection state. Missed events not recovered. No "disconnected" UI. |
| 22 | Presence events | `event: presence` | ❌ | Not surfaced on `IChatService`. (Tray handles elsewhere.) |
| 23 | Tick / keepalive | `event: tick`, 2× timeout | ❌ | No tick-based health signal. No connection status indicator. |
| 24 | Multiple concurrent sessions | `sessionKey` in events | ❌ | `IChatService` is single-session. Events filtered to "main" session only. No session switcher. |
| 25 | `chat.history` display normalization | Strip tags, truncate, exclude reasoning | ⚠️ | Relies on gateway-side normalization. No client-side stripping of `[[reply_to_*]]`, `<tool_call>`, control tokens. |
| 26 | History role mapping | Tool calls and reasoning from history | ❌ | Only role+content+timestamp mapped. Tool call cards from previous sessions are lost. Reasoning content from history also lost. |
| 27 | Context/model/token metadata | `model`, `inputTokens`, `outputTokens`, `contextPercent` | ✅ Partial | Captured on lifecycle start/end. Not captured on job events. |
| 28 | Block streaming | Gateway chunker behavior | ⚠️ | All deltas treated uniformly. Code fences split across blocks won't be merged. |
| 29 | Session RPCs | `sessions.list/create/subscribe/messages.subscribe` | ❌ | `IChatService` exposes only history/send/abort. Gateway client supports these but they're not surfaced. |
| 30 | `sessions.steer` | Interrupt-and-steer | ❌ | Not exposed. |
| 31 | `sessions.patch` | Model/thinking overrides per session | ❌ | Available on gateway client but not on `IChatService`. |
| 32 | Idempotency key passthrough | Required by gateway | ⚠️ | `ChatViewModel` generates key but `GatewayChatService.SendAsync` only passes text to `SendChatMessageAsync`. Key not forwarded. |
| 33 | Real `runId` from `chat.send` | Gateway returns `{runId, status}` | ⚠️ | Returns idempotency key as stand-in. Real RunId learned from first agent event. Concurrent runs would mis-bind. |
| 34 | Pairing/auth close codes | 1008 pairing-required, 4000 tick timeout | ❌ | Not surfaced on `IChatService`. Chat UI cannot show pairing instructions. |
| 35 | Shutdown event | `event: shutdown` | ❌ | Not propagated to chat. |
| 36 | Health / heartbeat | `event: health`, `heartbeat` | ❌ | Not used by chat control. |
| 37 | Sender label | `senderLabel` from gateway | ⚠️ | `ChatMessage.SenderLabel` exists but `GatewayChatService` never sets it. Footer always omits sender. |
| 38 | Event timestamps | `ts` from gateway events | ⚠️ | History uses `msg.Timestamp`; live messages use `DateTimeOffset.UtcNow`. No per-delta gateway timestamp. |
| 39 | `tool-events` capability | Connect handshake `caps` | ✅ | Fixed — `OpenClawGatewayClient` now sends `caps: ["tool-events"]`. |
| 40 | Read aloud / TTS | Text-to-speech integration | ⚠️ | `AssistantMessageControl.ReadAloudRequested` event exists. `NativeChatWindow` does not wire it to TTS yet. |

## Priority Gaps to Address

### High Priority (blocks feature parity with web chat)
1. **Embedded cards** — `[embed ref=...]` directives need parsing + placeholder UI
2. **Tool `details`** — media/artifacts from tool results should be rendered
3. **Reconnect integration** — auto-reload history on reconnection
4. **History tool calls + reasoning** — structured data from `chat.history` (requires gateway support)
5. **`chat.inject`** — subscribe to chat events for server-pushed messages

### Medium Priority
6. **Permission/approval flow** — `exec.approval` events + approve/deny UI
7. **Slash commands** — `/model`, `/status`, `/reasoning` with UI feedback
8. **Mermaid/LaTeX** — rich content rendering
9. **Session RPCs** — multi-session support, session list/switching
10. **StatusReceived wiring** — raise gateway lifecycle errors as status messages

### Low Priority
11. **Syntax highlighting** — code block color syntax
12. **Connection status indicator** — tick/presence/shutdown awareness
13. **Sender label** — extract from gateway session context
14. **Block streaming stitching** — merge split code fences
15. **Media/image rendering** — with security controls

## Bugs Fixed (2026-05-12)

| # | Bug | Severity | Fix |
|---|---|---|---|
| 1 | `ExtractIntField` crashes on fractional/large JSON numbers → lifecycle End suppressed → run stuck active | Critical | `TryGetInt32` + double fallback |
| 2 | Abort sends idempotency key instead of real RunId | High | Use `_activeStreamingMessage.RunId` if available |
| 3 | Cross-session event hijacking via `IsActiveRunEvent` | High | Filter events to "main" session in `GatewayChatService` |
| 4 | Inline code span bypass in markdown sanitizer | High | Multi-backtick span matching |
| 5 | History load erases in-flight send | Medium | Skip clear when `IsRunActive` |
| 6 | Null reference after `IsActiveRunEvent` in tool/reasoning handlers | Medium | Add null check for `_activeStreamingMessage` |
| 7 | `async void` copy/read-aloud handlers crash on disposal | Medium | Inner try-catch around post-delay UI access |
| 8 | `ScrollThrottleTimer` never disposed | Medium | Cleanup on `Unloaded` event |
