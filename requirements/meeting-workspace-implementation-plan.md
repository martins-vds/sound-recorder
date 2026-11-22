# Approved meeting workspace implementation plan

## Decision and status

Prototype **B (Meeting workspace)** was approved on 2026-11-21. Implementation is authorized and in progress; this plan is not final passing acceptance evidence.

- Frozen reference branch: `prototype/approved-meeting-workspace-b`.
- Frozen reference commit: `8410c7782506313510ae917d3f160a08f47e769e`.
- Reference files in that commit: `src/SoundRecorder.Desktop/Prototypes/meeting-recorder.html`, `demo-recording.wav` beside it, and `scripts/prototype-ui.ps1`.
- Select **B** with `?variant=B`. A and C are historical alternatives, not implementation targets.
- The reference contains synthetic recordings, a short unrelated speech clip, simulated jobs/downloads/deletion, and simplified one-job-per-recording history. Implement actual saved-audio playback, durable artifacts, and separate job attempts; preserve B's composition, not its shortcuts.

Keep the frozen branch unchanged. The user subsequently authorized implementation on the current `prototype/meeting-recorder-ux` branch, with prototype assets removed from its production tree during cleanup. Retain the frozen prototype in its separate reference worktree. The working branch is not ready to merge until implementation, cleanup, and all acceptance gates are complete.

### Implementation checkpoint

- Native B shell, library search/review, real saved-WAV playback, downloaded-only model selection, all 72 catalog choices, download progress, immutable job choices, Stop/Cancel/restart/history deletion, and two-scope artifact deletion are integrated.
- Original review regressions are corrected: canonical exact-job ownership, failed residual deletion without completed-review promotion, and pending model selection/deferred capture enqueue. Close drains pending model verification; confirmed exit suppresses new transcription while active work releases.
- Model artifacts start in permanent GUID directories; the verified manifest is atomically published last. Both JSON history stores reuse backed-up atomic snapshot publication and allow open readers to retain their previous snapshot. No automatic replacement of pinned identities or retry hacks were introduced.
- Normal Release binaries were restored after mutation runs. Build passes with zero warnings/errors. Full Core validation passes **917 tests**, with **13 existing device/native/release fixture skips**. The native UI suite passes **63 tests**, zero skips.
- Final UI validation exposed a test-host modal-discovery defect: checking only top-level windows could open a second owned deletion dialog. Confirmation now reuses the same owned/top-level discovery as other dialog steps. Both focused deletion cases and the full native suite pass; no production behavior was changed by this test correction.
- Full-project CRAP passes for Core, Infrastructure, and Desktop: all measured executable members are numeric, with highest displayed scores **7.0** and strict rejection of scores >= 8. Isolated SDK build outputs avoid locked runtime binaries during coverage collection.
- Unfiltered, configured **Complete** mutation runs cover every handwritten production file. Source/configuration hashes were unchanged across all three measurements. Exact results:
  - **Core: 395/398 = 99.2462311557789%**, passes the configured 99% gate.
  - **Infrastructure: 1693/1719 = 98.4874927283304%**, exceeds the requested >98% metric but fails the unchanged configured 99% gate.
  - **Desktop: 1141/1232 = 92.6136363636364%**, fails both thresholds.
- Reports and input fingerprints are in `artifacts/quality/full-unfiltered-20261122/`. Two orphaned fixture workers from the Infrastructure mutation run stalled its output stream and locked binaries; only those verified owned process IDs were stopped. Desktop measurement completed using file-backed output. No filters, new suppressions, threshold changes, elevation, or host-setting changes were used.
- Desktop Stryker tooling is no longer blocked: its temporary WPF type-inspection project compiles local types without regenerating the parent's XAML. Ordinary handwritten compilation, source generators, constructor compatibility, and production defaults remain enabled.
- Visual evidence is in `artifacts/quality/desktop/meeting-workspace/`. Full comparable-client/high-contrast visual closure, native/device release acceptance, final independent review closure, and implementation commits remain outstanding. The prototype assets were removed from production; the frozen reference remains unchanged.
- **Acceptance is incomplete.** Scoped passing feedback is not substituted for these full-project results. Remaining mutation failures must be resolved without weakening the gate; the branch is not ready to merge.

### Open the reference throughout implementation

From the repository root, use an unused sibling directory:

