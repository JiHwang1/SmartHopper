# Chat UI (Full WebView)

Chat UI architecture and interactions after migrating to a fully WebView-based interface.

---

## Metadata

| Property | Value |
| --- | --- |
| **Source Code** | `src/SmartHopper.Core.Grasshopper/UI/Chat/` |
| **Since Version** | 2.0.0 |
| **Last Updated** | 2026-10-24 |
| **Documentation Maintainer** | Devin AI |

_Note: This documentation was written by AI on its own. It may contain some mistakes. If you would like to help, read this documentation and delete this comment if everything is okay._

---

## Why Read This?

This document describes the chat UI architecture and its interactions after migrating to a fully WebView-based interface. It covers the bridge between JavaScript and C#, DOM update strategies, and performance optimizations.

**You should read this if you:**

- Are working on the chat UI, WebView integration, or conversation rendering
- Need to understand how JS events are bridged to C# and how DOM updates are marshaled
- Are debugging threading, streaming lifecycle, or WebView performance issues

---

## End-User Guide

The chat window is now a **full WebView UI** (no Eto input controls). The HTML page includes a status bar, the chat transcript, and an input bar with Send, Clear, and Cancel buttons.

User actions originate in the WebView's JavaScript and are bridged to C# via a **custom URL scheme** handled in the host:

- `sh://event?type=send&text=...`
- `sh://event?type=clear`
- `sh://event?type=cancel`
- Clipboard helper: `clipboard://copy?text=...`

C# intercepts these URLs using `WebView.DocumentLoading` and cancels navigation, then routes the action to the appropriate host method.

### Security and Sanitization

- **HTML Sanitization**: All rendered message contents are sanitized using embedded local DOMPurify (`purify.min.js`, licensed under Apache 2.0 and MPL 2.0) before insertion into the WebView DOM, protecting against XSS or arbitrary script injection.
- **Navigation Policy**: `ChatNavigationPolicy` strictly restricts WebView navigation to internal custom schemes (`sh:`, `clipboard:`), rejecting navigation to external web protocols or unauthorized origins.

### Task Planning and Progress

- **Plan Cards**: `WebChatTaskPlan` models multi-step execution plans proposed via `plan_propose`.
- **Progress Tracking**: Real-time progress updates are surfaced via `plan_tasks` with step-by-step task status cards.
- **Consent Gates**: Plan approval is kept distinct from concrete Canvas mutation consent.

### Execution Budgets and Document Tracking

- **Run Budgets**: Runs are bounded by per-request time (default 300 seconds) and token limits (default 100,000 tokens), preventing runaway calls and displaying token and time metrics.
- **Document Tracking**: `ChatDocumentTracking` binds mutations to the active Grasshopper document. Structural edits and document switching between requests are tracked using per-chat baselines via `ChatCanvasBaselineStore`.
- **Visual Context**: `CanvasVisualContextProviders` integrates canvas and viewport screenshots (`canvas_screenshot`, `viewport_screenshot`), rendering bounded image attachments directly in conversation bubbles.

### Clipboard

`chat-script.js` uses `clipboard://copy?text=...` for code-block copy. The host intercepts it and sets system clipboard text, then shows a toast in the WebView.

### Consumers

`CanvasButton` and `AIChatComponent` open/reuse the dialog via `WebChatUtils`. They receive incremental `ChatUpdated` snapshots to surface transcript/metrics in Grasshopper.

### Rendering contracts

For the canonical documentation of interaction contracts (`IAIRenderInteraction`, `IAIKeyedInteraction`, concrete interaction types), see:

- `docs/Providers/AICall/interactions.md`

This page references rendering at a high level and leaves type-level details to the Interactions documentation.

---

## Developer Reference

### Separation of Concerns

- `ConversationSession` is the single source of truth for conversation history and for orchestrating provider calls and tool passes.
- `HtmlChatRenderer` (+ `ChatResourceManager`) converts `IAIInteraction` instances to HTML message bubbles. Markdig is used for Markdown -> HTML.
- `WebChatDialog` is a thin host adapter:
  - Loads initial HTML (CSS/JS inline) via `HtmlChatRenderer.GetInitialHtml()`.
  - Handles JS -> host events (send/clear/cancel/clipboard) in `WebView.DocumentLoading`.
  - Calls session methods and injects UI updates back using `ExecuteScript(...)` (`addMessage`, `replaceLastMessageByRole`, `setStatus`, `setProcessing`).
