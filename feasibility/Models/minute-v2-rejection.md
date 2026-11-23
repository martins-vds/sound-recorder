# #4 bounded follow-up: candidate rejected for evidence-preserving analysis

**Recommendation: reject this Qwen2.5-7B Q4_K_M generation profile for the proposed
grounded recursive-analysis contract on the measured configuration.** Retain
Nomic v2-MoE as a retrieval candidate only. No new weights are proposed or fetched.
This is a diagnostic rejection, not release acceptance, qualified human entailment
approval, or issue #4 completion. Strict mutation remains below the gate.

## Coherent changes

- Native policy now uses managed context/inference records and replaceable native
  model, CPU-initialization and local-verification boundaries. LLamaSharp object
  construction occurs only in the real native adapter, after CPU configuration.
  This preserves the real configuration and avoids native initialization during
  deterministic policy tests.
- Deterministic tests exercise integrity-before-load, role separation, incompatible
  architecture/dimensions, initialization failure/resource disposal, complete
  non-causal batching, tokenization/sampling policy, context equality/overflow,
  missing data, cancellation before/during/after calls and actual diagnostic input
  identities/budgets. Tiny approved native adapter tests use one constrained token
  and one short embedding, rather than minute generations per mutant.
- TDD found stale `LastGeneratedTokens` after a cancelled second call; it now resets
  before preflight. Another red test showed a reduced root could lose child evidence
  yet return as complete. Reduction now rejects missing child-reference unions.
- Stronger prompts require every owner/deadline or explicit absence, claim kind,
  numbered batch decision, opposing reports/dates and unresolved/unverified status.
  They did not make the candidate adequate.

## Original minute rerun, not four-source proxy

Frozen before outputs: `runs\minute-atomic-ledger-v2.json`, based on the original
#3 corpus/query/coverage obligations. Both exact 20-excerpt minute fixtures were
hash-verified and reused unchanged. All eight local/global questions ran in each
language; global questions received all twenty original excerpts. All four
five-excerpt leaves were generated, then the actual pairwise minute tree was
attempted. It failed at the first reduction, so **no complete minute root exists**.
No source or expected ledger was substituted.

| Cell | Outcome |
|---|---|
| English eight questions | Eight structurally valid candidate outputs |
| Brazilian Portuguese eight questions | Eight structurally valid candidate outputs |
| English recursive minute tree | Rejected: lost child references `s000006,s000008` |
| Portuguese recursive minute tree | Rejected: lost `s000006,s000007,s000008,s000009` |
| Native test runner | Two failed language cases, zero skipped; 32m57s total |

The failure happens far below context exhaustion: maximum observed serialized
prompt was 1,198 English / 1,360 Portuguese tokens, plus a 768-token output reserve
in a 4,096-token context. Peak private process observations were 5,860,569,088 /
5,877,817,344 bytes; these include native and managed memory, not native allocation
attribution. Diagnostic durations were 928.829 / 1,048.445 seconds, including failed
work. Prior native cancellation observations remain retained, not repeated here.

### Named ledger observations (provisional direct reading)

- Local answers retained Ana and 5 September 2027, checklist owner/deadline absence,
  the two-versus-zero outage reports and their unverified status. Budget/password
  questions did not invent values or follow the quoted adversarial instruction.
- Portuguese whole-summary omitted the log action/owner/deadline, batch decision,
  and opposing outage statements. Its limitation called 10 September the
  **official** decision and 12 September another member's claim: the source does
  not establish an official resolved date or that speaker's identity.
- English whole-summary recovered the log deadline and batch decision kind, but
  omitted paper archival, temporary-account discussion and both outage reports/
  unverified count. It did not explicitly preserve the launch conflict's unresolved
  qualifier.
- English all-decisions attached `s000017` to the launch date although that excerpt
  only lists overview topics; valid citation identity does not establish support.
  Both all-decisions outputs were overinclusive, adding actions/non-decisions.
- Reduction dropped the log-action evidence in **both** languages despite explicit
  preservation instructions. Traversal coverage is not semantic/reference retention.

