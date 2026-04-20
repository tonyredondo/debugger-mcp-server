# Cross-Debugger Parity Plan

## Objective

Make opening and analyzing a dump with LLDB on Linux/macOS feel equivalent to opening and
analyzing a dump with WinDbg on Windows.

The goal is **observable parity for the user of this project**, not identical internal
implementation. Different debugger engines may use different commands and internals, but the
product should behave the same way for the common workflows that users care about:

- opening a dump
- restoring a session that already had a dump open
- resolving symbols from the same sources with the same cache model
- enriching and persisting dump metadata consistently
- auto-loading SOS for managed dumps when possible
- returning comparable data from MCP tools and analyzers
- surfacing predictable errors, timeouts, and recovery behavior

In short: different engines, same product behavior.

---

## Desired User Experience

For a user of the MCP server or CLI, the following workflow should feel the same on every
supported platform:

1. Create or restore a session.
2. Open a dump, or reconnect to a session that already had a dump open.
3. The debugger resolves symbols from the same configured sources and uses the same per-dump
   cache layout.
4. The dump metadata is populated or repaired with the same fields when possible.
5. If the dump is managed, SOS is loaded automatically when possible.
6. MCP tools such as crash analysis, object inspection, ClrStack, report generation, source
   resolution, and Datadog symbol handling return comparable results.
7. If symbols or uploaded binaries change, any cached report is invalidated consistently and
   source-resolution state is rebuilt from the current dump context when the dump remains open.
8. If the debugger hangs or breaks internally, the server surfaces a consistent error and
   recovers when possible.

The user should not need a different mental model for LLDB vs WinDbg for these common flows.

---

## Scope

This plan covers parity for the user-visible behavior of:

- fresh dump open / close workflows
- restored-session dump reopen workflows
- upload-time and open-time dump metadata handling
- metadata path resolution and persistence
- symbol path configuration and per-dump symbol cache usage
- session-scoped additional symbol configuration and its restore behavior
- live symbol or executable changes on an already open dump
- .NET runtime detection
- architecture and platform detection
- SOS discovery and auto-load
- executable path handling for standalone apps
- register capture needed by analyzers and tools
- cache invalidation for reports and source resolution
- resilient command execution and recovery
- debugger-agnostic MCP tool and analyzer behavior where feasible

This plan does **not** require:

- identical internal command sequences between LLDB and WinDbg
- removing debugger-specific capabilities that are still useful
- forcing WinDbg to emulate LLDB-only internals such as `verifycore` module loading
- forcing LLDB to emulate WinDbg-only internals
- eliminating every debugger-specific branch in analyzers that genuinely need different commands
  or parsers

Where a debugger-specific mechanism is necessary, the requirement is equivalent user outcome,
not literal command parity.

---

## Current Baseline

At the time of this plan:

- `LldbManager` contains substantially richer behavior than `WinDbgManager`.
- `DumpTools` already orchestrates important parts of fresh dump opening:
  - metadata lookup
  - executable path lookup
  - session symbol path configuration
  - debugger open
  - ClrMD attachment
  - metadata completion when fields are missing
- `SessionManager` separately reopens dumps during session restore, which means fresh open and
  restored open do **not** currently share one canonical workflow.
- Session restore currently reapplies symbol paths through a callback after the dump is reopened,
  so restored sessions do not follow the same preparation order as fresh opens.
- Additional symbol paths configured at session level are held in memory today and are not
  persisted as part of restored-session state.
- The current session symbol-path model is lossy:
  - configuring additional paths can replace previously derived dump-specific paths instead of
    preserving them as part of one logical symbol configuration
  - LLDB currently ignores URL-based symbol sources when building its effective path, which means
    some user inputs are silently not honored
- Session restore currently relies on the persisted absolute dump path instead of re-resolving the
  dump from user ID and dump ID under the current storage root.
- Dump-derived symbol-directory discovery is not sufficiently user-scoped today because it can scan
  across user directories by dump ID when resolving symbol folders.
- Metadata path resolution and metadata read/write logic are duplicated across multiple places,
  including `DumpController`, `DumpTools`, `SessionManager`, `SymbolTools`, and `LldbManager`.
- `SymbolManager` already owns session symbol path composition and dump-scoped symbol directories.
- Several consumers still depend directly on `LldbManager` or LLDB-only behavior for capabilities
  that should be shared.
- Some features are intentionally LLDB-specific today, such as `verify_core_modules`, and should
  remain explicit non-goals unless their user-visible outcome becomes part of the parity contract.

That means parity is not only a WinDbg implementation task. It is also an orchestration cleanup,
shared-helper extraction, and consumer-decoupling task.

---

## Product-Level Parity Contract

The parity contract should be defined in terms of what the product does for the user.

| Area | Required user-visible behavior | Primary owner |
| --- | --- | --- |
| Fresh open | Same high-level workflow and same class of result message | `DumpOpenCoordinator` called by `DumpTools` |
| Session restore / reopen | Restored sessions reopen dumps using the same preparation order and state model as fresh opens | `DumpOpenCoordinator` called by `SessionManager` |
| Session semantics | One dump per session remains the model, with the same clear behavior when another dump is requested | `DumpOpenCoordinator` + debugger manager |
| Symbol resolution | Same cache layout and the same supported symbol sources, with explicit handling for unsupported ones | `SymbolManager` + shared symbol helpers |
| Session symbol configuration | User-added supported symbol inputs survive restore and continue to compose with dump-derived paths | `SymbolManager` + session persistence |
| Live symbol-state updates | Supported symbol or executable changes on an already open dump update debugger and source-resolution state without requiring a hidden second workflow | `SymbolManager` + shared source-resolution layer + symbol-related flows |
| Dump path resolution on restore | Restore re-resolves dumps from current session metadata and current storage configuration, not only from stale absolute paths | `DumpOpenCoordinator` + `SessionManager` |
| Source resolution / PDB search | Source Link and PDB lookup use the same supported local inputs and resolved dump context as the debugger workflow | shared source-resolution helpers + `DumpOpenCoordinator` |
| Metadata path and persistence | Same metadata file resolution rules and same persisted fields | shared metadata store |
| Metadata ownership | Upload creates seed metadata; open/restore only enrich or repair computed fields | `DumpController` + `DumpOpenCoordinator` + shared metadata store |
| Runtime detection | Same runtime version field when determinable | debugger diagnostics + shared metadata flow |
| Architecture / platform detection | Same normalized values and same platform model exposed to consumers | debugger diagnostics |
| SOS | Auto-load if managed dump and SOS is available | debugger manager |
| Executable path | Standalone executable path is honored when present | shared metadata flow + debugger manager |
| Registers | Tools and analyzers can ask for registers without knowing the debugger type | `IDebuggerDiagnostics` |
| Cache invalidation | Dump or symbol changes invalidate cached report and source-resolution state consistently | `DumpOpenCoordinator` + symbol-related tools |
| Recovery | Timeouts and engine failures surface predictable errors and try recovery | debugger manager |
| Logging | Similar diagnostic cadence and key events in logs | debugger manager + orchestrator |

