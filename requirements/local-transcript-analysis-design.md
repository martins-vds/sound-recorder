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

## Approved feasibility pipeline corrections - 2026-11-23

The user requested all four extraction/reduction review corrections and confirmed
the public test interfaces before implementation. This authorizes changes to the
isolated feasibility harness, not application feature implementation:

- Stream source excerpts into a disk-backed ledger of structured transcript
  claims and source references. Native prompts and live processing state remain
  bounded; reducers must not reattach the complete original transcript.
- Preserve claim kind, stated owner/deadline or their explicit absence, and
  qualifications in deterministic merging. Combine only equal structured claims
  and union their evidence; retain conflicts and non-equal claims rather than
  guessing semantic equivalence. Initial extraction still requires semantic
  evaluation; source traversal or valid citations alone do not establish truth.
- Return explicit completed-versus-token-limited generation outcomes with
  stage-specific budgets, preserving honest context and cancellation failures.
- Run question and summary diagnostics independently, each with its own deadline.
  Question work must not consume a summary request's diagnostic budget.

The feasibility v2 pipeline preserves initial structured records on disk without
an LLM rewrite. Exact original quotes can be resolved from bounded original
source ranges; the model need not recopy them. Typed records and valid references
remain semantically unverified until the frozen claim/evidence rubric is applied.
Singleton range batches and explicit compact-record limits are feasibility
settings, not approved product/resource thresholds.

One bounded validation run with the already-downloaded Phi-mini model is approved.
No new model downloads, automatic tuning, relaxed quality gates, or final
profile/performance approval are included. Earlier experiment reports retain
their original results and configuration identities.

## Manual-validation walking skeleton authorization - 2026-11-23

The user subsequently requested a working walking skeleton in the existing
desktop application and explicitly excluded the 12-hour test. This authorizes
a narrow experimental integration, superseding the earlier feature-code
restriction only for this manual-validation slice.

Select one successful saved transcript, explicitly generate a local summary
or ask a question using the already-downloaded Phi-mini model, and inspect
the supporting original excerpts. Preserve the incumbent desktop layout and
recording workflow. Expose progress, cancellation, actionable errors, model
configuration requirements, and an unverified-output warning. No automatic
downloads, cloud fallback, new model trials, or final release claims are included.

Confirmed test interfaces are desktop commands/view-model behavior, the local
analysis adapter, and the existing native UI test host. Selection ownership and
stale results, recording/transcription exclusion, evidence inspection, cancellation
and errors are in scope. One short cached-model smoke test is authorized;
the 12-hour stress test and full release-acceptance campaign are not.

## Continuing the validated integration - 2026-11-23

The user reported that the walking skeleton worked well and that local-model
response time, while high, was acceptable. They then authorized continuing
implementation. Treat this as positive manual integration feedback, not semantic
or release acceptance.

The next slice hardens the working adapter and desktop lifecycle at the same
confirmed test interfaces: source/timestamp fidelity, bounded context, exact
attempt ownership, cancellation and competing capture/transcription intent,
and meaningful behavioral coverage of the remaining quality gaps. Preserve
the current model and working manual-validation artifact. The 12-hour test,
new model downloads and automatic tuning remain excluded. Final model quality,
distribution and release claims still require their separate evidence.

## Formal implementation baseline - 2026-11-23

After validating the walking skeleton, the user explicitly approved formal
implementation using LLamaSharp, the cached Phi-4-mini/Nomic profile, and local
SQLite as the initial stack. This supersedes earlier documentation-only and
walking-skeleton-only implementation restrictions for the production feature.
It does not retroactively turn model experiments into passing semantic evidence.

Keep the accepted decisions Q1-Q23 as the behavioral baseline, with these
implementation and validation clarifications:

- Production application code must not depend on projects under `feasibility`.
  Move reusable engine behavior into production-owned modules and preserve
  existing behavior through the same confirmed public interfaces.
- Core owns transcript/attempt identity and application state independently of
  WPF, SQLite, JSON, LLamaSharp and native APIs. Infrastructure supplies local
  storage, model and inference adapters; Desktop coordinates user actions.
- Implement persistent per-transcript analysis jobs and completed results,
  compatible resumable indexes/checkpoints, explicit cancellation/retry,
  safe owned-data deletion and recording-priority scheduling.
- Support local lookup and whole-transcript coverage separately; top-k retrieval
  cannot establish exhaustive answers. Keep original source citations and
  explicit uncertainty visible, including after restarting the app.
- Keep English/Brazilian Portuguese and native Windows ARM64/x64 scope. Use
  explicit model setup/import/download with integrity verification; no automatic
  download, cloud fallback, new model trial or GPU requirement is authorized.
- Preserve the incumbent desktop work areas and improve the existing controls
  incrementally. Use a dependency-ordered ticket graph and strict TDD at agreed
  interfaces; confirm new public interfaces before adding their tests.
