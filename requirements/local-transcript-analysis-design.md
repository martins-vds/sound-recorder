# Local transcript analysis design

**Design status: PRODUCT BASELINE INTERVIEW COMPLETE.**
**Product baseline: APPROVED (Q1–Q23).**
**Feature implementation status: NOT AUTHORIZED.**

This document records accepted answers Q1–Q23 from the design interview on
2026-11-21. These answers authorize terminology and requirements documentation,
not application code, test implementation, dependency changes, or an ADR.
Technical choices and numerical thresholds remain pending feasibility evidence
and a follow-up decision round; product baseline approval is not feature
implementation authorization.
No feasibility code or measurements are authorized by this documentation turn.

## Feasibility execution authorization - 2026-11-22

After creating the feasibility ticket graph, the user explicitly authorized
execution of GitHub tickets #2-#6 on an integration branch, including isolated
C# harnesses, dependency restoration, and measurements available on this machine.
This supersedes the earlier documentation-only restriction for feasibility work,
not the restriction on feature implementation.

Exact model files and download sizes require separate user approval before
download. Tests requiring unavailable native hardware remain blocked; compiled
artifacts and emulated execution do not establish native acceptance.
GitHub ticket #7 remains the human technical and numerical approval gate.
Feature integration and implementation remain **NOT AUTHORIZED**.

Subsequent explicit approvals on 2026-11-22:

- Download the pinned Qwen2.5-7B-Instruct Q4_K_M two-shard generation model
  and multilingual Nomic Embed Text v2-MoE Q8_0 embedding model, totaling
  5,195,298,752 bytes, for local feasibility evaluation. GitHub ticket #4
  records the approval; exact revisions, URLs, sizes, and SHA-256 hashes belong
  to its candidate profile. Store weights outside Git and temporary worktrees.
- Add and run a feasibility-only GitHub Actions workflow on a native Windows
  x64 hosted runner, including pushing the integration branch and workflow to
  this repository. GitHub ticket #8 tracks this supporting evidence. Measure
  the actual runner profile; native execution alone does not prove resource
  thresholds or release acceptance.

These approvals do not select the final models or approve numerical thresholds.

After the measured Qwen2.5 generation profile failed evidence-preserving minute
analysis, the user approved one bounded bilingual comparison of the official
Qwen3-8B Q4_K_M generation model, retaining the existing embedding candidate.
GitHub ticket #9 records its exact approved revision, URL, SHA-256, and
5,027,783,488-byte download into the external cache. The original rejection
evidence, frozen corpus, and strict quality gates remain unchanged. This is
permission to evaluate another candidate, not final model selection or feature
implementation authorization.

On 2026-11-23, the user approved one bounded evaluation of
Phi-4-mini-instruct Q4_K_M, the 3.8B mini model rather than full 14B Phi-4.
GitHub ticket #10 records the pinned Unsloth GGUF conversion, MIT license,
revision, SHA-256, and exact 2,491,874,272-byte download. Reuse the existing
embedding candidate and external cache. Verify the actual Phi chat template
and native compatibility; Portuguese support in the model card is not
grounding or release evidence. The frozen corpus, prior rejection evidence,
quality gates, and feature-implementation restriction remain unchanged.

Use the canonical terms in [GLOSSARY.md](../GLOSSARY.md). This proposal extends
the local transcript workflow; it is not evidence that transcript analysis or
native Windows ARM64 support already exists. The approved
[meeting workspace plan](meeting-workspace-implementation-plan.md) remains a
separate specification, not authorization to implement this design.

## Accepted decisions

### Q1 — One transcript at a time

Transcript summaries and transcript discussions use one selected transcript.
No cross-recording search, combined summaries, or cross-transcript answers are
included in this design.

### Q2 — Bounded memory and disk-limited processing

Support transcripts of any size that available local disk and processing
resources permit, without requiring the whole transcript to fit in memory or
one model context window. Process bounded portions and recursively reduce their
results as needed. Work must be cancellable.

“Any size” is not a promise of unlimited disk, unlimited model context, fixed
completion time, or successful completion after a resource failure. Numeric
memory, disk, and context budgets have not been selected.

### Q3 — Local data and explicit network access