If a row cannot be made identical, it must still be made equivalent enough that the user does
not need different mental models for LLDB vs WinDbg.

---

## Architectural Decisions

This implementation should follow these decisions from the start.

### 1. Parity means behavior parity, not command parity

WinDbg and LLDB may use completely different commands internally. The requirement is that the
user sees comparable behavior and data quality.

### 2. Keep `IDebuggerManager` focused on core lifecycle

`IDebuggerManager` should remain the common lifecycle/control surface:

- initialize debugger
- open/close dump
- execute command
- load SOS
- configure symbol path

Do **not** add optional parity-only methods directly to `IDebuggerManager` in the first pass.
Optional capabilities should be exposed through a separate interface such as
`IDebuggerDiagnostics`.

### 3. Canonical dump orchestration should live in a shared coordinator

The current fresh-open flow in `DumpTools` and restored-open flow in `SessionManager` should not
evolve separately.

Introduce a shared coordinator, referred to in this plan as `DumpOpenCoordinator`, that owns the
canonical dump open / reopen workflow. `DumpTools` and `SessionManager` should both call it.

### 4. `DumpTools` remains the user-facing entrypoint for open / close actions

`DumpTools` should remain the MCP tool layer for opening and closing dumps, but it should become a
thin wrapper around the shared coordinator rather than the sole home of orchestration logic.

### 5. `SessionManager` owns persistence, not duplicate dump-open logic

`SessionManager` should continue to own:

- session creation
- session persistence
- session restore
- session expiration and cleanup

It should **not** keep a second hand-rolled dump reopen pipeline. During restore, it should
delegate to the same coordinator used by fresh open.

### 6. One dump per session remains the product model

The current product model allows one open dump per session. The parity work should preserve this
unless scope explicitly changes later.

That means:

- opening a second different dump in the same session should not become an implicit replacement
  feature by accident
- restore and recovery may reopen the same tracked dump for that session
- any future change to multi-dump behavior would require a separate product decision, not an
  incidental side effect of this parity work

### 7. `SymbolManager` remains the owner of session symbol path composition

`SymbolManager` should continue to own:

- the effective logical symbol configuration for a session
- session symbol path state
- dump-specific symbol directory discovery
- WinDbg symbol path string building
- LLDB symbol path string building

Shared helpers may enrich this behavior, but responsibility should not be duplicated inside
both managers or inside the session-restore path.

The important design rules are:

- the logical symbol configuration must not be flattened too early into one built path string
- persisted session symbol state must store user intent, not dump-derived ephemeral paths

The effective session model must preserve separate inputs such as:

- default Microsoft symbol server participation
- dump-derived local symbol directories
- user-added local directories
- user-added remote URLs

Debugger-specific string building should happen after that logical model is composed.

Dump-derived symbol resolution should also be user-scoped whenever the caller knows the user ID and
resolved dump path. The implementation should not rely on scanning all user directories by dump ID
when the exact dump context is already available.

The persistent session model should store only the session-scoped user intent that survives restore,
for example:

- user-added local directories
- user-added supported remote inputs

In this plan, built-in Microsoft symbol-source participation is product-defined and is **not**
persisted as user-scoped session intent.

It should **not** persist dump-derived symbol directories because those must be recomputed from the
currently opened dump.

### 8. Unsupported symbol input kinds must never be silently ignored

If a user configures a symbol input that a debugger cannot actually consume, the product must not
pretend it worked.

For parity work, this means:

- supported symbol inputs must be persisted and reapplied consistently
- unsupported symbol inputs must fail clearly or be documented as explicit exceptions
- LLDB must not silently drop URL-based additional symbol sources and still report success

The built-in Microsoft symbol source used by shared helpers is a special case. For LLDB, its
participation happens through shared prefetch into local cache rather than by passing a remote URL
directly to the debugger. The rejection rule in this plan applies to user-added remote symbol
URLs, not to the built-in product-defined source used by the shared workflow.

### 9. Live symbol or executable changes on an open dump must refresh source-resolution state

Cache invalidation alone is not enough when source locations depend on already-attached ClrMD
state.

When supported symbol inputs or executable-path metadata change while a dump remains open, the
product must:

- update the active debugger symbol path when the debugger can consume the changed input live
- rebuild `SequencePointResolver` and `SourceLinkResolver` from the same effective dump context
- clear cached reports that depended on the previous source-resolution state

This plan chooses live refresh for supported local symbol and executable changes. Users should not
have to close and reopen the dump just to make newly available local symbols or executable context
take effect.

### 10. Source resolution and PDB search must use the same effective dump context

Source Link and ClrMD PDB search must not drift into a separate path-selection model.

The effective source-resolution inputs must be derived from:

- the resolved dump path
- the resolved executable path when present
- the effective local symbol directories for the current dump
- runtime/module directories discovered from the current dump
- product-defined local source-resolution inputs that are not tied to one dump, specifically the
  user `.dotnet/symbolcache` and `.nuget/packages` directories when they exist
- Datadog managed-symbol directories materialized under the current dump's symbol cache, when they
  exist

They must not be built from unrelated global scans when the exact dump context is already known.
The same inputs must drive both initial attachment and later live refresh of
`SequencePointResolver`, `SourceLinkResolver`, and any shared PDB search helpers.

### 11. Shared helpers own shared metadata and `dotnet-symbol` behavior

Anything that is conceptually cross-debugger should move to shared helpers:

- `dotnet-symbol` discovery
- symbol prefetch execution
- runtime version parsing from `dotnet-symbol`
- metadata path resolution
- metadata read/write
- symbol file list persistence
- executable path lookup from metadata

`LldbManager` should stop being the de facto home for shared helper logic.

### 12. Upload-time and open-time metadata have different responsibilities

The metadata model should be explicit:

- upload-time code creates the metadata file and stores user-facing seed data
- open / restore code enriches or repairs computed fields when missing
- enrichment must not overwrite unrelated user-provided fields such as description or uploaded
  executable metadata

### 13. Cache invalidation rules must be explicit

The current cache invalidation behavior is spread across tools. The parity implementation should
make the rules explicit:

- opening a different dump clears cached report and `SourceLinkResolver`
- closing a dump clears cached report and `SourceLinkResolver`
- changing symbol inputs clears cached report and `SourceLinkResolver`
- changing uploaded executable or metadata that affects source/symbol resolution clears cached
  report and `SourceLinkResolver`
