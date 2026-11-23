# Issue #4: bilingual in-process model feasibility

## Issue #11: streamed structured ledger (v2)

The current harness replaces the v1 in-memory source/tree reduction with
`GenerationProbe.ExtractAsync` / `SummarizeAsync` and independently timed
`AnswerAsync`. All consume `IAsyncEnumerable<ExcerptInput>` with a disposable
`TextReader` body and an explicit new ledger path. They return `AnalysisResult`,
not an accumulated claim array or tree. `ClaimLedger.Open` exposes streamed
`ReadClaims`, `ReadEvidence`, `ReadBatches` and exact-ownership `ReadRun`.
The feasibility-only API break is deliberate; callers/tests have moved together.

[pipeline-profile.json](pipeline-profile.json) freezes v2 settings separately
from every historical model profile. Final model prompts are counted with the
native tokenizer. A singleton original-range batch bounds both input and record
shape; oversized excerpts are read in 1,024-character blocks plus one surrogate
spare, then packed/split on complete Unicode scalars without dropping text.
Offsets remain original UTF-16 positions, and original saved-audio timestamps
are never interpolated. Metadata and candidate fields have explicit limits;
invalid/excessive records fail instead of truncating or repairing JSON.

SQLite persists original ranges and structured candidate claims: kind,
statement, explicit `Stated`/`Absent` owner and deadline values, and qualifiers.
Only exact structured equality deduplicates; evidence unions and keys stay
disk-backed. Conflicts/non-equal records remain separate, including different
qualifier order. Quotes come from resolved original references, not model
recopying. **Initial extraction is semantically UNVERIFIED.** Valid references
or a completed traversal do not establish entailment or recall. Optional prose
does not rewrite or replace the preserved records; no prose reducer is needed.

`IGenerationBackend.GenerateAsync` and the native adapter return
`GenerationResult(Text, Finish, GeneratedTokens)`. The sampling override
observes actual native EOG, including the case where identical valid text ends
at a length cap without EOG. Failed/cancelled calls retain sampled-token counts
but cannot become successful analyses. Stage reserves are 1,024 extraction and
512 answer tokens in a 4,096 context. `Partial`, `TokenLimited`, `Cancelled` and
`Failed` are honest artifact states, not complete summaries.

Question and summary native diagnostics are separate test cases and requests.
Each summary owns 20 minutes; each question owns 2 minutes (including retrieval),
with a separate 20-minute question-suite cost guard. Question time is not
standalone summary latency. The one authorized cached Phi validation selects
the frozen local-assigned-action question and both original minute summaries;
it does not repeat all previous profile experiments.

```powershell
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --filter "type!=IntegrationTests"
# Only within the explicitly approved one cached validation:
$env:SR_EVAL_PROFILE = "phi-mini"
$env:SR_EVAL_PHASE = "issue-11-ledger-v2"
$env:SR_EVAL_QUERY_ID = "local-assigned-action"
$env:SR_EVAL_RUN_DIRECTORY = "<private-local-report-root>"
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --filter "FullyQualifiedName~RealModelTests|purpose=PhiMiniNative"
```

Raw source/prompt/ledger/coverage traces remain local; do not upload them.
The x64 workflow runs deterministic v2 tests and real native SQLite persistence
without weights, retaining its existing native-extension checks. Native x64
model inference, capture/offline/release evidence and human profile approval
remain separate gates. See [pipeline-validation.md](pipeline-validation.md) for
the exact current outcome and strict quality evidence.

## Historical v1 profile records

The following sections retain the original measured settings, commands and
rejections at their recorded commits. They are not the v2 runtime instructions;
do not reinterpret their budgets/results as measurements of the new pipeline.

## Issue #10: separately approved Phi-4-mini comparison

[phi-mini-candidate-profile.json](phi-mini-candidate-profile.json) pins only the
approved Unsloth conversion of **3.8B Phi-4-mini-instruct**, not full 14B Phi-4
or Phi-3.5. It preserves both Qwen profiles and the approved Nomic embedding.
The actual GGUF and Microsoft card use
`<|system|>…<|end|><|user|>…<|end|><|assistant|>` without added newlines.
Native tokenization adds neither BOS nor EOS; EOS is 200020 (`<|end|>`), not
the distinct padding token. Microsoft’s greedy example is used, not Qwen3
ChatML/non-thinking sampling. Existing 4,096/768/zero-history budgets, grammar,
literal reference-union validation and 20-minute per-language safe-boundary
diagnostic cancellation remain. No model/template/source/output repair.

