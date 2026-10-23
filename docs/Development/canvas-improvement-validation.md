# Canvas Improvement Validation
> **Created**: 2026-10-22  
> **Last updated**: 2026-10-23  
> **Modified by**: Codex  
> **Version**: 1.0.0

## Metadata

| Property | Value |
| --- | --- |
| Source Code | `src/SmartHopper.Core/UI/Chat/CodexCanvasToolBridge.cs` |
| Since Version | 2.0.0-dev.260722 |
| Last Updated | 2026-10-23 |
| Documentation Maintainer | Codex |

---

## Why Read This?

Use these checks to distinguish automated source verification from actual Canvas behavior in Rhino.

## End-User Guide

## Runtime verification status

These checks require Rhino 8 and Grasshopper. They have not been executed as part of the source/build work. The current user session must not be restarted, modified, or used for testing without notifying the user. Save ongoing work before choosing to reload a rebuilt plugin.

## Developer Reference

Example of verified requested state:

```json
{"verification":{"status":"Succeeded","message":"The requested lock state is present on every targeted object."}}
```

Example of limited evidence that does not prove full content or placement correctness:

```json
{"verification":{"status":"ObservedChange","message":"Every reported gh_put instance exists; content and placement were not verified."}}
```

### Manual checks in a disposable document

1. Open two disposable Grasshopper documents, A and B. Start Canvas Assistant in A, choose confirmation before changes, request a group or move, and leave its approval pending. Switch to B and approve. Neither document may change; a document mismatch must be returned.
2. Start a selection-based request in A. Change the selection while approval is pending. Approval must reject the changed selection instead of modifying newly selected objects.
3. Approve a move with known absolute coordinates, then a relative move. Inspect exact positions and undo each operation. A no-op at an already-satisfied coordinate must not be reported as a failed move.
4. Lock/unlock and show/hide preview on selected objects. Verify the requested flags and Undo. Repeat an already-satisfied request; verification should return `AlreadySatisfied`.
5. Create a component with at least two inputs. Connect to its second input, verify the wire, then disconnect it. A wire on the first input must not satisfy verification of the second input.
6. Group selected objects. Verify membership and a single recoverable Undo operation. Attempt to include protected objects and inspect the reported partial/protected outcome.
7. Start script replacement and use Stop while generation is pending. No later replacement may occur after cancellation. Switch documents while generation is pending; the replacement must be rejected before applying to the new document.
8. Run `gh_get` on a Panel with Left, Center, and Right alignment. Verify GhJSON serialization and validation. Confirm schema validation failures are surfaced rather than bypassed.
9. Run `gh_tidy_up` on a small connected graph and inspect the resulting positions and Undo. Layout input is captured as a detached graph; pure `GhJson.CalculateLayout` may run off-thread, while `LayoutRefinementEngine.ApplyRefinements` resolves native bounds and ports on the UI thread before mutation. Do not infer a large-canvas speedup without measurement.
10. Inspect snapshots for the requested operation. Script and parameter edits serialize only their targeted objects; creation checks use a full document GUID-only baseline to distinguish newly created instances. Confirm the verifier rejects any reported creation GUID already present in that baseline. Do not describe these snapshots as full serialized state for every mutation.
11. Inspect `verification.status`: `Succeeded` verifies the implemented requested postcondition, `AlreadySatisfied` verifies an idempotent state, and `ObservedChange` records only the stated evidence. `ObservedChange` must not be described as proof of full design correctness.
12. Open two Canvas Assistant conversations on one Codex transport. Confirm each `item/tool/call` reaches only its registered thread handler; dispose one conversation and verify its requests are not routed to the other, then dispose the last and confirm the prior server-request handler is restored.
13. Verify the two selection contracts separately: for a Canvas bridge mutation, change selection while approval is pending and confirm the request is rejected with a retry-required result; for direct selected-tool execution, provide frozen `guids`, change the live selection before execution, and confirm lock/unlock, hide/show preview, group, `gh_get_selected`, `gh_get_selected_with_data`, and `gh_tidy_up` use those request GUIDs rather than the new live selection.
14. Saturate WebChat rendering with repeated same-key streaming updates, then stop a turn and separately induce a provider error. Confirm the latest partial text is visible before the Cancelled/Error status, no late delta overwrites the finished turn, and a stable final render arrives before Ready. Reset and dispose with queued work and confirm no stale render reaches the new or closed dialog.
15. Exercise a successful batch, a provider failure, and a timeout. During an active or restored batch, trigger ordinary `ResetForNextRun` and confirm the submission and restored sentinel-tree mapping remain available for polling/result reconstruction. Then start a genuinely new batch and exercise the pristine `ResetForRestoration` path; confirm stale sentinel tree/ID mappings are cleared only at those boundaries. Verify completion time is consumed once, terminal failure follows the shared path, and the existing completion callback and displayed metrics remain correct.

## Architecture & Design

### Deferred measurements

Only the detached graph calculation belongs off the UI thread. Keep Rhino/Grasshopper-aware refinements and native mutations on the UI thread. The native multi-chat, render saturation/cancellation/error, and batch lifecycle checks above have not been run in Rhino/WebChat. Measure UI responsiveness and layout time on representative large definitions before claiming a speedup; do not infer one from source changes or unit-test results.
