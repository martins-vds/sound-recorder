# Issue #4 model-free baseline evidence

This is the historical model-free handoff at `a94cd88`, before the user's exact
file/size approval. The authorized follow-up supersedes its download/tooling
status; no earlier unexecuted result is retroactively treated as a pass.

**Issue #4 remains incomplete.** This handoff enables a same-ticket follow-up
after exact model download/import approval. It is not model selection, bilingual
grounding evidence, production implementation or release acceptance.

## Isolation / synchronization

Worktree `C:\Repos\sound-recorder-feasibility-4`, branch
`feasibility/ticket-4-models`, started at integration `8838663`.
Integration advanced to `d7ae235a01316f2775a2bdc67c32fcf17e9164a3`
and was fast-forward merged into this branch before this report.
Only new ticket-owned `feasibility\Models` files were authored.
#3 evaluation seeds/protocol/manifest/queries/generator remain unchanged; the
test project references/links them read-only. No other agent was launched.
No weights, GGUF payload, tokenizer payload or private input was fetched.
Public metadata was checked again at both exact revisions and all three proposed
file bytes/SHA-256 matched `candidate-profile.json`.

## Actual commands/results

VSTest / xUnit 2, SDK **10.0.404**, native Windows ARM64. The available host is
the #3-inventoried Snapdragon X X1E80100, Windows 11 Enterprise build 26300,
33,895,186,432 physical RAM bytes. No supported memory/latency profile is inferred
from that inventory. Installed .NET 10 runtimes include 10.0.14 and 10.0.15.

The final commands, run from this dedicated worktree root:

```powershell
dotnet test .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj --no-restore --filter "type!=IntegrationTests" --logger "trx;LogFileName=final-model-free.trx" --results-directory .\feasibility\Models\runs\tests --collect "XPlat Code Coverage" -v minimal
dotnet build .\feasibility\Models\Harness\Models.Harness.csproj --no-restore -c Release -v minimal
```

Dependencies were restored only after creating/changing the isolated dependency
manifests; final LLamaSharp references use exact `[0.30.0]` ranges. All transitive
dependencies are in committed lock files.

- **34 model-free tests passed; 0 failed; 0 skipped.**
- Release harness build: **0 warnings, 0 errors**.
- Two real-model language cases are explicitly excluded by the filter,
  **unexecuted/pending**, not passed or counted as skipped acceptance evidence.
- Native CPU probe passed with actual in-process ARM64 modules and no GPU
  offload; it performed no model inference.
- Both reconstructed minute fixtures matched the original #3 hashes:
  English `f5d48c8f7d5d28acd824827cdb9b6139971e259657696fb133178dc9a088f234`;
  Portuguese `1cc4e34686cdb4e9095a2dbc6847ef5af0c2c69b2f8514f243c530e33e5865a2`.

Red/green TRX observations retained locally in `runs\tests`:

| Slice | Red | Green |
|---|---|---|
| Exact source identity and candidate evidence | `map-red.trx`: NotImplementedException | `map-green.trx` |
| Prompt + output context reserve | `budget-red.trx`: oversized call accepted | `budget-green.trx` |
| Invalid/missing citations and empty output | `citation-red.trx`: 5 cases accepted | `citation-green.trx` |
| Pairwise recursive coverage/reference union | `reduction-red.trx`: NotImplementedException | `reduction-green.trx` |
| Before/after-call cancellation | `cancel-red.trx`: 2 cases did not honor boundary | `cancel-green.trx` |
| Query/document prefixes and cosine normalization | `retrieval-red.trx`: NotImplementedException | `retrieval-green.trx` |
| Non-finite/zero/wrong-size vectors and embedding context | `embedding-boundaries-red.trx`: 5 wrong outcomes | `embedding-boundaries-green.trx` |
| Invalid generation budgets / conflicting overlap identity | `input-red.trx`: 4 wrong outcomes | `input-green.trx` |
| Native CPU loader | `native-red.trx`: NotImplementedException | `native-green.trx` |
| Missing local file | `local-model-red.trx`: NotImplementedException | `local-model-green.trx` |
| Size and same-size corruption | `integrity-red.trx`: 2 files accepted | `integrity-green.trx` |
| Generation/embedding load failure seam | `native-model-red.trx`: NotImplementedException | `native-model-green.trx` |
| Canonical bilingual fixture reuse | `fixture-red.trx`: NotImplementedException | `fixture-green.trx` |
| Fresh evidence for a Portuguese question | `answer-red.trx`: NotImplementedException | `answer-green.trx` |
| Invalid top-k / empty success | `top-k-red.trx`: 2 wrong outcomes | `final-model-free.trx` |
| Missing retrieval source / empty success | `missing-source-red.trx`: missing evidence accepted | `final-model-free.trx` |

