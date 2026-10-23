# SmartHopper Improvement Plan
> **Created**: 2026-10-22  
> **Last updated**: 2026-10-23  
> **Modified by**: Codex  
> **Version**: 1.0.0

## Metadata

| Property | Value |
| --- | --- |
| Source Code | `src/` |
| Since Version | 2.0.0-dev.260722 |
| Last Updated | 2026-10-23 |
| Documentation Maintainer | Codex |

---

## Why Read This?

This document records the implementation scope, verification requirements, and compatibility limits of the October 2026 improvements.

## End-User Guide

The running Rhino session is left untouched. Loading the rebuilt plugin and executing the disposable-document checks require a separate user-controlled runtime validation step.

## Developer Reference

Run the repository build command only from the isolated source copy:

```powershell
.\tools\Build-Solution.ps1 -Testing -Configuration Debug -OutErrors
```

Run the integrated tests against that same built copy:

```powershell
dotnet test SmartHopper.sln --no-build --configuration Debug
```

## Scope and constraints

Improve the existing implementation without changing released component GUIDs, input/output contracts, provider trust controls, or persisted document formats. Preserve the ten files that were already modified when this work started. Do not interact with, restart, or deploy assemblies to the user's running Rhino session.

## Implementation order

1. Revalidate the bound document after approval and at each UI execution boundary. Keep asynchronous canvas tools bound to that document. Replace document-wide change detection with relevant object state and explicit postconditions where available.
2. Evaluate JSON Schema constraints using the solution's existing JsonSchema.Net version. Cover nested types, required properties, enum and numeric constraints. Consolidate object-root schema wrapping while keeping provider-specific response repair.
3. Propagate timeout and caller cancellation to tool execution. Keep validation on public entry points without repeating it on the normal internal path.
4. Reuse HTTP connections while keeping authentication, headers, and timeouts request-scoped. Preserve streaming and batch behavior.
5. Share boolean result/fallback handling. Consolidate the Panel alignment compatibility boundary and retain legacy public methods as compatibility wrappers rather than removing APIs.
6. Clarify the shallow snapshot contract and deprecate unused validation extension fields without breaking external consumers. Avoid a broad immutable-model or inheritance migration.

## Verification

- Run the repository build/signing script against an isolated source snapshot with temporary test signing material and outputs, preserving the working tree and running Rhino assemblies.
- Run the Windows test projects against the exact integrated snapshot. Add regression tests for schema constraints, cancellation, HTTP isolation, boolean results, and Rhino-free canvas postcondition logic.
- Verify the non-Windows Infrastructure target by compilation/tests where the local host supports it.
- Record build and test results after the final edits. Build success is separate from Rhino runtime, Undo, provider network calls, and macOS host validation.
- Prepare a manual Rhino checklist; the user controls when a rebuilt plugin may be loaded.

## Architecture & Design

### Performance and compatibility follow-up

Measure large-canvas layout and streaming before moving Rhino-dependent computation across threads. Retain the existing DOM batching and provider security checks. A full immutable interaction model or component hierarchy redesign requires a separately scoped compatibility migration; it is not necessary for these fixes.

## Results — 2026-10-22 historical baseline

The source improvements are implemented and integrated. Final verification used the portable .NET SDK 7.0.410 / runtime 7.0.20 and independent temporary signing material. No Rhino runtime validation or deployment has been performed.

| Check | Result |
| --- | --- |
| Repository build/signing script, Debug | Passed, zero errors; includes the manual test-component project |
| Repository build/signing script, Release | Passed, zero errors; Release configuration emitted 12,488 compiler/analyzer warnings |
| Debug integrated tests | 1,286 passed, zero failed, zero skipped across five project/target runs |
| Release integrated tests | 1,286 passed, zero failed, zero skipped across five project/target runs |
| Infrastructure tests, each configuration | 467 Windows-target cases and 431 plain net7.0 cases |
| Core tests, each configuration | 92 cases for each target |
| Grasshopper helper tests, each configuration | 204 cases |
| Full documentation validation | 101 files passed, zero failed |
| PowerShell parser and source/Markdown whitespace checks | Passed; required metadata hard breaks are allowed |
| Working source vs. tested snapshot | Matched; only the isolated build's injected public signing key differs |

All target runs above occurred on Windows. A successful net7.0 test run is not evidence of execution on macOS. No real provider account, network inference call, Canvas mutation, Undo operation, or Rhino restart was used for verification.

### Implemented behavior