```powershell
git worktree add ..\sound-recorder-prototype-reference prototype/approved-meeting-workspace-b
git -C ..\sound-recorder-prototype-reference rev-parse HEAD
& ..\sound-recorder-prototype-reference\scripts\prototype-ui.ps1 -Port 18762
```

Verify HEAD equals the frozen commit, then open `http://127.0.0.1:18762/meeting-recorder.html?variant=B`. If the directory or port is occupied, choose another unused one; keep existing worktrees and services intact. Keep this browser open beside the native app. An unavailable reference is a blocker for UI work, not permission to approximate it from memory.

## Mandatory visual-reference gate

This gate is part of **every user-visible vertical slice**, not just a final review:

1. **Before editing:** open B and exercise the relevant state. Record the reference state and intended native behavior.
2. **After the first runnable native change:** render the real `MainWindow`, compare it side by side with B, and correct hierarchy/layout drift before expanding the slice.
3. **Before committing the slice:** repeat the comparison at 760 x 560 and 980 x 720 window sizes; include the changed populated, empty, busy, and failure states. Preserve at least two readable transcript lines while playback controls are visible.
4. **At final integration:** repeat the entire state matrix below and the native UI regression suite.

Record each check under the existing ignored `artifacts/quality/desktop/meeting-workspace/` area: reference commit and URL, slice, states, screenshots of both surfaces, window/client bounds, DPI/theme, discrepancies, fixes, and verdict. Compare client areas at comparable logical sizes; browser viewport pixels and native outer-window pixels are not interchangeable.

**Completion criterion:** every relevant state has a recorded comparison and no unresolved material drift. Each implementation handoff/PR identifies this evidence. A test pass, source inspection, one screenshot, or a comparison made only at the end does not satisfy this gate.

Reference state matrix:

| Area | Required states |
| --- | --- |
| Shell and library | Populated, empty, filtered results, no matches, long titles; library left, reader right, capture dock below |
| Capture dock | Collapsed/expanded setup, permission unchecked, Recording, Paused, Stop; capture actions remain available while the reader scrolls |
| Review | Actual audio playback/pause/seek, scrolling transcript, missing audio, intentionally audio-deleted entry |
| Models | Downloaded-only selector, full download catalog, English-only incompatibility, download/verification/cancel/failure, progress visible after leaving model settings |
| Jobs | Queued, preparing, coarse recognition estimates, writing, stopping, stopped, cancelled, failed, completed, missing progress |
| Deletion | Audio-only/all-artifacts confirmation, keyboard dismissal, active related job, partial failure, retained outputs after job-history deletion |

Use native WPF controls, focus/accessibility behavior, and Windows high-contrast support. In the normal theme, carry B's restrained green shell, typography hierarchy, spacing, and clear work areas. Document necessary native chrome/control differences; material layout changes require user approval.

## Behavioral contract

- Preserve microphone/system/both capture, device choice, permission, destination, English/Brazilian Portuguese, pause/resume capture, saved-audio timestamps, recovery, FIFO transcription, one worker, capture priority, offline/privacy, and atomic unique TXT/SRT output pairs.
- Recording, library entry, transcription job, and recording artifacts are separate concepts; use [the glossary](../GLOSSARY.md).
- Each **new job snapshots model identity and language when queued**. Changing selection affects new jobs only. Queued/running jobs never silently switch models.
- The downloaded selector lists complete, compatible, integrity-verified local artifacts only. Different encodings are distinct models. Successful downloads register an artifact; selecting it is a separate explicit action.
- Whisper.net 1.9.1's download catalog has 12 families and 6 encodings: 72 files, including four English-only families. Compatible custom packages remain an import route; VAD/encoder auxiliaries are not transcription-model choices.
- Audio-only deletion keeps all completed outputs, transcript content, and the library/job history; playback/retranscription become unavailable. Full deletion removes this recording's audio, all associated output pairs, and associated history, including outputs whose job history was already deleted. Models and other recordings are untouched.
- Job deletion removes terminal job history only. Stop/cancel and release active work before deletion; keep artifact ownership independently of job history.
- Stop interrupts one running job and discards that attempt; Cancel also handles queued work. Earlier outputs remain unchanged. Restart creates a **new job attempt** from the beginning, not a resumed or overwritten job.
- Pause/resume transcription is unavailable. Show the limitation rather than implementing thread/process suspension or treating cancellation as pause.
- Recognition percentages are coarse audio-position estimates, not elapsed-time or whole-job progress. Preparing and writing remain indeterminate without a genuine reporter. Only completed processing **and** successful publication of both output files establish success.
- Missing files, unavailable models/devices, worker errors, download failures, and partial deletions are explicit recoverable states. Never replace missing media/models/destinations or report incomplete operations as success.

