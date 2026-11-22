# SQLite retrieval feasibility — issue #2

Isolated C# experiment, **not product implementation or an approved technology
decision**. Nothing references the production projects or solution. No models
are downloaded; every embedding and excerpt reference is synthetic. No transcript
content is logged or copied into this cache.

## Reproduce

Use the native .NET 10 SDK/runtime on the architecture being evaluated. From the
repository root:

```powershell
dotnet restore .\feasibility\SqliteRetrieval\SqliteRetrieval.Tests\SqliteRetrieval.Tests.csproj --locked-mode
dotnet test .\feasibility\SqliteRetrieval\SqliteRetrieval.Tests\SqliteRetrieval.Tests.csproj --filter "Category!=Measurement"
dotnet test .\feasibility\SqliteRetrieval\SqliteRetrieval.Tests\SqliteRetrieval.Tests.csproj --filter "Category=Measurement" --logger "console;verbosity=detailed"
dotnet test .\feasibility\SqliteRetrieval\Native.Tests\Native.Tests.csproj --logger "console;verbosity=detailed"
dotnet publish .\feasibility\SqliteRetrieval\SqliteRetrieval.csproj --runtime win-arm64 --self-contained false --output .\feasibility\SqliteRetrieval\bin\publish-win-arm64
dotnet publish .\feasibility\SqliteRetrieval\SqliteRetrieval.csproj --runtime win-x64 --self-contained false --output .\feasibility\SqliteRetrieval\bin\publish-win-x64
```

The separate native-extension probe is intentionally a real loading test, not a
skip or a test expecting failure. It currently fails on native Windows ARM64.
It must pass before claiming an extension comparison or ARM64 extension support.
Running on x64 emulation would not establish ARM64 support. Publishing this
library stages RID assets; it is neither an installer nor native execution proof.

Measurement cases include indexing, close/reopen, first query, database size,
cumulative query allocations, and test-process peak working set. Peak working
set covers the entire test process, not just retrieval. They are single-run
synthetic observations with no approved performance thresholds, no model
inference, and no 12-hour transcript or acceptance claim. Keep benchmark,
coverage, TRX and mutation reports local; do not commit them.

## Minimal seam and ownership

`TranscriptIndex` operates on an explicitly chosen **cache-only** database:

- `Begin`: creates staging, or resumes only the exact compatible attempt,
  transcript hash, model hash, dimension, pooling, normalization, chunking
  version and expected excerpt count.
- `Append`: persists one excerpt and float32 BLOB in one SQLite statement.
  Only a contiguous ordinal prefix can advance; duplicate stable references,
  dimension mismatches, zero/nonfinite vectors, and negative timestamps fail.
- `Complete`: publishes only when every expected excerpt is present. Completed
  caches cannot accept further excerpts. Interrupted checkpoints remain private.
- `Search`: checks compatibility/completion, streams one BLOB at a time and
  retains only top-k citation metadata. Cancellation is observed at row
  boundaries. Saved-audio timestamps are milliseconds; absence retains a stable
  reference. Equal scores sort by ordinal, never unspecified database ordering.
- `DeleteCache`: explicit, exact attempt deletion cascades only its cache rows.
  It permits an explicit rebuild under changed configuration, never silent
  reuse. Audio, authoritative transcripts, completed summaries and discussion
  history are outside this database; synthetic file-preservation tests verify
  that cleanup does not touch them or other attempts.

The schema is in the constructor: one `targets` row per immutable attempt,
composite `(attempt, ordinal)` excerpt key, `(attempt, reference)` uniqueness,
nullable timestamp and BLOB. Checkpoint count is derived from durable excerpt
rows, not an independently committed counter. The caller must establish that
the selected source exists and belongs to a successful immutable attempt.
This harness does not validate source files, chunk text, model hashes against
weights, model output, disk preflight, or production lifecycle.

One worker owns each database; concurrent calls/writers are outside this
experiment. SQLite errors propagate, not success. Completed checkpoints are
tested across disposal/reopen (restart), not abrupt power loss or disk failure.
Deletion is logical cache removal, not a secure-erasure guarantee or automatic
disk reclamation. No filesystem cleanup or automatic model switching is offered.

## Compatibility and comparison

The controlled 3-dimensional fixture is:

