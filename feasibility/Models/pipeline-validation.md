# Issue #11: structured streaming pipeline v2

**INCOMPLETE: both mandatory quality gates fail and the cached native run aborted.**
The four pipeline corrections are implemented and exercised at the approved public
seams, but this is not ticket completion, model approval, or production authorization.
No push, integration-branch merge, issue closure, new weights, or automatic tuning.

Worktree: `C:\Repos\sound-recorder-feasibility-11`.
Branch: `feasibility/ticket-11-pipeline`.
Base/latest integration: `8a95d980647742d1ad8bc333f9ff3fc9f029b573`.
`git merge --no-edit feasibility/local-transcript-analysis` reported
`Already up to date` before this report. Application projects/solution are unchanged.

## Exact interfaces and callers

- `IGenerationBackend.GenerateAsync` now returns `Task<GenerationResult>`,
  containing `Text`, native `Finish` (`Completed`/`TokenLimit`), and
  `GeneratedTokens`. `RenderPrompt`/`CountTokens` retain their native conventions.
- `LlamaGeneration.GenerateAsync` forwards that result and preserves sampled-token
  observations on failures/cancellation. All explicit model-load entrypoints remain.
- `INativeModel.InferAsync` now returns `Task<GenerationResult>` and exposes
  `LastGeneratedTokens`. `LlamaNativeModel` observes actual sampled-token EOG through
  a `DefaultSamplingPipeline.Sample` override, not text length/retokenization.
- `GenerationProbe.ExtractAsync` and `SummarizeAsync` accept
  `(TranscriptIdentity, IAsyncEnumerable<ExcerptInput>, ledgerPath, language,
  deadline, cancellationToken)`. `AnswerAsync` additionally accepts `question`.
  `ExcerptInput` supplies a disposable `TextReader`, original ID and timestamps.
  Results are `AnalysisResult`, not retained sources/claim arrays/trees.
- `ClaimLedger.Open` exposes streaming `ReadClaims`, `ReadEvidence`, `ReadBatches`,
  and exact-ownership/last-persisted-status `ReadRun`.
- The old free-text `Claim`/`CandidateOutput`, `GenerationNode`,
  `MapAsync` and in-memory `MapReduceAsync` are removed. This is a deliberate
  feasibility-only API break.

Updated callers/tests: `GenerationProbeTests`, new `PipelineTests`,
`RealModelTests`/its captured-generation logger, `StructuredModelTests`,
`NativePolicyTests`/`NativeDouble`, `NativeAdapterSmokeTests` and
`PhiMiniNativeTests`. `EvaluationFixture` and embedding/retrieval behavior remain
unchanged. Model/profile rejection files retain their original hashes/results.

## Review findings and resolution

| Finding | Implemented correction and evidence |
|---|---|
| Full source retained/reattached at reduction root | Reader-backed source traversal, singleton bounded original-range batches, native-token-aware shrinking on Unicode scalar boundaries; no root prompt, source dictionary, retained tree or RAM claim-key inventory. SQLite uses disk indexes, a 512-KiB writer cache and file-backed temporary storage. |
| Free-text/ID proxy loses required facts | Typed kind/statement, explicit owner/deadline state/value, qualifiers and original range references. Exact serialized structured equality only; C# transactions union evidence without rewriting records. Conflicts/non-equal records remain separate. Quotes resolve from persisted original ranges, never model recopying. |
| Uniform output cap and implicit finish | Extraction reserve 1,024; answer reserve 512; final native prompt plus reserve strictly below 4,096. Compact record limits fail explicitly. Native EOG and length exhaustion are distinct, including identical valid output text. Capped batches are not committed as claims. |
| Queries consume summary deadline | Separate public requests and separate native test cases. Each summary owns 20 minutes; each question owns 2 minutes including retrieval, with a separate question-suite 20-minute cost guard. No shared summary timer. |

These are structural corrections, not semantic acceptance. Initial extracted
records remain **UNVERIFIED**, including syntactically valid claims with false
absence states or unsupported interpretations. A source reference does not make
generated claim text authoritative transcript evidence.

No optional prose reducer rewrites required records. Source offsets are original
UTF-16 positions; fragments retain original saved-audio timestamps, not interpolated
times. Source body/metadata consistency is checked, including a repeated source
whose prefix matches but whose tail is missing.

## Red-green and regression evidence

Vertical cycles were run before each implementation slice:

