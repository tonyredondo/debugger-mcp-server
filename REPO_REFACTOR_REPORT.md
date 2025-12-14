# Repository Refactor Opportunities Report

Scope: `debugger-mcp-server` (server + CLI + tests)

Goal: identify refactors that improve **testability**, **deduplication**, and **performance** without changing behavior.

Generated: 2025-12-14

---

## Executive Summary

The repo is in good shape functionally, and recent work added strong contract-style regression tests for the JSON report. The biggest remaining opportunities are structural:

- **Hotspots**: `DebuggerMcp/Analysis/DotNetCrashAnalyzer.cs` (~7.4k LOC) and `DebuggerMcp.Cli/Program.cs` (~7.6k LOC).
- **Dedup**: repeated JSON serialization options, report-generation orchestration duplicated across MCP tools + HTTP controllers, repeated sanitization + symbol-path construction.
- **Perf**: heavy use of ad-hoc `Regex.Match` in tight loops (stack parsing, module parsing, process-info parsing); repeated computations that could be cached per-run.

---

## High-Impact, Low-Risk Refactors (Recommended Next)

### 1) Centralize JSON serialization defaults
**Problem**
- Many different `JsonSerializerOptions` are defined across the repo (`DebuggerMcp`, `DebuggerMcp.Cli`, tests).
- Some use camelCase policy, some do not; some include `WhenWritingNull`, some don’t.

**Why it matters**
- Risk of subtle JSON shape drift between report endpoints/tooling.
- Harder to test and reason about output stability.

**Refactor**
- Introduce a single `internal static class JsonSerializationDefaults` (server) and `internal static class CliJsonSerializationDefaults` (CLI), with:
  - `Indented` options (WriteIndented, WhenWritingNull)
  - `Compact` options (no indentation)
- Replace scattered `new JsonSerializerOptions { ... }` with references to these defaults.

**Tests**
- Add tests asserting that `ReportService` JSON and `JsonReportGenerator` JSON have consistent casing/ignore-null behavior.

---

### 2) Replace `Console.WriteLine` diagnostics in SourceLink pipeline with logger
**Problem**
- There is at least one `Console.WriteLine` path in `DebuggerMcp/Analysis/CrashAnalyzer.cs` for SourceLink skip logging.

**Why it matters**
- Inconsistent logging, harder to correlate in `./logs`.
- Makes tests noisy (and encourages “log parsing” as an API).

**Refactor**
- Ensure all SourceLink/analysis diagnostics go through injected `ILogger` consistently.

**Tests**
- Unit test that SourceLink “skip” path does not throw and does not write to console (can be validated by injecting a test logger and/or capturing console in a narrow test).

---

### 3) Consolidate report-generation orchestration across MCP tools + HTTP controllers
**Problem**
- `ReportTools.GenerateReport` and `DumpController.GenerateReport` both:
  - sanitize identifiers
  - construct symbol paths / SourceLinkResolver
  - run crash analysis
  - run security analysis
  - run watches
  - generate report via a report service/generator
- But they do it in different ways, which risks divergence (and makes changes double-work).

**Refactor**
- Extract a shared orchestrator service:
  - `ReportOrchestrator.GenerateAsync(ReportRequest)` returning `{ metadata, analysis }` or final string/bytes.
  - Both `ReportTools` and `DumpController` call it.
- Keep behavior identical by parameterizing:
  - “prefer DotNetCrashAnalyzer if SOS loaded” vs “always CrashAnalyzer” (if that difference is intended).
  - inclusion flags (security/watches/raw).

**Tests**
- Add orchestrator unit tests using stubbed `IDebuggerManager` outputs similar to existing end-to-end tests.
- Ensure the `CrashAnalysisResultContract` invariants still pass.

---

## Medium-Risk / Medium-Reward Refactors

### 4) Replace reflection-based private-method tests with `internal` helpers
**Problem**
- Multiple tests use reflection to call private methods (e.g., `MergeNativeAndManagedFramesBySP` helpers).

**Why it matters**
- Tests become brittle to renames/moves and discourage refactors.

**Refactor**
- Extract parsing + transformation helpers into `internal` classes (like the new finalizer/utilities approach), and test those directly.
- Keep `InternalsVisibleTo` (already present in `DebuggerMcp.csproj`).

