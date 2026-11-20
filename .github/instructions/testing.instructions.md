---
applyTo: "**/*.cs,**/*.csproj,**/*.feature,**/*.runsettings,**/stryker-config.*,**/dotnet-tools.json"
---

# Testing Instructions

## C# and executable specifications

- Write automated test implementations in C#. Use [Reqnroll](https://github.com/reqnroll/Reqnroll) with the existing xUnit runner for Gherkin behavior tests; use focused C# xUnit tests for lower-level edge cases.
- Keep `requirements\*.feature` authoritative. Link those files into Reqnroll test projects, following the existing project pattern, rather than maintaining copied scenarios.
- Keep bindings thin: exercise application behavior and assert observable results. Share scenario state through scenario-scoped context or dependency injection.
- Use deterministic doubles for device, filesystem, clock, and model boundaries in unit tests. Use integration tests for real boundary behavior; doubles alone do not prove audio capture, persistence, offline operation, or performance requirements.
- Undefined, pending, skipped, or unexecuted scenarios are incomplete evidence, not passing requirements.

## Red-green-refactor

1. Select the requirement and add or update a focused Reqnroll scenario or C# test. Run it and verify that it fails for the intended missing or incorrect behavior.
2. Implement the smallest C# change that makes that test pass.
3. Refactor with tests green. Repeat this loop for each behavior and relevant failure or boundary case.
4. Run the affected tests during the loop, then the relevant regression suite and the quality gates below before declaring an implementation complete.

From the repository root, the existing Core test suite runs with:

```powershell
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj
```

Use the discovered test names with `--filter` for focused runs. Keep full-suite failures visible even when the changed behavior passes.

## CRAP analysis

