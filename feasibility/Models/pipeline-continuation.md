# Issue #11 continuation: context grouping and diagnostic closure

This is the historical v3 checkpoint. Its failures and diagnostic uncertainty
are retained below; [the final follow-up](pipeline-quality-followup.md) records
the later passing quality gates and separately approved Portuguese diagnostic.

This continues commit `6522abaed6343fd0d013f3e220dab1ef4458a8bd` in the same
ticket worktree/branch. The earlier v2 report and private raw results remain
unchanged. No application/prototype feature, new weights, push, issue closure
or other agent is involved.

## Tight native replay attempts

The captured Portuguese sixth-call JSON and first-six-call prefix were copied
locally from the original private event stream. The prompt includes the exact
original attempt/hash, s000006 quote/timestamps, 267 prompt tokens and 1,024
output reserve. Native configuration was frozen before changing the pipeline:
the original v2 grammar, seed 42, temperature 0, context 4,096, four threads,
batch/ubatch 128, CPU-only/offload disabled and `ThrowException`.
Later replay uses that frozen configuration directly through the existing
native generation boundary, not the changed v3 grammar/context requests.

The unattended command is the dedicated `purpose=NativeCrashReplay` test with
`SR_CRASH_REPLAY_INPUT`, `SR_CRASH_REPLAY_CONFIG`, approved cache and owned report
directory set. `--blame-crash` collects a local native crash dump if a crash
occurs; `--blame-hang-timeout` bounds this short diagnostic.

| Replay | Actual result |
|---|---|
| Exact sixth call alone | Passed, exit 0, 18 seconds |
| Exact first-six-call prefix | Passed, exit 0, 97 seconds |
| Prefix plus original Coverlet collector | Passed, exit 0, 87 seconds |
| Prefix plus collector and original 100-ms process-memory observer | Passed, exit 0, 93 seconds |

Each replay asserts native EOG completion and valid structured output for the
captured request(s); each preserves before/after records, exit and TRX. No crash
dump was produced because these runs did not crash.

**Phase 1 did not recover a reproducibly red minimized loop.** The original
92/93 aborted native run is still preserved, but it is not a seconds-long
reproducible diagnostic. Accordingly, no ranked causal hypotheses, speculative
native fix, masking catch/skip, or claim that the crash is resolved is made.
The missing reproducible trigger/native fault stack remains a genuine runtime
closure blocker. The full failed broad comparison was not repeated.

## Context-loss closure bugs: actual red-green

At the agreed public seams, an ideal backend recognizes only complete actual
source payloads, not instruction keywords:

- A known Ana/access-log/5-September relationship crosses the 1,024-character
  read boundary. The original implementation produced no complete action;
  `relationship-red.trx` records the empty matching-claim failure.
- Two tiny actual conflicting outage excerpts must be present in the same
  bounded request to produce the qualified report. The original singleton
  implementation produced only a background claim; `conflict-context-red.trx`
  records the failure.
- A backend `InvalidOperationException` must remain observable as that same
  exception, after persisting failure. The original broad catch returned a
  Failed result instead; `fault-red.trx` records the missing-exception failure.

Corrections, now covered by green tests:

1. Token-aware packing groups up to four fresh source ranges / 4,096 source
   characters when final rendered prompt plus stage reserve fits. Questions
   and summary extraction preserve adjacent source context without unbounded
   synthesis or an all-source root.
2. Oversized windows retain bounded, word-aligned overlap (at most 256 UTF-16
   characters), including overlap between token-limited pieces. Unicode
   boundaries and original offsets/timestamps are preserved.
3. Original fixed read blocks are persisted/validated independently from
   overlapping evidence windows. Evidence keys and local reference resolution
   include `(source ID, offset, length)`, avoiding same-offset range ambiguity.
   Unique original coverage uses interval endpoints, never overlapping-length
   sums. Archived v2 evidence-reader compatibility is retained without migration.
4. Expected I/O/data/JSON/SQLite errors have explicit Failed outcomes. Unexpected
   faults persist Failed and **rethrow the original exception**. An unrelated
   cancellation is not reported as user cancellation.