- `WebChatObserver` implements `IConversationObserver` and bridges `ConversationSession` streaming/partials/tool events to the WebView DOM incrementally.

### Host dialog initialization and event routing

```csharp
public class WebChatDialog
{
    private WebView _webView;
    private ConversationSession _session;
    private HtmlChatRenderer _renderer;

    public void Initialize()
    {
        _webView.DocumentLoading += OnDocumentLoading;
        _webView.LoadHtml(_renderer.GetInitialHtml());
    }

    private void OnDocumentLoading(object sender, DocumentLoadingEventArgs e)
    {
        if (e.Uri.Scheme == "sh")
        {
            e.Cancel = true;
            var query = HttpUtility.ParseQueryString(e.Uri.Query);
            switch (query["type"])
            {
                case "send": HandleSend(query["text"]); break;
                case "clear": HandleClear(); break;
                case "cancel": HandleCancel(); break;
            }
        }
        else if (e.Uri.Scheme == "clipboard")
        {
            e.Cancel = true;
            var text = query["text"];
            RhinoApp.SetClipboardText(text);
            InjectToast("Copied to clipboard");
        }
    }

    private void InjectToast(string message)
    {
        RhinoApp.InvokeOnUiThread(() =>
        {
            _webView.ExecuteScript($"showToast('{message}');");
        });
    }
}

```

### Observer bridging to WebView

```csharp
public class WebChatObserver : IConversationObserver
{
    private WebView _webView;

    public void OnDelta(AIReturn delta)
    {
        var html = HtmlChatRenderer.RenderInteraction(delta.Body.GetLastInteraction());
        var key = delta.Body.GetLastInteraction().GetStreamKey();
        RhinoApp.InvokeOnUiThread(() =>
        {
            _webView.ExecuteScript($"upsertMessage('{key}', {EscapeJsString(html)});");
        });
    }

    public void OnInteractionCompleted(AIReturn result)
    {
        // Finalize streaming aggregates and update metrics
    }

    public void OnToolCall(AIInteractionToolCall toolCall)
    {
        var html = HtmlChatRenderer.RenderInteraction(toolCall);
        RhinoApp.InvokeOnUiThread(() =>
        {
            _webView.ExecuteScript($"addMessage({EscapeJsString(html)});");
        });
    }

    public void OnFinal(AIReturn result)
    {
        RhinoApp.InvokeOnUiThread(() =>
        {
            _webView.ExecuteScript("setProcessing(false);");
        });
    }
}

```

### Host ↔ JS API

For the complete and authoritative API between the WebView and host, see:

- `docs/UI/Chat/WebView-Bridge.md`

This Chat UI overview intentionally avoids duplicating the detailed function lists to keep a single source of truth.

### Bridge & Schemes

- See: [WebView <-> Host Bridge](./WebView-Bridge.md)
  - Custom URL schemes: `sh://event`, `clipboard://copy`
  - Query encoding and routing
  - Threading rules and deferring actions from `DocumentLoading`

### Threading

All UI work (including `ExecuteScript`) is marshaled via `RhinoApp.InvokeOnUiThread(...)`. DOM updates are serialized by the dialog to avoid re-entrancy into the WebView's script engine.

### Performance pipeline (high level)

- The host serializes renders through `ChatRenderScheduler`. A pending render with the same DOM key is replaced before Markdown rendering, coalescing superseded streaming updates; this keyed queue is scheduling state, not a content cache. Reset and disposal cancel pending work.
- The scheduler has 64 active-plus-queued render slots. Intermediate streaming snapshots may be skipped when full; tool messages and final renders wait losslessly for a slot, and those admission waiters are not included in the slot bound. `OnFinal` queues the stable final message, then `FinishRenderingAsync` flushes rendering before returning the UI to Ready.
- A separate 100-key FIFO idempotency store suppresses repeated host HTML delivery. It is not the scheduler. The browser also has its own bounded LRU HTML cache for sampled equality checks.
- `ExecuteScript(...)` enforces a small concurrency gate to avoid piling scripts into the WebView.

