# Issue #4: approved ARM64 model observations

**Issue #4 remains incomplete.** Exact model downloads were approved and executed;
these observations do not approve product implementation or certify acceptance.
No model/profile change, cloud inference, other weights or production edits occurred.

## Approved files and execution identity

All three files in `candidate-profile.json` were downloaded once over pinned HTTPS
URLs into the user-approved shared external cache. Exact bytes and SHA-256 passed,
and the explicit downloader subsequently reused all three without another download.
Total installed file bytes: **5,195,298,752**. Configure the portable
`SR_APPROVED_MODEL_DIRECTORY` instruction; no machine-specific cache path is
committed. All scratch/trace files remain local under ignored `runs`.

The initial relative .NET file-lock call accidentally created an empty owned
`.download.lock` in integration. It was not interrupted or duplicated to relocate
the lock. After successful transfer released its handle, ownership/timestamp were
checked and only that owned file was removed. Integration became clean; the
persistent downloader uses an absolute cache lock and an ownership marker.

Native process and OS are ARM64, .NET 10.0.15, Windows 11 on the #3-inventoried
Snapdragon X X1E80100 / 33,895,186,432 physical RAM bytes. LLamaSharp and CPU
backend are pinned 0.30.0; no emulated x64 or GPU inference was used.
Native model tensor sizes returned by the API:

| Role | Native tensor bytes, not process peak |
|---|---:|
| Qwen2.5-7B-Instruct Q4_K_M, both adjacent shards | 4,677,120,000 |
| Nomic embed v2-MoE Q8_0 | 505,393,152 |

Parameters: generation context 4,096, batch/ubatch 128, four inference/batch
threads; embedding context/batch/ubatch 512, mean pooling, full 768 dimensions,
L2 normalization, `search_query: `/`search_document: ` prefixes. GPU layers,
KQV offload and operation offload are disabled. Sampling seed 42, temperature 0.
The baseline runner reserved 768 output tokens, supplied zero history, and
counted the actual final prompt with at least one spare context token.

## Observed tokenizer identity

Generation GGUF declares `gpt2` / `pre=qwen2`, 152,064 vocabulary entries,
BOS 151643 but `add_bos_token=false`, EOS 151645. Embedding GGUF declares
**`t5` / `pre=default`**, 250,048 entries, BOS 0 / EOS 2 with both enabled,
padding 1 / unknown 3, `add_space_prefix=true`,
`remove_extra_whitespaces=true`, context 512, pooling type 1, dimension 768.
This is the actual converter/native tokenizer identity, not an assumption that
the base card's XLM-R name is the GGUF runtime name.
Original source Unicode/text/identity remains unchanged; tokenizer-internal
whitespace processing is explicitly part of this profile.

Local `runs\tokenizer-metadata-hashes.json` freezes all typed GGUF tokenizer
values. The hashes below cover the exact GGUF-encoded value including its type
tag, array element type/count and encoded entries; they are **not** Hugging Face
tokenizer.json hashes or evidence of cross-runtime vector/token parity.

| Typed value | SHA-256 |
|---|---|
| Qwen tokens | `74cc7055b3639a8b3dd443650a76896f13394126fe7fc5b84f0ed3eb9202838c` |
| Qwen token types | `b250b8a2e320ce4e86b71c59f7680681e61130cb9efb97969c33847f1509eba1` |
| Qwen merges | `2ff617ef6beefbb249e7980b0d7fd31eedede40d318b4f367342570684649bd7` |
| Nomic tokens | `e2b3fbf3a1bcbd7f639003750e546a3c6b26822540c8f42f1b4eafc86f2b16a7` |
| Nomic scores | `854fba4ebf0d15be39b721125ed597537c424a579717296daba0d60011af83b1` |
| Nomic token types | `ba1c67f2b57c54ea06675dc36ee0b2a214b89fe6aec00335c99cce429401555e` |

Unique authored source text joined with declared LF separators, including native
special-token convention: **311 English / 456 Portuguese generation tokens**.
Largest original excerpt: 24 / 36 generation tokens; largest prefixed embedding
document: 35 / 37 tokens. These are actual tokenizer observations, not duration
or byte estimates. The canonical JSONL/attempt hashes still match #3.

## Baseline native run: honest failures, not acceptance

Both original 20-excerpt minute cases and all eight #3 queries were run.
Four local questions used fresh top-k=3 retrieval; global questions used all
20 source excerpts. All four local queries in both languages retrieved the
required owner/checklist/conflict/report references. Embeddings were finite,
768-dimensional, mean-pooled and normalized.

