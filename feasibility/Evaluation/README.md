# Issue #3 — local transcript analysis evaluation kit

**Protocol/corpus deliverable; not feature implementation or release evidence.**
No model was downloaded or invoked. No hardware performance or grounding result
is claimed. Thresholds and technology selection remain at issue #1's human gate.
Everything stays local; committing locally does not authorize sharing or pushing.

## Reproduce

From this dedicated worktree root, using .NET SDK 10.0.404 and PowerShell 7:

```powershell
.\feasibility\Evaluation\Generate.ps1
dotnet test .\feasibility\Evaluation\Generator.Tests\Evaluation.Generator.Tests.csproj
```

`Generate.ps1` refuses an existing `generated` directory. To repeat, preserve the
first output locally (for example move it into a new directory under `runs`)
before generating again. Do not overwrite source fixtures or private reports.
Local Git attributes pin corpus/C# files to LF and the Windows PowerShell script
to CRLF so checked asset hashes remain stable on a fresh checkout.
The console generator uses the SDK only; its xUnit test harness restores the
four pinned test packages in its project. There are no model dependencies.
Do not add these projects to the application solution.

The generator public seam is `CorpusGenerator.Write(writer, seed, blocks,
timestamps)`: one line is emitted at a time, using only the twenty authored seed
excerpts in memory. The console entry point takes three arguments: seed JSON
path, positive block count, and timestamp boolean, and writes UTF-8 JSONL to
stdout. Errors propagate as failure; an interrupted output is not a corpus.
`Generate.ps1` is orchestration, hashing, and golden-ledger materialization, not
transcript analysis. The focused C# artifact-integrity tests stream checks against
the checked-in manifest; neither runs inference. For generator-only tests before
materialization, add `--filter "type!=IntegrationTests"`.

## Contents

- [Corpus manifest](corpus-manifest.json): measured byte counts, segment counts,
  timeline durations, SHA-256, exact synthetic successful-attempt identity, and
  explicitly unmeasured tokenizer-dependent counts.
- `fixtures\*.seed.json`: original bilingual corpus text; twenty excerpts per
  minute, with twenty numbered excerpts repeated in each block.
- `generated\*.jsonl`: eight materialized transcripts plus eight exact batch
  decision/evidence ledgers, reproduced rather than committed.
- [Queries and expected evidence](queries.json): local/global questions, claims,
  contradictions, insufficient evidence, action owners/deadlines, and follow-ups.
- [Overlap plan](chunk-plan.json): concrete overlapping chunks and an incomplete
  coverage injection.
- [Protocol](protocol.md): execution matrix and measurement methods.
- [Scoring rubric](scoring-rubric.md): semantic support and coverage adjudication.
- [Run template](run-template.json): unmeasured fields, not fabricated results.
- [Corpus license](LICENSE-corpus.md): CC0-1.0 declaration and provenance.
- [Validation](validation.md): actual harness/corpus checks and explicit gaps.

## Corpus sizes

| Language | Case | UTF-8 bytes | Segments | Saved-audio timeline | Distinct batch decisions |
|---|---|---:|---:|---|---:|
| English | minute | 2,547 | 20 | 60 seconds | 1 |
| English | hour | 157,476 | 1,200 | 1 hour | 60 |
| English | 12hour | 1,920,029 | 14,400 | 12 hours | 720 |
| English | untimed | 2,517 | 20 | unavailable | 1 |
| Brazilian Portuguese | minute | 2,790 | 20 | 60 seconds | 1 |
| Brazilian Portuguese | hour | 172,056 | 1,200 | 1 hour | 60 |
| Brazilian Portuguese | 12hour | 2,094,989 | 14,400 | 12 hours | 720 |
| Brazilian Portuguese | untimed | 2,760 | 20 | unavailable | 1 |

Each timed excerpt covers exactly three seconds of a synthetic saved-audio
timeline; no audio file exists. This stresses transcript processing, **not speech
recognition or real twelve-hour capture**. Repeated content is intentionally
adversarial and is not representative of natural meeting diversity. Distinct
batch decisions span the entire timeline so a few matching chunks cannot satisfy
global coverage. A minute case has four unique decision groups (launch conflict,
archive forms, retain access register, batch); a twelve-hour case has 723.
Actual tokens depend on a pinned approved tokenizer; no ratio-based estimate is
substituted for measurement.

No production files, requirements baseline, solution, integration branch, or
application behavior are changed.