**Tests**
- Migrate a few reflection-heavy tests to direct tests on the extracted helper classes.

---

### 5) Reduce regex overhead in tight loops
**Problem**
- Many `Regex.Match` calls occur in per-line parsing loops (LLDB output parsing, SOS output parsing, process-info extraction).

**Refactor options**
- Convert hot regex patterns to:
  - `static readonly Regex` with `RegexOptions.Compiled`, or
  - manual parsing using spans where appropriate (best ROI in the tightest loops).
- Document which ones are “hot” and why (based on typical dump size).

**Tests**
- Keep existing parsing tests, and add a micro “stress” test that parses a large synthetic backtrace output quickly (without making the test flaky; cap runtime).

---

### 6) De-duplicate symbol-path construction and `.symbols_<dumpId>` discovery
**Problem**
- Symbol path logic appears in multiple places (e.g., controllers and symbol manager).

**Refactor**
- Make `SymbolManager` the single authority:
  - `SymbolManager.GetSymbolSearchPaths(userId, dumpId)` returning ordered candidates.
- Controllers/tools call that method and feed it into `SourceLinkResolver`.

**Tests**
- Unit tests around symbol path generation for:
  - with/without user directory
  - dumpId with extension
  - traversal-resistant inputs (already sanitized, but defend in depth).

---

## Larger / Longer-Term Refactors (High Reward, Higher Cost)

### 7) Break up `DotNetCrashAnalyzer.cs` into composable analyzers/passes
**Problem**
- `DebuggerMcp/Analysis/DotNetCrashAnalyzer.cs` is extremely large and mixes concerns:
  - orchestration / command scheduling
  - parsing
  - heuristics/recommendations
  - assembly/source-link enrichment
  - memory/sync/async analysis

**Refactor**
- Split into “passes” with clear inputs/outputs and minimal shared state:
  - `ThreadAndStackPass`
  - `ExceptionPass`
  - `AssembliesPass`
  - `MemoryPass`
  - `SynchronizationPass`
  - `RecommendationsPass`
  - `FinalizationPass` (already exists)
- Keep behavior by preserving ordering and returning the same `CrashAnalysisResult`.

**Tests**
- Add pass-level unit tests around each pass with small stub outputs.
- Keep end-to-end + contract tests as the safety net.

---

### 8) Break up `DebuggerMcp.Cli/Program.cs` into command modules
**Problem**
- CLI `Program.cs` is very large; command handlers and parsing are interleaved.

**Refactor**
- Extract command handlers into separate files (e.g., `Commands/ReportCommand.cs`, `Commands/DumpCommand.cs`, etc.).
- Keep `Program.cs` as wiring + root command registration.

**Tests**
- Add CLI-level unit tests per command handler (existing CLI tests can be expanded).

---

## Performance “Quick Wins” (No Behavior Change)

1) Cache computed aggregates during report generation:
   - total frame counts
   - faulting frame counts
   - summary clause rewrite inputs
   - module counts
   - assembly dedup key set

2) Ensure SourceLink resolution avoids repeated per-frame module scanning:
   - build a per-run map: `moduleNameOrPath -> ModuleSourceContext` once, then resolve frames via that.

3) Reduce allocations in stack parsing:
   - avoid repeated `Split('\n')` + per-line string trimming in hottest loops; consider line enumerators.

---

## Test Strategy Improvements

- Expand the existing `CrashAnalysisResultContract` to include:
  - report-level invariants (metadata consistency, json generator shape)
  - optional checks gated by presence (e.g., when `sourceUrl` exists, enforce fields; otherwise skip).
- Add an explicit “golden invariants” test that:
  - builds a minimal `CrashAnalysisResult` with representative data
  - generates JSON (`JsonReportGenerator` and `ReportService`)
  - deserializes and asserts the contract again.

---

## Known Hotspots / Notes

- `DebuggerMcp/Analysis/DotNetCrashAnalyzer.cs` is a long-term modularization candidate.
- `DebuggerMcp.Cli/Program.cs` modularization would improve maintainability and make CLI behavior easier to test.
- Multiple `JsonSerializerOptions` definitions are a common source of subtle behavior drift.

