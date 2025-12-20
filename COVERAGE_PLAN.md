# Code Coverage Plan (Aggressive)

This document is the step-by-step plan to aggressively raise code coverage across the whole repo (server + CLI) while keeping tests deterministic and CI-friendly.

## Baseline (Cobertura)

Artifacts:

- Server: `DebuggerMcp.Tests/TestResults/coverage/coverage.cobertura.xml`
- CLI: `DebuggerMcp.Cli.Tests/TestResults/coverage/coverage.cobertura.xml`

Note: do not hardcode “current coverage %” in this document. It drifts immediately.

To get the current baseline:

- Quick per-project summary: `dotnet test` (prints per-test-project coverage summary)
- Combined report (optional HTML): `./coverage.sh` (prints `./TestResults/coverage-report/Summary.txt` when `reportgenerator` is installed)

## How to run coverage

- Fast local loop: `dotnet test`
  - Produces per-project coverage under:
    - `DebuggerMcp.Tests/TestResults/coverage/`
    - `DebuggerMcp.Cli.Tests/TestResults/coverage/`
- Combined report (optional HTML): `./coverage.sh`
  - Produces combined artifacts under `./TestResults/` and (if `reportgenerator` is installed) `./TestResults/coverage-report/`.

## Rules of engagement (to keep “aggressive” sustainable)

1. Prefer deterministic unit tests (no network, no external debugger processes, no real dumps).
2. Prioritize “uncovered lines” over “lowest %” (big red files first).
3. When code is hard to test, refactor into small pure helpers (parsers/mappers/classifiers) and unit test those.
4. Keep environment-variable tests isolated/serialized to avoid flakiness.
5. Every batch ends with `dotnet test` (coverage) and (optionally) refreshing any screenshots/examples.

## Prioritized hotspots (largest uncovered files)

Server:

1. `DebuggerMcp/Analysis/ClrMdAnalyzer.cs`
2. `DebuggerMcp/Analysis/DotNetCrashAnalyzer.cs`
3. `DebuggerMcp/LldbManager.cs`
4. `DebuggerMcp/Analysis/Synchronization/SynchronizationAnalyzer.cs`
5. `DebuggerMcp/ObjectInspection/ObjectInspector.cs`
6. `DebuggerMcp/SessionManager.cs`
7. `DebuggerMcp/McpTools/ObjectInspectionTools.cs`
8. `DebuggerMcp/WinDbgManager.cs`
9. `DebuggerMcp/SourceLink/PdbPatcher.cs`
10. `DebuggerMcp/SourceLink/AzurePipelinesResolver.cs`

CLI:

1. `DebuggerMcp.Cli/Program.cs`
2. `DebuggerMcp.Cli/Display/ErrorHandler.cs`
3. `DebuggerMcp.Cli/Shell/ShellReadLine.cs`
4. `DebuggerMcp.Cli/Help/HelpSystem.cs`
5. `DebuggerMcp.Cli/Client/HttpApiClient.cs`
6. `DebuggerMcp.Cli/Client/McpClient.cs`
7. `DebuggerMcp.Cli/Shell/ISystemConsole.cs`

## Step-by-step execution plan

### Step 1 — Close remaining 0%/low-effort files (1–2 days)

- CLI:
  - Add targeted tests for `ProgressRenderer` (done) and then `McpClient`/`HttpApiClient` error handling and URL normalization.
  - Decide on `SystemConsole`:
    - either add a thin “smoke” test (non-interactive, no `KeyAvailable`/`Beep`) or explicitly exclude it via `[ExcludeFromCodeCoverage]` (only if agreed).
- Server:
  - Add tests for small helpers that are currently uncovered:
    - `HostInfo` parsing (done)
    - report generation helpers (e.g., `JsonReportGenerator`) (done)
    - WinDbg module-list detection logic via pure helper (done)

Exit criteria:
- CLI reaches **70%+ line**.
- Server reaches **52%+ line** without relying on external tools.

### Step 2 — Turn “big red” analyzers into testable units (3–7 days)

- `Analysis/ClrMdAnalyzer.cs`
  - Extract string/structure parsing into internal helpers (stack parsing, frame normalization, classification rules).
  - Add fixture-driven tests (`DebuggerMcp.Tests/Fixtures/ClrMd/`) with representative outputs.
- `Analysis/DotNetCrashAnalyzer.cs`
  - Extract “decision logic” into internal helpers (severity, crash-type mapping, recommendation selection).
  - Add matrix tests covering common exception types and missing-data edge cases.
- `Analysis/Synchronization/SynchronizationAnalyzer.cs`
  - Extract wait-chain parsing and deadlock detection into internal helpers.
  - Add fixture-driven tests for lock graphs and thread wait reasons.

Exit criteria:
- Each of the three files gains **+10 absolute** line coverage (not relative).

### Step 3 — MCP tools and integration seams (7–14 days)

- Add tests for MCP tools focusing on:
  - input validation (bad IDs, bad addresses, missing sessions)
  - stable JSON response shapes
  - error-handling branches
- Prefer test seams:
  - keep debugger-execution behind `IDebuggerManager` mocks
  - keep filesystem behind temp directories

Exit criteria:
- Server reaches **60%+ line** and **50%+ branch**.

### Step 4 — Coverage ratchet (continuous)

- Add a CI guard to prevent coverage regression (no decreases).
- Raise the floor weekly (example):
  - Week 1: 55%
  - Week 2: 58%
  - Week 3: 62%
  - Week 4: 65%