Tests verify exact source substrings, complete relationship state/value, both
original conflict references, unique reconstruction of long Unicode text,
bounded source read requests and source-context packing to independently
controlled token budgets. No model-output truth inference or rewriting is added.

## Meaningful behavior and native quality selection

New public-seam cases cover required/null/empty fields, Topic kind, unknown fields,
reference limits/negative indices, SHA-256 syntax, attempt/language/question
limits, original timestamp failures, missing artifacts and unrelated cancellation.
All retain real persisted artifact reader assertions rather than private queries.

Full-owner CRAP now includes the existing bounded `PhiMiniNative` and
`NativeAdapterSmoke` cases. Those actual native tests cover model/tokenizer/
sampling/embedding/disposal boundaries without running long `RealModelTests`.
The native `InferAsync` zero-coverage score of 12 is resolved by actual coverage,
not method splitting: its reported score is 3.

The operational error family is an explicit fixed type set, not a blanket
swallowing catch. Full-owner CRAP maximum is **7**, with **136 passed** selected
tests (131 deterministic plus five bounded native cases), zero skipped.
Constructors remain excluded by the fork and are not claimed analyzed.

Full-owner mutation retains Complete level, unchanged 99 thresholds and all
handwritten sources. The same bounded native purpose tests are included;
captured crash replay and long comparison tests are not repeated per mutant.
Final full-owner mutation: **421 killed, 80 survived, 13 uncovered, 4 timeout,
0 runtime error; 19 compile errors and 87 tool-default ignored mutants**.
Stryker detects killed plus timeout mutants: **425/518 =
82.04633204633204%**, failing the unchanged strict `>98%` gate (exit 2).
The 4 timeouts are not presented as ordinary assertion kills: native mutations
382/385 remove the explicit context options / replace the grammar with empty
text; overlap mutations 543/551 invert the loop predicate / remove the advancing
statement. Their exact replacements and statuses remain in `timeout-blockers.json`.
The unmutated bounded native tests and overlap cases pass. No timeout result is
used to claim the strict quality gate passed.

| Remaining source | Survived | Uncovered | Timeout |
|---|---:|---:|---:|
| ClaimLedger.cs | 5 | 1 | 0 |
| ClaimValidation.cs | 11 | 2 | 0 |
| LlamaGeneration.cs | 6 | 1 | 0 |
| NativeCpu.cs | 1 | 0 | 0 |
| NativeModelBoundary.cs | 4 | 0 | 2 |
| StreamedGeneration.cs | 53 | 9 | 2 |

The native method's original 18 uncovered mutants are now exercised by actual
bounded native tests; no native-adapter mutant has `NoCoverage`. The remaining
native initialization/template/disposal survivors, evidence-reader compatibility,
record/request/source limits, overlap and prompt/error-reporting mutations are
explicit unresolved blockers, not waived equivalents. No string-constant tests,
handwritten exclusions, threshold drops, suppression or artificial production
method splitting were used.

**#11 remains INCOMPLETE.** CRAP is now passing and the injected context/fault
regressions are corrected, but the original native crash has no reproduced
minimized trigger/fault stack and mutation remains below its mandatory gate.
No claim is made that the remaining mutants are all irreducible, or that the
native runtime fault is fixed; exact unresolved cases are retained for continued
work rather than relabelled as passing model-semantic evidence.

## Persistent evidence

Private root:
`C:\Users\vinima\.copilot\session-state\87f62508-2e44-47cc-80d2-0b9d91f62872\files\issue-11\continuation`

It retains captured/frozen inputs, replay JSONL/exit/TRX/collector evidence,
the three new red-green traces, deterministic regressions, full native-inclusive
CRAP/Cobertura/TRX, full mutation JSON/HTML and exact mutant blockers.
Earlier `files\issue-11` raw reports and v2 historical results are not overwritten.

Initial Phi extraction semantics remain **UNVERIFIED** and the previously
observed model-semantic defects remain separate rejection evidence. Native x64,
offline/capture/release acceptance and production model approval are not claimed.