| Slice | Red evidence | Green evidence |
|---|---|---|
| Explicit native finish result | `native-finish-red.log`: missing finish/result API | `native-finish-green.trx`: 28 passing selected cases |
| Persisted structured equality/evidence union | `ledger-red.log`: missing public ledger/extraction API | `ledger-green.trx`: real disk reopen, exact fields, equal-record union and conflict retention |
| Oversized streamed Unicode source | `packing-red.trx`: over-context failure | `packing-green.trx`: complete original quote/range reconstruction |
| Independent deadlines/stage budgets | `deadlines-red.log`: missing independent answer interface/budget | `deadlines-green.trx`: expired question, fresh successful summary |
| Compact structured validation | `validation-red.trx`: invalid kind/state and empty output accepted | `validation-green.trx`: invalid batches rejected without partial claim commit |
| Empty/invalid Unicode source | `source-validation-red.trx` | `source-validation-green.trx` |
| Repeated source with missing tail | `source-length-red.trx` | `source-length-green.trx` |
| Invalid/unbounded request budgets | `budget-red.trx`, `request-red.trx` | corresponding green TRX files |
| Smallest fitting surrogate pair | `scalar-fit-red.trx`: rejected fitting first scalar | `scalar-fit-green.trx` |

Final deterministic regression: **99 passed, 0 failed, 0 skipped**.
Debug and Release builds: **0 warnings/errors**.
`git diff --check` passed.

Tests use the confirmed public seams and real artifact readers, never private DB
queries. They cover exact-record discriminators, absence states, qualifier/value
limits, mixed invalid references, capped/cancelled/malformed partial work,
conflicting original timestamps, missing tails, Unicode/control-token escaping,
and independent deadlines. The oversized case now deliberately places a high
surrogate at the 1,024-character boundary: a guarded source rejects whole-body
draining, observes the one-character spare read, and verifies exact original
quote concatenation and timestamps across the long source plus 200 excerpts.

`MaximumLiveSourceCharacters` is a conservative owned-buffer/prompt-copy bound,
not a GC/native-allocation profiler. Prompt budgets and source read requests are
also checked independently. No total-model-RAM or arbitrary-time acceptance claim.

## One cached Phi-native validation

Only the already approved Phi-mini/Nomic cache and frozen original minute corpus
were used. Source SHA-256s:

- English: `f5d48c8f7d5d28acd824827cdb9b6139971e259657696fb133178dc9a088f234`
- pt-BR: `1cc4e34686cdb4e9095a2dbc6847ef5af0c2c69b2f8514f243c530e33e5865a2`

Native Windows ARM64 process/OS, SDK 10.0.404 / .NET 10.0.15; LLamaSharp/CPU
backend 0.30.0. No new download or alternate model run. New configuration identity
is `sr-structured-ledger-v2`; the historical model profiles are not relabelled.

Actual native finish proof: grammar-constrained **`OK`** was returned in both
calls. With cap **8**, one non-EOG token plus native EOG produced `Completed`.
With cap **1**, the same one-token text produced `TokenLimit`, without EOG.
The test and `runs\native-finish-proof-v2.json` preserve this actual observation.
All three Phi-native smoke/tokenizer/finish cases passed.

| Cell | Completed/started calls | Max prompt | Reserve | Max generated | Request outcome/time |
|---|---:|---:|---:|---:|---|
| English local-assigned-action | 3/3 | 265 | 512 | 58 | Structural completion; 41.6307675 s extraction, 42.4490680 s including retrieval |
| pt-BR local-assigned-action | 3/3 | 280 | 512 | 52 | Structural completion; 39.8809597 s extraction, 40.7231633 s including retrieval |
| English minute summary | 20/20 | 263 | 1,024 | 61 | Completed original-range traversal; 266.5231047 s |
| pt-BR minute summary | 5/6 | 273 | 1,024 | 63 | Host crashed during the sixth call; no completed summary/deadline verdict |

Completed cells observed native EOG, not cap exhaustion. English traversal
preserved 20 initial records with original evidence; this is **not** 20 supported
facts or semantic recall. Portuguese's artifact retains unfinished last-persisted
`Running` state/committed initial batches, not success or automatic recovery.

The frozen native-inclusive coverage selection expected 93 cases (86 then-current
deterministic cases plus seven native cases). **92 passed, one active case aborted**:
`RealModelTests.Summary_ApprovedBilingualMinute_PersistsInitialUnverifiedClaimLedger`
for pt-BR. TRX reports `testhost process crashed`; it contains native loader stderr
but no diagnostic fault stack/cause. No corresponding Application-error event was
available in the targeted local read. No repeat inference, hypothetical fix,
automatic continuation or model tuning followed the crash.

**Reject semantic suitability of this measured initial-extraction profile.**
Provisional direct observations, not qualified bilingual acceptance scores:

- The log action mentions Ana/date in text but emits `Absent` owner/deadline
  fields; Portuguese also omits the deadline from the statement.
- Unrelated retrieved source ranges produce unsupported access-log actions.
- English launch/date disagreement loses dates/qualifications in initial records;
  decisions, topics and quoted hostile instructions are frequently labelled actions.
- Qualifier fields remain empty and the final batch decision loses its numbered
  identity/kind. Short EOG-completed outputs show that reserve increase alone did
  not solve these initial semantic defects.