Transcript content, excerpts, summaries, discussion history, indexes, and
processing data stay local. Network access is limited to explicitly requested
model downloads. No cloud inference or cloud fallback is permitted.

### Q4 — Evidence-grounded output

Support claims with citations to transcript excerpts. Use saved-audio timestamps
when available; otherwise use stable excerpt references. Recursive reduction
must preserve evidence references back to the selected transcript.

When the transcript does not provide sufficient evidence, respond with that
limitation rather than inventing an answer. A citation is evidence from the
transcript, not independent verification that the recorded statement is true.

### Q5 — Windows architectures and acceleration

Target native Windows ARM64 and x64 with CPU inference as the baseline.
GPU acceleration is optional, not a prerequisite for transcript analysis.
This changes the proposed target scope, not the current application's x64-only
support declaration or release evidence.

### Q6 — Candidate stack and feasibility gate

Libraries and generation/embedding models remain candidates, not final choices.
Inference is in-process; Ollama is not part of the design.

The feasibility gate must establish the selected native dependencies, model
compatibility, and local database/index path on both Windows architectures.
The native Windows ARM64 database-extension availability gap remains unresolved;
do not assume a SQLite connector supplies a compatible native extension.
LLamaSharp, GGUF models, sqlite-vec, and the Semantic Kernel/CommunityToolkit
SQLite connector are evaluation candidates, not approved dependencies.

### Q7 — Exact successful transcript ownership

The exact immutable transcript from one successful transcription attempt owns
its summary, index, and transcript discussion. A retry creates a new attempt
and therefore a new analysis target, even for the same recording.

Do not silently retarget existing analysis to the newest transcript or reuse
analysis from another attempt. Imported transcripts/audio and transcript editing
are outside this design. Failed, incomplete, and interrupted transcription
attempts are not successful analysis targets.

### Q8 — Languages

Support English and Portuguese; the existing Brazilian Portuguese transcription
choice remains the relevant application workflow. Default analysis output to the
selected transcript's language. Translation and speaker diarization are outside
scope.

### Q9 — Summary contents

A transcript summary contains an overview, topics, explicit decisions, and
action items grounded in transcript evidence. Do not infer owners or deadlines
that the transcript does not state. Deduplicate material repeated by overlapping
processing portions without losing its evidence references.

### Q10 — Recording priority and cancellation

Recording takes priority. Heavy analysis inference runs in the background and
is serialized; while recording is active, do not start new analysis inference
calls. Resource use must not compromise capture.

Cancellation takes effect at a safe boundary. In-process native inference may
not support immediate interruption of a call already in progress; expose that
limitation honestly rather than promising immediate cancellation or native
pause/resume. Numerical scheduling and release thresholds require feasibility
evidence and approval before feature coding.

### Q11 — Persistence, caches, deletion, and interruption

Persist completed results and transcript discussion history locally. Indexes are
rebuildable private caches, not the authoritative transcript or analysis result.
Deletion removes the data owned by its requested target; never remove unrelated
recordings or analysis.

Interrupted work is not success. Never silently reuse stale results or indexes.
Keep exact attempt ownership intact across persistence, cache rebuilding,
interruption, and deletion. Q19–Q21 further define resumability, disk safety, and
source availability.

### Q12 — Documentation now, confirmation before code

Record the resolved language and accepted decisions before implementation.
Q23 completes the product baseline confirmation; technical approval and feature
implementation authorization remain separate. No ADR is requested at this stage.

### Q13 — Explicit installation and distinct model roles

Before an explicitly approved model download, show its size, license, and local
storage location. Alternatively, accept a compatible local model package import.
Once compatible models are installed, analysis works offline.

Keep transcription, generation, and embedding model roles distinct; analysis
models do not replace the transcription model. Invalidate incompatible indexes
when the model configuration changes. Validate one bilingual default profile;
defer Lite/Pro profiles and GPU variants.

The local package import here is for models, not transcript/audio import.

### Q14 — User-triggered work and honest lifecycle

Summary generation and indexing start only on a user request, not automatically.
Expose progress, cancellation, errors, and explicit retry. Reuse compatible
completed results without presenting partial or incompatible work as complete.

### Q15 — Local lookup and global coverage

Support both local lookup questions and questions requiring coverage of the
whole selected transcript. Global coverage uses a coverage scan and recursive
synthesis, not only top-k retrieval.