Native successful model paths cannot be exercised before approved files exist.
They compile against the pinned API, but this is not red/green real-model
compatibility evidence. Controlled doubles do not establish entailment or native
memory/cancellation behavior. A compilation mistake during one input-guard change
was corrected before its green run; final output is warning/error-free.

Final local coverage file:
`runs\tests\6c1731fe-06fc-47b1-94d7-08c430f3faf4\coverage.cobertura.xml`.
Coverage intentionally leaves successful native model load/inference paths
uncovered. No aggregate coverage percentage is treated as native proof.

**CRAP/mutation gates:** no production project or handwritten production member
changed, and the harness is outside the solution. No production CRAP or mutation
run/pass is claimed, consistent with #3's isolation boundary.
`crap4csharp` is not available on PATH; maximum per-member CRAP is **unavailable**.
Mutation score for the isolated harness is **unmeasured**, not >98%.
Promoting any of this behavior into production requires separate authorization
and strict full-owner CRAP <8 / mutation >98 gates. Validation is deliberately
incomplete for successful native inference and those quality gates, not waived.

## Native package/archive identities

| Package 0.30.0 | Bytes | SHA-256 |
|---|---:|---|
| LLamaSharp | 375,590 | `1b5a57b5471602a36205efc7025d8f1db13f4824b89c100723f31b3d635afdac` |
| LLamaSharp.Backend.Cpu | 42,345,236 | `d99f24c96d56a0f86fa7b20b436bd85040919a851bff5d693bdc6e9fde8c4e1c` |

ARM64 CPU-package files (all PE machine `AA64`):

| DLL | Bytes | SHA-256 |
|---|---:|---|
| ggml-base.dll | 801,792 | `6811aca7ad0a51ca602bb63e6c71ca0c51a12381ad99314d7712d6c4bb09695a` |
| ggml-cpu.dll | 684,544 | `d09d5802d3c654a0005b3d61ee73af9f2908c51285534d486612d03a21ac0f08` |
| ggml.dll | 67,072 | `8358242c6d19d8c1ad1a9e20b9b2c70fc0d37ac3c4c63a1f409771e3576b31c8` |
| llama.dll | 2,299,904 | `db91ad87abfe347d2e7b823feeaec068562822053f887a137ef005a50bb03ba8` |
| mtmd.dll | 1,055,232 | `10274594b0daecee4cc5a1c946caa315016f5dffa11478b851565ac59bf4d84f` |

The first four were observed loaded through `Process.Modules` from the native
ARM64 test output; `mtmd.dll` was inventoried, **not observed loaded**.
This is a loader observation, not ticket #5 distribution/offline packaging proof.

## Remaining completion gates

| Required evidence | Status |
|---|---|
| Exact revisions/files/bytes/SHA-256/license metadata | Proposed and revision-verified; user approval pending |
| Native LLamaSharp CPU ARM64 loader/API | Model-free pass |
| Actual generation shard/model and embedding compatibility | Blocked until exact file approval/import |
| Actual token IDs/vocabulary hashes/BOS/EOS/template parity | Pending loaded GGUF inspection |
| Native load/resource failure, total/private/resident peaks and release | Pending model run; tensor file size and KV arithmetic are not peaks |
| English/Brazilian Portuguese rubric, unsupported/contradictory claims | Pending real outputs and qualified bilingual review |
| Recursive semantic evidence preservation and global completeness | Structural double probe passes; semantic score pending |
| First usable answer/index/summary latency | Unmeasured; existing log is only an initial diagnostic observer |
| Native safe cancellation and recording priority | Double safe boundaries only; no capture/scheduler acceptance claim |
| Native win-x64 and both-RID offline audit/packaging | Downstream/available-hardware gates, not exercised |
| 12-hour reduction and resource/capture stress | Ticket #6, deliberately not run |
| Final technology/resource thresholds / feature implementation | Human gate pending; implementation not authorized |

Next action: parent requests exact approval using `candidate-profile.json`.
After approval, continue **this same ticket** with local integrity checks,
small-fixture real-model execution and rubric adjudication; do not close #4
based on this model-free handoff.