```powershell
.\feasibility\Models\DownloadApprovedModels.ps1 -Cache $env:SR_APPROVED_MODEL_DIRECTORY -ProfileFile phi-mini-candidate-profile.json
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --filter "purpose=PhiMiniNative"
$env:SR_EVAL_PROFILE = "phi-mini"
$env:SR_EVAL_PHASE = "issue-10-phi-mini"
$env:SR_EVAL_SKIP_CANCELLATION = "1"
$env:SR_EVAL_RUN_DIRECTORY = "<approved-private-report-root>\runs"
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RealModelTests"
```

The one bounded comparison and exact quality gaps are recorded in
[phi-mini-comparison.md](phi-mini-comparison.md). Do not rerun it or infer
production acceptance from native compatibility. Raw evidence remains private.
The full-owner mutation run uses `--test-case-filter "type!=IntegrationTests"`
to avoid native generations per mutant; native smoke evidence is retained
separately, with all handwritten production sources still in mutation scope.

## Issue #9: separately approved bounded alternate

The single approved Qwen3-8B Q4_K_M file is pinned separately in
[qwen3-candidate-profile.json](qwen3-candidate-profile.json). It does not replace
the rejected Qwen2.5 profile or the approved Nomic embedding. No other weights
or production changes are authorized.

```powershell
.\feasibility\Models\DownloadApprovedModels.ps1 -Cache $env:SR_APPROVED_MODEL_DIRECTORY -ProfileFile qwen3-candidate-profile.json
$env:SR_EVAL_PROFILE = "qwen3"
$env:SR_EVAL_PHASE = "issue-9-qwen3"
$env:SR_EVAL_SKIP_CANCELLATION = "1"
# Optional: absolute, private external report directory, not a model directory.
$env:SR_EVAL_RUN_DIRECTORY = "<approved-private-report-root>\runs"
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "FullyQualifiedName~RealModelTests"
```

The alternate uses the official non-thinking ChatML assistant prefill
`<think>\n\n</think>\n\n`, plus `/no_think`, not stripped generated reasoning.
The final serialized prompt includes that prefix in its actual token budget.
Sampling differs deliberately from old greedy decoding: temperature 0.7,
top-p 0.8, top-k 20, min-p 0, presence penalty 1.5, seed 42. Other pinned
LLamaSharp defaults (including a 64-token penalty window) are recorded in the
profile. Context remains 4,096, output reserve 768 and history zero; no YaRN,
32k thinking output, context shifting, identity repair or relaxed reference-union
validation. The alternate diagnostic has a 20-minute per-language cancellation
request limit at the existing safe native boundary, not an immediate abort promise.
See [qwen3-comparison.md](qwen3-comparison.md) for the bounded comparison and gaps.

**Latest follow-up: reject the current generation profile for evidence-preserving
recursive analysis.** The original twenty-excerpt minute trees still lose required
evidence after stronger prompts and grammar. See
[minute-v2-rejection.md](minute-v2-rejection.md) for exact failed ledger obligations,
the improved native/transport seams, mutation 86.95652173913044% (still failing),
and persistent private evidence paths. No issue-completion or human-entailment
approval is claimed.

**Status: exact files approved for local evaluation; issue #4 is NOT complete.**
The user approved the three pinned files on 2026-11-22. This approval does not
select a production library/model or establish a supported resource profile.
All work is isolated here; no application, solution or product feature changes.

## Approved exact model inventory

Approval covers **only the three files** in [candidate-profile.json](candidate-profile.json),
with its exact revision-qualified URLs, byte counts and SHA-256 values:

| Role | Publisher / revision | File | Bytes |
|---|---|---|---:|
| Generation, Q4_K_M | Qwen / `bb5d59e06d9551d752d08b292a50eb208b07ab1f` | `qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf` | 3,993,201,344 |
| Generation, Q4_K_M | Same revision; required second shard | `qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf` | 689,872,288 |
| Embedding, Q8_0 | Nomic / `ffbcf4c99e5d617dda10ec8c0e9f75754b0cbb80` | `nomic-embed-text-v2-moe.Q8_0.gguf` | 512,225,120 |