If coverage is incomplete, label the answer incomplete. Top-k matches alone must
never be presented as exhaustive evidence that no other relevant material exists.

### Q16 — Bounded conversation context, fresh evidence

Keep one conversation per selected transcript. Supply bounded discussion history
for conversational context, not as factual evidence. Ground each answer in fresh
transcript excerpts rather than treating prior generated answers as source facts.

### Q17 — Measured supported resource profiles

Validate supported CPU profiles on Windows ARM64 and x64 machines with at least
16 GB RAM. Support claims are tied to measured profiles, not a guarantee that
every model or native configuration fits every machine with that RAM capacity.

Perform resource preflight and explicitly report unavailable resources or model
loading failure. Do not promise universal native memory bounds.

### Q18 — Feasibility and acceptance evidence

Required gates cover offline operation on both architectures; English and
Portuguese grounding; unsupported questions; repeated, overlapping, and
contradictory evidence; model/index changes; and cancellation, restart, and
deletion.

Include a 12-hour transcript stress case with bounded peak memory and full
recursive reduction. Use controlled inference doubles for deterministic behavior
checks and real-model evaluation for native feasibility and grounding evidence;
neither substitutes for the other.

Feasibility must measure first-answer latency and full indexing and summary
duration. Numerical performance and resource thresholds must be approved before
feature coding. This requirement alone does not authorize feasibility execution; the separate
2026-11-22 authorization above defines the permitted work.

### Q19 — Compatible chunk resumability

Privately retain compatible completed chunks so work can resume across
cancellation or restart. Partial work never counts as a completed index, summary,
or answer. Transcript or model changes invalidate incompatible retained work;
do not silently resume against a different target or configuration.

### Q20 — Disk preflight and safe cache removal

Estimate required disk space before work and report disk exhaustion explicitly.
Offer removal of rebuildable caches only, with explicit user choice.

Cache cleanup must not destroy audio, source transcripts, completed summaries,
or transcript discussion history. Never silently delete other data to make room.

### Q21 — Exact citation navigation and missing sources

A citation opens its exact transcript excerpt and timestamp when available.
Audio seeking is offered only when playback is available. Excerpts remain usable
when audio is missing.

If the source transcript is missing, report an explicit error; never swap to
another transcription attempt to satisfy the citation or continue analysis.

### Q22 — Required feasibility deliverables

Feasibility must produce pinned bilingual model profiles, proven packaging for
both `win-arm64` and `win-x64`, measured peak memory and latency, licensing and
download details, and a chosen SQLite storage/index approach.

If sqlite-vec cannot be reliably distributed for native Windows ARM64, evaluate
a portable SQLite alternative. Do not silently drop ARM64 support. Exact
technology selection and numerical thresholds remain blocked by feasibility
evidence and a follow-up technical decision round. Q23 confirms the product
baseline without approving these remaining choices.

### Q23 — Product baseline confirmation

The user answered yes to final product baseline confirmation on 2026-11-21.
The product baseline Q1–Q23 is approved and its interview is complete.

The next phase is feasibility validation, followed by a decision round approving
exact technical choices and numerical thresholds. Feature implementation remains
**NOT AUTHORIZED**. The 2026-11-21 turn authorized documentation only, not
feasibility code or measurements; no feasibility or measured results were claimed.

## Pending choices — blocked by feasibility evidence

- **Exact technology, models, and storage:** Select and pin libraries, native
  backend packages, bilingual generation/embedding profiles, model versions and
  quantizations, licenses, context/pooling configuration, and the SQLite
  storage/index approach. Establish reliable packaging on both supported RIDs,
  including the portable SQLite alternative if needed.
- **Numerical performance and resource thresholds:** Approve measured limits for
  peak memory, disk estimates, context/history budgets, first-answer latency,
  full indexing/summary duration, capture responsiveness, and cancellation/release
  behavior, including the 12-hour stress case.

These are the remaining technical and numerical choices, not permission to begin
feature work. Full feature implementation remains **NOT AUTHORIZED**.
Feasibility execution is limited to the separate 2026-11-22 authorization above.
Resolve and approve the technical choices and numerical thresholds in the
follow-up decision round, and obtain feature implementation authorization before
feature coding.
