# JSON Report Audit: `DebuggerMcp.Cli/6239b1aa.json`

Goal: track every inconsistency/bug found in the report, plus the plan and notes for fixing each item one-by-one without losing context.

Scope:
- Report: `DebuggerMcp.Cli/6239b1aa.json` (mtime: `2025-12-14 00:02:12` local)
- Logs: latest run in `logs/server-2025-12-13-1.log` (UTC timestamps)

Legend:
- **Severity**: High / Medium / Low
- **Status**: Open / In progress / Fixed / Verified
- **Owner**: (fill as we work)

---

## Guardrail: Validate Before Patching

Before implementing any “final fix” for an issue in this document:

1) **Verify the hypothesis against the real pipeline**
   - Reproduce the issue deterministically (ideally with a test or a minimal input).
   - Confirm the symptom is produced by the suspected component (parser vs enrichment vs serializer).

2) **Inspect all related code paths**
   - Follow the data end-to-end (raw debugger output → parser → enrichment → report generator/serializer).
   - Confirm there are no alternative writers overriding the same fields later in the pipeline.

3) **Add/adjust tests first (when feasible)**
   - Encode the failure as a regression test (unit/integration depending on scope).
   - Ensure the test fails for the current behavior before applying the fix.

4) **Only then implement the fix**
   - Keep the fix minimal and targeted to the confirmed root cause.
   - Re-run `dotnet build` + `dotnet test` to validate.

---

## Index

