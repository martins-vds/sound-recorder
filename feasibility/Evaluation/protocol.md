# Protocol sr-eval-v1

Implements issue #3's evaluation artifact against design Q2–Q4, Q8–Q10,
Q15–Q18, and Q22, with lifecycle probes from Q19–Q21. It does not approve
technology, model downloads, numeric budgets, or application implementation.

## 1. Freeze the run

1. Generate and verify the corpus. Record git commit, protocol version,
   manifest/queries/chunk-plan SHA-256 and all selected transcript hashes.
   References are `(successfulTranscriptionAttemptId, transcriptSha256,
   excerptId)`; never identify analysis merely by recording or filename.
2. Record the model profile only after explicit model approval/import: generation
   and embedding roles separately; repository/revision, all file hashes, license,
   quantization, exact tokenizer implementation/version/vocabulary/hash,
   special-token/BOS/EOS rules, context size, pooling, thread count, batch sizes,
   sampling parameters/seed, inference backend, package/native-DLL hashes and RID.
   An installed transcription model does not imply approved analysis models.
3. Count actual tokenizer output for source text and for the actual serialized
   chunk prompts, history, citations, and recursive nodes. Report sum of source
   tokens, largest excerpt, largest prompt, total processed prompt/output tokens,
   and overlap amplification separately. Chunk token totals are not a unique
   source token count. Record separators and special tokens. If an excerpt cannot
   fit, fail explicitly or record a source-preserving split with exact offsets;
   no truncation. Token fields remain null until this run exists.
4. Do not strip Portuguese accents, fold case, change punctuation, translate, or
   retime sources. Canonical corpus bytes are UTF-8/no BOM/LF, compact JSONL with a
   final LF per record. The JSON text value is the original authored excerpt.
   No Unicode normalization is applied at runtime; any evaluation-only Unicode
   normalization is separately declared and may not change citation identity.
5. Start with isolated local directories under `feasibility\Evaluation\runs`.
   Record available disk, filesystem/allocation unit size, quotas, and baseline
   caches/results. Never use production data or another ticket's cache directory.
   Keep results and traces private; no issue attachments or external evaluators.

## 2. Native execution matrix and reference profiles

Require **real native** Windows 11 `win-x64` and Windows 11 `win-arm64`, CPU-only
baseline, each with at least 16 GB installed RAM. Record exact processor model,
physical/logical cores, installed/available RAM, OS edition/build, process and
OS architecture, power mode, storage model/type/free bytes, SDK/runtime versions,
security software, temperature/throttling and competing load. Give each measured
configuration a stable anonymous profile ID; omit hostname/user identifiers.
Sixteen GB is the design's profile floor, **not proof a model fits**.

| Profile | Required native OS/process | RAM | Current evidence |
|---|---|---|---|
| reference-x64 | Windows 11 / x64, CPU | >=16 GB; exact configuration recorded at run time | Hardware inventory/native model run not established in this task |
| reference-arm64 | Windows 11 / ARM64, CPU | >=16 GB | Available host inventoried below; native model run not performed |

Available local host inventory, observed on 2026-11-22 (not a model-performance
or supported-resource-profile result): Snapdragon X 12-core X1E80100 @ 3.40 GHz,
12 physical/12 logical cores; 33,895,186,432 bytes physical RAM reported by
`Win32_ComputerSystem`; Windows 11 Enterprise 10.0.26300/build 26300; OS and
current PowerShell/.NET process architecture both ARM64. The generator and focused
managed tests execute here. This establishes one actual host, **not both RIDs**.
Power/storage/free-RAM conditions must still be captured for future inference runs.

Do not invent specific reference hardware or infer ARM64 success from x64,
emulation, cross-compilation, a managed test, or package metadata. Packaging
identity and both actual native executions must be retained separately.
Unavailable hardware, native DLLs, or approved models block the corresponding
execution cell; mark `blocked`, not `pass`. No model is approved/downloaded here.

For each native profile and candidate model/index configuration, execute all four
sizes in both languages, local questions, all-decisions, whole-summary, unsupported
questions, follow-up probes, and the overlap/incomplete plan. Exercise cold and
warm states separately. Repeat measured timing runs three times and retain all
raw observations, median and range; three is a protocol sampling choice, not an
approved latency threshold. Record deterministic-double runs in a separate
matrix, never pool them with real-model grounding or native resource results.

## 3. Process/native memory and disk

### Offline boundary before any performance run

Install only already-approved local model packages and pinned dependencies before
isolating the reference machine. No automatic model/update fetch is permitted.
On a dedicated test machine, disable outbound network for the evaluated process
and all native children (or disconnect networking), then run every language/size
cell without accounts or cloud fallback. Record the exact isolation method.
Use Windows Filtering Platform connection auditing/ETW (including blocked
connect attempts) and packet capture such as pktmon for the test interval;
pin observer versions and PID correlation. Capture DNS/HTTP/model-fetch attempts
as well as successful traffic. “No packets” alone does not establish absence of
attempted network access. Repeat a warm request and restart while still isolated.
Fail the offline invariant for unsolicited download/update/telemetry attempts or
cloud fallback even when a firewall blocks them. Missing observer privileges or
trace loss is an explicit evidence gap. Do not modify shared-host firewall rules
or disconnect another user's machine. Do not perform these actions in this ticket.

