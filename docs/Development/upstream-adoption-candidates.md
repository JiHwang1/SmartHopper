# Upstream Adoption Candidates
> **Created**: 2026-10-23  
> **Last updated**: 2026-10-24  
> **Modified by**: Codex  
> **Version**: 1.2.0

## Metadata

| Property | Value |
| --- | --- |
| Source Code | `src/`, `tools/`, `.github/` |
| Since Version | Fork baseline `14026e72` |
| Last Updated | 2026-10-24 |
| Documentation Maintainer | Codex |
| Upstream Repository | <https://github.com/architects-toolkit/SmartHopper> |
| Fork Baseline | `14026e722f3a464e00a00bf9520995f2fb6ff7f8` |
| Upstream Baseline | `679d6e843da5902c32997b3b299a3e3f4a3e6151` |
| Review Status | A-group and B-group integrated; local verification and native runtime boundaries recorded below |

---

## Why Read This?

This document identifies upstream changes worth adopting into the fork while preserving its Codex account integration, concurrent chat isolation, schema validation, HTTP connection reuse, streaming renderer, and Canvas verification improvements.

At the reviewed revisions, the fork has 13 commits absent from upstream, and upstream has 317 commits absent from the fork. These counts describe history, not independent features. Version bumps, repeated model-list updates, generated documentation, and release bookkeeping are not individual adoption candidates.

The review used fetched Git history, selected commit diffs, endpoint source comparisons, and the existing local CodeGraph index for current-fork structure. It did not inspect every changed line or establish that each candidate can be cherry-picked independently. No subagents were used for this upstream review.

## End-User Guide

### Expected Benefits

The first adoption group addresses interrupted conversations, incorrect state transitions, misleading metrics, and Canvas edits. Later groups add visual change review, richer Canvas tools, image context, document management, and local AI providers.

Nothing in this inventory is enabled merely by documenting it. The existing fork remains the implementation baseline. Features must be integrated and verified before they can be described as available in the fork.

### Recommendation

Adopt narrowly scoped correctness fixes first. Integrate Canvas changes with their GhJSON dependencies next. Treat Provider SDK migration and immutable interaction contracts as separate projects rather than prerequisites for every small fix.

## Developer Reference

### First Adoption Group: Correctness and Recovery

Priority means expected usefulness to the current fork, not an upstream severity rating. The commits below are source references, not a ready-to-run cherry-pick list.