- recovery may preserve caches only if the reopened state is provably identical; otherwise clear
  them

### 14. Consumers must stop depending on `LldbManager` for shared behavior

Tools and analyzers should depend on shared capabilities, not on concrete debugger types, whenever
the behavior is supposed to exist on both debuggers.

### 15. WinDbg should use a dedicated STA execution model

For DbgEng, a dedicated STA thread with serialized command execution is the preferred design.
This is more robust than a simple lock for:

- COM apartment consistency
- timeouts
- interrupts
- recovery

### 16. LLDB-only features must be documented as explicit exceptions

Not every LLDB-only feature should be ported to WinDbg. Features such as
`LoadVerifyCoreModules` / `verify_core_modules` should remain clearly labeled as LLDB-specific
unless their user-visible outcome becomes part of the parity contract later.

---

## Proposed Target Architecture

### Core roles

- `DumpTools`
  - user-facing MCP entrypoint for open / close actions
  - converts MCP inputs into a `DumpOpenCoordinator` request
  - formats user-facing result messages

- `SessionManager`
  - persists and restores sessions
  - recreates debugger instances
  - delegates dump rehydration to `DumpOpenCoordinator`
  - rehydrates persisted session-scoped symbol intent even when no dump is reopened

- `DumpOpenCoordinator`
  - owns the canonical open / reopen workflow
  - resolves metadata and executable inputs
  - prepares symbol state
  - opens the dump
  - attaches ClrMD and source-resolution helpers
  - enriches metadata
  - applies cache invalidation rules
  - returns state changes for the caller to persist

- `SymbolManager`
  - owns the effective logical symbol configuration for each session
  - owns runtime session symbol-path state
  - owns dump-scoped symbol directory discovery
  - builds debugger-specific symbol path strings

- shared source-resolution layer
  - owns PDB / Source Link search-path composition from the resolved dump context
  - owns live refresh of `SequencePointResolver` and `SourceLinkResolver` for an already open dump
  - feeds `PdbSearchPathBuilder`, `SequencePointResolver`, and `SourceLinkResolver`

- shared helper layer
  - `PersistedSessionSymbolConfiguration`
  - `EffectiveSessionSymbolConfiguration`
  - `SourceResolutionPathBuilder`
  - `SourceResolutionStateRefresher`
  - `DumpMetadata`
  - `DotnetSymbolRunner`
  - `DumpMetadataStore`
  - `DumpAnalyzer`
  - `DumpAnalysisResult`
  - shared parsing / normalization helper classes for diagnostics

- `LldbManager`
  - LLDB process lifecycle
  - LLDB-specific command execution
  - LLDB-specific SOS and native module logic
  - LLDB implementation of `IDebuggerDiagnostics`

- `WinDbgManager`
  - DbgEng lifecycle
  - WinDbg-specific command execution
  - WinDbg-specific SOS, reload, interrupt, recovery, and register capture
  - WinDbg implementation of `IDebuggerDiagnostics`

- analyzer / tool layer
  - depends on `IDebuggerManager` for core control
  - depends on `IDebuggerDiagnostics` for optional shared parity capabilities
  - never branches on `LldbManager` for a capability that should work on both debuggers

### Recommended request / result model for the coordinator

The coordinator should operate on explicit request / result objects rather than many loosely
related parameters.

Recommended request shape:

- session
- session ID
- user ID
- dump ID
- optional explicit absolute dump path for fresh open when the caller already resolved it
- optional persisted absolute dump path used only as a restore-compatibility fallback
- whether this is a fresh open or a restored reopen
- whether the caller is allowed to reuse the already tracked dump for restore / recovery

Recommended result shape:

- dump path
- metadata path (if any)
- executable path (if any)
- applied symbol path
- the logical symbol inputs that were honored
- the logical symbol inputs that were rejected or skipped intentionally
- whether session-scoped symbol intent was rehydrated or recomputed
- the local PDB/source-resolution search paths derived for the current dump
- whether source-resolution helpers were attached or refreshed from the current dump context
- whether the dump was detected as managed
- whether SOS was loaded
- normalized runtime / architecture / platform info when available
- whether caches must be cleared by the caller
- whether session state must be persisted by the caller
- timing / status information for user-facing messages and logs

The coordinator should **not** depend on `DebuggerSessionManager` directly. That would create an
unnecessary circular dependency once `SessionManager` starts calling the coordinator. The caller
should persist session state after applying the coordinator result.

### Async boundary decision

The current codebase has both async fresh-open paths and sync session-restore paths.

To avoid duplicating workflow logic:

- the coordinator should have one shared async core implementation
- the current sync restore entrypoint should become a thin sync adapter over that same core
  workflow until restore callers are migrated
- the plan must not create separate async and sync versions of the open pipeline with different
  behavior

### New shared interface

Introduce a shared optional-capability interface named `IDebuggerDiagnostics`.

Required capability surface:

- `TryGetPlatformInfo()`
- `TryGetDetectedRuntimeVersion()`
- `GetTopFrameRegisters(threadIds)`
- `GetFrameRegistersForThread(osThreadId)`

Return small typed DTOs instead of raw debugger-specific strings. The contract must cover:

- normalized platform info
- normalized runtime info
- top-frame register access
- per-frame register access for the faulting thread

This interface should be implemented by both managers, while `IDebuggerManager` remains stable.

---

## Implementation Plan

## Phase 1: Baseline Inventory and Responsibility Freeze

### Goal

Start with a precise inventory so the implementation does not drift into duplicated ownership or
silent scope expansion.

### Tasks

1. Create a capability checklist derived from the parity contract table above.
2. Mark each capability as:
   - already equivalent
   - partially equivalent
   - LLDB-only today
   - WinDbg-only today
3. Inventory every current metadata-path and metadata read/write call site, including:
   - `DumpController`
   - `DumpTools`
   - `SessionManager`
   - `SymbolTools`
   - `LldbManager`
4. Inventory every current dump-open or dump-reopen path, including:
   - fresh open from `DumpTools`
   - restored reopen from `SessionManager`
   - WinDbg internal recovery reopen
5. Inventory current LLDB-only user-facing tools or actions that should remain explicit
   exceptions, including:
   - `LoadVerifyCoreModules`
   - `verify_core_modules`
6. Inventory the current symbol-input model and classify each input kind:
   - default Microsoft symbol server
   - dump-derived local symbol directories
   - user-added local directories
   - user-added remote URLs
7. Inventory the current source-resolution input model and classify which inputs come from:
   - resolved dump path
   - resolved executable path
   - per-dump symbol directories
   - persisted session-scoped user intent
   - runtime/module directories
   - product-defined local caches such as `.dotnet/symbolcache` and `.nuget/packages`
   - Datadog managed-symbol directories under the current dump cache when present