| Stable reference | Ordinal | Timestamp ms | Vector | Cosine to `[1,0,0]` |
| --- | ---: | ---: | --- | ---: |
| excerpt-a | 0 | 1250 | `[2,0,0]` | 1 |
| excerpt-b | 1 | absent | `[1,1,0]` | 0.7071067811865475 |
| excerpt-c | 2 | 4000 | `[-1,0,0]` | -1 |

Portable search stores little-endian IEEE float32 BLOBs on both target Windows
architectures, accumulates dot products/norms in float64, and computes cosine
similarity (higher is nearer). No unit normalization is assumed. The native
probe specifies `distance_metric=cosine` explicitly (do not accept default L2),
uses the same vectors, and compares `1 - distance` with portable scores within
`1e-6`. Both are exact scans, not approximate/global-coverage answers. Pooling,
normalization and chunking labels are compatibility metadata; this harness does
not actually pool model output. Real dimensions/configuration remain undecided.

The 384-dimensional synthetic cases are storage/search scale experiments, not
an embedding model profile. Portable search has O(N × (D + K log K)) work and
O(D + K) managed live data, plus SQLite's page cache. Cumulative allocations grow
with N even though the corpus is not retained in memory. Raw vectors cost
`N × D × 4` bytes before SQLite pages, metadata and indexes. One durable excerpt
commit prioritizes checkpoint clarity over ingestion throughput. sqlite-vec can
reduce scan overhead with native vector operations/partitioning; no performance
advantage is claimed without a successful comparable run.

## Public packaging and licenses (checked 2026-11-22)

| Component | Exact evaluated version | Windows assets / license |
| --- | --- | --- |
| Microsoft.Data.Sqlite / Core | 10.0.0 | Managed ADO.NET; MIT. Not itself a native extension. |
| SQLitePCLRaw bundle/config/core/provider | 3.0.5 | Apache-2.0; native binding/initialization. |
| SQLite NuGet native package | 3.53.4 | `runtimes\win-arm64\native\e_sqlite3.dll` and `win-x64`; package license file declares SQLite public domain. |
| sqlite-vec NuGet | 0.1.7-alpha.2.1 | Windows `win-x64\native\vec0.dll` only; MIT package declaration. No `win-arm64`. |
| sqlite-vec upstream release | v0.1.9 | Windows x86_64 loadable archive; no Windows ARM64 archive. Upstream dual MIT/Apache-2.0. |
| CommunityToolkit.VectorData.SqliteVec | 1.0.4-preview | MIT; depends on sqlite-vec >= 0.1.7-alpha.2.1 and invokes `LoadVector()`. Documentation's auto-loading is not ARM64 binary support. Inspected, not added to the harness. |

The bundle override avoids the vulnerable old SQLitePCLRaw native package
2.1.11 pulled by Microsoft.Data.Sqlite's default dependency range; lockfiles
record the resolved 3.0.5 / SQLite 3.53.4 graph. Preserve applicable license and
copyright notices when redistributing managed libraries and extension binaries.
SQLite's public-domain status does not waive other components' notice duties.

Primary sources:

- [sqlite-vec v0.1.9 assets](https://github.com/asg017/sqlite-vec/releases/tag/v0.1.9),
  [release workflow](https://github.com/asg017/sqlite-vec/blob/v0.1.9/.github/workflows/release.yaml),
  [Makefile](https://github.com/asg017/sqlite-vec/blob/v0.1.9/Makefile),
  [MIT](https://github.com/asg017/sqlite-vec/blob/v0.1.9/LICENSE-MIT),
  [Apache](https://github.com/asg017/sqlite-vec/blob/v0.1.9/LICENSE-APACHE).
- [sqlite-vec NuGet package](https://www.nuget.org/packages/sqlite-vec/0.1.7-alpha.2.1).
- [Connector package/dependencies](https://www.nuget.org/packages/CommunityToolkit.VectorData.SqliteVec/1.0.4-preview),
  [source project](https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/SqliteVec/SqliteVec.csproj),
  [connection factory](https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/SqliteVec/SqliteConnectionFactory.cs),
  [Microsoft connector documentation](https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/sqlite-connector).
- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.0),
  [SQLitePCLRaw](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5),
  [SQLite native package](https://www.nuget.org/packages/SQLite/3.53.4),
  [SQLite copyright](https://sqlite.org/copyright.html).

Upstream amalgamation/source portability could support a separately maintained
ARM64 build, but is not tested Windows packaging. Building would require an
ARM64 C toolchain, SQLite extension headers/ABI, explicit SIMD/compiler settings,
license notices, repeatable artifact hashes and native loading/retrieval checks.
The upstream release workflow currently builds only Windows x86_64; no custom
ARM64 build or unofficial DLL is supplied here.

### Alternate ARM64 source-build route

The follow-up checked an existing-toolchain build, rather than treating missing
release assets as impossibility proof. Pin `v0.1.9` to commit
`e9f598abfa0c06b328d8fe5da9c3760cce74be10`.
The public Windows release recipe uses MSVC `/LD /O2 /Ivendor/` on
`sqlite-vec.c`; `scripts/vendor.sh` obtains SQLite 3.45.3 extension headers from
`https://www.sqlite.org/2024/sqlite-amalgamation-3450300.zip`.
The source exposes `sqlite3_vec_init` and enables ARM NEON only when
`SQLITE_VEC_ENABLE_NEON` is defined. A first scalar ARM64 build should not
enable that optional SIMD path without compiler validation.

On the available host, these explicit probes found no existing compiler:

```powershell
Get-Command cl.exe,clang.exe,clang-cl.exe,gcc.exe,zig.exe -ErrorAction SilentlyContinue
& $env:ComSpec /c 'cl.exe /nologo /?'
```

The command lookup returned no compilers. The `cl.exe` probe returned exit 1,
**“'cl.exe' is not recognized as an internal or external command”**.
Both `C:\Program Files\Microsoft Visual Studio` and its Program Files (x86)
counterpart were absent, as were the Installer `vswhere.exe`, the standard
Windows Kits 10 root and `C:\Program Files\LLVM\bin\clang-cl.exe`.
Visual Studio setup/SxS registry keys and the VS/VC/Windows SDK environment
variables were also absent.

Consequently no compiler environment could be initialized, no source compilation
was attempted, and no extension DLL/hash or controlled native comparison was
produced. No compiler/SDK was installed. This establishes an **existing-toolchain
blocker on this host**, not that ARM64 source compilation is impossible.
When an approved ARM64 toolchain is available, the pending route is: pin source
and generated version header, obtain the specified SQLite extension headers,
initialize the ARM64 MSVC environment, build a loadable scalar DLL, verify its
ARM64 PE identity/exports/license notices/hash, then run the controlled native
comparison against portable retrieval. That route remains unexecuted.

**Recommendation:** carry portable SQLite BLOB + exact cosine forward as the
minimal dual-RID candidate, with native x64 execution and downstream model/scale
measurements still required. Do not choose the connector/extension as a
dual-architecture default while the ARM64 loading gap remains. **Issue #2 stays
blocked/incomplete:** extension comparison never reached retrieval on ARM64;
native x64 execution is not available on this host. Native SQLite ARM64 success
does not establish either missing criterion.

## Quality gates

Use the prescribed martins-vds/crap4csharp fork (release 1.1.0), not a presumed
NuGet publication. Run from the repository root with its installed command:

```powershell
crap4csharp .\feasibility\SqliteRetrieval\TranscriptIndex.cs --test-project .\feasibility\SqliteRetrieval\SqliteRetrieval.Tests\SqliteRetrieval.Tests.csproj --framework net10.0 --test-filter "Category!=Measurement" --crap-threshold 8 --dry-run
# Repeat without --dry-run, then require every unrounded member score < 8.
dotnet tool restore
Push-Location .\feasibility\SqliteRetrieval\SqliteRetrieval.Tests
try {
    dotnet stryker --project SqliteRetrieval.csproj --test-case-filter "Category!=Measurement" --reporter json --reporter html --threshold-high 98 --threshold-low 98 --break-at 98 --concurrency 2 --skip-version-check --output ..\TestResults\mutation
} finally {
    Pop-Location
}
```

The tests explicitly target the native ARM64 SDK architecture when applicable
because Stryker/VSTest otherwise requests an unavailable x64 host for AnyCPU.
This does not add an emulated x64 runtime. Full handwritten harness source is
analyzed; measurements are excluded from quality runs because deterministic
seam tests cover their behavior. The extension probe is separate and remains
failed, not ignored evidence. CRAP excludes constructors; Stryker's automatic
compile-error/duplicate-block filters are reported separately from evaluated
mutants. Require the unrounded mutation score > 98%, not just tool exit zero.