## Approved test seams and staging

The user approved these seams:

| Seam | Evidence | Limits |
| --- | --- | --- |
| Public Core application/queue/library/model boundaries | Intent, immutable job choices, lifecycle, ownership, cancellation | Does not establish actual filesystem/audio/backend behavior |
| Public Infrastructure adapters with disposable real files and a controlled worker | Migration, real deletion, lock release, download staging, worker protocol/progress | Native Whisper/device evidence still needs the existing opt-in fixtures |
| Real `MainWindow` via isolated FlaUI/UIA3 host | Wiring, keyboard/accessibility, layout, visible states | Does not replace manual visual comparison, screen-reader evaluation, or real-device acceptance |
| Real WPF application lifecycle with disposable state and controlled environment | Startup routing, fatal-error reporting, Exit subscriptions and cleanup | No user-profile effects or physical devices; instrumented process coverage must be proved if a child process is used |
| Public `MainWindow` composition with controlled playback | Matching recording ownership, release callbacks and dependency lifetime | Production defaults remain unchanged; no private-state hooks |
| Public job-row refresh and notification interface | Labels, notification subscribers, Delete/Restart availability | Does not replace queue persistence, worker release or UIA wiring checks |

The user explicitly approved the three additional seams during mutation-gate resolution. Keep their interfaces small, reuse existing adapters, and retain ordinary production behavior.

Meeting-workspace scenarios are tagged `@meeting_workspace`, **discoverable and unignored**. Remove `@planned` only after their functional checks execute successfully. Undefined steps, planned checks, and skipped device/release fixtures remain incomplete evidence; never silence them with new ignore tags.

For each slice: pick one scenario, add thin C# bindings/focused public-seam tests, obtain an assertion failure for the missing behavior, implement minimally, and rerun relevant regressions. Undefined steps or compilation errors are not sufficient behavioral red evidence. Repeat one behavior at a time; remove scenario-level `@planned` only after it executes successfully. A feature-level `@planned` remains until all its scenarios pass, or is moved to individual unfinished scenarios. Preserve a discoverable list of remaining planned scenarios.

## Implementation sequence

### 1. Native B shell and library review

**Depends on:** reference gate baseline.

- Replace the scrolling all-in-one page with B's master-detail library/reader and persistent capture dock; keep model/job views in the reader area.
- Preserve recording commands and lifecycle behavior. Scope capture busy state separately from playback, model download, and transcription.
- Match B's idle Start entry point: it can expand setup to collect permission, but capture begins only after fresh confirmation and valid prerequisites. Show only the capture actions appropriate to Idle/Recording/Paused.
- Keep input labels/live feedback, keyboard access, DPI resizing, native high contrast, and readable overflow. Search titles and completed transcript content without changing files or unexpectedly changing the selected recording.
- Extract only view sections that improve readability; keep business orchestration out of code-behind.
- Update old tab/global-button UI scenarios and bindings to the approved navigation/action placement without weakening their underlying behavior.
- UI bindings navigate collapsed sections through real controls rather than forcing panels visible or setting private view-model state.

**Done:** workspace/recording/accessibility scenarios pass for this slice, existing capture/lifecycle regressions remain green, and the shell's visual gate passes at both supported sizes.

### 2. Durable library metadata and artifact ownership

**Depends on:** public seams established in slice 1; complete before destructive actions.

- Evolve [recording history](../src/SoundRecorder.Core/IRecordingHistory.cs) and its JSON adapter to support stable library entries, optional titles, intentional audio removal, and all generated output references.
- Keep output ownership independently of [job history](../src/SoundRecorder.Core/TranscriptionJob.cs), so deleting a job does not lose the ability to review or later delete its outputs.
- Preserve legacy recording/job IDs, language, paths, captured duration, status, timestamps, and existing output files. Use backward-compatible/versioned DTO migration and preserve constructor/call-site compatibility.
- Keep existing interrupted/recovered recording semantics separate from intentional audio deletion. Preserve existing privacy/logging rules.