**Total download/installed file bytes: 5,195,298,752.**
Set `SR_APPROVED_MODEL_DIRECTORY` to the approved shared cache outside the
worktree. The environment variable is a local run instruction; no per-machine
cache path is committed in the model profile.
Staging, caches and results need additional disk; file bytes are not a RAM budget.
The explicit operational `DownloadApprovedModels.ps1 -Cache <approved-cache>`
command verifies size/SHA-256 and resumes only those files, with an owned lock
at the absolute cache path. No automatic startup fetch, model update or telemetry
is implemented.
Do not interpret a test command, profile JSON or upstream quickstart as download
authorization. Approval or an explicitly approved exact local import is required
before model runs.

Both publishers declare **Apache-2.0**. Redistribution permits use/modification,
including commercial use, subject to retaining the license and applicable
copyright/attribution/NOTICE material and identifying modifications. Qwen supplies
an explicit LICENSE; Nomic's pinned GGUF model card declares the license.
The backend/library declare MIT; retain their notices too. These are publisher
license declarations, not legal approval or permission to redistribute any corpus.
Final library/profile/redistribution approval remains the human gate.

## Why this one profile

- Qwen2.5-7B-Instruct's official GGUF card explicitly lists English and Portuguese
  among 29+ languages despite its `en` metadata tag. Q4_K_M is a reasonable
  CPU/file-size starting point, not proof of grounding or Brazilian Portuguese
  quality. This is generation only, separate from speech recognition.
- Nomic **v2-MoE**, unlike the older English-focused embedding profile, explicitly
  includes Portuguese and English and publishes its own llama.cpp GGUFs.
  Q8_0 keeps the embedding quantization conservative for the initial bilingual
  comparison; it is not an accepted production quality threshold.
- A community multilingual-e5-small GGUF inspected during research targets a
  CrispEmbed/XLM-R fork. Its existence does not prove compatibility with this
  LLamaSharp backend, so it is not the proposed download.
- GPU, alternative Lite/Pro profiles, packaging and 12-hour stress runs are not
  part of this bounded baseline.

## Native API evidence

Pinned direct dependencies: `LLamaSharp` and `LLamaSharp.Backend.Cpu` **0.30.0**,
with exact version ranges and committed transitive lock files. The upstream
release maps to llama.cpp
`815a2a5915f22ce6a760c676389c5dfe8535c08f`; LLamaSharp source tag `v0.30.0`
resolves to `d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f`.

The restored CPU package contains PE `AA64` DLLs. A real model-free native probe
loaded `llama.dll`, `ggml.dll`, `ggml-base.dll`, and `ggml-cpu.dll` from the test
output's `runtimes\win-arm64\native` directory in an **ARM64 process on ARM64
Windows**, with `llama_supports_gpu_offload() == false`.
This establishes the in-process ARM64 loader/API boundary. By itself it does **not**
establish that models load, compute correct vectors, fit RAM, or
meet offline/capture/latency requirements. The subsequent approved ARM64
model run is documented in `real-model-validation.md`; x64 real-model execution
remains unmeasured.

`NativeCpu` configures CUDA/Vulkan off and automatic backend fallback off before
calling any LLamaSharp native API. Model parameters also set zero GPU layers,
no KQV offload and no operation offload.
`LLamaWeights.LoadFromFileAsync`, `StatelessExecutor.InferAsync`, and
`LLamaEmbedder.GetEmbeddings` compile against the pinned package.
The pinned native source implements `nomic-bert-moe` and automatic split-file
loading from shard zero. Both Qwen shards must be adjacent and verified before
load; no concatenation or merged-file download is requested. Actual split-model
loading still requires the approved run.

## Context, tokenizer and embedding constraints