- [ISSUE-001: `analysis.summary.description` counts don’t match report data](#issue-001-analysis-summarydescription-counts-dont-match-report-data)
- [ISSUE-002: `threads.summary.total` disagrees with `threads.all.length`](#issue-002-threadssummarytotal-disagrees-with-threadsalllength)
- [ISSUE-003: Recommendation claims “9 dead threads” but none are marked dead](#issue-003-recommendation-claims-9-dead-threads-but-none-are-marked-dead)
- [ISSUE-004: LLDB frame parser corrupts `module` when backticks are present](#issue-004-lldb-frame-parser-corrupts-module-when-backticks-are-present)
- [ISSUE-005: Register formatting inconsistent with pointers (`0x` prefix mismatch)](#issue-005-register-formatting-inconsistent-with-pointers-0x-prefix-mismatch)
- [ISSUE-006: Many managed frames missing `sourceFile`/`lineNumber` (metric)](#issue-006-many-managed-frames-missing-sourcefilelinenumber-metric)
- [ISSUE-007: Assemblies list contains duplicates and missing `repositoryUrl` for some `commitHash` values](#issue-007-assemblies-list-contains-duplicates-and-missing-repositoryurl-for-some-commithash-values)
- [ISSUE-008: `threads.all[*].topFunction` points to placeholders (JIT/Runtime) instead of a meaningful frame](#issue-008-threadsalltopfunction-points-to-placeholders-jitruntime-instead-of-a-meaningful-frame)
- [ISSUE-009: `osThreadId` is hex while `threadId` shows decimal tid](#issue-009-osthreadid-is-hex-while-threadid-shows-decimal-tid)
- [ISSUE-010: Native frames have source paths but missing `sourceUrl` (dotnet runtime paths)](#issue-010-native-frames-have-source-paths-but-missing-sourceurl-dotnet-runtime-paths)

---

## ISSUE-001: `analysis.summary.description` counts don’t match report data

- **Severity**: High
- **Status**: Fixed
- **Owner**:

### Symptom
The human-readable summary embeds counts that don’t match the report’s actual data.

### Evidence
- Computed from JSON:
  - Threads: `47` (`analysis.threads.all.length`)
  - Total frames: `1639` (sum of `callStack` lengths)
  - Faulting thread frames: `60`
- `analysis.summary.description` says:
  - `Found 47 threads (1280 total frames, 49 in faulting thread)` → total frames and faulting frames are wrong.

### Likely cause
Base summary text is generated before .NET enrichment adds/merges frames (ClrMD/SOS), and the summary text is not recomputed afterwards.

### Final solution implemented
- Implemented `CrashAnalyzer.RefreshSummaryCounts(CrashAnalysisResult)` to rewrite the embedded count clause using the final `Threads.All[*].CallStack` and `Modules.Count`.
- Called it at the end of `.NET` analysis (`DotNetCrashAnalyzer.AnalyzeDotNetCrashAsync`) after `UpdateDotNetSummary` so late-stage frame merging is reflected.

### Tests
- Added `DebuggerMcp.Tests/Analysis/CrashAnalyzerPrivateHelpersTests.cs` `RefreshSummaryCounts_WhenDescriptionHasCounts_UpdatesToFinalThreadAndFrameCounts`.

### Notes / Discussion
- (fill)

---

## ISSUE-002: `threads.summary.total` disagrees with `threads.all.length`

- **Severity**: High
- **Status**: Fixed
- **Owner**:

### Symptom
`analysis.threads.summary.total` is `51` while `analysis.threads.all.length` is `47`.

### Evidence
- `analysis.threads.summary`:
  - `total: 51, foreground: 1, background: 41, unstarted: 0, dead: 9, pending: 0` (breakdown sums to `51`)
- `analysis.threads.all.length`: `47`

### Likely cause
`threads.summary.*` is populated from CLR thread stats (SOS/ClrMD) while `threads.all` comes from LLDB’s OS thread list; these are not always 1:1.

### Final solution implemented
- Clarified the schema without breaking it:
  - `threads.summary.total` remains the CLR thread count (from `!clrthreads` when available).
  - Added `threads.osThreadCount` (new field) for the OS thread count as reported by LLDB’s thread list.
- `analysis.summary.description` uses the OS thread list count (since it’s grounded in `threads.all`).

### Tests
- Covered implicitly by `RefreshSummaryCounts_WhenDescriptionHasCounts_UpdatesToFinalThreadAndFrameCounts` (it sets `threads.osThreadCount` from `threads.all` count).

### Notes / Discussion
- (fill)

---

## ISSUE-003: Recommendation claims “9 dead threads” but none are marked dead

- **Severity**: High
- **Status**: Fixed
- **Owner**:

### Symptom
`analysis.summary.recommendations` includes: `Found 9 dead thread(s)...` but:
- no thread has `state == "Dead"`
- no thread has `isDead == true`

### Evidence
- `analysis.threads.summary.dead = 9`
- `analysis.threads.all[*].isDead` is always `false` / missing

### Likely cause
Recommendation is based on CLR thread stats, but the UI/data model implies it is about OS threads in `threads.all`.

### Final solution implemented
- Updated the recommendation text to explicitly reflect the CLR-originated count:
  - “CLR reports {dead} dead managed thread(s). These may not appear in the OS thread list.”

### Tests
- Covered by `DebuggerMcp.Tests/Analysis/DotNetCrashAnalyzerParsingTests.cs` `ParseClrThreads_WithHeaderAndThreadLine_UpdatesSummaryAndEnrichesThread` (asserts the recommendation is emitted).

### Notes / Discussion
- (fill)

---

## ISSUE-004: LLDB frame parser corrupts `module` when backticks are present

- **Severity**: Medium
- **Status**: Fixed
- **Owner**:

### Symptom
At least one native frame has `module` containing backtick-delimited function fragments (clearly not a module name).

### Evidence
- Example frame (native):
  - `module`: `libcoreclr.so\`ds_ipc_stream_factory_get_next_available_stream(callback=(libcoreclr.so`
  - `function`: `server_warning_callback(char const*, unsigned int)`
  - `sourceFile`: `ds-server.c`

This also appears in logs as misleading PDB warnings, e.g. `No PDB found for module: libcoreclr.so\`ds_ipc_stream_factory...`

### Likely cause
Regex in LLDB `ParseSingleFrame` uses `(\S+)` for the module; with backticks in other parts of the line it can capture too much. It should be non-greedy: `(\S+?)`.

### Final solution implemented
- Hardened `CrashAnalyzer.ParseSingleFrame` to ensure the captured module name is trimmed at the first backtick, even if the input line contains nested backtick fragments.

### Tests
- Added `DebuggerMcp.Tests/Analysis/CrashAnalyzerPrivateHelpersTests.cs` `ParseSingleFrame_WithBacktickAndSp_ParsesModuleFunctionAndSource`.

### Notes / Discussion
- (fill)

---

## ISSUE-005: Register formatting inconsistent with pointers (`0x` prefix mismatch)

- **Severity**: Low
- **Status**: Fixed
- **Owner**:

### Symptom
`stackPointer`/`instructionPointer` are formatted as `0x...` but `registers.sp`/`registers.pc` are stored without the prefix.

### Evidence
- Example:
  - `stackPointer`: `0x0000ffffca31ade0`
  - `registers.sp`: `0000ffffca31ade0`

### Impact
Not incorrect, but it forces consumers to normalize formats and complicates equality checks.

### Proposed fix
- Pick one format and apply consistently:
  - Prefer `0x` prefix everywhere for pointer-like values, or
  - Prefer “hex digits only” everywhere.

### Tests
- Updated `DebuggerMcp.Tests/Analysis/DotNetCrashAnalyzerPureHelpersTests.cs` `ParseRegisterOutput_ExtractsRegistersAndPreserves0x`.

### Notes / Discussion
- (fill)

---

## ISSUE-006: Many managed frames missing `sourceFile`/`lineNumber` (metric)

- **Severity**: Low (unless we expect near-100% for this dump)
- **Status**: Verified
- **Owner**:

### Observation
Managed frame source coverage is not complete.

### Evidence (current report)
- Managed frames: `688`
- Managed frames with `sourceFile`+`lineNumber`: `186`

### Likely causes (non-exclusive)
- JIT stubs / runtime frames / `[JIT Code @ ...]` that lack metadata/sequence points
- Frames missing stack pointer or not mergeable
- Missing/partial PDB coverage for some assemblies

### Proposed approach
- Track this as a metric and improve incrementally:
  - Identify top missing assemblies by frame count,
  - Ensure their PDBs exist and match GUID,
  - Ensure IL offset mapping is correct.

### Tests
- N/A (metric). Add targeted tests only when a specific resolution bug is found.

### Notes / Discussion
- Marked as “Verified” (expected behavior for runtime/JIT frames and for frames without sequence points). Not treated as a correctness bug in this audit.

---

## ISSUE-007: Assemblies list contains duplicates and missing `repositoryUrl` for some `commitHash` values

- **Severity**: Low
- **Status**: Fixed
- **Owner**:

### Symptoms
- Duplicate entries exist for the same assembly path (example: `xunit.runner.visualstudio.dotnetcore.testadapter` appears multiple times).
- Some entries have `commitHash` but no `repositoryUrl` (e.g. `MessagePack` has short hash-like suffix `6cbd8196e7` in informational version).
- Some `analysis.assemblies.items[*].sourceUrl` can be 404 for squashed commits (expected sometimes).

### Evidence
- Duplicates:
  - `xunit.runner.visualstudio.dotnetcore.testadapter` appears 3 times (same path/version).
- Missing repoUrl with commitHash:
  - `Datadog.Trace.Tests` / `Datadog.Trace.TestHelpers`, `MessagePack`, several `xunit.*`.

### Proposed fix
- Deduplicate assemblies by `(path,moduleId)` when building the list.
- Only populate `commitHash` when a `repositoryUrl` is known (or introduce `commitId` vs `commitHash` semantics).
- Accept that `sourceUrl` can 404 when commits are not present in public history; optionally detect and omit.

### Tests
- Added `DebuggerMcp.Tests/Analysis/DotNetCrashAnalyzerParsingTests.cs` coverage for assembly deduplication.
- Added `DebuggerMcp.Tests/Analysis/DotNetCrashAnalyzerPureHelpersTests.cs` coverage for commit-hash extraction gating decisions.

### Notes / Discussion
- Implementation details:
  - `DotNetCrashAnalyzer.ParseAssemblyVersions` now deduplicates assembly entries using a stable key (prefers `Path`, then `Name|ModuleId`, then `Name`).
  - `DotNetCrashAnalyzer.EnrichAssemblyMetadata` now clears `CommitHash`/`SourceUrl` unless the assembly has repository context (`RepositoryUrl` or `SourceCommitUrl`).

---

## Action Log

Use this section to record decisions and link commits to issue IDs.

- 2025-12-14: Implemented fixes for ISSUE-001..ISSUE-007 (see code changes; commit pending).
- 2025-12-14: Implemented fixes for ISSUE-008 (see code changes; commit pending).

---

## ISSUE-009: `osThreadId` is hex while `threadId` shows decimal tid

- **Severity**: Low (consumer clarity)
- **Status**: Fixed
- **Owner**:

### Symptom
`threads.all[*].threadId` shows `tid: <decimal>` (e.g. `tid: 884`) while `threads.all[*].osThreadId` comes from `!clrthreads` OSID and is represented as a hex string without `0x` (e.g. `374`).

This is numerically consistent (`0x374 == 884`) but visually confusing, and can be misread as a mismatch.

### Evidence
- Example: `threadId: "1 (tid: 884) \"dotnet\""` and `osThreadId: "374"`.

### Likely cause
SOS `!clrthreads` reports OSID in hex, whereas LLDB thread list typically prints TID in decimal.

### Final solution implemented
- Added `threads.all[*].osThreadIdDecimal` (derived from `!clrthreads` OSID) so both forms are available without breaking existing consumers of `osThreadId`.

### Tests
- `DebuggerMcp.Tests/Analysis/DotNetCrashAnalyzerParsingTests.cs`: `ParseClrThreads_WithHeaderAndThreadLine_UpdatesSummaryAndEnrichesThread` asserts `osThreadIdDecimal`.

---

## ISSUE-010: Native frames have source paths but missing `sourceUrl` (dotnet runtime paths)

- **Severity**: Medium
- **Status**: Fixed
- **Owner**:

### Symptom
Many native frames include `sourceFile`/`lineNumber` from DWARF (e.g. `/__w/1/s/src/runtime/src/coreclr/vm/threads.cpp:7058:5`), but `sourceUrl` is missing.

### Likely cause
The SourceLink resolver path is PDB-centric; native Linux/macOS frames typically have DWARF debug info rather than PDB/Portable PDB SourceLink metadata.

### Guardrail validation
Before generating URLs, restrict to known dotnet build-agent paths to avoid incorrect URL generation.

### Final solution implemented
- For Linux/macOS native frames, avoid attempting PDB-based SourceLink resolution (prevents noisy “PDB not found” warnings).
- Add a safe dotnet-runtime URL mapping:
  - `/__w/1/s/src/...` → `https://github.com/dotnet/dotnet/blob/<commit>/src/runtime/src/...#L<line>` (with repo-layout rewrites as needed)
  - Commit is sourced from `analysis.assemblies.items` (prefers `System.Private.CoreLib`).

### Tests
- Add unit tests for the mapping and for the dotnet/dotnet `src/native` → `src/runtime/src/native` rewrite.
  - `DebuggerMcp.Tests/Analysis/CrashAnalyzerPrivateHelpersTests.cs`: `NormalizeRepoRelativePath_WhenDotnetRepoAndSrcNative_RewritesToSrcRuntime`
  - `DebuggerMcp.Tests/Analysis/CrashAnalyzerPrivateHelpersTests.cs`: `TryResolveDotnetRuntimeSourceUrl_WithDotnetAssemblyMetadata_ResolvesNativeRuntimePath`

---

## ISSUE-008: `threads.all[*].topFunction` points to placeholders (JIT/Runtime) instead of a meaningful frame

- **Severity**: Medium
- **Status**: Fixed (needs report regen)
- **Owner**:

### Symptom
Many threads have `topFunction` values like `[Runtime]` or a header-derived value that does not match the most meaningful frame in the final merged `callStack`.

### Evidence
In the regenerated `6239b1aa.json`, some threads have:
- `topFunction = [Runtime]` while `callStack` includes meaningful frames such as `libcoreclr.so!...` or `System.*` managed methods.
- `topFunction` pointing at the pre-merge header/function while the merged stack starts with placeholder frames like `[JIT Code @ ...]`.

### Likely cause
`topFunction` is computed from:
- `thread list` headers (often truncated or placeholder-y), and/or
- the first frame in `bt all` (which can be `[JIT Code @ ...]`),
and was not re-selected after late-stage SP-based stack merging reorders frames.

### Final solution implemented
- Implemented deterministic “meaningful top frame” selection that:
  - skips known placeholders: `[JIT Code @ ...]`, `[Native Code @ ...]`, `[Runtime]`, `[ManagedMethod]`
  - selects the first non-placeholder frame in the call stack (top-to-bottom)
  - falls back to the first frame if all frames are placeholders
- Applied consistently in:
  - LLDB `bt all` parsing (`CrashAnalyzer.ParseLldbBacktraceAll`)
  - managed/native merge (`DotNetCrashAnalyzer.MergeManagedFramesIntoCallStack`)
  - SP-based reordering merge (`DotNetCrashAnalyzer.MergeNativeAndManagedFramesBySP`)
  - ClrMD thread creation where `TopFunction` was missing (`DotNetCrashAnalyzer.PopulateManagedStacksViaClrMd`)

### Tests
- `DebuggerMcp.Tests/Analysis/CrashAnalyzerParsingTests.cs`:
  - `ComputeMeaningfulTopFunction_SkipsJitAndRuntimePlaceholders`
  - `ParseLldbBacktraceAll_WhenFirstFrameIsJit_UsesFirstNonPlaceholderAsTopFunction`
- `DebuggerMcp.Tests/Analysis/DotNetCrashAnalyzerPureHelpersTests.cs`:
  - `MergeNativeAndManagedFramesBySP_RecomputesTopFunctionAfterReordering`

### Notes / Discussion
- Verification step: regenerate `DebuggerMcp.Cli/6239b1aa.json` and confirm `topFunction` equals the first non-placeholder frame in `callStack` for each thread.