On the WebView side (`chat-script.js`):

- Message mutations (`addMessage`, `upsertMessage`, `upsertMessageAfter`, `replaceLastMessageByRole`) are enqueued and flushed using `requestAnimationFrame` (with a small timeout fallback).
- Rendering work is reduced using:
  - Template cloning for repeated HTML.
  - A bounded LRU cache of rendered HTML plus sampled equality checks to skip redundant DOM updates. This browser cache is distinct from the host's keyed coalescing queue and FIFO idempotency store.
  - Optional patch payloads (JSON `{ patch, html }`) to append/replace only message content during streaming instead of re-sending full bubbles.
  - A max message HTML length cap to prevent huge DOM inserts.
- Lightweight perf counters are sampled to keep overhead low; only outlier renders are logged.

These changes also mitigate issue #261 ("ui eventually freezes on opening webchat").

`HtmlChatRenderer` omits runtime diagnostics that are not surfaceable or whose severity is below `Info`.

### Manual acceptance checks

- Fill the 64 active-plus-queued render slots with streaming updates that reuse a DOM key. Confirm a pending same-key update is replaced before Markdown rendering and that the most recent stable final render reaches the WebView before the Ready status.
- Stop a turn and induce a provider error while streaming under queue pressure. Confirm the latest partial text is delivered before the Cancelled or Error status and no late delta overwrites the finished turn.
- Reset or close the dialog with renders queued. Confirm pending work is cancelled and no stale render reaches the replaced or disposed WebView.

---

## Architecture & Design

- Paths
  - C# host: `src/SmartHopper.Core/UI/Chat/WebChatDialog.cs`
  - Observer: `src/SmartHopper.Core/UI/Chat/WebChatObserver.cs`
  - Rendering: `src/SmartHopper.Core/UI/Chat/HtmlChatRenderer.cs`
  - Planning & Tracking: `src/SmartHopper.Core/UI/Chat/WebChatTaskPlan.cs`, `ChatDocumentTracking.cs`, `ChatNavigationPolicy.cs`
  - Visual Context: `src/SmartHopper.Core/AIContext/CanvasVisualContextProviders.cs`
  - Resources: `src/SmartHopper.Core/UI/Chat/Resources/`
    - CSS: `css/chat-styles.css`
    - JS: `js/chat-script.js`, `js/purify.min.js`
    - HTML templates: `templates/chat-template.html`, `templates/message-template.html`, `templates/error-template.html`
  - Conversation/session: `src/SmartHopper.Infrastructure/AICall/Sessions/ConversationSession.cs`
  - Utilities/lifecycle: `src/SmartHopper.Core/UI/Chat/WebChatUtils.cs`

### Rendering Interaction Types

Supported via `HtmlChatRenderer.RenderInteraction(...)` and CSS classes:

- `AIInteractionText` -> Markdown content (with optional reasoning panel)
- `AIInteractionToolCall` -> compact card with tool name and arguments
- `AIInteractionToolResult` -> result payload (e.g., JSON block)
- `AIInteractionImage` -> `<img>` with caption/alt text
- `AIInteractionError` -> styled error bubble

Each message uses role classes like `.message.user`, `.message.assistant`, `.message.system`, `.message.tool`, enabling distinct styling.

### Streaming lifecycle and re-keying

- During streaming, assistant token deltas are surfaced via `IConversationObserver.OnDelta(...)` and aggregated under a constant stream key computed via `GetStreamKey()`; the UI updates the same DOM node using `upsertMessage(key, ...)`.

- After streaming ends, `ConversationSession` persists a single stable snapshot and surfaces it via `OnInteractionCompleted(...)`/`OnFinal(...)`. The streaming bubble is replaced with a finalized message keyed by the interaction's **dedup key** (`GetDedupKey()`):

  - This prevents new assistant turns from overwriting previous replies and ensures accurate history replay.

Notes:

- Non-assistant interactions (tool calls/results, system, errors) are appended only once persisted by the session.

- DOM updates are serialized and marshaled to Rhino's UI thread to avoid WebView re-entrancy.