- Revalidate the bound document, selection, and permissions after approval; recheck documents/cancellation at queued mutation and script-replacement boundaries.
- Verify move and layout destinations, lock/preview flags, exact connection input ports, and group membership. Return `AlreadySatisfied` for implemented idempotent postconditions. Report limited placement/content observations as `ObservedChange`, without claiming full semantic verification.
- Propagate timeout/caller cancellation. Keep a cancelled mutating operation's execution ownership until its underlying task actually completes, even if it ignores cancellation. Preserve validation for public manager entry points while eliminating duplicate validation in the normal internal path.
- Evaluate JSON Schema constraints, reject unresolved external schema fetches, cache at most 128 definitions up to 16K characters each, and preserve provider-specific response repair.
- Reuse HTTP connection pools without sharing request authentication, headers, cookies, or client timeouts.
- Share boolean result parsing and provider schema wrapping. Preserve both legacy movement entry points and component GUID/input/output contracts.
- Normalize Panel alignment once before the library's validating writer. Retain the compatibility handler until supported GhJSON versions no longer need it.
- Keep mutable interaction compatibility explicit, expose read-only snapshot containers, isolate empty-body markers, and deprecate unused validation extension fields without removing their APIs.
- Read strong-name public key bytes instead of parsing localized console output; count documentation code fences by their actual language, including JSON and PowerShell.

### Remaining runtime and measurement work

The native document guards, actual Canvas state capture, Panel compatibility, and Undo behavior still require the disposable-document checklist in `canvas-improvement-validation.md`. Large-canvas layout and streaming speedups have not been measured. The interaction model remains shallowly mutable by design, and tools without complete semantic postconditions report their limited evidence explicitly. Compiler/analyzer warnings remain; this work does not claim a warning-free lint run.

Verification logs and TRX results are retained under `%TEMP%/SmartHopper-improvements-20261022/`. No commits, pushes, plugin installation, or edits to the user's existing signing keys were performed.

## 2026-10-23 follow-up

This section records the separately reviewed follow-up implementation; the table and results above remain the historical October 22 baseline. The final Debug and Release runs used the same frozen source.

### Implemented scope

- Route Codex `item/tool/call` requests by thread, unregister disposed chats, and restore the prior handler when the shared transport is released. Keep Dynamic Tool execution gated and cancellation-aware; MCP and Codex share protocol-neutral descriptors/execution while JSON-RPC shares only the envelope, not transport framing or role-specific routing.
- Capture only targeted script/parameter state for those edits; use a document-wide GUID-only baseline for creation verification. `gh_tidy_up` captures a detached graph, runs pure `CalculateLayout` off-thread, then performs native refinements and document application on the UI thread.
- Reuse the original request for seven selected wrappers (lock/unlock, hide/show preview, group, `gh_get_selected`, and `gh_get_selected_with_data`) and `gh_tidy_up` selection handling, preserving frozen GUIDs.
- Coalesce pending same-key WebChat renders before Markdown work, flush stable final output before Ready, and reset/dispose scheduler work with the dialog lifecycle. Retain the bounded FIFO schema and idempotency stores; they are separate from the keyed render coalescer and browser HTML LRU.
- Simplify `AIStatefulAsyncComponentBase` around `BatchRunState` for sentinel clearing and completion-time consumption; share terminal failure finalization while preserving protected `OnBatchCompleted` dispatch and serialized contracts.
- Use invalidated read-only tool descriptor snapshots and cloned parsed-schema descriptors; retain bounded FIFO schema behavior. Keep selection and provider behavior compositional through `SelectingSupport` and `ProviderSelectionCore`, not as a linear inheritance chain.

### Verification boundary

Both official `Build-Solution.ps1 -Testing -Configuration <Debug|Release> -OutErrors` runs exited successfully with zero errors. Compiler/analyzer warnings remain in both configurations; they are reported as counts, not classified as pre-existing or warning-free.

| Check | Debug | Release |
| --- | --- | --- |
| Official build script | Passed; 0 errors; 16,102 compiler/analyzer warnings | Passed; 0 errors; 13,064 compiler/analyzer warnings |
| Integrated tests | 1,438 passed; 0 failed; 0 skipped across 5 runs | 1,438 passed; 0 failed; 0 skipped across 5 runs |
| Infrastructure tests | 519 Windows-target + 483 plain `net7.0` | 519 Windows-target + 483 plain `net7.0` |
| Core tests | 116 per target | 116 per target |
| Grasshopper tests | 204 | 204 |

Both configurations ran on Windows. The tests include the final `OnStart` lifecycle and lossless error/cancellation flush regressions. Compared across 740 source files, byte hashes match the working source except for the isolated build-generated public signing key in `Infrastructure.csproj`; the original `.snk` and `.pfx` hashes are unchanged. Logs and TRX results are under `%TEMP%/SmartHopper-refactor-20261023/` (`final-debug.log`, `final-release.log`, `FinalTestResultsDebug/`, `FinalTestResultsRelease/`, and `final-verification-summary.json`).

Rhino/Grasshopper behavior, macOS execution, and large-canvas or streaming performance remain unverified; use the disposable-document and UI acceptance checks in `canvas-improvement-validation.md`. Main build outputs and the running Rhino installation were left untouched. No commits or pushes were made. Do not interpret source-level layout offload or renderer coalescing as measured speedups.

### Review corrections

Earlier review wording that described selection and provider support as a linear inheritance chain was inaccurate; these are composed branches around the existing component hierarchy. Canvas snapshots were not full serialized documents for every mutation. The keyed DOM queue is a render coalescer, not a cache, and the bounded schema FIFO is valid FIFO policy rather than an LRU defect. The retained stores have distinct purposes and were not unified.
