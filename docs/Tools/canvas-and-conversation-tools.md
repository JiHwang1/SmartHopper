# Canvas and Conversation Tools
> **Created**: 2026-10-24  
> **Last updated**: 2026-10-24  
> **Modified by**: Codex  
> **Version**: 1.0.0

## Metadata

| Property | Value |
| --- | --- |
| Source Code | `src/SmartHopper.Core.Grasshopper/AITools/`, `src/SmartHopper.Core/UI/Chat/`, `src/SmartHopper.Infrastructure/` |
| Since Version | Unreleased |
| Last Updated | 2026-10-24 |
| Documentation Maintainer | Codex |

---

## Why Read This?

Use this page for the B-group tool surface, review boundaries, document identity, media transport, and per-run budgets. The [adoption record](../Development/upstream-adoption-candidates.md) distinguishes local verification from native runtime acceptance.

## End-User Guide

Inspect before editing. `gh_get`, `gh_get_selected`, `gh_get_by_guid`, and `gh_get_errors` are the four advertised queries. Use `detail: "summary"` with `fields` for compact responses, or full GhJSON when editing requires serialized state. Previous query names remain executable compatibility aliases but are omitted from advertised catalogs.

`gh_put`, `gh_move`, `gh_connect`, `gh_disconnect`, `gh_remove`, `gh_set_value`, and `gh_run` present their proposed changes for review. Accept only the intended items. Plan approval is separate from change approval. Changes are refused if the reviewed document or target state changes before application. Applied Canvas changes use operation-scoped Undo grouping; a rerun is a net-zero lock pulse and does not create an Undo entry.

`gh_set_value` edits supported existing value holders or persistent parameters. It accepts `instanceGuid`, `value`, and an optional `param` selector. `gh_run` accepts `instanceGuids`, skips locked or protected objects, and refuses to start during an active solution. Both accept an optional active `documentId` check. Inspect returned failures and rejected or protected GUIDs instead of treating a successful transport response as proof that every target changed.

`canvas_screenshot` and `viewport_screenshot` return PNG attachments. Capture dimensions are capped at 4096 pixels per side and image output at 8 MiB. Images are rendered in chat and transported as native image parts for supported provider formats, Codex dynamic tools, and MCP. Actual vision support still depends on the selected model. Selection context contains at most 25 object descriptions; viewport context contains camera, projection, and units metadata.

`gh_document_list` supplies IDs for open documents. `gh_document_read` and `gh_document_save` can target a non-active open document by `documentId`. Reading is paginated and excludes internalized data unless requested. Switching, creating, opening, and saving documents require native confirmation. Save confirmation displays the destination and whether it overwrites an existing file. Mutating document tools are hidden by the default read-only MCP policy and excluded from batch execution.

Ordinary WebChat runs bind mutations to the active document when a run starts. An explicit, confirmed document switch rebinds that run. A manual switch cannot redirect an already pending edit. Codex Canvas sessions retain their original document binding: document list/read are available, but document switching, creation, opening, and saving are not advertised there. Return to the bound document or start a new Codex chat to edit another document.

Chat reports document switches and structural edits between requests. Each chat owns a separate baseline file under the user's application-data `Grasshopper/SmartHopper/ChatCanvasBaselines` folder. Closing the chat disposes its tracker. Baselines are not captured after every tool call.

`plan_propose` asks for plan approval; `plan_tasks` updates progress. WebChat starts each message with a fresh time and token budget, defaulting to 300 seconds and 100,000 tokens. Token limits stop subsequent ordinary provider or tool calls after reported or estimated usage reaches the budget. Codex cancellation uses app-server token-usage notifications. These controls are execution limits, not a guaranteed billing cap for a request already in flight.

## Developer Reference

Compact query arguments:

```json
{
  "detail": "summary",
  "fields": ["instanceGuid", "name", "pivot"]
}
```

Value edit arguments, using an actual instance GUID returned by inspection:

```json
{
  "instanceGuid": "00000000-0000-0000-0000-000000000001",
  "value": 12.5
}
```

`AITool.Surfaces` restricts execution surfaces; `Advertise` controls catalog descriptors without removing compatibility execution. `MutationInvocationContext` carries per-run document validation, cancellation, consent, and plan presenters. A consent decision accepts only keys from the current proposal. Mutating operations revalidate native objects on the UI thread after review, and `CanvasUndoBatch` exists only inside one synchronous mutation callback.

`AIInteractionToolResult.Images` separates image attachments from compact JSON. `ToolResultMediaExtractor` permits bounded PNG, JPEG, GIF, and WebP data; `SendToModel` distinguishes model-visible media from display-only images. Provider adapters preserve tool-call correlation. OpenAI-compatible chat formats place tool responses before following image messages; the Responses API, Anthropic, and Gemini use their native shapes. Codex uses `inputImage` with `imageUrl`; MCP uses image content blocks.

`FallbackChain.BoundSteps` retains each modality conversion's resolved provider and model. The legacy constructor and `Steps` list remain available. Audio transport selects the audio source, constructs multipart transcription requests, normalizes binary speech responses, and encodes supported OpenAI-compatible chat audio without changing provider authentication ownership.

MCP adds `resources/list`, `resources/read`, `prompts/list`, and `prompts/get` through the shared JSON-RPC envelope. Resources are an embedded allowlist, not a file-reading API. Internal instruction entries remain unexposed. The five named prompts are `grasshopper-expert`, `script-writer`, `canvas-debugger`, `definition-builder`, and `performance-reviewer`. Unknown resources and prompts return JSON-RPC errors; failed tool calls retain MCP's `isError` result envelope.

## Architecture & Design

Infrastructure owns tool metadata, consent contracts, task state, media envelopes, fallback routing, run budgets, and MCP. Core owns WebChat rendering and presentation. Core.Grasshopper owns native capture, reviews, document access, parameter changes, tracking, and Undo. No Provider SDK migration or new package version is required for this integration; GhJSON remains at 1.2.0.

Codex verification distinguishes content changes (`ObservedChange`), unchanged accepted values (`AlreadySatisfied`), and reruns reported by the tool (`ExecutionReported`). A rerun report does not independently prove a particular solution output. Review rejection, protected objects, partial application, and rollback failure remain observable.

Native Rhino Canvas behavior, Eto dialogs and WebView, macOS execution, live provider compatibility, large-document capture costs, and Undo/Redo interactions require host acceptance. A failed mutation that records Undo can clear pre-existing Redo history even when the tool restores native state; this integration does not reconstruct Grasshopper's Redo stack.
