# Attempt-bound transcript processing

`AnalysisProcessingService` implements Core's existing `ILocalTranscriptAnalysis`
over `ITranscriptAnalysisStore`, `IGenerationBackend`, and `IEmbeddingBackend`.
`AnalysisProcessingNative` binds the same processor to the installed Phi-mini /
Nomic CPU adapters. Production processing has no feasibility-project dependency.

## Queue / caller bridge

Before enqueueing, call `AnalysisProcessingNative.PinAsync(request, token)` to
capture the exact successful attempt, TXT/SRT hashes, language, approved weight
identities, canonical effective configurations, algorithm version and dimensions.
Retain the explicitly verified model directory with that job; never read the
globally selected directory when executing it. Setup remains an explicit separate
action through `IAnalysisModelSetup`.

The scheduler supplies the active store revision, pinned directory, explicitly
chosen mode, optional pinned discussion intent, and native-call lease:

```csharp
ILocalTranscriptAnalysis processor = new AnalysisProcessingNative(
    store, revision, pinnedModelDirectory, privateArtifactDirectory,
    AnalysisProcessingMode.GlobalQuestion,
    new TranscriptDiscussionContext(IsFollowUp: true, Antecedent: "the draft"),
    enterInferenceAsync: enterRecordingPriorityLeaseAsync);
var candidate = await processor.AnalyzeAsync(request, progress, token);
```

The controlled-backend constructor takes the same revision/mode/context/lease,
with generation and embedding adapters instead of a directory. Its backends must
belong to the revision's pinned configuration. The native constructor rechecks
that identity and verifies exact installed files before loading either role.
The existing `NativeModelLoader` / `ModelFileVerifier` seams permit deterministic
native-policy tests, not model-correctness claims. Production uses their defaults.
Each model load, embedding call and generation call owns a recording-priority
lease when provided. The scheduling caller must serialize jobs and provide that
lease; the processor is not an independent scheduling implementation.

## Coverage, records and conversation

Choose `Summary`, `GlobalQuestion`, or `LocalQuestion` explicitly. Question wording
does not classify coverage. Summary/global processing visits all saved source
ranges; local lookup supplies only three ranked ranges and cannot establish
exhaustiveness or absence. The saved TXT/SRT pair must agree exactly. Source files
remain read-locked while processing; deleted, changed or mismatched sources fail,
without retargeting another attempt.

The reusable index stores normalized token-fitted original ranges, exact cue
timestamps and UTF-16 offsets. A checkpoint commits one range/vector atomically.
Compatible resumed prefixes and previous completed indexes are revalidated
against every source range and reused without reembedding. A bounded disk spool
avoids holding a store reader while committing to the same SQLite database; it is
removed on success/failure. Changed source/model/configuration cannot reuse an
incompatible identity.

The production engine's disk claim ledger is authoritative initial extraction:
structured kind, statement, stated/absent owner and deadline, qualifications and
original evidence. There is no destructive LLM prose reduction. Completed store
output portions contain that exact structured JSON in `Content` and readable
claim text/original citations in `Claims`; header/limitation portions are readable
text with no claims. Evidence larger than one portion is split without dropping
citations. The bounded API preview shows at most 200 claims / 16 citations per
claim; complete records and citations remain in the store and private ledger.

Follow-up intent and antecedent are explicit. At most four recent user questions
(48 UTF-16 units each) from the exact completed identity provide context, never
factual evidence. Previous assistant text is never sent to inference. Missing
history or antecedent produces a persisted clarification without guessing.
Fresh source evidence is retrieved/scanned for every answer.

Output/conversation activation is one store transaction, including prior ordered
portions. Partial nonempty extraction, token limits and errors leave the new
revision unfinished and preserve earlier complete output. Explicit cancellation
retains the compatible index for an explicitly resumed run; extraction itself
restarts over that index. A fully traversed zero-candidate extraction with an
explicit limitation may commit an **insufficient evidence** response, not a claim
that an answer is absent. That response retains every per-batch limitation.

## Boundaries and evidence

Generation retains the engine's 4,096-token context, 1,024-token summary /
512-token answer reserves, and independent five-/three-minute deadlines.
Embedding includes task prefixes within 512 tokens. Source cues are bounded by
the existing reader; index ranges by 1,024 UTF-16 units before token fitting.
These are conservative implementation bounds, not approved hardware or release
thresholds. Initial extraction remains **UNVERIFIED**: full traversal and valid
citations prove neither faithful interpretation nor real-world truth.

Tests use real reopened SQLite stores, tiny TXT/SRT pairs, controlled vectors and
the existing controlled native-model seam. They prove persistence, identity,
coverage routing, bounds and failure behavior—not Phi/Nomic semantic accuracy,
native distribution, end-to-end desktop restoration, or 12-hour acceptance.
No weights, downloads, cloud fallback, new model trials or shared UI are changed.
