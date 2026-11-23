# Issue #9: bounded Qwen3 comparison — reject this measured profile

**The single authorized comparison is complete as a negative experiment.**
Reject this bounded Qwen3-8B Q4_K_M generation profile for the proposed
evidence-preserving analysis contract on the measured machine. Native loading
works, but neither complete minute root exists, Portuguese whole-summary output
is truncated, and accepted outputs contain grounding/coverage failures.
No prompt tuning, output-budget increase, additional candidate or repeated
alternate generations followed these failures. **Issue #4 remains open.**
This is not a rejection of every possible Qwen3 configuration.

## Exact candidate and actual native compatibility

The separately pinned [alternate profile](qwen3-candidate-profile.json) leaves
the rejected Qwen2.5 profile/reports and approved Nomic v2-MoE embedding unchanged.
Only the approved Qwen3 file was downloaded into the existing external cache:

- Official `Qwen/Qwen3-8B-GGUF`, revision
  `7c41481f57cb95916b40956ab2f0b139b296d974`, Apache-2.0.
- `Qwen3-8B-Q4_K_M.gguf`, **5,027,783,488 bytes**.
- SHA-256 `d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785`.
- Exact revision-qualified URL is in the profile. Curl resumed/retried an HTTP
  502 for this same file; final size/hash verification passed. No other weights.

Actual Windows 11 Enterprise build 26300 / ARM64 OS and ARM64 test process,
Snapdragon X X1E80100, 33,895,186,432 physical RAM bytes; .NET SDK 10.0.404,
runtime .NET 10.0.15, LLamaSharp/CPU backend 0.30.0. Both native loads computed:
Qwen3 declares **`general.architecture=qwen3`**, native tensor bytes
**5,021,827,072**; unchanged Nomic declares `nomic-bert-moe`, tensor bytes
505,393,152. These are tensor counters, not process peaks. CPU-only configuration,
four threads, batch/ubatch 128, no GPU/KQV/operation offload or fallback.
Native DLL hashes are retained locally. No native x64 model result is claimed.

Actual Qwen3 GGUF tokenizer: `gpt2`, pre-tokenizer `qwen2`, **151,936**
vocabulary entries; BOS/padding 151643, BOS addition false, EOS 151645.
Typed GGUF value SHA-256 fingerprints:

| Value | SHA-256 |
|---|---|
| Tokens | `e57f98503c6876d5fcb2ba00b832101e87f617961e344456e1ccca9469eac3a8` |
| Token types | `8cd769bdc00bd0abbb5831bbae9eeb19f1aac2a39cfeabb3efb5fb0c8ac27e4b` |
| Merges | `2ff617ef6beefbb249e7980b0d7fd31eedede40d318b4f367342570684649bd7` |
| Embedded chat template | `408b35a3afa91f14a3b1a3027860f75ce241f1d74b58ffb8a7169e33a59fb44b` |

These hash typed GGUF encodings, not Hugging Face tokenizer.json or runtime parity.
The GGUF advertises context 40,960 while its pinned upstream card describes
32,768 native context. Neither advertisement was used as a resource guarantee:
the actual allocated context remained **4,096**.

## Official non-thinking transport and actual budgets

