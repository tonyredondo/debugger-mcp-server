# Debugger MCP Server — Compact MCP Tool List (Canonical)

This server intentionally exposes a **small** MCP tool surface to fit within common MCP client limits.

If you see older documentation referencing tools like `create_session` or `open_dump`, those legacy tool names have been removed. Use the compact tools below.

## Tool Set (11 total)

### 1) `session`
Manage sessions.

- **create**: `session(action: "create", userId: "...")`
- **list**: `session(action: "list", userId: "...")`
- **close**: `session(action: "close", sessionId: "...", userId: "...")`
- **restore**: `session(action: "restore", sessionId: "...", userId: "...")`
- **debugger_info**: `session(action: "debugger_info", sessionId: "...", userId: "...")`

### 2) `dump`
Open/close a dump inside a session.

- **open**: `dump(action: "open", sessionId: "...", userId: "...", dumpId: "...")`
- **close**: `dump(action: "close", sessionId: "...", userId: "...")`

### 3) `exec`
Execute a raw debugger command (WinDbg/LLDB syntax). Use as a last resort.

- `exec(sessionId: "...", userId: "...", command: "image list")`

### 4) `report`
Generate reports (returns report content).

- **full**: `report(action: "full", sessionId: "...", userId: "...", format: "json", includeWatches: true)`
- **summary**: `report(action: "summary", sessionId: "...", userId: "...", format: "markdown")`

### 5) `analyze`
Run analysis on the currently open dump.

- **crash**: `analyze(kind: "crash", sessionId: "...", userId: "...")`
- **dotnet_crash**: `analyze(kind: "dotnet_crash", sessionId: "...", userId: "...")`
- **performance**: `analyze(kind: "performance", sessionId: "...", userId: "...")`
- **cpu**: `analyze(kind: "cpu", sessionId: "...", userId: "...")`
- **allocations**: `analyze(kind: "allocations", sessionId: "...", userId: "...")`
- **gc**: `analyze(kind: "gc", sessionId: "...", userId: "...")`
- **contention**: `analyze(kind: "contention", sessionId: "...", userId: "...")`
- **security**: `analyze(kind: "security", sessionId: "...", userId: "...")`
- **security capabilities**: `analyze(kind: "security", action: "capabilities")`

### 6) `compare`
Compare two sessions (each must have a dump open).

- **dumps**: `compare(kind: "dumps", baselineSessionId: "...", baselineUserId: "...", targetSessionId: "...", targetUserId: "...")`
- **heaps**: `compare(kind: "heaps", ...)`
- **threads**: `compare(kind: "threads", ...)`
- **modules**: `compare(kind: "modules", ...)`

### 7) `watch`
Manage watch expressions (watches are associated with the currently open dump in the session).

- **add**: `watch(action: "add", sessionId: "...", userId: "...", expression: "0x1234", description: "optional")`
- **list**: `watch(action: "list", sessionId: "...", userId: "...")`
- **evaluate_all**: `watch(action: "evaluate_all", sessionId: "...", userId: "...")`
- **evaluate**: `watch(action: "evaluate", sessionId: "...", userId: "...", watchId: "...")`
- **remove**: `watch(action: "remove", sessionId: "...", userId: "...", watchId: "...")`
- **clear**: `watch(action: "clear", sessionId: "...", userId: "...")`

### 8) `symbols`
Symbol management.

- **get_servers**: `symbols(action: "get_servers")`
- **configure_additional**: `symbols(action: "configure_additional", sessionId: "...", userId: "...", additionalPaths: "..., ...")`
- **clear_cache**: `symbols(action: "clear_cache", userId: "...", dumpId: "...")`
- **reload**: `symbols(action: "reload", sessionId: "...", userId: "...")`
- **verify_core_modules**: `symbols(action: "verify_core_modules", sessionId: "...", userId: "...", moduleNames: "libcoreclr.so,libclrjit.so")`

### 9) `source_link`
Source link utilities for the current session/dump.

- **resolve**: `source_link(action: "resolve", sessionId: "...", userId: "...", sourceFile: "/_/src/...", lineNumber: 123)`
- **info**: `source_link(action: "info", sessionId: "...", userId: "...")`

### 10) `inspect`
ClrMD/SOS helpers for inspection.

- **object**: `inspect(kind: "object", sessionId: "...", userId: "...", address: "0x...", methodTable: "0x...", maxDepth: 5)`
- **module**: `inspect(kind: "module", sessionId: "...", userId: "...", address: "0x...")`
- **modules**: `inspect(kind: "modules", sessionId: "...", userId: "...")`
- **lookup_type**: `inspect(kind: "lookup_type", sessionId: "...", userId: "...", typeName: "Namespace.Type", moduleName: "*")`
- **lookup_method**: `inspect(kind: "lookup_method", sessionId: "...", userId: "...", typeName: "Namespace.Type", methodName: "Method")`
- **clr_stack**: `inspect(kind: "clr_stack", sessionId: "...", userId: "...", includeArguments: true, includeLocals: true)`
- **load_sos**: `inspect(kind: "load_sos", sessionId: "...", userId: "...")`

### 11) `datadog_symbols`
Datadog symbol workflows.

- **prepare**: `datadog_symbols(action: "prepare", sessionId: "...", userId: "...", loadIntoDebugger: true)`
- **download**: `datadog_symbols(action: "download", sessionId: "...", userId: "...", commitSha: "...", targetFramework: "net8.0")`
- **list_artifacts**: `datadog_symbols(action: "list_artifacts", commitSha: "...")`
- **get_config**: `datadog_symbols(action: "get_config")`
- **clear**: `datadog_symbols(action: "clear", sessionId: "...", userId: "...", clearApiCache: false)`