- Use the [martins-vds/crap4csharp fork](https://github.com/martins-vds/crap4csharp), which combines cyclomatic complexity and Coverlet Cobertura coverage. Provision the tool from this fork's source or release packages, not the Microsoft upstream repository.
- Before provisioning or changing analysis commands, read the fork's [README](https://github.com/martins-vds/crap4csharp/blob/master/README.md) and check the installed tool's `--help`. Its package ID is `Microsoft.Crap4CSharp` despite being supplied by the fork; install from a fork release package or a local feed built from its source, not a presumed NuGet publication.
- Run `crap4csharp` from the repository root. Pass the production C# files explicitly, grouped by their owning project; each invocation supports only one owning project.
- Each analyzed project needs a test project referencing it directly or transitively, using VSTest and `coverlet.collector`. Automatic discovery uses `<Project>.Tests` or `<Project>.UnitTests`; use explicit `--test-project` paths for other names or repeat the option to combine multiple suites for the same owner.
- Automatic discovery fails on ambiguous matches. Explicit test selectors must each match eligible in-root projects outside `bin`/`obj`, all referencing the same production owner; explicit selection bypasses naming only.
- The tool clears its `coverage` directory once, then runs the selected test projects sequentially in sorted, deduplicated order. Each must produce exactly one usable Cobertura report and valid native TRX diagnostics. Multiple suites use separate results directories and combine coverage by union of member source lines, not by averaging percentages. Preserve needed evidence before rerunning and serialize invocations sharing that directory.
- The default test filter is `type!=IntegrationTests`, which includes untagged tests. Mark xUnit integration tests with `[Trait("type", "IntegrationTests")]`; for Reqnroll integration scenarios, ensure their generated traits match that filter and verify discovery.
- Require **CRAP < 8 for every analyzed production member**, not an average. Analyze all handwritten production C# in each project before final completion; changed-file runs are only development feedback.
- The fork's `--crap-threshold` defaults to 8 and fails only for unrounded scores **> 8**; equality passes the tool's gate. Check the unrounded scores as well: **8 is a failure here**, even with exit code zero. Treat missing coverage or `N/A` for an executable member as incomplete analysis, resolve it, and rerun.
- Lower complexity through cohesive simplification and add meaningful tests for uncovered behavior. Record tool limitations, including its exclusion of constructors, rather than claiming they were analyzed.

### CLI selection and diagnostics

| Argument | Behavior |
| --- | --- |
| `--help` | Prints usage; takes precedence over other arguments. |
| No source arguments | Selects all C# under `src`, including when only options are supplied; still requires a single owning project. |
| `<file ...>` / `<directory ...>` | Selects explicit files or each directory's `src` subtree. A production project directory is not shorthand for all C# directly inside it. |
| `--changed` | Selects changed C# under `src`; conflicts with explicit source paths. |
| `--test-project <path-or-glob>` | Selects test projects explicitly; repeat for multiple suites or quote a supported glob. |
| `--test-filter <expression>` | Replaces the default with one quoted native VSTest expression. Include `type!=IntegrationTests` explicitly if the focused run must retain that exclusion. |
| `--all-tests` | Omits the test filter; conflicts with `--test-filter`. |
| `--framework <tfm>` | Selects one test target framework, such as `net10.0`, for every selected suite; use it to avoid multiple coverage reports per project. |
| `--crap-threshold <number>` | Sets a finite, nonnegative invariant-culture threshold; equality passes the tool, subject to the stricter repository gate above. |
| `--dry-run` | Previews selected sources, owner, suites, filter, framework, threshold, results directories, and command tokens without building, testing, changing coverage artifacts, or scoring. |

- Only `--test-project` is repeatable. Supply one value per occurrence. Use the documented options directly: the installed command supports neither arbitrary runner-argument passthrough nor a `--` separator.
- Quote test-project globs. Matching is case-sensitive against the entire root-relative path and supports `*`, `?`, and whole-segment `**`; backslashes are normalized. Absolute globs, `..` escapes, exclusions, character classes, and brace expansion are unsupported.
- Keep analysis on VSTest, not Microsoft.Testing.Platform. Dry-run checks known incompatible routing but does not fully evaluate SDK/MSBuild configuration and is not proof of runner compatibility.
- Exit codes are `0` for success, `1` for usage/resolution/runner/coverage failures, and `2` for exceeding the configured threshold. No selected files can exit zero, and there is no separate zero-tests gate: verify source selection, executed tests, coverage, and numeric results before claiming a pass. Filter errors must be resolved rather than retried without the filter.

For a focused Core analysis, preview the command below, then rerun the same command without `--dry-run`. This example covers one file only; extend the explicit source list to every handwritten file in that owner for final evidence.

```powershell
crap4csharp .\src\SoundRecorder.Core\RecorderApplication.cs --test-project .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj --framework net10.0 --crap-threshold 8 --dry-run
```

## Mutation testing

- Use [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) to mutate production C# and verify that tests detect behavior changes.
- Restore the repository's pinned local tool with `dotnet tool restore` when needed. Run from the relevant test project directory, specifying the production project filename when multiple project references exist.
- For the existing Core project:

```powershell
Push-Location .\tests\SoundRecorder.Core.Tests
try {
    dotnet stryker --project SoundRecorder.Core.csproj --reporter html --reporter json --threshold-high 98 --threshold-low 98 --break-at 98
} finally {
    Pop-Location
}
```

- Require **mutation score > 98% for each production project**. Use full-project runs for final evidence, not changed-file-only results or a cross-project average.
- Stryker's break threshold fails only below the configured value. The command above is an initial floor: additionally evaluate the unrounded score from the JSON report using Stryker's scoring rules and **fail when the score is <= 98%**. Exit code zero or a rounded display value alone does not establish a pass.
- Investigate surviving and uncovered mutants; strengthen behavior assertions or simplify the implementation. Investigate timeouts and tool errors before accepting results.
- Keep handwritten production behavior in scope. Any exclusion or equivalent-mutant suppression needs an explicit, reviewed rationale; changing scope or thresholds merely to improve the score is not acceptable. Zero evaluated mutants or an unavailable score is incomplete evidence.

## Completion evidence

Report the commands, analyzed projects and source scope, test results, maximum per-member CRAP score, mutation score per project, report locations, and any justified exclusions or tool limitations. Keep reports local unless explicitly approved for sharing.

An implementation is complete only when the relevant tests pass and both strict quality gates pass. If tooling, test discovery, coverage, or mutation analysis is blocked, state the blocker and mark validation incomplete. These instructions define the policy; they do not themselves install tooling or enforce gates in CI.
