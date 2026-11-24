# Durable analysis scheduling (#15)

`AnalysisJobCoordinator` owns one FIFO worker, separate from transcription.
`AnalysisQueueRepository` is the local scheduling journal. It stores ordered
job metadata only; `ITranscriptAnalysisStore` remains the sole owner of
revision states, checkpoints, chunks and completed outputs.

Construct one coordinator per journal/store pair. Before accepting commands,
call `RecoverAsync` once on the fresh coordinator. Recovered unfinished jobs
are **Interrupted** and never start automatically. A new explicit `EnqueueAsync`
request starts its worker unless recording intent is active.

Each `AnalysisJobRequest` pins the exact successful transcript output paths,
attempt, language, source hashes, model hashes/configurations, algorithm,
verified immutable model directory and explicit coverage mode. Do not resolve
the globally selected model when processing eventually begins.
Follow-up requests also retain their explicit bounded `Discussion`
antecedent; never reconstruct it from changing UI state after restart.

The processor resolver receives `AnalysisJobExecution`. Bind its
`Request.Identity`, `Revision`, `Request.ModelDirectory` and explicit
`Request.Mode` to the processing adapter. Pass `EnterInferenceAsync` to the
adapter and acquire/dispose a lease around **every** heavy model-loading,
generation or embedding call. The adapter must validate saved source/model
hashes and atomically complete the supplied revision through the existing
store. A returned result alone never makes the job complete.

Announce `SetRecordingIntentAsync(true)` **before** attempting capture, and
retain that intent through recording pause/resume. Clear it only after capture
ends or its start is abandoned. This prevents new native calls; it does not
kill, immediately pause or reclaim resources from a call already in progress.
The capture host need not wait for `WaitForIdleAsync`.

`CancelAsync` attempts to durably record **CancellationPending** before signalling
the worker token. If that write fails, it still signals cancellation for capture
safety and surfaces the persistence failure; it does not claim durable cancellation.
Only safe worker release produces **Cancelled**. Queued work can
be cancelled without inference. `RetryAsync` creates a new job and revision
with the original immutable pins and a link to the previous job.
`ResumeAsync(jobId, expectedIdentity)` explicitly continues only the current
compatible revision/checkpoint; a stale or changed identity is rejected.

`DeleteOwnedAsync(attemptIds)` cancels and releases only the explicit owned
attempts, deletes their store revisions/indexes/checkpoints, then removes their
journal entries. It does not stop unrelated jobs or delete source files/models.
The Desktop caller removes those attempts' private diagnostics afterward and
keeps recording ownership until the requested full deletion succeeds. Errors may
leave partial cleanup and must remain explicit; audio-only and transcription-job
history deletion do not call this analysis deletion boundary.

Read `Jobs` for immutable scheduling snapshots, and use the store for actual
completed output and evidence. `WaitForIdleAsync` observes worker release and
surfaces persistence faults; it neither starts interrupted work nor substitutes
for capture-resource or native release evidence.
Failed snapshots expose an operational `ErrorCode` (exception type), never
transcript content, generated content or user-title paths in logs.

No Desktop composition, model installation, automatic retry, downloads,
generic queue framework or release-performance claims are included.

The new scheduler and journal are exercised through the public coordinator
with held inference and cancellation callbacks, controlled time, and reopened
real SQLite storage. These are deterministic behavior/persistence checks,
not native resource, grounding, architecture distribution or release evidence.
Full-owner quality gates remain required independently of focused module
feedback; see the handoff evidence for actual scores and any incomplete runs.
