# Explicit analysis model setup

`IAnalysisModelSetup` (Core) is the consumer boundary. `AnalysisModelSetup`
(Infrastructure) supplies the initial **Phi-4-mini + Nomic** CPU profile, using
the production engine's embedded profile files. It does not use or replace
transcription models. This module does not start inference, schedule work, or
integrate desktop controls.

## Consumer actions

Construct `AnalysisModelSetup` with an application-owned **local** cache
directory, an `HttpClient`, and explicitly configured `AnalysisModelLimits`.
Construction and `Profile` do not access storage or the network.

- `GetReadinessAsync()` rechecks the selected directory's complete size/SHA-256
  identities and GGUF v3 format, then measures resources. It is offline and
  never falls back to another directory. Only `Ready` exposes an engine-ready
  directory. Readiness is an observation, not a lease against external changes.
- `SelectDirectoryAsync(directory)` explicitly selects an already-verified
  external cache **without writing, renaming, or deleting its weights**.
- `ImportAsync(zipPath, progress, cancellationToken)` accepts a local ZIP with
  exactly the two filenames below. No manifest, directories, duplicate names,
  links, arbitrary paths, extra files, TAR archive, or caller-provided hashes
  are accepted. ZIP entry sizes and streamed bytes are bounded by the pins.
- `DownloadAsync(progress, cancellationToken)` is the only network action.
  URLs come exclusively from the pinned embedded production profiles; there
  is no caller URL/profile argument, metadata lookup, retry, or automatic
  startup download.

Import/download are themselves explicit activation requests. Both stage a
complete profile in an owned unique directory, verify every file, recheck the
resource reserve, rename the complete directory, and atomically replace the
flushed local selection pointer. Transfer progress reports aggregate bytes
against the pinned total; `Verifying` is separate from transfer completion.
`Completed` means activation committed, not inference or semantic acceptance.
Cancellation is honored up to that commit point. Use nonthrowing progress
handlers. Previously selected directories are retained, including after a
failed/cancelled update. Only the current operation's staging/new directory
is removed on failure; unrelated directories are never swept.

```csharp
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Analysis.Models;

// Supply application-configured budgets; these are not hardware support claims.
IAnalysisModelSetup setup = new AnalysisModelSetup(
    ownedLocalCache, httpClient,
    new AnalysisModelLimits(configuredDiskReserveBytes, configuredAvailableRamBytes));
await setup.SelectDirectoryAsync(existingApprovedCache, cancellationToken);
var readiness = await setup.GetReadinessAsync(cancellationToken);
if (readiness.State == AnalysisModelReadinessState.Ready)
{
    // Pass readiness.Directory to the existing production analysis adapter.
}
```

Consumers must surface `InvalidDataException` (pinned integrity/format/package
failure), `IOException`/`UnauthorizedAccessException` (storage/access),
`HttpRequestException` (explicit network request), and
`AnalysisModelResourceException` (platform/RAM/disk preflight) with an explicit
retry/setup action. Cancellation is not a successful installation.
No background installer or model selection is implied by an error.

## Frozen profile and licensing

`Profile.Files` exposes the exact names, roles, sizes, SHA-256 hashes and separate
licenses. Source revisions and URLs remain in the existing production
`SoundRecorder.Analysis\Profiles` resources, not an imported manifest:

| Role | Filename | Bytes | License | SHA-256 |
|---|---|---:|---|---|
| Generation | `Phi-4-mini-instruct-Q4_K_M.gguf` | 2,491,874,272 | MIT | `88c00229914083cd112853aab84ed51b87bdf6b9ce42f532d8c85c7c63b1730a` |
| Embedding | `nomic-embed-text-v2-moe.Q8_0.gguf` | 512,225,120 | Apache-2.0 | `06e7a7e594a26985523c18383aba4aad39fe6e14f08ffc6ab5b554e1ccdc3cff` |

Total weight bytes: **3,004,099,392**. Phi is the approved Unsloth conversion
revision `78eb92a46fc37e6b524df991ed9aca9bc6aa7b80`, not full Phi-4 or Qwen.
Nomic is revision `ffbcf4c99e5d617dda10ec8c0e9f75754b0cbb80`.
Carry the appropriate MIT/Apache-2.0 notices when distributing these models;
the weight-only ZIP is not a license bundle. Existing profile provenance
qualifications and rejected model experiments remain unchanged.

Compatibility is the exact approved cryptographic identity plus GGUF v3,
LLamaSharp/CPU 0.30.0 and native Windows ARM64/x64; this module does not invoke
native model loading. English/Brazilian Portuguese are the approved scope,
not newly established model-quality evidence.

## Resource and validation boundaries

Disk preflight needs the whole new profile **plus** the caller's disk reserve;
retained old weights and an existing ZIP already consume measured free space.
RAM uses available physical memory, not total installed memory. Limits are
integer bytes; negative/overflowing reserves and nonpositive RAM budgets are
rejected. Floating-point/NaN/infinity budgets are not part of the API.
No default hardware threshold is introduced. Memory use can change after
preflight; this is **not an OOM guarantee**. Local mapped/network drives and
reparse points are rejected. Storage must be application-owned and not
concurrently edited by other processes; file verification cannot promise
immunity to an external change after verification.

`AnalysisModelSetupTests` exercises the public boundary with tiny GGUF-header
fixtures, real isolated local storage, a controlled HTTP handler and resource
snapshots. Its internal fixture constructor is available only to the existing
friend test assembly; production callers cannot supply files, hashes or URLs.
No approved cache weight is downloaded, modified or needed for these tests.

Run focused tests from the repository root:

```powershell
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj `
  --filter "FullyQualifiedName~AnalysisModelSetupTests"
```

Full owning Core/Infrastructure CRAP and mutation scores remain separate gates.
Focused-module feedback is not a passing full-owner or release-acceptance claim.
UI/scheduler integration, native distribution, semantic validation and the
deferred 12-hour test belong to later tickets.