8. Inventory every current operation that changes symbol or executable inputs after a dump is
   already open, including:
   - `ConfigureAdditionalSymbols`
   - symbol reload/upload flows
   - Datadog symbol prepare/download/clear flows
   - executable upload / metadata update flows
   and record whether each one currently refreshes debugger symbol state,
   `SequencePointResolver`, and `SourceLinkResolver`.
9. Decide and document which symbol-input kinds are:
   - persistent user intent
   - runtime-derived from the current dump
   - product-defined built-ins
10. Decide and document the supported behavior for each symbol-input kind on each debugger:
   - supported and persisted
   - supported only on one debugger and explicitly documented
   - rejected clearly
11. Inventory the current dump-path resolution behavior for restore and classify which source should
   be authoritative:
   - current storage-root resolution from dump ID + user ID
   - persisted absolute dump path as compatibility fallback only
12. Freeze and document these ownership rules:
   - `DumpOpenCoordinator` owns canonical open / reopen orchestration
   - `SessionManager` owns persistence and restore
   - `SymbolManager` owns session symbol paths
   - shared helpers own metadata, source-resolution composition, and `dotnet-symbol`
   - managers own debugger-specific execution
13. Freeze the non-goal inventory so later phases do not accidentally expand into full
   debugger-command parity.

### Deliverable

A stable capability checklist, exception inventory, and ownership map used by all later phases.

---

## Phase 2: Extract Shared Metadata and Symbol Helpers

### Goal

Move cross-debugger helper behavior out of `LldbManager` and out of ad hoc controller / tool code.

### New shared components

Add a new shared area:

- `DebuggerMcp/Symbols/PersistedSessionSymbolConfiguration.cs`
- `DebuggerMcp/Symbols/EffectiveSessionSymbolConfiguration.cs`
- `DebuggerMcp/SourceLink/SourceResolutionPathBuilder.cs`
- `DebuggerMcp/SourceLink/SourceResolutionStateRefresher.cs`
- `DebuggerMcp/Dumps/DumpMetadata.cs`
- `DebuggerMcp/Symbols/DotnetSymbolRunner.cs`
- `DebuggerMcp/Dumps/DumpMetadataStore.cs`
- `DebuggerMcp/Dumps/DumpAnalyzer.cs`
- `DebuggerMcp/Dumps/DumpAnalysisResult.cs`

### Tasks