**Done:** legacy/new state round-trips through real disposable storage, multiple output pairs remain owned after job-history removal, and no existing content is overwritten or dropped.

### 3. Real recording playback and independent transcript review

**Depends on:** slice 1; use slice 2's library/artifact metadata.

- Add a small playback boundary with a native WPF playback adapter. Play the selected actual WAV; support play/pause, seek, elapsed/duration, and existing native volume controls where available.
- Keep the player visible while reading; preserve playback position across unrelated UI/progress refreshes. Pause when leaving review; stop and close the media handle before deleting audio.
- Read saved TXT/SRT results independently of job-history existence. Keep previously completed transcript content clearly identified while another attempt is running.
- Retain explicit transcript copying through the existing clipboard boundary; use the isolated adapter in UI tests rather than the system clipboard.
- Report missing/corrupt/unsupported audio explicitly; keep retained transcripts usable. No synthetic clip, fabricated waveform, timer, or speech alignment ships.

**Done:** playback and retained-transcript scenarios pass with a disposable valid WAV, native file handles release for deletion, and playback/reading visual gates pass.

### 4. Model inventory, catalog, download progress, and explicit selection

**Depends on:** slice 2; slice 5 consumes its model identity.

- Extend [the single-current-package manager](../src/SoundRecorder.Infrastructure/Models/LocalTranscriptionModelManager.cs) into a verified local inventory plus a selected-model pointer; preserve existing verified packages and the legacy working selection.
- Separate download/import registration from explicit selection. Preserve valid selection during failures and bundled-package discovery, including an English-only selection.
- Map every pinned library family/encoding to the download catalog. The library downloader returns raw model streams, while the current app expects ZIP packages: bridge that explicitly through staged metadata/integrity validation instead of feeding raw bytes to ZIP import.
- Persist stable model/encoding identity and language support. Obtain expected integrity data from the trusted catalog/package source, verify completion/compatibility/SHA-256 before inventory registration, and clean only owned staging files on failure/cancellation.
- Emit actual transferred bytes/total when available, otherwise indeterminate progress; show verification separately. Keep capture responsive and progress visible in B's header after navigating away.
- Preserve offline package import. Do not download models during tests, enumerate arbitrary fine-tunes as built-in catalog entries, or upgrade Whisper as part of this UI work.

**Done:** model catalog/inventory, failure, persistence, compatibility, and UI progress scenarios pass; no incomplete model appears in selection and no download changes selection.

### 5. Per-job model/language snapshots and real progress transport

**Depends on:** slices 2 and 4.

- Snapshot selected model identity and language at enqueue in [the queue](../src/SoundRecorder.Core/TranscriptionQueue.cs); preserve each attempt's identity/results and pin the worker to that artifact's actual local path.
- Expose bound model and language in job details so queued choices remain understandable after selection changes.
- If a pinned model becomes unavailable, report the failure; never substitute the current selection. Migrate legacy jobs without inventing historical model provenance, preserving established queued order and requesting explicit resolution where needed.
- Add structured optional phase/recognition progress at public boundaries while preserving existing transcriber entry points/callers.
- Register `WithProgressHandler` in [the local adapter](../src/SoundRecorder.Infrastructure/Transcription/WhisperLocalTranscriber.cs). Keep native callbacks short; transport typed, job-correlated messages through [the worker protocol](../src/SoundRecorder.Infrastructure/Transcription/WhisperProcessTranscriber.cs). Distinguish progress, start, result, and error frames so progress cannot be mistaken for a final result.
- Dispatch/coalesce UI updates without blocking capture or native inference. Ignore stale progress for terminated/replaced attempts. Observe worker exit and validate output publication before success.

**Done:** queued/running jobs retain their choices, controlled-worker progress reaches UI, unknown progress stays indeterminate, and no callback percentage alone produces success.

### 6. Stop, cancel, restart, and job-history deletion

**Depends on:** slices 2 and 5.

- Reuse owned-worker cancellation/termination and lifetime protection. Acknowledge the request before releasing the worker; keep Stopping visible and gate restart/delete until release.
- Represent user Stop as interruption with an explicit user-stop reason; use existing Core interrupted/canceled semantics rather than a false paused state. UI labels can map `Succeeded` to Completed, `Canceled` to Cancelled, and user interruption to Stopped.
- Retain cancellation's existing 1-second acknowledgment and 5-second release/queue-advancement contracts; cover Stop through the same owned-worker boundary.
- Restart creates a new attempt with the then-selected model/language; retain earlier attempts and unique TXT/SRT pairs.
- Delete only terminal job history, leaving independent artifact ownership and active transcript review intact.

