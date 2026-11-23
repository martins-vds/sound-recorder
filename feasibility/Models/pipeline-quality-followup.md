# Issue #11: concrete quality-gap follow-up

This continues `a1c861c8016e8e366dca40d68ef182ea0d504318` in the same isolated
ticket worktree. Prior raw v2/v3 reports, rejected model profiles, frozen corpus,
and their configuration identities are retained unchanged. No source exclusions,
manual mutant suppression, threshold reductions or model downloads.

## Final verification after resuming

The preserved final validation command was still running after the worker was
cancelled. It was resumed without launching a duplicate. All four requested
pipeline corrections and the context/overlap/fault regressions now pass their
behavior tests:

- **180 selected tests passed**, zero failed/skipped: 175 deterministic/model-free
  cases plus five bounded native adapter cases.
- Full-owner CRAP: **81 numeric members, maximum exactly 7**, strictly below 8.
  All ten handwritten owning source files were selected; constructors remain
  outside the fork's analysis.
- Full-owner mutation: **494 killed, five detected timeouts, five survivors**;
  **499 / 504 = 99.0079365079365%**, strictly above 98 and above the configured
  99 break threshold. Stryker exited **0**. Nineteen compile errors and 88
  tool-default ignored mutants are separate, not counted as detected. No
  handwritten exclusions or manual suppressions were introduced.
- Every source embedded in the final mutation report was compared with the
  current source, normalizing line endings only; all ten matched.
- The separately approved original Portuguese summary diagnostic executed
  exactly once and passed. The earlier abort remains causally unexplained;
  passing replays and this run are not a claimed native crash fix.

The final reports are `last-crap.txt`, `last-coverage/`, `last-mutation/reports/`
and `resumed-final-summary.json` in the persistent private directory below.
Earlier failing runs and their counts remain historical evidence, not the
current gate result. The five timeout detections are not ordinary assertion
kills; raw replacements/statuses remain in the final mutation JSON.

These results complete the pipeline corrections, not the model selection gate.
The current v4 request contract has no claimed real-model semantic evaluation;
initial extraction remains **UNVERIFIED**. No production feature, final model
approval, capture/offline/stress acceptance or native x64 model run is implied.

Commands from the isolated worktree:

```powershell
dotnet test feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type!=IntegrationTests"
# Full-owner CRAP: pass all ten Harness\*.cs paths explicitly to the fork.
dotnet exec --roll-forward Major <fork-dll> <all-ten-source-paths> --test-project feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --framework net10.0 --crap-threshold 8 --test-filter "type!=IntegrationTests|purpose=PhiMiniNative|purpose=NativeAdapterSmoke"
# From feasibility\Models\Harness.Tests; the unchanged config selects the same bounded cases.
dotnet exec <stryker-dll> --config-file ..\stryker-config.json --output <owned-private-directory>\last-mutation
```

## Focused changes

- Public record/request/source guard tests assert useful failure reasons beyond
  an exception type. Missing/null/empty/kind/reference failures retain atomic
  artifact assertions, including exactly 16 references and all accepted kinds.
- Exact maximum attempt/excerpt/question/language lengths and zero/equal original
  timestamps are accepted and persisted unchanged; malformed metadata/Unicode
  and invalid timelines report the actual reason.
- Golden archived-v2 and current SQLite artifacts are constructed as **input**.
  Assertions use `ReadRun`, `ReadClaims`, `ReadEvidence` only: same-offset,
  different-length source windows are joined correctly, unioned without
  duplication, and old ledgers remain readable.
- Explicit Qwen3/Phi public loaders fail on the independently expected pinned
  missing file before native initialization. Qwen no-thinking and plain transport
  assert executable role/control-token framing, not English instruction prose.
- Native tiny tests now exercise disposal of the actual embedding context and
  rejection at the actual bounded native context, in addition to the prior
  EOG-versus-cap/tokenizer/grammar/model-free probe checks.
- Word/surrogate overlap, strict token-boundary packing, original source order,
  exact 4,096-character packing, live-state lower bounds and midstream cap
  termination are tested through public generation/artifact readers.
- `ReadCoverage` exposes completed original-source lengths as a stream, allowing
  callers to verify interval-union coverage without summing overlap.

The request is now a compact machine-readable contract: explicit `task`,
`language`, `question`, `outputSchema`, immutable identity and original ranges.
The semantic instruction still requires grounding, untrusted-source handling,
stated associations and opposing qualifications. Repeated schema/limit prose
was removed; managed validation and native grammar still enforce the same limits.
The public request-contract tests went red on missing `task`, then green.