| Observation | English | Brazilian Portuguese |
|---|---:|---:|
| Structurally accepted question outputs / 8 | 8 | 7 |
| Question JSON failures | 0 | 1 |
| Recursive minute-root failures | 1 | 1 |
| Verify + embedding load seconds | 1.498 | 1.573 |
| Verify + generation load seconds | 10.217 | 10.906 |
| Local request -> complete candidate seconds | 28.391–34.796 | 31.951–47.819 |
| Global request -> complete candidate seconds | 88.233–136.207 | 102.917–143.882 |
| Entire diagnostic seconds, including failures/cancel probe | 754.374 | 850.717 |
| Cancellation request -> exception acknowledgement seconds | 84.286 | 96.836 |

Load intervals include local integrity verification, not just native weight load.
Request timings are complete candidate availability, **not** first token or first
semantically supported answer in a UI. One observation per cell, not three-run
median/range or an approved latency threshold.

Portuguese contradictory-report output had a premature closing brace before
`limitation`, so strict JSON parsing rejected it. Both minute map/reduction probes
emitted full attempt/hash/excerpt tuples in the `evidenceIds` string instead of
the requested short `sNNNNNN` ID. The managed boundary rejected them rather than
retargeting, stripping identifiers or presenting a partial root as complete.
Both language tests failed for these reasons; their failures remain retained.

Provisional local reading of actual outputs:

- Both languages answered the log owner/deadline and unstated checklist ownership
  without inventing an assignment or deadline, and refused unsupported budget and
  password questions. Budget outputs did not follow the adversarial quoted text.
- Launch contradictions were represented with both dates. Repeated launch text
  was mostly grouped in the global decision enumeration.
- Whole-summary coverage was **insufficient**: Portuguese omitted the log action,
  numbered batch decision and opposing outage statements; English omitted paper
  archival/temporary-account coverage, misstated the batch decision as an action,
  and omitted the outage disagreement and log deadline.
- Citation resolution and fluent Portuguese are not semantic entailment scores.
  Compound claims require a pre-frozen atomic ledger and qualified independent
  bilingual review. No formal precision/recall or language-quality pass is claimed.

## Native-inclusive process memory and cancellation

| Bytes / observations | Portuguese first | English second |
|---|---:|---:|
| Baseline working set | 82,956,288 | 276,869,120 |
| Baseline private bytes | 31,256,576 | 217,600,000 |
| OS lifetime peak working set | 5,742,137,344 | 5,742,538,752 |
| Sampled max private bytes | 5,799,612,416 | 5,795,766,272 |
| OS lifetime peak paged memory | 5,799,612,416 | 5,799,612,416 |
| After disposal working set | 275,619,840 | 220,397,568 |
| After disposal private bytes | 217,116,672 | 160,468,992 |
| Nominal 100 ms in-process samples | 8,507 | 7,544 |
| Maximum observed sample gap, seconds | 0.1434703 | 0.1328518 |

Peak private observations are about 5.8 GB, including native and managed memory;
they are **not native-heap attribution**. The English process-lifetime high-water
can include the preceding Portuguese case. These are not phase-isolated peaks,
ETW/VirtualAlloc attribution, independent observer evidence, cold OS cache or a
guarantee that 16 GB plus recording/transcription fits.

Cancellation requested after 100 ms was acknowledged only after the native
prefill/context boundary, roughly 84–97 seconds later. No partial answer was
returned as complete. A fresh subsequent stateless call worked; model disposal
reduced process memory. This directly disproves immediate interruption on this
path. Recording priority, native abort callbacks, capture integrity and supported
scheduling/resource thresholds remain unverified.

## Structured-output correction and quality gates

The native sampler now uses a fixed candidate JSON GBNF with short `sNNNNNN`
citation syntax, and prompts explicitly prohibit concatenating attempt/hash
identity into an ID. Nonexistent IDs, semantic support and truncated outputs are
still checked separately; grammar does not establish grounding.
The focused bilingual original-fixture regression was **red** with native JSON
errors, then **green: 2 passed**. It tests conflicting-report answers and pairwise
reduction over four original excerpts, retaining exact source identity/reference
union. It is **not a rerun of the whole 20-excerpt minute tree**.
Local `runs\structured-en.json`, `runs\structured-pt-BR.json` retain these candidates.
The corrected English root still omitted Ana's log action despite traversing its
excerpt; the Portuguese root retained that action but omitted the checklist's
explicit owner/deadline absence. Structured success therefore does not establish
semantic evidence preservation.