The C# ledger intentionally does not repair those records from generated text.
Qualified independent bilingual rubric adjudication remains unavailable; support
precision/recall are not replaced with citation-count scores.

## Full strict quality gates

Owner: `feasibility\Models\Harness\Models.Harness.csproj`.
All **10 handwritten production files** are selected:
`ClaimLedger.cs`, `ClaimValidation.cs`, `EmbeddingProbe.cs`, `GenerationProbe.cs`,
`LlamaEmbedding.cs`, `LlamaGeneration.cs`, `ModelFiles.cs`, `NativeCpu.cs`,
`NativeModelBoundary.cs`, `StreamedGeneration.cs`.

- Fork `crap4csharp` 1.1.0, executed with `dotnet exec --roll-forward Major`
  against its installed net8 DLL. Explicit owner sources, test project,
  framework `net10.0`, unchanged threshold 8.
- Final 99-case deterministic run reports **77 numeric members**, exact maximum
  **CRAP 12**: `LlamaNativeModel.InferAsync`, CC 3, selected-test coverage 0,
  therefore `3² × (1−0)³ + 3 = 12`. **FAIL `<8`**.
  Other reported members are below 8; maxima outside that native method are 7.
- Native-inclusive analysis was attempted in the one approved native run, but
  testhost abort caused coverage command exit 1 and no valid normal CRAP report.
  Its partial native coverage is retained, not averaged/merged into a fabricated pass.
  Constructors are excluded by the fork and are not claimed analyzed.
- Stryker.NET **5.1.0**, Complete level, full owner, two workers, unchanged 99
  thresholds; test execution `type!=IntegrationTests` prevents extra model runs,
  without excluding any handwritten source from mutation.
- Final: **343 killed, 65 survived, 39 uncovered, 0 timeout, 0 runtime error**.
  **343/447 = 76.7337807606264%**, **FAIL `>98%`**, exit 2.
  Twelve compile errors and 82 tool-default block-filter ignored mutants are
  reported separately, not credited as killed or manually suppressed.
- First full run: 338/447 = 75.6152125279642%. Real aligned-surrogate, mixed-reference
  and conflicting-timestamp regressions improved detection by five mutants;
  no prompt-constant assertions or score-only production code were added.

| Remaining blocker source | Survived | Uncovered |
|---|---:|---:|
| ClaimLedger.cs | 1 | 2 |
| ClaimValidation.cs | 13 | 6 |
| LlamaGeneration.cs | 5 | 2 |
| NativeModelBoundary.cs | 0 | 18 |
| StreamedGeneration.cs | 46 | 11 |

Every remaining ID/replacement/line/status is in private `mutant-blockers.json`;
this includes diagnostics, request/source/record validation, prompt behavior and
native adapter coverage. No equivalent-mutant exclusion or threshold relaxation.
The native crash, full-owner CRAP 12 and these **104 remaining mutants** are exact
completion blockers, not waived risks.

## Commands and persistent private reports

From this worktree (every PowerShell invocation first selected it):

```powershell
dotnet test feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type!=IntegrationTests"
dotnet build feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore -c Release
```

Full fork command: all ten absolute owning source paths, then
`--test-project feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj
--framework net10.0 --crap-threshold 8`. Native attempt additionally used
`--test-filter "type!=IntegrationTests|purpose=PhiMiniNative|FullyQualifiedName~RealModelTests"`
with `SR_EVAL_PROFILE=phi-mini`, `SR_EVAL_QUERY_ID=local-assigned-action`.

Mutation from `feasibility\Models\Harness.Tests`:
`Stryker.CLI.dll --config-file ..\stryker-config.json
--test-case-filter "type!=IntegrationTests" --output <owned-report-directory>`.

Private root:
`C:\Users\vinima\.copilot\session-state\87f62508-2e44-47cc-80d2-0b9d91f62872\files\issue-11`

- Red/green logs/TRX and `final-regression.trx`, final build logs.
- `native-coverage\crap4csharp-native_net10.0_20261123081558.trx`,
  `native-crap-console.txt`, `native-run-summary.json`, `runs\` raw prompts,
  outputs, SQLite ledgers/public-reader exports and native finish proof.
- `crap-final-99.txt`, `deterministic-coverage-99\` Cobertura/TRX,
  `mutation-final\reports\mutation-report.json` / `.html`.
- `strict-quality-summary.json`, `mutant-blockers.json`,
  first-run reports, frozen obligations, host/base and source/profile/report hashes.
- Workflow PowerShell syntax validation and latest integration merge transcript.

Reports are preserved outside the worktree before cleanup; raw source/models are
not uploaded. The existing approved x64 workflow is extended to require all 99
deterministic ledger/model-free native cases and TRX, retaining the original
native SQLite-extension checks. It is **not executed here** and contains no weights.
Native x64 inference, offline audit, capture contention, release/resource
thresholds and final model/technology approval remain unmeasured/downstream.
#4/#1 are not closed or approved.