| ID | Candidate and Sources | Expected Benefit | Integration Requirements |
| --- | --- | --- | --- |
| A01 | Interrupted tool-call history recovery: [04516d9c](https://github.com/architects-toolkit/SmartHopper/commit/04516d9c) | Reconciles tool calls left without results after cancellation, errors, or exhausted execution limits. Prevents malformed conversation history from poisoning subsequent provider requests. Hides tools when `ProcessTools` is false. | Adapt recovery to the current mutable contracts and inspect the separate Codex session path. Do not execute an abandoned mutation merely to complete its history entry. |
| A02 | Debounce callback invalidation: [7ee31dff](https://github.com/architects-toolkit/SmartHopper/commit/7ee31dff) | Rejects stale timer callbacks after cancellation or rescheduling using generation and target state captured at scheduling time. | Preserve the fork's batch reset and completion behavior. Include the corresponding state-manager tests. |
| A03 | Error, finish-reason, and usage correctness: [e6a96062](https://github.com/architects-toolkit/SmartHopper/commit/e6a96062), [e75e0e17](https://github.com/architects-toolkit/SmartHopper/commit/e75e0e17), [cc6c220d](https://github.com/architects-toolkit/SmartHopper/commit/cc6c220d) | Deduplicates error reporting, avoids presenting failures as normal completion, separates reasoning tokens from generation tokens, and suppresses metrics when no provider call occurred. | Preserve existing error diagnostics and schema validation. Test streaming and non-streaming usage separately. Trust-warning deduplication must not hide error-level integrity failures. |
| A04 | Existing-object updates and accurate `gh_put` results: [01a33e3f](https://github.com/architects-toolkit/SmartHopper/commit/01a33e3f), [36e4e060](https://github.com/architects-toolkit/SmartHopper/commit/36e4e060) | Updates objects identified by `instanceGuid`, protects positions and external wires, reports actually applied changes, and rejects malformed documents before later processing. | Review GhJSON API requirements and visual-review dependencies. Preserve scoped mutation verification and distinguish accepted proposals from successful application. |
| A05 | Layout pivot correctness and meaningful Undo: [a77561dc](https://github.com/architects-toolkit/SmartHopper/commit/a77561dc), [04d0b87d](https://github.com/architects-toolkit/SmartHopper/commit/04d0b87d), [a9deacab](https://github.com/architects-toolkit/SmartHopper/commit/a9deacab) | Handles different pivot conventions for components, panels, sliders, and floating parameters. Avoids sub-pixel moves and Undo records for unchanged objects. Consolidates layout computation. | Preserve the fork's separation of UI-thread capture and application from background layout computation. Verify required GhJSON pivot and layout APIs before package changes. |
| A06 | Secret-decryption failure handling and chat HTML sanitization: [804a84bc](https://github.com/architects-toolkit/SmartHopper/commit/804a84bc), [ae03dfe3](https://github.com/architects-toolkit/SmartHopper/commit/ae03dfe3) | Avoids returning ciphertext as a usable secret and sanitizes rendered chat HTML. | Audit current rendering and navigation paths. Separately review sanitizer version and CSP behavior. The macOS Keychain implementation needs an existing-key migration plan and failure-safe replacement; do not import it wholesale. |

### Second Adoption Group: Canvas and Conversation Features

| ID | Candidate and Sources | Expected Benefit | Integration Requirements |
| --- | --- | --- | --- |
| B01 | Visual Canvas review and coordinated Undo: [d5ac8126](https://github.com/architects-toolkit/SmartHopper/commit/d5ac8126), [5d2c46e7](https://github.com/architects-toolkit/SmartHopper/commit/5d2c46e7) | Previews additions, modifications, removals, and connections; applies accepted changes; groups a tool operation into an Undo step; restores reviewed removals when replacement fails. | This is a cross-cutting integration involving consent boundaries, tool metadata, review services, and Undo. Connect it to Codex dynamic tools without duplicating approval or mutation ownership. |
| B02 | In-place value changes and component reruns: [c7f7c14c](https://github.com/architects-toolkit/SmartHopper/commit/c7f7c14c), [8b950884](https://github.com/architects-toolkit/SmartHopper/commit/8b950884) | Adds `gh_set_value` and `gh_run`, reducing the need to recreate objects for simple edits or execution. | Bring UI-thread dispatch, active-solution guards, lock handling, Undo, and applicable GhJSON parameter-data APIs together. |
| B03 | Screenshot tools and selection/viewport context: [f0a1052e](https://github.com/architects-toolkit/SmartHopper/commit/f0a1052e), [b80749d2](https://github.com/architects-toolkit/SmartHopper/commit/b80749d2), [a308631e](https://github.com/architects-toolkit/SmartHopper/commit/a308631e) | Gives assistants visual Canvas and viewport context, including image-bearing tool results. | Integrate image transport for both ordinary providers and Codex. Preserve bounded image output and UI-thread capture. |
| B04 | Consolidated `gh_get` tools and smaller responses: [ea918484](https://github.com/architects-toolkit/SmartHopper/commit/ea918484), [b5af34e4](https://github.com/architects-toolkit/SmartHopper/commit/b5af34e4) | Reduces the tool family from 14 variants to four and adds summary, filters, and field projection. Can reduce tool-catalog size and response volume. | Include the parity follow-up. Define compatibility aliases or a migration path for removed names and changed arguments. Preserve runtime and internalized-data semantics. |
| B05 | Audio transport and modality fallback correctness: [7a09e19e](https://github.com/architects-toolkit/SmartHopper/commit/7a09e19e), [942130da](https://github.com/architects-toolkit/SmartHopper/commit/942130da), [ff166ad6](https://github.com/architects-toolkit/SmartHopper/commit/ff166ad6) | Repairs speech pipelines, multipart source selection, audio payload transport, and provider/model selection for each conversion step. | Closely coupled to Provider SDK helpers and capability definitions. Retain current HTTP request isolation and cancellation semantics. Use deterministic request-format tests before live provider checks. |
| B06 | Multiple Grasshopper documents: [94c14db0](https://github.com/architects-toolkit/SmartHopper/commit/94c14db0) and its split-file follow-ups | Adds document list, switch, new, and open tools; supports reading or saving a non-active document by ID. | Bind each operation to the intended document. Preserve concurrent chat isolation and prevent a document switch from redirecting a pending edit. |
| B07 | Conversation document and Canvas change tracking: [c54a3b2f](https://github.com/architects-toolkit/SmartHopper/commit/c54a3b2f), [80fd0651](https://github.com/architects-toolkit/SmartHopper/commit/80fd0651), [f1564754](https://github.com/architects-toolkit/SmartHopper/commit/f1564754) | Reports document switches and user edits between AI responses using persisted baselines and change summaries. | Retain follow-up fixes for capture failures and stable IDs. Measure snapshot, serialization, and baseline I/O costs on large documents; do not reintroduce repeated full-document work per tool. |
| B08 | Plan cards and autonomous execution budgets: [adf79963](https://github.com/architects-toolkit/SmartHopper/commit/adf79963), [09796890](https://github.com/architects-toolkit/SmartHopper/commit/09796890), [31d1d435](https://github.com/architects-toolkit/SmartHopper/commit/31d1d435) | Adds interactive plans, progress reporting, and time/token limits for autonomous runs. | Distinguish plan approval from edit approval. Define how ordinary-session limits relate to Codex execution and cancellation. Include lifecycle and run-reset follow-ups. |
| B09 | MCP knowledge resources, prompts, and shared context: [1eca5caa](https://github.com/architects-toolkit/SmartHopper/commit/1eca5caa), [e75129fd](https://github.com/architects-toolkit/SmartHopper/commit/e75129fd) | Exposes reusable Grasshopper guidance through MCP resources/prompts and a shared knowledge catalog. | Reconcile the expanded dispatcher with the fork's shared JSON-RPC envelope handling. Keep resource content embedded and avoid rebuilding large instruction payloads unnecessarily. |

### Separate Migration Decisions

| ID | Candidate | Recommendation |
| --- | --- | --- |
| C01 | Current upstream LocalAI and Ollama providers | Prefer the current upstream implementation over the previously archived experimental branch. The reviewed upstream includes `IsConfigured` and base-URL normalization changes missing from that older draft. Adopt only if local inference is wanted; server behavior remains unverified here. |
| C02 | Provider SDK, common OpenAI-compatible helpers, shared schema adapters, and model capability registry | Consider a separate migration. These changes reduce duplication but move contracts and namespaces across projects. Preserve Codex registration, complete schema enforcement, connection reuse, and cancellation ownership. See [3db6b3f4](https://github.com/architects-toolkit/SmartHopper/commit/3db6b3f4), [93f41832](https://github.com/architects-toolkit/SmartHopper/commit/93f41832), and [89c34a39](https://github.com/architects-toolkit/SmartHopper/commit/89c34a39). |
| C03 | Immutable bodies and interaction records | Treat [94702036](https://github.com/architects-toolkit/SmartHopper/commit/94702036) as an API migration. Do not replace current snapshots or builders without validating streaming aggregation, identity, mutable JSON payloads, and tool-result history. Include session-owned turn-ID corrections such as [2d509546](https://github.com/architects-toolkit/SmartHopper/commit/2d509546). |
| C04 | HTTP client factory and provider contract tests | The fork already reuses connections. The additional value of [f2ae4fc6](https://github.com/architects-toolkit/SmartHopper/commit/f2ae4fc6) is injectable transport and deterministic HTTP tests, not a second independent pooling layer. |
| C05 | Signed remote model catalogue | [9347d322](https://github.com/architects-toolkit/SmartHopper/commit/9347d322) adds signature verification, strict wire validation, seed overlays, and an offline cache. Include [5ad8dff6](https://github.com/architects-toolkit/SmartHopper/commit/5ad8dff6) and [d47e409e](https://github.com/architects-toolkit/SmartHopper/commit/d47e409e). Decide explicitly whether the fork should depend on upstream's catalogue server and signing key. Model-ID canonicalization, [19827a6d](https://github.com/architects-toolkit/SmartHopper/commit/19827a6d), is a smaller independent candidate. |
| C06 | Single-main CI and release workflow migration | Upstream's retirement of `dev` matches the fork's current branch layout. Review [3e619692](https://github.com/architects-toolkit/SmartHopper/commit/3e619692) and later workflow fixes selectively. Do not import upstream publishing destinations, signing configuration, automated AI PR writing, or release credentials as routine cleanup. |
| C07 | GhJSON package upgrades and delegated parameter handling | Upstream references GhJSON 1.3.2 at the reviewed head. Treat the upgrade as a dependency migration supporting Canvas features, not an isolated version bump. Review [df9e9f47](https://github.com/architects-toolkit/SmartHopper/commit/df9e9f47) together with the APIs needed by A04, A05, and B02. |

### Changes to Exclude or Reassess

- Do not reimplement local-tool metrics-validation suppression: the current fork already sets `SkipMetricsValidation` in `AIToolCall`.
- Do not replace the current HTTP transport solely because upstream introduces a client factory. First preserve and test the fork's request-state isolation.
- Do not blindly overwrite the fork's streaming scheduler with upstream WebChat files. Preserve render coalescing, partial responses on cancellation, and disposal behavior.
- Do not discard scoped Canvas mutation verification when adding consent and review infrastructure.
- Skip historical version dates, generated model summaries, and old provider hash manifests as feature candidates. Regenerate applicable metadata from the delivered state.
- Reassess Markdown block caching separately. It overlaps the current rendering work and introduces cache ownership and eviction concerns; a performance benefit must be measured.

### Suggested Implementation Order

1. Integrate A02 and narrowly scoped parts of A01, A03, and A06 with regression tests.
2. Define the required GhJSON upgrade and integrate A04 and A05 without moving pure layout computation back onto the UI thread.
3. Add B02, B03, and B04 while maintaining existing tool compatibility.
4. Integrate B01 and the remaining multimodal/document features as separate reviewable changes.
5. Decide C01 through C07 individually. Reorder work only where a confirmed dependency requires it.

### Verification Requirements

Recheck the comparison after fetching the current upstream refs. These commands inspect history and do not merge source changes:

```powershell
git fetch origin --prune
git rev-list --left-right --count main...origin/main
git log --no-merges --oneline main..origin/main
git show 04516d9c -- src/SmartHopper.Infrastructure/AICall/Sessions/ConversationSessionHelpers.cs
```

Validate this document group after updating the inventory. Implementation changes additionally require the checks listed below:

```powershell
.\tools\Validate-Documentation.ps1 -Path .\docs\Development
git diff --check
```

| Area | Required Evidence Before Acceptance |
| --- | --- |
| Conversations | Cancel during a pending tool call, fail a tool, exhaust a run limit, and issue a subsequent request. Confirm valid history and session-owned turn IDs. Repeat with concurrent Codex chats where applicable. |
| State management | Test rapid debounce restarts, cancellation, disposal, stale callbacks, and preservation of batch completion/reset behavior. |
| Metrics and errors | Use fixed provider responses to check reasoning/generation accounting, missing usage, failed calls, and non-AI conversions. Verify distinct diagnostics remain visible. |
| Canvas edits | Use disposable documents to check GUID preservation, external wires, unchanged positions, partial rejection, failed placement rollback, and one meaningful Undo step per applied operation. |
| Layout | Check panels, sliders, floating parameters, and ordinary components. Measure repeated tidy-up behavior, UI responsiveness, and large-document capture costs. |
| Tools and documents | Test old tool names or documented aliases, summary parity, non-active document access, document switching during pending operations, and internal-tool exposure. |
| Images and audio | Assert encoded requests using fake transports, then separately verify actual supported provider paths. Test image result rendering, file inputs, URL inputs, cancellation, and per-step fallback routing. |
| Security and storage | Test malicious HTML and navigation behavior. Check unreadable secrets and migration/replacement failures without exposing secret values. macOS Keychain requires macOS evidence. |
| Repository checks | Run the repository-required Debug and Release builds, applicable tests, and documentation validation on the final delivered source. Preserve signing material during verification. |

Upstream test files and reports are useful inputs, not proof that this fork passes. No candidate has been built, benchmarked, or exercised in Rhino as part of this inventory.

## Architecture & Design

### Preserve Fork-Owned Behavior

The upstream tree does not contain the fork's Codex account implementation. Namespace moves, new Provider SDK contracts, provider registration changes, and expanded consent boundaries must therefore be reconciled with that implementation explicitly.

Use small changes that preserve the fork's behavior at integration boundaries. A source-file replacement can remove fork-specific logic even when the upstream feature itself is desirable. A successful textual merge also does not establish compatibility.

### Dependency Boundaries

- State-machine fixes can be evaluated without adopting the entire Provider SDK.
- Canvas update and parameter-edit features depend on specific GhJSON behavior and must be tested against the chosen package version.
- Visual review combines tool metadata, execution scope, consent, staged document changes, UI presentation, and Undo coordination. Porting only its dialog is insufficient.
- Document tracking adds capture, serialization, comparison, and persistence costs. Its capture cadence must be coordinated with existing mutation verification.
- Provider SDK, immutable interactions, media codecs, and common provider helpers form a related migration area, but their individual benefits should still be evaluated separately.
- Remote catalogue adoption adds a network and trust dependency. Keep embedded-model fallback behavior and failure handling explicit.

### Review Boundaries

This is a dated planning inventory. Commit links identify reviewed upstream work; follow-up commits may be required. Refresh branch tips and dependency requirements before implementation. The recommendations do not authorize merging, pushing, enabling remote services, or publishing a release.

## A-Group Implementation, 2026-10-23

The current source integrates A01 through A06 selectively. This section supersedes the inventory-only status for that group. The B-group integration is recorded separately below; C remains a set of migration candidates.

- A01: Close abandoned tool calls with synthetic failure results without executing them. Preserve provider tool filters when tool processing is disabled and keep Codex thread ownership.
- A02: Bind debounce callbacks to their scheduling generation and target. Invalidate callbacks on cancel, restart, immediate transitions, and disposal.
- A03: Deduplicate identical structured diagnostics without merging different severity/origin/code. Retain error finish states, distinguish reasoning from generated output, and keep local execution out of provider metrics.
- A04: Treat matching component instance GUIDs as updates independently of editMode. Preserve omitted pivots and existing wires, skip rejected/protected objects, restore originals after failed replacement, and return applied, already satisfied, and unapplied counts separately. Creation/update GUIDs have separate payload fields; existence-only verification remains ObservedChange. Existing group membership changes remain a separate gh_group operation.
- A05: Use GhJSON 1.2.0's shared layout pipeline and per-object pivot semantics. Capture native bounds/port metrics on the UI thread, compute over detached snapshots off-thread, and revalidate targets before UI application. Ignore movement below 0.5 canvas units before recording Undo.
- A06: Return no usable secret when decryption fails, retain unreadable stored values during migration, sanitize all chat HTML insertions with local DOMPurify, and block non-bridge WebView navigation. macOS Keychain migration is excluded.

### Dependency Decision

GhJSON.Core and GhJSON.Grasshopper move together from 1.1.1 to 1.2.0. The reviewed 1.1.2 tag lacks PivotSemantics and ComputeLayout; 1.2.0 provides both plus a host-independent LayoutNodeMetrics contract. The published package targets .NET 7 and retains the current Rhino 8 SDK baseline. Keep local-source overrides intact. This is the minimum version needed here, not the separate 1.3.2 adoption candidate.

Sources: [GhJSON 1.2.0 package](https://www.nuget.org/packages/GhJSON.Grasshopper/1.2.0), [shared layout entry point](https://github.com/architects-toolkit/ghjson-dotnet/blob/1.2.0/src/GhJSON.Grasshopper/GhJsonGrasshopper.cs), [pivot semantics](https://github.com/architects-toolkit/ghjson-dotnet/blob/1.2.0/src/GhJSON.Grasshopper/Shared/PivotSemantics.cs).

### Integration Verification

Final verification ran on Windows using the official Build-Solution.ps1 signing/build flow in an isolated source copy, followed by integrated dotnet test runs. The delivered code and assets matched the tested snapshot across 1,061 files; only the isolated build's injected friend-assembly public key is excluded. The workspace signing keys are unchanged.

| Check | Debug | Release |
| --- | --- | --- |
| Official build/signing script | Passed, 0 errors | Passed, 0 errors |
| Compiler/analyzer warnings | 16,451 | 13,413 |
| Integrated tests, five project/target runs | 1,519 passed, 0 failed, 0 skipped | 1,519 passed, 0 failed, 0 skipped |
| Infrastructure | 551 Windows-target and 513 portable-target tests | 551 Windows-target and 513 portable-target tests |
| Core | 123 tests per target | 123 tests per target |
| Grasshopper helpers | 209 tests | 209 tests |

Documentation validation passed all 102 files. git diff --check passed. Chrome 155 tested nine DOM sanitizer cases, all passed: inline scripts/events, javascript/data URLs, send/clear/clipboard bridge links, SVG/MathML payloads, and safe Markdown preservation. These browser checks exercise the sanitizer in a separate local browser, not Eto's native WebView.

DOMPurify 3.4.17 is embedded locally with its upstream licenses. See the [upstream release](https://github.com/cure53/DOMPurify/releases/tag/3.4.17). No CDN or account credentials are required by the chat sanitizer.

Logs, TRX reports, source hashes, the DOM harness/results, and final-verification-summary.json are retained in %TEMP%/SmartHopper-A-adoption-20261023/. The final build logs are final-debug.log and final-release.log. Build outputs are isolated; no plugin installation, Rhino restart, commit, push, or release publication occurred.

### Remaining Acceptance Boundaries

- Native Canvas/Undo behavior, actual Eto WebView rendering/navigation, macOS execution, and real provider calls remain unverified. Follow canvas-improvement-validation.md in disposable documents before runtime acceptance.
- Compiler/analyzer warnings remain in both configurations. They have not been classified as all pre-existing and this is not a warning-free lint result. Layout/streaming performance has not been measured.
- Ordinary providers hide tools when ProcessTools is false. Codex rejects execution in a disabled turn; a tool catalogue already registered on its existing app-server thread is not dynamically removed per turn.
- Existing group membership/color edits are retained rather than applied by gh_put and are reported as unapplied; group creation and unchanged groups have separate counts. Full visual change review and coordinated multi-operation Undo belong to B01.
- ImageProcessingService's existing string-only description interface does not expose nested usage to its caller; this change preserves usage when inner calls actually return metrics but does not add a new media-service contract.
- Sol medium performed conversation/usage implementation and Luna high handled timer/security work and focused Canvas review. TheHive was unavailable in the enabled subagent model list.

## B-Group Implementation, 2026-10-24

The current source integrates B01 through B09 within the existing .NET 7, Rhino 8, mutable interaction, provider, and Codex contracts. The separate Provider SDK and GhJSON 1.3.2 migrations remain deferred. See [Canvas and Conversation Tools](../Tools/canvas-and-conversation-tools.md) for usage and transport boundaries.

| Item | Delivered Integration |
| --- | --- |
| B01 | Selectable Canvas reviews, non-mutating preview overlays, dependency-aware accepted items, document/target revalidation, and operation-scoped Undo grouping. Failed `gh_put` replacements restore the original objects and wires; rollback failures remain observable. Codex uses the same review instead of a duplicate approval for these tools. |
| B02 | Reviewed `gh_set_value` for supported native value holders and persistent parameters, plus `gh_run` with solution, lock, protection, cancellation, and identity guards. Codex exposes these through its existing document-bound tool bridge. Rerun verification reports execution rather than claiming independently verified outputs. |
| B03 | Bounded PNG Canvas/viewport captures, selection/viewport metadata, compact image-bearing results, chat rendering, and native image parts for provider codecs, Codex, and MCP. Capture stays on the UI thread; images are restricted to raster formats and 8 MiB each. |
| B04 | Four advertised `gh_get` queries with full/summary modes, filters, pagination, projection, and data/message parity. Legacy names remain executable, unadvertised aliases. Embedded workflows use the consolidated names. |
| B05 | Multipart audio-source selection, binary speech normalization, compatible chat audio encoding/decoding, and provider/model routing retained per modality fallback step. The old fallback constructor and `Steps` API remain available; `BoundSteps` supplies resolved routing. |
| B06 | Document list, switch, new, open, read, and save with IDs, bounded non-active reads, explicit internalized-data opt-in, native confirmation for changes, and post-confirmation checks. Ordinary chat runs rebind only after an explicit successful document operation. Codex retains its original document binding and exposes list/read only. |
| B07 | Per-chat file baselines, stable IDs, structural change summaries, document events in both conversation implementations, and tracker disposal. Capture occurs at conversation boundaries rather than per tool. Failed capture preserves the previous baseline for later comparison. |
| B08 | Separate plan consent and progress cards, text-node rendering, per-run time/token budgets, cancellation, and fresh budgets after a subsequent message. Ordinary sessions count reported or estimated provider usage; Codex uses owned app-server usage notifications. Plan approval never pre-approves Canvas edits. |
| B09 | Embedded knowledge catalog, allowlisted MCP resources, five prompts, cached Markdown loading, shared JSON-RPC envelopes, protocol errors, notification semantics, and cancellation forwarding. Internal instruction resources and arbitrary file/network URIs remain unexposed. |

### B-Group Verification

Final verification ran on Windows through the official `Build-Solution.ps1 -Testing` flow, followed by `dotnet test SmartHopper.sln --no-build` in each configuration. Builds and test outputs are isolated under `%TEMP%/SmartHopper-B-adoption-20261023/`; the primary checkout's signing material is preserved. The final delivered source matches that snapshot, excluding the isolated build's injected friend-assembly public key.

| Check | Debug | Release |
| --- | --- | --- |
| Official build and signing flow | Passed, 0 compiler errors | Passed, 0 compiler errors |
| Compiler/analyzer warnings | 17,460 | 14,422 |
| Integrated tests, five project/target runs | 1,624 passed, 0 failed, 0 skipped | 1,624 passed, 0 failed, 0 skipped |
| Infrastructure tests | 598 Windows-target and 557 portable-target | 598 Windows-target and 557 portable-target |
| Core tests | 123 per target | 123 per target |
| Grasshopper helper tests | 223 | 223 |

Documentation validation passed 103 files. Whitespace review preserves the required Markdown metadata hard breaks and the original vendored MPL license whitespace. Warnings remain and have not all been classified as pre-existing; this is not a warning-free lint result. Audio fake-HTTP tests run on the Windows target because instantiating the existing provider base loads System.Drawing.Common. Pure codec and fallback-routing tests also run on the portable target.

The validation folder retains `final-debug.log`, `final-release.log`, final-run TRX files, `documentation.log`, `final-source-hashes.json`, and `final-verification-summary.json`.

Chrome 155 ran the actual chat template, stylesheet, sanitizer, and script with 13 passing checks: script readiness, inert plan text, the 30-task display bound, run-control state changes, four unsafe link schemes, script/event removal, preservation of an image attachment above the normal text limit, safe Markdown, and reset behavior. The harness and results are retained in `browser-check/` within that validation folder. This is a separate headless browser test, not native Eto WebView evidence.

### B-Group Remaining Acceptance Boundaries

- Rhino Canvas review, native dialogs, Undo, reruns, screenshots, cross-document operations, concurrent chats, and actual Eto WebView rendering still require host acceptance in disposable documents. Compiler success does not prove those native behaviors.
- macOS execution, live provider media calls, and large-document snapshot, serialization, baseline I/O, and UI latency have not been measured. Fake HTTP/request-format tests do not establish provider-account or model capability compatibility.
- Undo records created by a failed but restored mutation may clear pre-existing Redo history. The integration removes its restored Undo records but does not reconstruct Grasshopper's Redo stack.
- Token usage arrives after work occurs. Limits can interrupt a Codex turn or prevent further ordinary calls; they cannot guarantee a strict billing ceiling for work already in flight. Codex token enforcement depends on the app-server's usage notifications.
- Document switching, creation, opening, and saving are supported in ordinary tool-enabled chats and eligible direct/MCP calls with confirmation. They are deliberately absent from the document-bound Codex catalog. The default MCP policy still excludes mutating tools.
- Source changes do not install the plugin, restart Rhino, publish packages, commit, or push repository state.
