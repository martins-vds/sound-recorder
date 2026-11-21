---
description: 'Mandatory prototype B reference gate for the approved native meeting workspace'
applyTo: 'src/SoundRecorder.Desktop/**,tests/SoundRecorder.Ui.Tests/**,tests/SoundRecorder.Ui.TestHost/**'
---

# Approved meeting workspace

When implementing or reviewing the meeting workspace, read [the implementation plan](../../requirements/meeting-workspace-implementation-plan.md) before editing. Its behavioral contract, slice completion criteria, and visual-reference gate are authoritative for this work.

Use prototype **B** from frozen commit `8410c7782506313510ae917d3f160a08f47e769e` on `prototype/approved-meeting-workspace-b`, in a separate reference worktree. Keep the reference open beside the real WPF app.

**Every UI slice:** inspect the relevant B state before editing, compare the first runnable native change, and repeat side-by-side checks at 760 x 560 and 980 x 720 before committing. Capture both surfaces, DPI/client bounds, discrepancies, fixes, and a closure verdict in the plan's evidence location. Each handoff links that evidence.

An unavailable prototype or unresolved material visual drift blocks UI-slice completion. Automated tests and a final-only screenshot are not substitutes. Native controls/high contrast may adapt the rendering while preserving B's work areas and hierarchy.

Keep `@planned` scenarios discoverable and unignored until implemented. Add C# bindings and behavioral tests one vertical slice at a time, exercising the approved public seams.

Implement real saved-audio playback, durable separate job attempts/artifact ownership, and real optional progress transport. Keep the browser prototype and its synthetic/simulated behavior out of production.