These concrete missing/unsupported obligations are enough to reject the candidate
profile. Formal per-claim precision/recall and ambiguous-claim consensus still
require qualified independent bilingual reviewers; none is claimed.

## Full owning-source quality evidence

No source exclusions, scope relocation, suppression or threshold reduction.
All seven handwritten owning `Models.Harness` files remain in scope, including
the real native adapter. Tool defaults are recorded separately; constructors
remain the fork's documented exclusion.

- Model-free regression: **72 passed**, including the separate native loader probe.
- Bounded quality selection: **74 passed** (72 plus two tiny native adapter cases).
- Explicit Release build: zero warnings/errors.
- Fork CRAP full-source maximum: **7**, every reported executable member numeric
  and <8. Native policy/validation members now have meaningful deterministic coverage.
- Final Complete-level Stryker run: **179 killed, 27 survived, 1 timeout,
  0 uncovered, 0 runtime errors**. Score including the detected timeout:
  **86.95652173913044%** (180/207), **fails >98%**.
  Sixteen compile errors and 49 tool-default block-filter ignored mutants are separate.

Exact remaining mutants, source lines and replacements:
`runs\quality\minute-v2-mutant-blockers.json` and the complete
`mutation-minute-v2\reports\mutation-report.json`. They are not suppressed:

- Twenty `GenerationProbe` mutants remain, principally removed prompt instructions
  about exhaustive preservation, owners/deadlines, disagreements and source-vs-
  generated evidence, plus reduction reference/deduplication cases. A model-free
  double cannot establish their effect on real semantic output; mechanical prose/
  constant assertions were deliberately not used to manufacture a passing score.
- One ChatML transport-string mutant remains in `LlamaGeneration`.
- One process-wide CPU initialization-body mutant remains: a later capability call
  also initializes the library; warm process state limits the existing observation.
- Five native adapter survivors affect pre-load probing, actual context initializer,
  template application and embedding-context release; one grammar-root mutation
  timed out and is counted as detected, not equivalent or passing.

These are **precise incomplete gates**, not a claim all survivors are irreducible
or equivalent. Further independent boundary observability/process isolation and
real prompt-effect tests are needed. The bounded effort stops rather than running
long transcript generations for every mutant or suppressing failures.

## Reproduction and persistent private evidence

Set the already-approved shared cache with `SR_APPROVED_MODEL_DIRECTORY`.
From the dedicated worktree:

```powershell
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type!=IntegrationTests"
$env:SR_EVAL_PHASE = "full-minute-v2"
$env:SR_EVAL_SKIP_CANCELLATION = "1"
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "FullyQualifiedName~RealModelTests" --collect "XPlat Code Coverage"
Push-Location .\feasibility\Models\Harness.Tests
dotnet stryker --config-file ..\stryker-config.json --output ..\runs\quality\mutation-minute-v2
Pop-Location
```

Persistent private root, outside the temporary worker:

`C:\Users\vinima\.copilot\session-state\87f62508-2e44-47cc-80d2-0b9d91f62872\files\issue-4`

- `runs\full-minute-v2-20261123T010408-pt-BR-333fd8cf44874395ae9595286e7c9f05\events.jsonl`
- `runs\full-minute-v2-20261123T012137-en-ea053ce8fdf548a5bc99e6a3db655802\events.jsonl`
- `runs\minute-v2-candidates.json`, `minute-v2-observations.json`,
  `minute-atomic-ledger-v2.json`, `minute-v2-coverage`.
- `runs\quality\mutation-minute-v2`, `minute-v2-mutant-blockers.json`,
  `crap-minute-v2-console.txt`; final coverage under `coverage`.
- All earlier failed/structured runs, tokenizer hashes, quality output and coverage
  remain alongside them. No model weights were copied; the shared models cache
  remains authoritative. This preservation is local, not upload/publication.

## Handoff boundaries

#4 remains open: this measured candidate fails full-minute evidence preservation
and the strict mutation gate. Both-RID real-model/offline matrix, independent
entailment adjudication, native/capture resource attribution and human technology/
threshold approval remain gaps. No #5 packaging, #6 twelve-hour run, production
feature, new model, push or issue closure was performed.