| Concern | Initial experimental setting / constraint |
|---|---|
| Generation context | 4,096 tokens; no YaRN/extrapolation or context shifting |
| Generation output | Baseline 512; authorized diagnostic runner uses at most 768 non-EOG sampled tokens per call; truncated/malformed JSON fails explicitly |
| Prompt budget | Count the **actual final ChatML prompt**; prompt + output must be strictly below 4,096, leaving at least one spare token |
| Discussion history | Zero in this harness. A future experimental history cap of 512 would come out of the prompt budget, not extend context; Q16/history poisoning is unmeasured |
| Source/citation budget | Whatever remains after measured instructions, serialized identity, question, separators, special tokens and output reserve; no characters-to-tokens estimates |
| Generation tokenizer | GGUF-embedded Qwen tokenizer, native `Tokenize(..., add_bos: true, special: true, UTF8)` convention shared with the stateless executor |
| Chat template | Explicit no-tools/no-history Qwen ChatML system/user/assistant prefix; do not apply a second template |
| Embedding architecture | Non-causal `nomic-bert-moe`, 475M total / ~305M active parameters, 12 layers, 8 experts with top-2 routing |
| Embedding context | **512 total native tokens**, including task prefix and BOS/EOS; do not borrow generation context or the base config's 2,048-position value |
| Query prefix | `search_query: `, unchanged for English and Portuguese |
| Document prefix | `search_document: `, unchanged for English and Portuguese |
| Pooling / vector | Mean pooling, exactly one sequence vector, full 768 dimensions; no Matryoshka truncation |
| Normalization | L2 vectors, cosine/dot retrieval; reject zero/non-finite/wrong-size vectors |
| Embedding batch | `BatchSize = UBatchSize = 512`, one complete sequence; do not split a non-causal sequence into smaller decode batches |
| CPU threads | Four inference and batch threads; generation batch/ubatch 128 |
| Sampling | Seed 42, temperature 0, default other pipeline parameters pinned by package version; no cross-machine bitwise reproducibility claim |

LLamaSharp 0.30.0's embedder implementation L2-normalizes its returned vectors,
although its XML remarks say they are not normalized. The harness normalizes
validated vectors independently before cosine comparison. Actual backend
tokenizer, vocabulary/BOS/EOS parity and vector behavior remain a real-model
gate. The base embedding tokenizer is XLM-R/SentencePiece-derived, with
`<s>`/`</s>` special tokens; inspect the actual embedded GGUF metadata after load.
No separate tokenizer or remote Python model code is downloaded.

Qwen's card distinguishes 32,768 native context from advertised 131,072
extrapolation; neither is a native-memory guarantee. With 28 layers, 4 KV heads
and 128 head dimensions, an **illustrative F16 K/V calculation** at 4,096 tokens
is 234,881,024 bytes. That excludes tensor mappings, graph/batch/context buffers,
metadata, managed memory and allocator overhead; it is NOT a measured peak,
preflight guarantee or approved RAM allowance. All MoE expert weights still
consume storage/model memory, not merely the active parameter subset.

Actual unique source tokens, largest prompt/excerpt, overlap amplification,
load/peak native-inclusive memory, latency, and per-phase disk observations remain
**unmeasured**. Do not claim a 16 GB machine fits this profile from file size.

## Harness boundaries and commands

From the dedicated worktree root:

```powershell
# Model-free suite, including the native loader probe; no model files needed.
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type!=IntegrationTests"

# Only the actual native model-free loader boundary.
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type=NativeProbe"

# Configure SR_APPROVED_MODEL_DIRECTORY to the user-approved external cache first.
# Only AFTER exact file/size approval; never run this at application startup:
.\feasibility\Models\DownloadApprovedModels.ps1 -Cache $env:SR_APPROVED_MODEL_DIRECTORY
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type=IntegrationTests"
```

On a fresh authorized checkout, restore the committed manifests/locks before
using `--no-restore`; use `dotnet restore` on the isolated test project with
`--locked-mode`. Never restore/build or add this harness to the product solution
as a way to implement the feature.

- Public feasibility seams: generation/map/reduction, fresh-evidence answers,
  and embedding/retrieval. Unit doubles prove structural behavior, **not**
  semantic entailment, model quality or native cancellation.
- The test project links #3's seeds, manifest and queries and references its
  generator without editing them. It reconstructs only each 20-excerpt minute
  case in memory and verifies its frozen original transcript SHA-256.
  Source text, accents, timestamps and exact successful-attempt identity remain
  unchanged. Unicode-preserving, HTML-safe JSON escaping protects prompt delimiters without rewriting
  source identity; tokenizer-internal processing is a separately pinned concern.
- The native sampler constrains candidate JSON and short `sNNNNNN` citation
  syntax with GBNF. Semantic support and source-ID existence still require
  validation; generation cap exhaustion remains an explicit malformed-output
  failure, never a repaired partial success.
- Pairwise recursive synthesis retains original source references and supplies
  original excerpts alongside candidate text. Coverage tracks traversal, not
  proof that decisions survived or that a cited statement entails a claim.
  This **small-fixture** implementation retains its sources/nodes in memory and
  fails over-budget prompts; it is not a bounded-memory arbitrary-length pipeline.