Derived evidence insertion no longer repeats canonical source validation.
Original fixed read blocks still validate source text, length and timestamps
before evidence windows are yielded. The redundant surrogate-prefix adjustment
was removed because the following word-boundary scan already advances past
non-whitespace surrogate halves in validated Unicode. The pinned LLamaSharp
`StatelessExecutor.ApplyTemplate` default is false; its redundant false initializer
was removed without adding a template or changing serialization.

## Exactly one separately approved Portuguese summary

After four exact short replay configurations passed without reproducing the old
crash, the user approved one current Portuguese summary in the complete isolated
test context. No additional full comparison or eight-question rerun followed.

`PortugueseSummaryDiagnosticTests` provides one explicit `[Fact]` delegating to
the normal `RealModelTests` summary operation with `pt-BR`. It does not silently
return an English case or rely on ambiguous theory filtering.

The project is xUnit v2 on **VSTest**, SDK 10.0.404; no MTP/global runner override.
The executed filter was `purpose=OriginalPortugueseSummary`, with:

```powershell
dotnet test feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore `
  --filter "purpose=OriginalPortugueseSummary" --collect "XPlat Code Coverage" `
  --logger "trx;LogFileName=portuguese-summary.trx" `
  --results-directory <owned-private-directory> `
  --blame-crash --blame-crash-dump-type mini `
  --blame-hang-timeout 21m --blame-hang-dump-type mini
```

The request itself retained its **20-minute deadline**, normal cached Phi loader,
disk ledger, collector and 100-ms observer. Native parameters/seed/sampling and
approved weights were unchanged.

**TRX: exactly one test executed and passed, zero skipped; exit 0.**
Test duration **142.5991994 seconds**; summary request **137.3030807 seconds**.
Five native calls completed, max prompt **433**, reserve **1,024**, reported
owned-source/prompt bound **9,982 characters**. Artifact status was `Completed`
for traversal, not semantic verification. No crash/hang dump was produced.

This run used the frozen pre-v4 v3 request bytes. The subsequent machine-readable
request change is independently tested but has **no claimed model-semantic run**.
The original v2 abort still has no reproduced fault stack or causal explanation.
A green diagnostic is **not** presented as a native crash fix.

Initial semantic defects remain separate rejection evidence: the inspected
Portuguese output labelled decisions/topics/quoted hostile content as actions,
left stated log-action owner/deadline fields absent, and lost qualifications.
No C# postprocessor repaired or promoted those generated records. Support/recall
scores remain unapproved and semantically **UNVERIFIED**.

## Strict full-owner quality evidence

All ten handwritten production sources of `Models.Harness.csproj` remain selected.
Fork CRAP uses explicit source paths, the owning test project, `net10.0` and
unchanged threshold 8. Bounded test selection:
`type!=IntegrationTests|purpose=PhiMiniNative|purpose=NativeAdapterSmoke`.
No long `RealModelTests` or original-summary diagnostic is run per mutant.

Completed full-run progression retained privately:

| Run | Detected / evaluated | Exact score | Strict result |
|---|---:|---:|---|
| Before this follow-up | 425/518 | 82.04633204633204% | fail |
| Actionable reasons, boundaries, reader/protocol cases | 461/514 | 89.6887159533074% | fail |
| Additional concrete guard/cap/lifecycle cases | 485/511 | 94.9119373776908% | fail |

Detected includes Stryker timeout classifications; timeouts are retained
separately rather than described as ordinary assertion kills.
Intermediate failing build/test attempts and their corrections remain in the
private evidence rather than being erased.

Current final gate reports and exact remaining mutant IDs/replacements determine
ticket status. CRAP requires **every unrounded score <8**; mutation requires
**unrounded score >98%**. Constructors excluded by the fork are not claimed
analyzed. No equivalent-mutant waiver or source-scope change is assumed.

## Private evidence

`C:\Users\vinima\.copilot\session-state\87f62508-2e44-47cc-80d2-0b9d91f62872\files\issue-11\quality-followup`

Preserved artifacts include:

- The exact one-case Portuguese TRX, raw event/claim/ledger exports, collector
  coverage, selection/count validation, exit code and assembly identity.
- Request-contract red/green, focused guards/boundaries/readers/wire-protocol
  tests, native context/disposal checks, and all prior partial quality attempts.
- Complete source scope/hashes, final full-owner CRAP/Cobertura/TRX and Stryker
  JSON/HTML, exact remaining blockers and timeout classifications.
- Latest-integration merge transcript and final release validation.

No raw transcript/model/report upload, push, integration edit, issue closure or
application feature is included. Native x64/offline/capture/release acceptance
and final model/technology approval remain separate evidence gates.