The embedded official template's no-tools/no-history `enable_thinking=false`
branch supplies `<|im_start|>assistant\n<think>\n\n</think>\n\n`.
The adapter uses this exact prefill and `/no_think` in the system message.
It does not generate then strip reasoning, repair citations or apply a second
template. The [pinned official card](https://huggingface.co/Qwen/Qwen3-8B-GGUF/blob/7c41481f57cb95916b40956ab2f0b139b296d974/README.md)
recommends non-greedy temperature 0.7, top-p 0.8, top-k 20, min-p 0 and
presence penalty 1.5 for this quantized non-thinking mode. All were applied,
with seed 42. This deliberately differs from the old temperature-zero profile.
Remaining pinned pipeline defaults, including its 64-token penalty window,
are explicit in the alternate profile.

The original generation JSON grammar, exact immutable attempt/hash references,
source-existence validation and literal child-reference-union rejection remain.
No grammar proves semantic support. All calls reserve 768 output tokens, use zero
history, and count the final serialized prompt/prefill; prompt + reserve must
remain strictly below 4,096, with no source truncation or context shifting.

| Actual token observation | English | Brazilian Portuguese |
|---|---:|---:|
| Unique source text, LF separators and native special convention | 311 | 456 |
| Largest original excerpt | 24 | 36 |
| Largest prefixed Nomic document | 35 | 37 |
| Largest attempted final prompt | 1,206 | 1,368 |
| Sum of started prompt tokens, including repeated sources/reductions | 10,476 | 10,410 |
| Generated non-EOG tokens from completed native calls | 2,692 | 2,600 |

Started-prompt totals are not unique source tokens. Cancelled calls have unknown
partial generated-token totals; the table does not silently treat them as zero.
Portuguese summary exhausted its 768-token output cap, not the 4,096 context.

## Frozen cases, complete traversal and incomplete recursion

The original #3 twenty-excerpt English/pt-BR minute fixtures, all eight queries
per language and `minute-atomic-ledger-v2.json` were frozen before outputs.
Runtime reconstruction verified original transcript hashes
`f5d48c8f7d5d28acd824827cdb9b6139971e259657696fb133178dc9a088f234` and
`1cc4e34686cdb4e9095a2dbc6847ef5af0c2c69b2f8514f243c530e33e5865a2`.
Corpus, queries, timestamps, accents and ledger were not changed.

| Cell | English | Brazilian Portuguese |
|---|---|---|
| Eight local/global queries attempted | 8 | 8 |
| Structurally accepted question candidates | 8 | 7 |
| Whole-summary | Valid JSON, missing required account discussion | Truncated JSON in claim 15; rejected |
| Four five-excerpt map leaves | All completed | All completed |
| Literal leaf reference union | Exact `s000001`–`s000020` | Exact `s000001`–`s000020` |
| First pair reduction | Completed; all ten child references preserved | Cancelled at safe boundary |
| Second pair reduction | Cancelled at safe boundary | Not reached |
| Complete root | **None** | **None** |

The **actual full minute tree** was requested, not replaced with the four-source
proxy. Time-limited failures prevented completing it. Each language requests
cancellation after 20 minutes; acknowledgement follows the existing native safe
boundary. The runner ended after 40m53s: two failed cases, zero skipped.
No incomplete root was published as success. Timeout does not prove that the
unexecuted final root would lose references; it means root preservation is
unestablished. The call ledger records exact expected child unions, the one
completed union, and null output unions for cancelled calls.

### Named atomic-ledger observations — provisional direct reading

- Both languages preserve `logs-owner` / `logs-deadline` (Ana, 5 September 2027),
  checklist owner/deadline absence, and refuse unsupported budget/password
  questions without obeying the quoted instruction. No invented owner/deadline
  was observed in these accepted answers.
- English local date output omits `launch-unresolved`. Portuguese local date
  output also omits it and reverses the disagreement: it describes someone
  disagreeing with the 12 September decision, whereas `s000004` states that date
  as the opposing decision. Both dates appearing is insufficient.
- English local outage answer retains both reports and unverified status.
  Portuguese gives an honest actual-count limitation, but omits explicit
  `reports-unverified` and changes unidentified report **speakers** (`s000019`)
  into unidentified report **origin**. That broader statement is not established.
- English all-decisions emits actions/non-decisions and changes “the report
  says” in `s000011` into **“The pilot had two outages.”** A real citation does
  not support treating an unverified report as external fact. Portuguese is
  overinclusive too and claims lack of full transcript coverage despite receiving
  all twenty excerpts.
- English whole-summary retains log owner/deadline, paper/register/batch decisions,
  checklist/archive-review absence and opposing unverified outage reports.
  It omits **`accounts-discussion`**, despite fresh `s000009`, and repeats topics
  and the same launch decision. Portuguese partial summary contains many of these
  facts but omits account discussion/batch before truncation; it is not accepted.
- All map leaves preserve the twenty literal references and original facts.
  The English first reduction correctly unions repeated launch IDs and preserves
  the first ten references. This improves on the rejected Qwen2.5 first-reduction
  failures, but cannot substitute for either complete root.

`provisional-ledger-review.json` records every frozen obligation against these
summary/map observations. Semantic precision/recall and language-quality scores
remain **blocked pending qualified independent bilingual reviewers**; no formal
entailment, reviewer consensus or acceptance threshold is fabricated.

## Measured latency and native-inclusive memory

| Observation | English | Brazilian Portuguese |
|---|---:|---:|
| Integrity verification + embedding load seconds | 1.452 | 1.519 |
| Integrity verification + generation load seconds | 9.601 | 9.092 |
| Local request → complete candidate seconds | 38.550–53.068 | 40.280–53.354 |
| Global request → outcome seconds, including failed output | 101.825–180.540 | 120.951–230.278 |
| Entire diagnostic seconds, including incomplete tree | 1,211.881 | 1,241.520 |
| OS lifetime peak working-set bytes | 6,466,650,112 | 6,466,650,112 |
| Sampled maximum private bytes | 6,550,396,928 | 6,573,953,024 |
| OS lifetime peak paged-memory bytes | 6,573,953,024 | 6,573,953,024 |
| Post-disposal working-set bytes | 249,925,632 | 143,556,608 |
| Post-disposal private bytes | 237,940,736 | 165,203,968 |
| Nominal 100ms memory samples | 12,119 | 12,415 |
| Maximum sample gap seconds | 0.1774162 | 0.1437545 |

Portuguese ran first in the same test process, so English lifetime peaks can
include it. These are native-inclusive **process** counters, not native-heap
attribution, independent observer evidence, phase-isolated peaks or proof of a
16GB supported profile. Timings are one comparison, not three-run medians,
first-token/first-supported-UI-answer latency or complete-index/root duration.
The host is shared, Balanced power mode; OS file-cache coldness, competing load,
thermal behavior and resource thresholds are not certified. Cancellation request
clock events were not separately recorded for the diagnostic timer; no exact
request-to-acknowledgement latency is claimed. Earlier cancellation observations
remain intact rather than being repeated.

## Strict owning-source quality — still incomplete

Focused explicit-alternate transport/sampling test: red compilation for the
missing alternate seam, then green. Affected model-free regressions: **75 passed**;
bounded quality selection including two existing tiny native adapters:
**77 passed**, zero skipped. Release compilation has no warnings/errors.

All seven handwritten production files of `Models.Harness` remain in full
CRAP/mutation scope. Fork `Microsoft.Crap4CSharp` 1.1.0, documented CLI checked:
every reported executable member numeric and **CRAP <8, maximum exactly 7**.
Constructors remain the fork's exclusion. Quality test selection is
`type!=IntegrationTests|purpose=NativeAdapterSmoke`; the long failed comparison
is retained separately, not disguised as passing quality tests.

Stryker.NET 5.1.0, Complete level, two workers, unchanged strict thresholds:
**191 killed, 32 survived, 1 timeout, 1 uncovered**, no runtime errors.
Detected/evaluated = **192/225 = 85.33333333333333%**, **fails >98%**.
Sixteen compile errors and 49 tool-default block-filter ignored mutants are
separate. The prior **86.95652173913044% failure remains retained**.
No handwritten source exclusion, threshold reduction or equivalent suppression.
Twenty prompt/reference `GenerationProbe` blockers, seven generation
transport/profile blockers (one uncovered), one CPU initialization blocker and
five native-adapter blockers remain explicitly listed. Constant prompt-string
assertions were not used to manufacture semantic coverage, and no alternate
minute generation was run per mutant. Implementation-quality completion is
therefore not claimed, even though this negative experiment is finished.

## Exact persistent local evidence and handoff

Private root outside the worktree:

`C:\Users\vinima\.copilot\session-state\87f62508-2e44-47cc-80d2-0b9d91f62872\files\issue-9`

- `runs\issue-9-qwen3-20261123T032101-pt-BR-746dd6d9003f427982567657a2dc4fcf\events.jsonl`
- `runs\issue-9-qwen3-20261123T034143-en-30f8a2ea61724befb1e93012f59696e1\events.jsonl`
- `runs\qwen3-observations.json`, `qwen3-candidates.json`,
  `qwen3-call-ledger.json`, `provisional-ledger-review.json`.
- `runs\frozen-run-inputs.json`, `minute-atomic-ledger-v2.json`,
  `installed-qwen3.json`, `tokenizer-metadata-hashes.json`,
  `arm64-native-dll-identities.json`, `official-qwen3-card.md`.
- `runs\tests\qwen3-comparison.trx`, `native-comparison-console.txt`.
- `quality\strict-quality-summary.json`, `crap-console.txt`, `coverage`,
  `qwen3-mutant-blockers.json`, `mutation-qwen3\reports\mutation-report.json`
  and `mutation-report.html`; red/green/regression evidence under `quality\tests`.

Commands are in [README.md](README.md). Raw prompts/candidates and quality reports
remain local; only this synthetic diagnostic report/profile and code are committed.
No production edits, #5/#6 work, twelve-hour test, new agents, push or issue closure.
Offline auditing, real x64 model/capture tests, human technology/threshold approval
and formal entailment remain blocked. Parent owns integration/graph decisions.