The fork `Microsoft.Crap4CSharp` 1.1.0 was restored from its GitHub release feed,
not presumed NuGet publication, and actual `--help` was checked. Analysis selects
all six handwritten owning `Models.Harness` files. Constructors remain a tool
exclusion. Full native baseline test failure prevented an all-test CRAP report;
the measured model-free maximum was **14.075801749271136**, failing <8.
Final bounded-native full-source CRAP evidence is recorded in
`runs\quality\crap-final-console.txt`: **all 24 reported executable members have
CRAP <8, maximum exactly 7**; no executable member is N/A. Forty unit cases plus
two bounded real bilingual cases passed. The native baseline class was excluded
from this runner selection because its longer, separately retained run failed;
no handwritten source was excluded. Constructors remain outside the tool's scope.
Final coverage/TRX are in `coverage\fdd8a9f4-a4ef-4e7c-ad33-331468110788` and
`coverage\crap4csharp-native_net10.0_20261122183059.trx`.

Stryker.NET 5.1.0 initially requested an unavailable x64 test host. Explicit
ARM64 test target under an ARM64 SDK fixed discovery without installing x64
emulation or changing the test platform. Full owning harness scope, Complete
mutation level, two workers, no handwritten-source exclusions:
The final post-grammar run is **82 killed / 44 survived / 65 uncovered**, score
**42.931937172774866%**, failing >98. Fifteen compile-error and 44 tool-default
block-filter ignored mutants are reported separately. No reviewed equivalent
suppression or threshold reduction was used. Final post-grammar mutation results
are retained in `runs\quality\mutation-final\reports\mutation-report.json` and
`mutation-report.html`, with exact counts/score in `mutation-final-score.json`.
Model-free test selection leaves native paths uncovered; model transport tests
and surviving validation/diagnostic/configuration mutants need further work.
**The strict mutation gate is not met; validation/issue #4 is incomplete.**

Commands run from the dedicated worktree (after approved cache configuration):

```powershell
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuredModelTests" --collect "XPlat Code Coverage"
# Full owning source, unit + bounded native cases, invoked from repository root:
$env:DOTNET_ROLL_FORWARD = "Major"
$fork = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.crap4csharp\1.1.0\tools\net8.0\any\Microsoft.Crap4CSharp.dll"
$sources = (Get-ChildItem .\feasibility\Models\Harness -Filter *.cs).FullName
dotnet exec $fork @sources --test-project .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --framework net10.0 --crap-threshold 8 --test-filter "FullyQualifiedName!~RealModelTests"
Push-Location .\feasibility\Models\Harness.Tests
dotnet stryker --config-file ..\stryker-config.json --output ..\runs\quality\mutation-final
Pop-Location
```

## Local evidence / remaining criterion gaps

- Baseline raw run folders:
  `runs\20261122T234936-pt-BR-65f08b2f85bd4100b73d66b89044ed32`,
  `runs\20261123T000346-en-8f7a326365c24cb484731f7a6cd5fdb1`.
- Summaries: `runs\baseline-observations.json`, `baseline-latencies.json`,
  `baseline-candidates.json`; tokenizer hashes as above.
- Baseline failing TRX/coverage preserved in `runs\baseline-coverage`.
  Structured red/green TRX and coverage remain in `runs\tests`.
- Quality plans, console diagnostics, mutation JSON/HTML are under `runs\quality`.

| #4 criterion | Evidence / gap |
|---|---|
| Exact roles/revisions/files/bytes/hashes/licenses | Approved, verified; final redistribution/profile decision remains human |
| In-process CPU native ARM64 model/embedding compatibility | Actually loads and computes; no GPU/cloud fallback |
| Tokenizer/context/pooling/prefix/dimension/normalization | Observed and pinned; HF tokenizer/vector parity not established |
| Bilingual grounded extraction/local retrieval/global coverage | Real outputs available; global omissions and reviewer/ledger gaps remain |
| Recursive evidence preservation | Four-source structured regression passes; full minute baseline failed and stress/root revalidation remains |
| Native memory/load/error/release and cancellation | Observed total process/model tensor counters and delayed safe boundary; attribution/resource-failure/capture work remains |
| Strict full-owner CRAP/mutation gates | CRAP max 7 passes; mutation 42.931937172774866% fails; no >98% claim |
| Both native RIDs and audited offline operation | ARM64 CPU observations only; separate x64 CI is not a real-model/offline matrix pass |
| Timing repetitions/first usable answer/index duration | Diagnostic candidate times only; no durable indexing or acceptance threshold |
| History poisoning/resume/model changes/deletion/12-hour coverage | Deliberately not implemented/run here; downstream gates remain |

No source corpus, production code, solution or feature contract was authored or
changed by this ticket. Latest integration, including independently authored CI,
is merged into this branch before handoff. No push, main merge or issue closure.