Use an independent observer, keyed to the exact process ID and process-start
identity, from before startup until shutdown. Use a monotonic high-resolution
clock and sample every 100 ms, logging actual intervals and missed samples.
This interval is measurement granularity, not a memory/cancellation budget.

- On Windows, record `GetProcessMemoryInfo` / `PROCESS_MEMORY_COUNTERS_EX`
  WorkingSetSize, PeakWorkingSetSize, PrivateUsage and PeakPagefileUsage;
  .NET `Process.PeakWorkingSet64`, `WorkingSet64`, `PrivateMemorySize64` and
  `PeakPagedMemorySize64` provide accessible observations of these process
  counters. Call `Refresh` for sampled values. Preserve OS high-water counters
  separately from sampled maxima, and snapshot immediately before normal exit.
- Record total process committed/private memory and resident peak including
  native backend allocations and memory-mapped model pages. Private bytes do not
  account for all mapped/shared resident pages; working set alone is not total
  commit. Report both, baseline at each phase, and absolute maxima rather than
  only deltas. Include native worker processes, if any, separately and a sampled
  aggregate; do not sum individual peaks at different times as simultaneous use.
- Managed GC heap/counters are supplementary. **Do not call managed heap peak
  total/native peak or subtract GC heap from private bytes to infer native use.**
  To attribute native allocations, take a separately labelled diagnostic run
  with Windows Performance Recorder/ETW heap and VirtualAlloc tracing enabled
  before process start, and inspect committed reservations and mapped model
  residency in WPA/VMMap. Pin the tracing profile/tool version. Retain trace loss,
  rights requirements, and overhead notes. If tracing is unavailable, total
  process memory still includes native use, but native attribution remains
  unavailable. Diagnostic timings are not pooled with uninstrumented latency.
- Report phase peaks for load, streaming/indexing, each recursive level, local
  answer, global answer, recording contention, cancellation and post-unload.
  A sampled maximum is a lower bound for unsampled spikes; OS high-water values
  cover the process lifetime. Run each phase in a separate fresh process when a
  phase-specific high-water value is required.

Disk observations: enumerate all files in the isolated run directory before,
throughout, and after each phase; record logical bytes plus allocated size
(`GetCompressedFileSizeW` or equivalent), peak temporaries, WAL/journal/checkpoint,
index/embedding data, committed results/history, and retained partial chunks.
Model files are a separate installed footprint. Record free-volume bytes before/
after and sampled minimum. Periodic directory totals are sampled lower bounds;
use file-I/O tracing for transient peaks when needed. Disk estimates and actual
peaks must be compared, not described as guaranteed bounds.

Before running, record an estimate broken down by model/index/cache/output/
temporary components and contingency. Do not choose an approved headroom ratio.
Inject low-space/exhaustion using an isolated quota-constrained test volume
or failing filesystem adapter; never fill the host disk. Record explicit failure,
no success flag, no silent deletion/fallback, and only user-approved removal of
rebuildable cache. Audio, transcript, completed summary and discussion history
must remain unchanged.

## 4. Times and cold/warm definitions

Record timestamped events with `Stopwatch.GetTimestamp` (or equivalent) and
frequency; retain raw tick values plus UTC for correlation. No wall-clock-derived
duration or modeled/token-throughput estimate substitutes for elapsed time.

- **Application-cold:** new process, no compatible per-transcript index/result,
  no model loaded. Record request -> first supported claim/citation visible and
  request -> complete answer. Break out preflight, model load, tokenization,
  indexing, retrieval/coverage scan, prefill, first token, full generation,
  grounding/citation validation and final publication. First token alone is
  not first usable answer. A refusal's first honest limitation is a usable answer.
- **Index-warm/model-cold:** retain a verified compatible completed index but use
  a fresh process. Do not reuse an answer cache.
- **Model/index-warm:** same process, approved models resident, compatible index
  complete; a new request still obtains fresh source evidence. Distinguish result
  cache hits from fresh model answers.
- **OS-cache-cold:** a reboot on a dedicated reference machine, with its exact
  procedure documented, is a separate condition. Do not flush shared machine
  caches. A new process is not evidence of cold OS file cache.

Measure indexing request -> **complete** durable compatible index (all source
ranges covered) and summary request -> **complete** grounded recursive root.
Record every chunk/reduction node, parent IDs, input/source coverage, reference
union, prompt tokens, output tokens, start/end ticks, commit and outcome.
Use `chunk-plan.json` for a deterministic overlap/reduction probe. Confirm all
14,400 stress excerpts are traversed, all 720 batch decision IDs survive reduction,
and all leaf-to-root references resolve to immutable source excerpts. If model
output/context cannot represent the requested enumeration, record incomplete/
resource-limited output rather than silently pass a compressed incomplete answer.
Also record the candidate's own chunk plan; never quietly replace one plan with
another or compare timings across plans as equivalent work.