**Done:** lifecycle scenarios, existing cancellation/native-worker regressions, artifact-retention tests, and job-view visual gates pass.

### 7. Explicit recording/artifact deletion

**Depends on:** slices 2, 3, and 6.

- Match B's two-scope confirmation, safe cancellation/Escape, focus return, and explicit consequences.
- Stop related queued/running jobs and await owned-worker/media-handle release before file removal. Do not stop unrelated capture/jobs.
- Resolve an explicit set of owned audio/output files across **all** attempts, including deleted job history. Protect other recordings, models, neighbor files, and destination directories; do not recursively delete an output folder.
- Treat multi-file deletion as potentially partial, not atomic. Persist enough intent/per-artifact outcome to report failures, retain unfinished entries, and retry/recover idempotently after restart. Remove history/ownership only once its requested removal is complete.
- Audio-only deletion keeps review/outputs and disables playback/retranscription. Full deletion selects a remaining entry or the empty-library state.

**Done:** real-file ownership, partial failure, restart recovery, late-worker-output, and UI confirmation scenarios pass; destructive-state visual gates pass.

### 8. Integration, evidence, and production cleanup

**Depends on:** all previous slices.

- Execute all meeting-workspace scenarios and affected existing regressions; align legacy UI locators and [scope policy](../src/SoundRecorder.Core/ProductScope.cs) with the approved capabilities.
- Keep release accuracy/performance acceptance separate. Report missing native/device fixtures and unexecuted scenarios as incomplete evidence.
- Follow [the existing testing/quality policy](../.github/instructions/testing.instructions.md): build/type-check, relevant/full suites, CRAP < 8 per executable production member, and mutation score > 98% per production project, with numeric reports and limitations.
- Complete the full visual matrix and accessibility checks. Document native adaptations and preserve all user data.
- Ship only native implementation, specs, and product documentation. The browser switcher, sample WAV, simulated jobs/downloads/deletion, and A/C remain on the frozen reference branch.

**Done:** no planned/undefined/skipped scenario is counted as passing, quality/visual evidence is complete, prototype assets are absent from production, and any blocker is explicitly reported.

## Specification ownership

| Specification | Owns |
| --- | --- |
| [Recording library](../tests/SoundRecorder.Core.Tests/Features/recording-library.feature) | Playback/review, durable ownership, audio-only/full deletion, real boundary failures |
| [Model catalog](../tests/SoundRecorder.Core.Tests/Features/model-catalog.feature) | Verified inventory, all 72 choices, explicit selection, actual download progress |
| [Job management](../tests/SoundRecorder.Core.Tests/Features/transcription-job-management.feature) | Immutable choices, progress/phase transport, Stop/restart/history deletion |
| [Existing transcription](../tests/SoundRecorder.Core.Tests/Features/transcription.feature) | FIFO, cancellation, output pairs, retry, restart and preserved exclusions |
| [Workspace UI](../tests/SoundRecorder.Ui.Tests/Features/meeting-workspace.feature) | B's layout, library search, reader/capture independence |
| [Artifact UI](../tests/SoundRecorder.Ui.Tests/Features/recording-artifacts.feature) | Functional playback and the two deletion scopes |
| Existing UI model/history/recording/accessibility features | New navigation, job controls, model states, and preserved lifecycle/accessibility behavior |

## Primary library references

Whisper.net 1.9.1 release commit: `98278acc38ae23590cdfa9859f78f089abae52a7`.

- [Model families](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/Ggml/GgmlType.cs), [encodings](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/Ggml/QuantizationType.cs), and [download mapping](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/Ggml/WhisperGgmlDownloader.cs).
- [Progress handler](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperProcessorBuilder.cs#L462) and [callback signature](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperProcessorEvents.cs).
- [Native inference loop](https://github.com/ggml-org/whisper.cpp/blob/f24588a272ae8e23280d9c220536437164e6ed28/src/whisper.cpp#L7006): coarse recognition progress, not a whole-pipeline completion signal.
