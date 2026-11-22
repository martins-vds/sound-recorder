# Issue #3 completion evidence

## Isolation and scope

Started on `feasibility/ticket-3-protocol` in
`C:\Repos\sound-recorder-feasibility-3`, clean, with HEAD and merge-base both
`aa2cc0deb986d95d82f69bdbc82adf0ef838ab58`, then the integration tip.
All ticket-owned files are under `feasibility\Evaluation`. No application,
solution, product feature specification or requirements baseline was edited.
Current integration is merged into this branch before handoff, never vice versa.
No push, ticket closure, public report, or model download is authorized or done.

## Actual validation

Commands below were run from this dedicated worktree root:

```powershell
dotnet test .\feasibility\Evaluation\Generator.Tests\Evaluation.Generator.Tests.csproj --filter FullyQualifiedName~Write_RepeatedBilingualSeed
dotnet test .\feasibility\Evaluation\Generator.Tests\Evaluation.Generator.Tests.csproj --filter FullyQualifiedName~Write_UntimedTranscript
dotnet test .\feasibility\Evaluation\Generator.Tests\Evaluation.Generator.Tests.csproj --filter "FullyQualifiedName~Write_InvalidBlockCount|FullyQualifiedName~Write_EmptySeed"
.\feasibility\Evaluation\Generate.ps1
dotnet test .\feasibility\Evaluation\Generator.Tests\Evaluation.Generator.Tests.csproj --no-restore --logger "trx;LogFileName=final-green.trx" --results-directory .\feasibility\Evaluation\runs\tests --collect "XPlat Code Coverage" -v minimal
```

Red/green observations:

- Bilingual streaming/timeline test: red with `NotImplementedException`; green
  after canonical JSONL generation.
- Untimed-reference test: red because synthetic timestamps were incorrectly
  present; green after null timestamps were emitted.
- Input validity: zero/negative blocks and empty seed produced no error before
  validation; all three red cases passed after validation was added.
- Eight artifact-integrity cases verify frozen SHA-256/bytes, all original
  excerpt IDs/timestamps, final duration/segment count, and every numbered golden
  claim's exact excerpt text/ID/timestamp. One asset-identity check verifies the
  frozen protocol/generator/query/license hashes.
- Final focused suite: **14 passed, 0 failed, 0 skipped**. The Release generator
  build succeeded with zero warnings/errors.
- The available host was actually inventoried: native ARM64 Snapdragon X
  X1E80100 (12 cores), Windows 11 Enterprise build 26300, 33,895,186,432 physical
  RAM bytes. This is managed harness execution, not native inference feasibility.
- Independent regeneration matched all eight transcript hashes and all eight
  golden coverage hashes. The large files are reproducible local outputs;
  the committed seeds, generator, queries, coverage-generation rules and manifest
  suffice to reconstruct them without any model.

Local TRX/coverage output stays in `runs\tests`; the earlier red/green TRX files
remain there. Coverlet observes 100% line/branch coverage for `CorpusGenerator`;
the external console entry point, exercised through materialization, is not
covered by the in-process coverage run. This is not a whole-application coverage
or performance claim.

Production CRAP and mutation gates were not run: no handwritten production member
or production project changed, and these harnesses are not added to the product
solution. No CRAP or mutation-score pass is claimed for the harness or application.
The fork's `crap4csharp` CLI was not available on PATH. Promoting harness behavior
to production would require the repository's strict full-owner CRAP/mutation
gates, as well as separate feature authorization.

## Criterion-by-criterion status

| Issue #3 criterion | Artifact status | Evidence / remaining execution boundary |
|---|---|---|
| Synthetic or licensed English and Brazilian Portuguese | Complete | Twenty original excerpts in each seed, CC0-1.0/provenance declaration; no third-party corpus |
| Concrete corpus/protocol/manifest/rubric | Complete | Eight reproducible transcripts, query fixture, overlap plan, exact hash manifest, rubric and run template |
| Exact source identity and claim-to-excerpt evidence | Complete | Attempt + transcript SHA + excerpt ID; golden batch ledgers and per-query evidence, integrity tests |
| Local lookup and whole-transcript coverage | Complete | Local queries plus every-batch global ledger, 723 stress decision groups, final-chunk omission |
| Insufficient evidence and contradictions | Complete | Budget/password refusals; both launch-date/outage claims and unresolved/unverified qualifiers |
| Repetition/overlap, unstated owners/deadlines | Complete | Repeated launch claims, concrete overlapping windows, checklist/archive-review action constraints |
| Bounded follow-up history | Complete | Three turns, history poisoning, zero/bounded/evicted history and exact-attempt switch probes |
| Twelve hours and multiple sizes, explicit bytes/segments/tokens | Complete protocol/artifacts | Eight measured byte/segment inventories; 14,400 segments per twelve-hour case. Token counts null pending pinned tokenizer, not invented |
| Hashes/normalization/licenses | Complete | Frozen source/output/coverage/protocol hashes, UTF-8/no BOM/LF corpus, original text preserved, CC0-1.0 |
| Process/native peaks and disk | Complete method | OS high-water + sampled private/resident counters, ETW native attribution and allocated/logical disk; actual candidate run pending |
| Cold/warm latency, indexing and recursive-summary duration | Complete method | Precisely separated cache/load states, monotonic event boundaries and leaf-to-root coverage logs; candidate run pending |
| Capture priority, cancellation and compatible checkpoint resume | Complete method | Real capture metrics, native-call boundary timings, exact compatibility/invalidation matrix; no simulated pass |
| Reference hardware and both native RIDs offline | Complete method, execution unproven | Actual ARM64 CPU/OS/RAM inventoried; x64 inventory unavailable here. Both require real offline/native model runs and network-attempt audit |
| Budgets and human gate | Complete separation | No passing numeric budget/technology chosen; issue #1 retains the decision |
| Privacy/no feature/model work | Complete | Local-only output; no downloads, inference, app/solution/spec changes, publication or sharing |

## Explicit limits/blockers

There is **no unmet synthetic-corpus licensing or protocol-definition criterion**.
Real-model/native feasibility execution is blocked pending exact approved model
profiles/imports and complete reference-run conditions for each RID. The ARM64 host
exists; x64 hardware inventory/run was not established by this task.
Token counts, native memory/time, offline/capture/cancellation results and semantic
candidate scores are unmeasured, not passing. Qualified bilingual reviewers are
required before scoring real outputs. Corpus timestamps are not audio, and this
synthetic repeated corpus does not certify natural-meeting accuracy or speech
recognition. These boundaries do not turn the delivered protocol into release
acceptance evidence.