1. Implement `DotnetSymbolRunner`:
   - find `dotnet-symbol` on Windows, Linux, and macOS
   - honor `DOTNET_SYMBOL_TOOL_PATH`
   - support Windows `.exe` discovery
   - include global tool locations for all supported OSes
   - run symbol prefetch with timeout
   - capture stdout and stderr safely
   - parse runtime version from both `/` and `\` path styles
2. Implement `DumpMetadataStore`:
   - resolve preferred metadata path
   - resolve existing metadata path
   - prefer `<dumpId>.json`
   - fall back to legacy `.metadata_<dumpId>.json`
   - load/save dump metadata
   - load/save runtime version
   - load/save symbol file list
   - load/save executable-path-related fields
3. Preserve existing metadata semantics:
   - upload creates the metadata file
   - open / restore enrich only computed fields when needed
   - enrichment does not overwrite user-facing seed fields
4. Introduce a structured `PersistedSessionSymbolConfiguration` model that stores only session-
   scoped user intent, such as:
   - user-added local directories
   - any supported session-scoped remote inputs
   Do not persist dump-derived directories in this model.
5. Introduce `EffectiveSessionSymbolConfiguration` as the required internal composition step that
   merges:
   - product-defined built-ins
   - persisted session-scoped user intent
   - dump-derived local symbol directories
6. Refactor symbol composition so `ConfigureAdditionalSymbols` augments the persisted session-scoped
   user intent instead of replacing previously derived dump-specific inputs.
7. Preserve current session-scoped semantics intentionally:
   - supported additional symbol inputs survive dump close
   - supported additional symbol inputs survive session restore
   - supported additional symbol inputs are cleared when the session is closed
   This plan does not add a new per-session clear API.
8. Persist supported session symbol configuration as part of session restoration state, with
   backward compatibility for existing persisted sessions that do not have the new fields.
   Extend `SessionMetadata` for this instead of introducing a second parallel persistence store.
9. Ensure session symbol configuration is restorable even when the session currently has no open
   dump, so users can configure supported symbol inputs before opening a dump and keep them across
   session restore.
10. Implement `SourceResolutionPathBuilder` so PDB/source-resolution paths are derived from:
   - the resolved dump path
   - the resolved executable path when present
   - the effective local symbol directories for the current dump
   - runtime/module directories from the current dump
   - user `.dotnet/symbolcache` and `.nuget/packages` directories when they exist
   - Datadog managed-symbol directories under the current dump cache when they exist
   Do not add unrelated global scans when the exact dump context is already known.
11. Refactor current source-resolution path construction to use the shared builder instead of ad hoc
    logic in:
   - `DumpTools`
   - `SessionManager`
   - `DebuggerToolsBase`
   - `PdbSearchPathBuilder` call sites
12. Implement `SourceResolutionStateRefresher` so an already open dump can rebuild and reattach
    `SequencePointResolver` and `SourceLinkResolver` from the current resolved dump context,
    current `ClrMdAnalyzer`, and current effective local symbol directories.
13. Refactor these callers to use the shared metadata store rather than ad hoc path logic:
   - `DumpController` upload / list / details / delete / upload-binary paths
   - `DumpTools`
   - `SessionManager`
   - `SymbolTools`
   - any metadata helper logic currently inside `LldbManager`
14. Refactor dump-derived symbol-directory resolution so it can use the known user-scoped dump
   context instead of scanning every user directory by dump ID when the caller already knows the
   dump owner and resolved dump path.
15. Move the shared dump metadata model out of `DumpController` namespace into a shared dumps area,
   then update call sites to use the shared type instead of `Controllers.DumpMetadata`.
16. Move `DumpAnalyzer` and `DumpAnalysisResult` out of controller-owned code if they are still
   living under `DumpController` implementation details, because both are used by non-controller
   paths.
17. For restore, resolve the dump path from dump ID and user ID under the current dump storage
    configuration. Use the persisted absolute path only as a backward-compatibility fallback when
    necessary.
18. For this parity plan, reject user-added remote symbol URLs on LLDB at configuration time with
   a clear message instead of silently accepting them. Do not introduce a generic remote-symbol
   materialization pipeline as part of this plan.
19. Refactor `LldbManager` to use `DotnetSymbolRunner` for shared `dotnet-symbol` behavior.
20. Refactor `DumpAnalyzer` to use the same shared `dotnet-symbol` discovery and runtime parsing
   helpers.
21. Preserve compatibility wrappers where useful so existing LLDB tests can migrate gradually.

### Deliverable

Shared metadata and `dotnet-symbol` behavior lives in dedicated helpers rather than being
reimplemented in multiple layers.

---

## Phase 3: Introduce the Canonical Open / Restore Coordinator

### Goal

Eliminate the divergence between fresh open and restored reopen.

### Canonical workflow

`DumpOpenCoordinator` should perform the following steps in this order for both fresh open and
restored reopen:

1. validate session and user ownership
2. initialize the debugger if it is not already initialized
3. enforce the one-dump-per-session rule
4. resolve dump path
5. resolve metadata path using the shared metadata store
6. load metadata if it exists
7. resolve optional executable path from metadata
8. configure session symbol paths through `SymbolManager`
9. create / resolve per-dump symbol cache directory
10. run shared symbol prefetch when enabled
11. build and apply debugger symbol path
12. open dump in debugger
13. attach ClrMD and source-resolution helpers from the current effective dump context when the
    dump/runtime supports them
14. load or verify SOS
15. fill missing metadata fields
16. update in-memory session state
17. apply cache invalidation rules
18. return a consistent result object that tells the caller what must be persisted

### Tasks

1. Add `DumpOpenCoordinator` as a shared service used by both `DumpTools` and `SessionManager`.
2. Move the current orchestration logic out of `DumpTools.OpenDump` into the coordinator.
3. Replace the hand-rolled reopen logic in `SessionManager.RestoreSessionFromDisk` with a call to
   the coordinator.
4. Keep the workflow single-sourced across async open and sync restore:
   - implement one async core workflow
   - keep the current sync restore entrypoint as a thin adapter over that async core until callers
     are migrated
5. Ensure restored sessions configure symbol paths **before** reopening the dump, not afterward.
6. Stop relying on the `Program` `OnSessionRestored` callback workaround once the coordinator owns
   reopen preparation.
7. Keep support for both metadata naming conventions through the shared metadata store.
8. Keep per-dump symbol cache path consistent across debuggers:
   - `{dumpDir}/.symbols_<dumpName>`
9. During restore, resolve the dump path from the current user ID + dump ID under the current
   storage root. Use persisted absolute path only if current storage-root resolution fails.
10. Reapply persisted supported session symbol configuration during restore before building the
   effective debugger symbol path.
11. Ensure supported session symbol configuration is restored even for sessions that currently have
   no open dump, so a later fresh open still sees the same logical symbol inputs.
12. Ensure dump-derived symbol directories are recomputed from the resolved dump context during each
    open or reopen instead of being read back from persisted session symbol state.
13. Build PDB/source-resolution paths from the same resolved dump context and effective local symbol
    directories used by the open workflow, then attach them through
    `SourceResolutionStateRefresher`.
14. Ensure ClrMD attachment and `SequencePointResolver` / `SourceLinkResolver` setup follow the
    same rules for fresh opens and restored reopens.
15. Preserve the current one-dump-per-session contract with explicit behavior:
   - opening the same tracked dump during restore / recovery is allowed
   - opening a different dump in an already-occupied session must fail clearly unless product
     scope changes later
16. Ensure open / reopen result messages are comparable for both debuggers and can surface symbol
    inputs that were intentionally rejected.
17. Ensure restore failure leaves the session in a consistent state:
    - session remains valid
    - dump-specific session state is cleared if reopen fails
    - stale caches are not left behind
18. Ensure the coordinator returns persistence and invalidation decisions to the caller instead of
    reaching back into `SessionManager` directly.
19. Register the coordinator and helper services in `AddDebuggerServices` so HTTP mode and stdio
    mode use the same composition.
20. Remove obsolete startup wiring once the new composition is in place:
    - delete the `Program`-level `OnSessionRestored` symbol callback after both startup paths use
      coordinator-driven restore, so it cannot remain as a second restore path

### Deliverable

There is exactly one canonical dump open / reopen workflow in the product.

---

## Phase 4: Introduce Shared Diagnostics and Decouple Consumers from `LldbManager`

### Goal

Make shared parity capabilities available without concrete LLDB dependencies.

### Tasks

1. Introduce `IDebuggerDiagnostics` without expanding `IDebuggerManager`.
2. Add small typed DTOs for normalized data such as platform info, runtime info, and register
   payloads.
3. Implement `IDebuggerDiagnostics` in `LldbManager` using existing behavior.
4. Update direct LLDB-dependent consumers to use the interface instead of `LldbManager`:
   - `DotNetCrashAnalyzer`
   - `ObjectInspectionTools`
   - `DatadogSymbolsTools`
5. Audit additional consumers for accidental LLDB-only assumptions and classify them:
   - `CrashAnalyzer`
   - `SecurityAnalyzer`
   - `PerformanceAnalyzer`
   - `DumpComparer`
   - `ProcessInfoExtractor`
   - `AiAnalysisOrchestrator`
   - `SessionTools`
6. Produce an explicit user-facing tool / analyzer parity inventory so the plan states which tools
   are part of the parity contract and which remain documented exceptions.
7. For the audited consumers, keep debugger-specific branches only when they represent legitimate
   command or parser differences. Remove only the branches that exist because shared capabilities
   were previously trapped inside `LldbManager`.
8. Normalize returned data:
   - same architecture strings
   - same register-name casing
   - same thread-ID conventions where possible
9. Keep explicit LLDB-only flows such as `LoadVerifyCoreModules` documented as exceptions rather
   than forcing them through the shared interface.

### Deliverable

Consumers can obtain shared diagnostics without depending on concrete LLDB types, while legitimate
debugger-specific parsing remains explicit and intentional.

---

## Phase 5: Bring WinDbg Symbol, Metadata, and SOS Behavior to Parity

### Goal

Make WinDbg participate in the same symbol / metadata / SOS product model as LLDB.

### Tasks

1. Add logging and state to `WinDbgManager`:
   - `ILogger<WinDbgManager>`
   - default `NullLogger` constructor
   - `_currentExecutablePath`
   - `_symbolCacheDirectory`
   - `_detectedRuntimeVersion`
   - `_lastSymbolPath`
2. Update `DebuggerFactory` to pass a logger into `WinDbgManager`.
3. Ensure WinDbg uses the per-dump symbol cache directory provided by the coordinator.
4. Ensure WinDbg symbol paths include:
   - Microsoft symbol server cache path from `SymbolManager`
   - dump-scoped symbol cache directory
   - dump-specific uploaded symbol directories
5. Implement WinDbg runtime detection:
   - first via ClrMD if available
   - else via `lmv m coreclr`
   - else via `lmv m clr`
6. Implement WinDbg architecture detection:
   - use DbgEng processor type APIs
   - normalize to `x64`, `x86`, `arm64`, `arm`
7. Ensure metadata completion can consume WinDbg-provided runtime and architecture hints through
   the shared metadata flow.
8. Implement WinDbg SOS discovery and load sequence:
   - `.loadby sos coreclr`
   - `.loadby sos clr`
   - `.load <resolved sos.dll>` as fallback
9. Implement WinDbg SOS search order:
   - `SOS_PLUGIN_PATH`
   - per-dump symbol cache
   - dotnet-sos user install
   - .NET runtime folders
   - .NET Framework folders
10. Validate SOS using a real command such as `!eeversion` or `!soshelp`.
11. Set `IsSosLoaded` only after validation succeeds.
12. Ensure `IsDotNetDump`, `CurrentDumpPath`, and related state remain coherent for fresh open,
    restore, and recovery flows.

### Deliverable

WinDbg participates in the same symbol / metadata / SOS product model as LLDB.

---

## Phase 6: Implement WinDbg Executable Handling and Open-Pipeline Parity

### Goal

Make WinDbg honor standalone executable metadata in the same user-visible way as LLDB.

### Tasks

1. When metadata contains `ExecutablePath`, pass it into the WinDbg open flow through the shared
   coordinator.
2. In WinDbg, apply executable search behavior before or immediately after opening:
   - `.exepath+` with the executable directory
   - feed the same executable directory into the shared source-resolution inputs
   - do not add a second WinDbg-only executable-specific symbol-path rule outside the shared model
3. If a custom executable path is missing or unreadable:
   - log it
   - continue opening the dump
   - do not fail the open operation solely for that reason
4. After opening, reload modules when needed:
   - prefer a targeted reload strategy
   - use `.reload /f` or `.reload /i` only where justified and documented
5. Ensure the same executable-path logic is used during:
   - fresh open
   - restored reopen
   - WinDbg internal recovery reopen
6. Ensure the open sequence logs key steps consistently with LLDB:
   - symbol path applied
   - executable path applied
   - dump opened
   - runtime detected
   - SOS loaded or failed

### Deliverable

WinDbg open behavior is as close as possible to LLDB from the user’s point of view.

---

## Phase 7: Implement WinDbg Platform and Register Diagnostics

### Goal

Provide the same diagnostics surface to tools and analyzers that LLDB already provides.

### Tasks

1. Implement normalized platform diagnostics for WinDbg:
   - OS: always `Windows`
   - architecture from DbgEng
   - runtime version from ClrMD or `lmv`
2. Implement top-frame register capture for WinDbg:
   - parse `~` to map OS TID to WinDbg thread index
   - select the relevant thread
   - parse `r` output
   - return normalized register names and values
3. Implement per-frame register capture for the faulting thread:
   - map OS TID to WinDbg thread index
   - capture stack with `kv`
   - use `.frame` and `r` per frame
   - correlate registers with stack pointer where possible
4. Normalize data formats so they match LLDB consumers:
   - lower-case register names
   - consistent hex formatting
   - normalized thread identifiers
5. Implement `IDebuggerDiagnostics` in `WinDbgManager` using the normalized DTOs introduced
   earlier.
6. Update any remaining consumer paths that now can use the shared diagnostics interface once the
   WinDbg implementation exists.

### Deliverable

Register-enriched analysis and platform-aware tooling work on WinDbg without special-casing LLDB.

---

## Phase 8: Add Resilient WinDbg Command Execution and Recovery

### Goal

Make WinDbg failures and timeouts behave as predictably as LLDB failures and timeouts.

### Chosen approach

Use a dedicated STA thread for DbgEng operations and marshal all debugger calls through that
thread.

### Tasks

1. Create a WinDbg execution queue bound to a dedicated STA thread.
2. Ensure all DbgEng COM calls run on that thread.
3. Add configurable command-timeout support for WinDbg through:
   - `WINDBG_COMMAND_TIMEOUT_SECONDS`
4. On timeout:
   - call `SetInterrupt`
   - wait briefly for the engine to return to a clean state
   - surface a clear timeout message
5. Detect engine corruption or repeated COM failures.
6. Implement recovery:
   - release COM objects
   - reinitialize DbgEng
   - reapply symbol path and executable path state
   - reopen the dump if one was open
7. Preserve these fields through recovery:
   - current dump path
   - current executable path
   - last symbol path
   - symbol cache directory
   - detected runtime version if already known
8. Ensure recovery keeps session persistence coherent:
   - `CurrentDumpPath` remains accurate
   - the persisted session can still reopen later
9. Apply cache invalidation rules after recovery if the reopened state cannot be proven identical.

### Deliverable

WinDbg behaves like a managed component of the product rather than a thin best-effort wrapper.

---

## Phase 9: Align User-Facing Tool Behavior and Explicit Exceptions

### Goal

Make the tool layer behave as a product layer, not as a mix of parity features and accidental
engine leakage.

### Tasks

1. Update `DatadogSymbolsTools`:
   - use shared diagnostics for platform info when available
   - use the correct debugger command per debugger where fallback parsing is still needed
   - avoid assuming `image list` exists everywhere
2. Update `ObjectInspectionTools`:
   - obtain top-frame registers through the shared diagnostics interface
3. Update `DotNetCrashAnalyzer`:
   - remove LLDB-only faulting-thread register logic that is now covered by shared diagnostics
4. Update `SymbolTools` behavior and messages so they reflect the chosen support matrix for
   additional symbol inputs and never imply support that is not real on the current debugger.
5. Update symbol-changing and executable-affecting flows so that, when a dump is already open and
   the changed input is supported live, they:
   - reapply the debugger symbol path when the debugger consumes that input live
   - rebuild `SequencePointResolver` and `SourceLinkResolver` through
     `SourceResolutionStateRefresher`
   - clear cached report state derived from the previous source-resolution inputs
   Cover at least:
   - `ConfigureAdditionalSymbols`
   - symbol reload / upload flows
   - Datadog symbol load / clear flows
   - executable metadata changes that affect source lookup
6. Review user-facing tool descriptions and docs for parity-sensitive areas:
   - MCP tool descriptions
   - workflow guide
   - resource docs
7. Keep explicit exceptions explicit:
   - `verify_core_modules` remains LLDB-only unless scope changes
   - raw `exec` remains a debugger-specific escape hatch, not part of the parity contract
8. Review remaining direct debugger-type branches in MCP tools and analyzers and either:
   - keep them with a documented reason, or
   - replace them with shared capability use

### Deliverable

The tool layer exposes a coherent product model, and the remaining debugger-specific exceptions
are deliberate and documented.

---

## Phase 10: Tests

### Goal

Lock in parity behavior with deterministic tests.

### Test strategy

Most tests should remain pure unit tests and avoid requiring WinDbg or LLDB to be installed.
Windows-only COM interactions can be covered by guarded smoke tests, but parser and helper
behavior should be fully unit tested.

### Required test additions

1. `DotnetSymbolRunnerDiscoveryTests`
   - Windows `.exe` discovery
   - `DOTNET_SYMBOL_TOOL_PATH`
   - user global tool locations
2. `DotnetSymbolRunnerRuntimeParsingTests`
   - runtime version parsing for `/` and `\` path styles
3. `DumpMetadataStoreTests`
   - metadata path resolution
   - legacy fallback
   - runtime version persistence
   - symbol-file-list persistence
   - executable-path persistence
   - preservation of user-facing fields during enrichment
   - compatibility with the shared `DumpMetadata` model after moving it out of controller code
4. `DumpOpenCoordinatorFreshOpenTests`
   - metadata resolution
   - executable path resolution
   - symbol preparation sequencing
   - consistent result behavior
   - effective symbol path preserves dump-derived inputs when additional session inputs are present
5. `DumpOpenCoordinatorRestoreTests`
   - restored reopen uses the same sequencing as fresh open
   - symbol path is applied before reopen
   - ClrMD / source-resolution setup follows the same rules
   - persisted supported session symbol configuration is restored before reopen
   - restore prefers current dump-path resolution from user ID + dump ID over stale absolute-path
     metadata when both are available
6. `SessionRestoreParityTests`
   - restored sessions reopen dumps using the canonical coordinator
   - failed reopen leaves session state consistent
   - sync restore path uses the same core workflow as async open
   - older persisted sessions without new symbol-configuration fields still restore safely
   - sessions with supported symbol inputs but no open dump still restore those inputs correctly
7. `DebuggerSessionCacheInvalidationTests`
   - dump change clears cached report
   - dump change clears `SourceLinkResolver`
   - symbol changes clear cached report and `SourceLinkResolver`
   - executable-path changes that affect source resolution clear cached report and
     `SourceLinkResolver`
8. `WinDbgManagerPlatformTests`
   - DbgEng processor-type mapping to normalized architecture
9. `WinDbgManagerSosSearchTests`
   - Windows SOS search order
10. `WinDbgManagerThreadMapTests`
    - `~` parsing to map OS TID to debugger thread index
11. `WinDbgManagerRegisterParsingTests`
    - `r` output parsing for x64, x86, arm64
12. `WinDbgManagerStackParsingTests`
    - `kv` parsing and stack-pointer extraction
13. `WinDbgManagerRecoveryTests`
    - timeout and recovery behavior without requiring real WinDbg
14. `DatadogSymbolsParityTests`
   - WinDbg paths do not assume `image list`
15. Update existing LLDB tests after helper extraction
    - LLDB helper tests should move to shared-helper tests where appropriate
16. `DumpAnalyzerSharedLocationTests`
   - non-controller callers can use the shared analyzer and result types without referencing
     controller-owned implementation details
17. `SessionSymbolConfigurationTests`
    - configuring additional paths preserves existing dump-derived symbol inputs
    - persisted session-scoped user intent round-trips through persistence
    - LLDB remote URL inputs follow the chosen explicit behavior (reject or materialize), never
      silent ignore
    - dump-derived symbol-directory resolution uses the known user-scoped dump context when available
    - dump-derived directories are recomputed at open time and are not read back from persisted
      session-scoped symbol intent
18. `SourceResolutionPathBuilderTests`
    - effective local symbol directories feed PDB/source-resolution paths
    - resolved executable directory feeds PDB/source-resolution paths when present
    - user `.dotnet/symbolcache` and `.nuget/packages` directories are included when present
    - Datadog managed-symbol directories under the current dump cache are included when present
    - stale root-level or cross-user paths are not introduced when exact dump context is known
19. `SourceResolutionStateRefresherTests`
    - fresh open attaches `SequencePointResolver` and `SourceLinkResolver` from the current
      effective dump context
    - live symbol changes rebuild source-resolution helpers without requiring dump reopen
    - live executable-path changes rebuild source-resolution helpers without requiring dump reopen

### Deliverable

Parity behavior is protected by tests, not only by documentation.

---

## Phase 11: Documentation

### Goal

Make the parity model discoverable to maintainers and users.

### Tasks

1. Update `README.md`:
   - explain that dump opening and analysis aim to behave the same across debuggers
   - mention restored-session parity, automatic symbol handling, and metadata enrichment
2. Update `ADVANCED.md`:
   - document `DOTNET_SYMBOL_TOOL_PATH`
   - document `SOS_PLUGIN_PATH`
   - document WinDbg timeout configuration if added
   - document the cache invalidation model at a high level if user-facing behavior depends on it
3. Update user-facing resource docs where parity expectations matter:
   - `mcp_tools.md`
   - workflow guide
   - relevant debugger reference docs
4. Add a short developer note describing:
   - ownership boundaries
   - `DumpOpenCoordinator`
   - `IDebuggerDiagnostics`
   - why parity means user-visible parity, not command parity
   - which LLDB-only features remain explicit exceptions

### Deliverable

The implementation model, user-visible behavior, and explicit exceptions are documented rather
than left as tribal knowledge.

---

## Phase 12: Validation and Rollout

### Goal

Validate parity in progressively more realistic environments.

### Validation steps

1. Run `dotnet build`
2. Run `dotnet test`
3. Validate representative LLDB fresh-open flow:
   - open managed dump
   - verify symbol-cache usage
   - verify metadata fields
   - verify SOS-loaded managed commands
4. Validate representative WinDbg fresh-open flow:
   - open managed dump
   - verify symbol-cache usage
   - verify metadata fields
   - verify SOS-loaded managed commands
5. Validate restored-session flow on both debugger families:
   - persist a session with an open dump
   - restore the session
   - verify symbol path, SOS state, and ClrMD-dependent behavior
6. Validate restoration of user-added supported symbol inputs:
   - configure additional symbol inputs
   - persist and restore the session
   - verify the same supported inputs are still active
7. Validate restore under a changed dump-storage root or equivalent simulated environment so the
   system proves it can re-resolve dumps from current storage configuration instead of depending
   only on stale absolute paths.
8. Validate at least one standalone-app scenario where executable metadata matters.
9. Validate at least one symbol-change scenario:
   - upload custom symbols or Datadog symbols
   - verify cache invalidation
   - verify source-resolution state is rebuilt without closing and reopening the dump
10. Validate at least one live executable-metadata change scenario on an already open dump:
   - apply or update executable metadata that affects source lookup
   - verify source-resolution state is rebuilt without reopening the dump
11. Validate source-resolution parity for a managed dump:
   - verify PDB/source paths are rebuilt from the same effective dump context on fresh open and
     restore
   - verify supported local symbol inputs affect source resolution in the same way after restore
   - verify product-defined local caches such as `.dotnet/symbolcache` and `.nuget/packages`
     participate consistently when present
12. Validate explicit behavior for unsupported symbol inputs on LLDB under the chosen support
    matrix.
13. Validate analyzer / tool parity for:
   - crash analysis
   - ClrStack
   - object inspection
   - Datadog symbol preparation
14. Validate at least one WinDbg timeout / recovery scenario or a deterministic simulated recovery
   path.

### Deliverable

Parity is demonstrated with actual end-to-end scenarios, not inferred from code alone.

---

## Recommended Implementation Order

The recommended order is:

1. Phase 1
2. Phase 2
3. Phase 3
4. Phase 4
5. Phase 5
6. Phase 6
7. Phase 7
8. Phase 8
9. Phase 9
10. Phase 10
11. Phase 11
12. Phase 12

This order matters. Shared helpers and the canonical open / restore coordinator should exist
before large WinDbg feature work, otherwise the same orchestration and consumers will have to be
refactored twice.

---

## Risks and Mitigations

### `dotnet-symbol` behavior on Windows dumps may be incomplete

- Mitigation:
  - standardize on `dotnet-symbol` first
  - keep fallback support for another Windows-only helper only if real-world gaps require it
  - avoid making fallback complexity part of the primary design unless it is proven necessary

### WinDbg output parsing can be brittle

- Mitigation:
  - isolate parsers into pure helper methods
  - test against multiple sample outputs
  - normalize outputs aggressively

### DbgEng apartment / threading issues can destabilize the server

- Mitigation:
  - use a dedicated STA thread
  - do not spread COM calls across arbitrary thread-pool threads

### Fresh open and restored reopen can drift apart again

- Mitigation:
  - keep both paths behind `DumpOpenCoordinator`
  - do not let `SessionManager` grow a second reopen pipeline
  - test restore sequencing explicitly

### Metadata ownership can drift and cause silent field loss

- Mitigation:
  - centralize metadata read / write in `DumpMetadataStore`
  - preserve the distinction between seed fields and computed enrichment fields
  - test that enrichment does not overwrite unrelated user data

### Symbol configuration can remain lossy and silently change user intent

- Mitigation:
  - separate persisted session-scoped user intent from dump-derived effective symbol state
  - represent effective symbol inputs as structured logical state instead of a prebuilt path string
  - persist only supported session-scoped user intent across restore
  - reject or intentionally materialize unsupported inputs instead of silently dropping them
  - test `ConfigureAdditionalSymbols` after dump open so dump-derived inputs are not lost

### Source-resolution paths can drift from debugger symbol state

- Mitigation:
  - derive PDB/source-resolution paths from the same resolved dump context and effective local
    symbol directories
  - stop using separate ad hoc path builders that do not know about session-scoped symbol intent
  - test fresh open and restore against the same managed dump and compare resulting source inputs

### Live symbol changes can leave `SequencePointResolver` or `SourceLinkResolver` stale

- Mitigation:
  - introduce `SourceResolutionStateRefresher` and use it from open, symbol-change, and
    executable-change flows
  - treat live source-resolution refresh as a product requirement, not a best-effort cache clear
  - test that symbol/executable changes on an already open dump improve source results without a
    reopen

### Restore can depend on stale absolute dump paths and break after storage changes

- Mitigation:
  - re-resolve dump paths from current user ID + dump ID under the current storage configuration
  - use persisted absolute paths only as compatibility fallback
  - test restore behavior when the old absolute path is no longer the canonical location

### Dump-derived symbol resolution can bleed across user scopes

- Mitigation:
  - use known user-scoped dump context when resolving dump-derived symbol directories
  - avoid scanning all user directories by dump ID when the caller already knows the dump owner
  - test symbol resolution with multiple users and overlapping dump IDs if relevant

### New shared services can accidentally create circular dependencies

- Mitigation:
  - keep `DumpOpenCoordinator` independent of `DebuggerSessionManager`
  - return state changes for callers to persist instead of persisting from inside the coordinator
  - verify service registration and constructor dependencies before implementation expands

### Async open and sync restore can diverge if they get separate implementations

- Mitigation:
  - keep one core workflow
  - allow only a thin sync adapter if current restore APIs still require it
  - test that async open and sync restore follow the same sequencing

### Cache invalidation bugs can leave reports or source resolution stale

- Mitigation:
  - define invalidation rules explicitly
  - centralize invalidation at dump-open and symbol-change boundaries
  - test invalidation behavior directly

### Refactor risk to LLDB behavior

- Mitigation:
  - move shared logic out in small steps
  - keep LLDB wrappers during migration
  - preserve current LLDB tests until shared-helper tests fully replace them

### Timeout and recovery logic can leave state inconsistent

- Mitigation:
  - make recovery state explicit
  - preserve dump path, executable path, symbol path, and cache path
  - clear caches if identical restored state cannot be proven
  - test recovery paths without real debugger dependencies where possible

---

## Definition of Done

This plan is complete only when all of the following are true:

- A user can open a dump on LLDB or WinDbg and get the same class of behavior.
- A restored session reopens dumps through the same preparation model as a fresh open.
- The one-dump-per-session model behaves consistently across debuggers, restore, and recovery.
- Symbol resolution and per-dump cache layout follow the same product model on both debuggers.
- Supported session symbol inputs persist across restore, and unsupported inputs are never silently
  ignored.
- Source-resolution inputs are built from the same effective dump context on both debuggers,
  including the shared local built-ins defined by this plan.
- Supported live symbol or executable changes on an already open dump refresh debugger and
  source-resolution state without requiring a reopen.
- Dump metadata is resolved, persisted, and enriched consistently on both debugger paths.
- Upload-time and open-time metadata responsibilities are explicit and preserved.
- SOS auto-load works comparably for managed dumps on both debuggers.
- Tools and analyzers no longer depend directly on `LldbManager` for shared capabilities.
- WinDbg supports platform info and register diagnostics through the same interface as LLDB.
- WinDbg command execution has timeouts and recovery behavior.
- Cache invalidation for reports and source resolution is defined, implemented, and tested.
- Explicit LLDB-only exceptions remain documented rather than being accidental gaps.
- Tests cover the shared-helper logic, canonical open / restore flow, and WinDbg parser /
  diagnostic logic.
- Documentation explains the parity model, ownership boundaries, and the key configuration knobs.
- The user-facing tool / analyzer parity inventory is documented so remaining exceptions are
  explicit.

If any of the above is missing, parity is not complete.