- Local questions use top-k=3; global questions explicitly use all 20 original
  excerpts. Top-k is never labelled exhaustive. Actual claim support, contradiction,
  deduplication, Portuguese fluency and unsupported-question behavior require the
  #3 rubric and bilingual review.
- Model-free cancellation tests verify before/after-call safe boundaries and no
  next call/no completed partial result. Native decode/encode, context allocation
  and initial generation prefill may not acknowledge cancellation immediately.
  Load uses the library's progress callback where supported; no latency is claimed.
- Real probes execute heavy calls serially. There is **no recording scheduler**,
  capture adapter or production background worker here. A future coordinator must
  refuse new inference calls while recording, while honestly handling an existing
  native call; thread count alone does not establish capture integrity.
- Missing files, wrong byte counts/SHA-256, native load errors, context exhaustion,
  malformed/empty output and invalid embedding vectors fail explicitly. No cloud,
  alternate model/device or retry fallback is implemented.

Real probes, once authorized, retain raw prompts, model outputs, vectors, token
counts, coverage trees, UTC/monotonic events and process-counter snapshots under
ignored `runs\<run-id>`. These are private local traces, not issue attachments.
Process working/private/lifetime peaks include native allocations but do not
attribute them. Snapshots are not the #3 100 ms observer, ETW native attribution,
phase-isolated peaks or first-usable-answer latency. The runner now also records
nominal 100 ms **in-process** diagnostic samples and their actual timing gaps,
not an independent observer. The runner deliberately leaves
semantic scores null; a test pass only means structural execution, not rubric
acceptance. Each language test reloads model weights in the same test process:
neither a fresh application process nor OS-cache-cold is claimed.

## Primary evidence

All accessed on 2026-11-22; only public metadata/source text was fetched.

- [Qwen pinned file metadata](https://huggingface.co/api/models/Qwen/Qwen2.5-7B-Instruct-GGUF/revision/bb5d59e06d9551d752d08b292a50eb208b07ab1f?blobs=true),
  [official pinned card](https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/blob/bb5d59e06d9551d752d08b292a50eb208b07ab1f/README.md),
  [LICENSE](https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/blob/bb5d59e06d9551d752d08b292a50eb208b07ab1f/LICENSE).
- [Nomic pinned file metadata](https://huggingface.co/api/models/nomic-ai/nomic-embed-text-v2-moe-GGUF/revision/ffbcf4c99e5d617dda10ec8c0e9f75754b0cbb80?blobs=true),
  [official pinned GGUF card/license/prefixes](https://huggingface.co/nomic-ai/nomic-embed-text-v2-moe-GGUF/blob/ffbcf4c99e5d617dda10ec8c0e9f75754b0cbb80/README.md),
  [base tokenizer configuration](https://huggingface.co/nomic-ai/nomic-embed-text-v2-moe/blob/1066b6599d099fbb93dfcb64f9c37a7c9e503e85/tokenizer_config.json).
  The currently inspected base revision is not proof of the converter's exact
  original base revision; actual GGUF identity is the approved file SHA-256.
- [LLamaSharp release/version map/MIT](https://github.com/SciSharp/LLamaSharp/blob/v0.30.0/README.md),
  [CPU package layout](https://github.com/SciSharp/LLamaSharp/blob/v0.30.0/LLama/runtimes/build/LLamaSharp.Backend.Cpu.nuspec),
  [embedder implementation](https://github.com/SciSharp/LLamaSharp/blob/v0.30.0/LLama/LLamaEmbedder.cs),
  [stateless executor](https://github.com/SciSharp/LLamaSharp/blob/v0.30.0/LLama/LLamaStatelessExecutor.cs).
- [Pinned native MoE implementation](https://github.com/ggml-org/llama.cpp/blob/815a2a5915f22ce6a760c676389c5dfe8535c08f/src/models/nomic-bert-moe.cpp),
  [split loader](https://github.com/ggml-org/llama.cpp/blob/815a2a5915f22ce6a760c676389c5dfe8535c08f/src/llama-model-loader.cpp).

See [validation.md](validation.md) for historical model-free results and
[real-model-validation.md](real-model-validation.md) for observed native
resources, bilingual successes/failures, tokenizer fingerprints and full criterion
gaps. CRAP max 7 passes; mutation 42.931937172774866% fails. Raw traces stay local
under ignored `runs`; #4 is not complete.