- The 12-hour stress test remains deferred. Full-project quality gates and
  directly relevant functional validation remain required. Supported numerical
  performance/resource limits and release acceptance are not inferred from
  acceptable manual latency.

### Production implementation graph

1. Establish production ownership and analysis contracts, preserving the working
   desktop slice and removing its dependency on feasibility projects.
2. Provide explicit verified model-profile setup and resource preflight.
3. Persist exact-source results, indexes, checkpoints and owned deletion.
4. Coordinate durable jobs, restart/retry/cancellation and recording priority.
5. Implement source-grounded summary/discussion with complete global coverage and
   bounded conversation context over the persistent source/index.
6. Integrate production lifecycle, setup, evidence navigation and result/history
   restoration into the existing desktop.
7. Validate native distribution and complete the affected quality gates before
   converging implementation branches into main and cleaning task-owned worktrees.

Steps 2 and 3 can proceed independently after step 1. Scheduling requires step 3;
grounded processing requires steps 2 and 3. Desktop integration joins scheduling
and processing. Final validation requires the integrated desktop. Ticket blocking
relationships, rather than list order, determine the implementation frontier.

### Step 1 foundation checkpoint - 2026-11-23

The reusable engine now belongs to `src\SoundRecorder.Analysis`, not
`feasibility\Models\Harness`. Core owns the existing `ILocalTranscriptAnalysis`,
`ILocalAnalysisDiagnostics` and request/result/claim/evidence records.
Infrastructure retains the adapter and saved TXT/SRT reader; Desktop retains
the existing commands and lifecycle. There is no new coordinator, durable job
behavior, UI redesign or public-seam expansion in this step.

The runtime project graph is `Desktop -> Infrastructure -> Analysis`, with
Desktop and Infrastructure also referencing Core. Historical
`Models.Harness.Tests` references Analysis directly and retains its original
test identities, fixtures and native experiment cases. The obsolete engine
project is removed rather than kept as a forwarding runtime dependency.
These manually validated in-repository symbols are migrated to production
namespaces; no external NuGet API compatibility claim is made.

All eleven engine source files preserve their behavior byte-for-byte after
namespace/resource binding changes and newline normalization. SQLite schemas,
serialized record fields, original-source evidence, native package versions,
model filenames/SHA-256 and inference parameters are unchanged. Production
embeds frozen copies of the approved profile metadata so it has no build-time
resource dependency on feasibility. Original profiles and historical reports
remain intact. The application still explicitly loads Phi-mini and Nomic;
legacy Qwen loaders/resources remain only for existing experiment consumers,
not fallback or automatic model selection.

Baseline: solution build passed, **54 analysis tests** and **175 model-free
pipeline tests** passed, with no skipped cases. Post-move Debug/Release builds
passed; **302 relevant Core/Desktop consumer tests** and the same **175 pipeline
tests** passed. Clean framework-dependent publishes succeeded for `win-arm64`
and `win-x64`, without the old engine DLL or dependency. Forty deployed native
binary hashes across those publishes match the baseline; the model-free native
CPU probe passed on Windows ARM64. Publishing x64 is not native x64 execution
or distribution acceptance. No new behavior is introduced, so the existing
confirmed seam tests remain the behavioral oracle rather than adding
assembly/file assertions.

Quality remains **incomplete**, not waived by ownership migration. The recorded
pre-move full Infrastructure mutation failure (**77.385%**) and focused analysis
failure (**83.76%**) remain unresolved historical blockers; neither becomes a
passing gate because an owner changes. Full-owner Analysis CRAP selected all
eleven handwritten sources with the 175 permitted model-free tests: **86 numeric
members**, maximum **20**, with source-boundary and native inference coverage
gaps, so it failed.
Constructors remain outside the fork's analysis. Native model execution is not
authorized in this step; the model-free native CPU probe is not model-semantic
or distribution evidence.

The isolated full-owner Analysis mutation run selected all eleven source files
and 175 model-free tests: **504 killed**, **3 detected timeouts**, **9 survivors**
and **43 uncovered**; **507 / 559 = 90.69767441860465%**, so Stryker exited **2**
against the unchanged 99 break threshold. Eighteen compile errors and 92
tool-default ignored mutants are not detected mutations; no handwritten source
exclusions or manual suppressions were added. Every source embedded in the
report matches the production owner. An earlier run overlapped a Release build
and was discarded; only the isolated completed run is authoritative. Subsequent
clean build and pipeline regression passed.

Current local evidence is under `artifacts\analysis-foundation` in the foundation
worktree: `crap-analysis.txt`, `coverage`, `mutation-summary.json`,
`mutation-analysis-isolated\reports`, `ownership-evidence.json` and `tests`.
Reports are private and ignored by Git. The full reusable engine stays
in its new owner's CRAP/mutation scope. No full legacy quality rerun, 12-hour
stress, model trial/download, release acceptance or working-app replacement is
included in this ownership checkpoint.