## 5. Recording priority and cancellation

Run a deterministic scheduler/inference-double probe first (behavior only), then
a real native inference/capture contention run on an isolated interactive machine.
No harness double proves device integrity or native cancellation.

- Compare capture alone against capture during indexing, an already-running
  analysis call, and pending next calls. Record callback intervals/missed
  deadlines, buffer overruns/underruns, dropped frames, actual PCM frame count,
  start/pause/resume/stop UI input-to-acknowledgement, UI heartbeat gaps, CPU load,
  and process peaks. Use the same selected device/source and a reproducible local
  audio stimulus; record its identity/license/hash and captured duration.
  Validate saved WAV frames at 48 kHz, 16-bit PCM with paused intervals absent.
  Record observer overhead and stimulus routing. Corpus timestamps do not
  substitute for this measurement.
- Assert the observable scheduling invariant: while recording is active, no
  **new** analysis native inference call begins; an existing call may finish.
  Source/device fallback or compromised capture must be explicit failure.
  Do not implement that scheduler in this ticket.
- Request cancellation before load, during load, before/inside/after a chunk
  inference call, after a committed chunk, during recursive reduction, before
  root/result commit, and during recording contention. Log request, acknowledgement,
  active-call completion, last safe boundary, last cache commit, and native resource
  release. Measure each latency separately. Do not promise immediate interruption
  or native pause/resume where the backend lacks it.
- Verify no next native call after the cancellation boundary, no partial root/
  answer/index marked complete, and retained completed chunks remain private.
  A process exit or power interruption is not cancellation success.

## 6. Resume, invalidation, ownership and deletion probes

Save completed chunks; kill only the isolated harness process by its exact PID
at recorded boundaries; restart with identical configuration and explicitly
request retry/resume. Check resumed reference/coverage union and final supported
claims against a from-scratch run; compare semantic claims, not stochastic bytes.
Partial child outputs must be recomputed and never treated as complete.

A checkpoint compatibility identity must cover exact successful-attempt ID and
transcript hash, generation/embedding model hashes, tokenizer, native backend/
package versions, prompt/template/schema versions, chunk plan, normalization,
index algorithm/configuration and result schema. Pin checkpoint hashes and
committed covered ranges. Change each compatibility input in a separate probe.
Expected: explicit incompatibility/rebuild, no silent reuse against another
attempt/model/source. Equal audio/recording identity is insufficient.

Delete only isolated rebuildable caches after explicit user choice; verify source
transcript, completed summaries and discussion history hashes unchanged.
Delete the target's analysis deliberately in a separate clone of the run fixture;
check unrelated targets unchanged. Simulate missing audio: citations remain usable,
audio seeking unavailable. Simulate missing transcript: explicit source error,
no swap to another attempt. These are feasibility probes, not new application tests.

## 7. Grounding, global coverage and bounded history

Execute `queries.json` in the transcript's language. Review actual claims under
`scoring-rubric.md`; existence of a citation-shaped string is not support.
Resolve each citation to exact source identity, ID and timestamp/text. Track
coverage separately from supported-answer precision. Repeated blocks permit
equivalent **exact** occurrences of the same common claim; numbered batch claims
require their specific ledger occurrence.

For unsupported global questions, inspect the whole source range or a compatible
complete coverage artifact; never infer absence from top-k. Intentionally omit
the final chunk using the incomplete probe: the answer must explicitly say its
coverage is incomplete even if other claims are correctly cited.

Execute the three-turn follow-up, then repeat with the poisoned assistant turn.
History supplies pronoun context, **not evidence**. Every turn must retrieve fresh
transcript excerpts and cite them, including when the previous answer was wrong.
For the experimental history profile, retain at most the last two completed
turn-pairs plus the current question, bounded further by a declared tokenizer
token cap that fits the candidate's context after source/prompt/output reserves.
Record the actual cap rather than selecting an approved production budget.
Test zero-history, normal bounded history, cap overflow/eviction, expired
antecedent, and switching attempt. Record serialized history bytes/tokens and
truncation decisions; evicted pronouns require clarification, not hallucination.

## 8. Reporting and decision gate

Use `run-template.json`, per-event local JSONL, raw counter samples, coverage
ledger, and claim-scoring rows. Record errors, skipped cells, unavailable values,
native-library/model/hardware blockers and measurement uncertainty. Keep real
model and double evidence distinct. Numerical measurements are observations,
not a product acceptance decision.

No numerical memory/disk/time/capture/cancellation/quality passing budget is
approved by this protocol. Bring exact measured profiles, licensing, packaging
and worst-case observations to issue #1's human technical gate. Ticket #3 provides
the protocol and synthetic corpus; an unexecuted cell is a future execution
blocker, never evidence the candidate passed either RID.
