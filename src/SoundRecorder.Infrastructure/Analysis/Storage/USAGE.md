# Attempt-owned analysis storage

`ITranscriptAnalysisStore` and `AnalysisStore*` records are Core contracts.
`SqliteTranscriptAnalysisStore` supplies the local SQLite adapter using the
already-referenced production Analysis dependency. It does not run inference,
schedule work, resolve source files, or validate claim semantics.

## Identity and lifecycle

Supply the successful transcription attempt ID, SHA-256 of both immutable saved
TXT/SRT files, language, generation and embedding model IDs/weight SHA-256s,
their canonical effective configuration strings and SHA-256s, algorithm version,
and embedding dimensions. The caller constructs canonical UTF-8 configuration
bytes and pins every setting affecting processing, including runtime/template/tokenizer,
pooling/task prefixes, seed and generation budgets. Storage compares these exact
fingerprints and preserves the exact configuration for later processing; a
configuration/fingerprint mismatch is rejected. It does not guess settings from
a model name. SHA-256 hex casing is canonicalized. A changed source or language
cannot reuse the same attempt.

Create a new database explicitly with `Create(path)` in an existing private
local directory. Both entry points reuse the application's local-path policy
before opening any file; UNC/mapped network and network-style namespace paths
are rejected, not substituted. The stored path is absolute.
`Open(path)` never creates a missing database or replaces an unsupported schema.
Format 1 uses typed JSON payloads inside SQLite, with SHA-256
integrity checks on chunks, checkpoints and output portions. Core has no
JSON/SQLite dependency. Unsupported format versions require an explicit future
migration, not an empty successful load.

`BeginAsync` creates a new processing revision while retaining every earlier
completed revision. A processing writer must be explicitly stopped or resumed
before another replacement begins for the attempt. Only the newest revision
accepts writes/resume. `LoadAsync` requires an exact processing identity; missing
attempts and incompatible identities are errors. Its `Completed` and `Unfinished`
tokens are separate: the latest status/checkpoint never relabels partial work
as complete.

Commit contiguous original-source/index portions with `SaveCheckpointAsync`.
The chunks and cursor advance in one transaction. `SetUnfinishedAsync` accepts
Cancelled, Interrupted or Failed; `ResumeAsync` explicitly resumes compatible
retained chunks, including processing state left by a crashed writer. Storage
does not silently classify a process as crashed or retry it. An old writer token
or duplicate ordinal is rejected.

`CompleteAsync` streams ordered summary/discussion output portions into one
transaction and activates the revision only when enumeration and commit finish.
The revision includes its already-committed index/checkpoint. Failed, cancelled,
invalid or process-interrupted replacement cannot remove the old completion.
`ReadOutputsAsync` rejects unfinished revisions. Discussion portions retain
questions, generated answer content, claims and original citations. They are
ordered conversation history, not fresh factual evidence or a semantic verdict.

## Public use

```csharp
ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Open(databasePath);
var revision = await store.BeginAsync(pinnedIdentity, cancellationToken);
await store.SaveCheckpointAsync(revision,
    new AnalysisStoreCheckpoint(nextOrdinal, "Index", algorithmCursor),
    boundedSourceAndVectorChunks, cancellationToken);
await store.CompleteAsync(revision, boundedSummaryAndDiscussionPortions,
    cancellationToken);

var saved = await store.LoadAsync(pinnedIdentity, cancellationToken);
var completed = saved.Completed
    ?? throw new InvalidOperationException("Analysis has no completed revision.");
await foreach (var output in store.ReadOutputsAsync(completed,
    cancellationToken))
{
    // Consume one bounded portion; do not accumulate the complete transcript.
    RenderCandidate(output);
}
```

The caller supplies the identity and streams in the example; storage does not
generate vectors or narrative. Read source/index chunks with `ReadChunksAsync`
using a compatible completed or unfinished revision token. Both readers hold a
SQLite snapshot only until enumeration is disposed, never an all-source/vector
key inventory. Dispose readers promptly before writing/deleting the same store.

Per-portion transport bounds are 4,096 UTF-16 units of original text/model
configuration, 4,096 vector dimensions, 16 claims with at most 16 citations each,
16,384 units of generated
content, a 512-unit question, and a 1,024-unit checkpoint cursor. These are
storage transport limits, not approved native/model resource or release limits.
Split larger results into ordered portions without truncation. Empty vectors
denote source-only chunks; nonempty embeddings require exact dimensions, finite
values and a nonzero vector. Storage does not normalize or retrieve vectors.
Original timestamp pairs are either absent or nonnegative with end strictly
after start, matching saved subtitle cues; citation start positions can be zero.

## Failure, privacy and deletion

Each operation owns its connection; SQLite transactions serialize independent
writers, and ordinal/current-revision guards reject stale work. Cancellation is
checked at enumeration and commit boundaries, not advertised as instantaneous
interruption of SQLite's synchronous calls. Transactions use full synchronization,
disk-backed temporary work and a bounded SQLite cache.

Missing files, wrong identities, unsupported format, corrupt payloads, invalid
portions and stale writer tokens throw explicitly. SQLite access/integrity
failures are not success-shaped fallback results. Operational logs contain no
transcript text, model paths, titles or questions.

`DeleteOwnedAsync(attemptId)` atomically cascades through all that attempt's
analysis revisions, outputs, indexes and checkpoints. It never receives or
deletes recording/source/model paths or unrelated attempts. Secure SQLite
deletion overwrites deleted cells; this is not a forensic-erasure guarantee for
filesystem backups or device snapshots. It is explicit analysis deletion, not
the rebuildable-cache-only cleanup required by later lifecycle work.

This module does not yet restore desktop history, enforce recording priority,
estimate disk requirements, perform cache-only cleanup or establish language,
native distribution, semantic accuracy or 12-hour acceptance.