### Full-owner foundation validation follow-up - 2026-11-23

The initial model-free Harness-only checkpoint above is retained as partial
evidence, not the complete production-owner gate. The production engine is also
exercised directly by Core's `TranscriptAnalysisTests`. Current owner validation
therefore combines both referencing suites, selecting every handwritten
`SoundRecorder.Analysis` source without changing production scope or thresholds.

Fork CRAP dry-run confirms two separate coverage directories, one for Harness's
single `net10.0` TFM and one for Core's single `net10.0-windows` TFM. No framework
harmonization or `--framework` override is needed. Repeated `--test-project`
selectors combine source-line coverage by union. Stryker's installed `init`
command confirms repeated selectors become a `test-projects` array; the runner
executes from the production owner. Its owner-local configuration persists both
consumers and fails immediately if the initial test run is red.

The user clarified permission for exactly the five existing
`NativeAdapterSmoke`/`PhiMiniNative` cases: native EOG versus cap, tokenizer/role
tokens, bounded JSON transport, mean pooling and disposal, using the cached
approved weights only. These are native interface checks, not new model trials
or real-model semantic comparisons. All four required cached files passed their
unchanged size/SHA-256 identities before execution.

Reqnroll exposes existing `@type:IntegrationTests` tags as
`Category=type:IntegrationTests`, whereas xUnit uses the `type` trait. The gate
filter honors both declared forms before explicitly adding the five permitted
native purpose cases:
`(type!=IntegrationTests&Category!=type:IntegrationTests)|purpose=NativeAdapterSmoke|purpose=PhiMiniNative`.
No scenario is retagged or newly quarantined. Earlier mutation attempts that
incorrectly included the reference-PC worker integration scenario failed their
initial run and are retained as invalid attempts, not scores.

Union coverage initially established CRAP maximum **7** without changing engine
logic. Completed full-owner mutation then measured **530 / 559 =
94.81216457960644%** (522 kills, eight detected timeouts, 24 survivors and five
uncovered), still failing the unchanged 99 break threshold. The remaining range
gaps were not hidden by the owner move or by calling native coverage unavailable.

Fourteen focused C# cases now exercise `EmbeddingProbe` through its public
backend seam: blank sources, sentence/word separators, range-midpoint and
one-character overflow boundaries, surrogate-safe token fitting, deterministic
source references, overlap and complete original-text traversal. Existing
request-contract cases also require nonempty trusted instructions separate from
the supplied source. No private-method, source/assembly-location or constant-only
tests were added. Focused mutation feedback detected all 26 identified
range/protocol gaps; that restricted feedback is not a final gate.

Final union CRAP after those assertions selects all eleven sources and reports
**86 numeric members**, maximum **7**, with constructors outside the fork's
analysis. **194 Harness cases** (189 model-free plus the five permitted native
cases) and **757 Core cases** passed. The seven ignored release-acceptance
scenarios remain incomplete release evidence, not passing requirements.

Production engine code, model/profile identities, native binaries, application
project references and existing UI behavior are unchanged by this follow-up.
Full Infrastructure and earlier adapter mutation failures remain historical
blockers; successful engine validation cannot reset or waive them.

Final full-owner Analysis mutation after those tests: **552 killed**, **four
detected timeouts**, **three survivors**, **zero uncovered**; **556 / 559 =
99.46332737030411%**. Stryker exited **0** against the unchanged 99 break
threshold. Eighteen compile errors and 92 tool-default ignored mutants are
separate from detected mutations. All eleven report-embedded source files match
the production owner. There are no handwritten source exclusions, manual
suppressions, new native cases or semantic model runs.

The three retained survivors remove the iterator's completed-task await, the
native loader's CPU-initialization call, or embedding-context disposal. They are
reported, not suppressed or asserted equivalent. The strict owner gate passes;
this is not a claim of perfect mutation detection or proof of every native
resource-lifetime behavior. Stryker's WPF simulated-build warning remains a tool
limitation; actual dependent builds and the initial test run succeeded.

Clean post-mutation Release build passed with zero warnings/errors. **189**
model-free pipeline cases and **302** relevant Core/Desktop consumer cases
passed, including the unchanged 54 analysis cases. Source/model/native identity
checks and both clean architecture publishes from the ownership checkpoint
remain retained. No integration/main edit, push, ticket closure, working-app
replacement or release claim is included.

Current reproducible commands are in the root README and
`src\SoundRecorder.Analysis\stryker-config.json`. Raw coverage/TRX, native proof,
both full mutation runs, targeted feedback, source/model identities and
publication dependency/hash reports are preserved in
`files\formal-foundation\analysis-foundation-01d7ed9` under the persistent parent
session directory, outside the implementation worktree.

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
