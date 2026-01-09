# Debugger MCP Server

A cross-platform MCP (Model Context Protocol) server for controlling debuggers (WinDbg/LLDB) and analyzing memory dumps programmatically, with:

- An optional HTTP API for dump uploads, symbol management, and report download
- A companion CLI (`dbg-mcp`) for interactive analysis

If you’re looking for the deeper reference material (Docker multi-platform, Claude config, full env-var reference, architecture, etc.), see `ADVANCED.md`.

---

## Quick Start (recommended)

### 1) Build from source

```bash
git clone https://github.com/tonyredondo/debugger-mcp-server.git
cd debugger-mcp-server
dotnet build -c Release
```

### 2) Run the server (MCP-over-HTTP + HTTP API)

```bash
cd DebuggerMcp
dotnet run -- --mcp-http
```

### 3) Use the CLI to upload + analyze a dump

```bash
cd DebuggerMcp.Cli
dotnet run
```

In the `dbg-mcp` shell:

```text
dbg-mcp> connect http://localhost:5000
dbg-mcp> dumps upload ./crash.dmp
dbg-mcp> open <dumpId>
dbg-mcp> analyze crash -o ./crash.json
dbg-mcp> report -o ./crash-report.md
```

Optional AI analysis (requires MCP sampling + LLM config in the CLI):

```text
dbg-mcp> analyze ai -o ./crash-ai.json --refresh
```

See `DebuggerMcp.Cli/README.md` for LLM provider setup and `DebuggerMcp/Resources/analyze_ai.md` for the `analyze ai` deep dive.

---

## Requirements

### Operating system

- Windows 10/11 or Windows Server 2016+
- Linux (any modern distro)
- macOS 10.15+

### Software

- **.NET 10 SDK**
- Debugger backend:
  - Windows: WinDbg / Debugging Tools for Windows
  - Linux/macOS: LLDB (SOS is auto-loaded for .NET dumps when available)

### Install .NET 10

```bash
# Download and install from:
https://dotnet.microsoft.com/download/dotnet/10.0

# Or use the install script (Linux/macOS):
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
```

For OS-specific debugger installation and Docker images, see `ADVANCED.md`.

---

## Documentation

### MCP resources (served by the server as `debugger://...`)

| Resource URI | Name | Description |
|--------------|------|-------------|
| `debugger://mcp-tools` | MCP Tools | Canonical compact MCP tool list (11 tools) |
| `debugger://workflow-guide` | Workflow Guide | Complete workflow for analyzing memory dumps |
| `debugger://analysis-guide` | Analysis Guide | Crash analysis, .NET analysis, and dump comparison features |
| `debugger://windbg-commands` | WinDbg Commands Reference | Common WinDbg commands for crash analysis |
| `debugger://lldb-commands` | LLDB Commands Reference | Common LLDB commands for macOS/Linux |
| `debugger://sos-commands` | .NET SOS Commands Reference | SOS commands for .NET debugging |
| `debugger://troubleshooting` | Troubleshooting Guide | Solutions to common issues |
| `debugger://cli-guide` | CLI Guide | Using the dbg-mcp command-line client |

### Additional repository docs (not exposed as `debugger://` resources)

- `ADVANCED.md` — Docker, Claude config, full env vars, architecture/testing
- `ANALYSIS_EXAMPLES.md` — Crash analysis JSON output examples
- `DebuggerMcp/Resources/analyze_ai.md` — Deep dive into `analyze(kind="ai")` (sampling, checkpoints, evidence/hypotheses, judge pass)
- `DebuggerMcp.Cli/llmagent.md` — Deep dive into `llmagent` (baseline enforcement, checkpoints/evidence, juror pass, loop guards)
- `DebuggerMcp.Cli/README.md` — Full CLI reference

---

## Server Modes

- MCP (stdio): `cd DebuggerMcp && dotnet run`
- HTTP API only: `cd DebuggerMcp && dotnet run -- --http`
  - Alias: `--api`
- MCP over HTTP/SSE + HTTP API: `cd DebuggerMcp && dotnet run -- --mcp-http`

Full details (Docker/multi-platform, Claude Desktop/Cline config, workflow curl examples) are in `ADVANCED.md`.

---

## MCP Tools Available

The server intentionally exposes a compact MCP tool surface (11 tools). The canonical reference is `DebuggerMcp/Resources/mcp_tools.md` (also served as `debugger://mcp-tools`).

| Tool | Purpose |
|------|---------|
| `session` | Create/list/restore/close sessions |
| `dump` | Open/close dumps in a session |
| `analyze` | Crash/.NET/perf/security analysis |
| `compare` | Compare two sessions/dumps |
| `report` | Full/summary reports (json/markdown/html) |
| `watch` | Add/list/evaluate/remove watches |
| `inspect` | ClrMD/SOS helpers (object/module/clr_stack/load_sos) |
| `symbols` | Symbol servers/config/cache/reload |
| `source_link` | Resolve Source Link URLs/info |
| `datadog_symbols` | Datadog symbol workflows |
| `exec` | Raw debugger command (last resort) |

Quick workflow:
```text
1. session(action="create", userId="user1") → sessionId
2. dump(action="open", sessionId, userId="user1", dumpId="abc123")
3. analyze(kind="crash", sessionId, userId="user1")
4. report(action="full", sessionId, userId="user1", format="html")
5. session(action="close", sessionId, userId="user1")
```

---

## HTTP API Endpoints

### Dump Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/dumps/upload` | Upload a dump file |
| `GET` | `/api/dumps/{userId}/{dumpId}` | Get dump information |
| `GET` | `/api/dumps/user/{userId}` | List all dumps for a user |
| `POST` | `/api/dumps/{userId}/{dumpId}/binary` | Upload an executable/binary for a dump |
| `DELETE` | `/api/dumps/{userId}/{dumpId}` | Delete a dump |
| `GET` | `/api/dumps/stats` | Get session and storage statistics |
| `POST` | `/api/dumps/compare` | Compare two dumps (via HTTP API) |
| `GET` | `/api/dumps/{userId}/{dumpId}/report` | Generate and download a report |

### Symbol Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/symbols/upload` | Upload a single symbol file |
| `POST` | `/api/symbols/upload-batch` | Upload multiple symbol files |
| `POST` | `/api/symbols/upload-zip` | Upload a ZIP archive of symbols |
| `GET` | `/api/symbols/dump/{dumpId}` | List symbols for a dump |
| `GET` | `/api/symbols/dump/{dumpId}/exists` | Check if dump has symbols |
| `DELETE` | `/api/symbols/dump/{dumpId}` | Delete symbols for a dump |
| `GET` | `/api/symbols/servers` | List available symbol servers |

### Server Information

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health` | Health check endpoint |
| `GET` | `/info` | Server host information (OS, arch, Alpine status) |
| `GET` | `/api/server/capabilities` | Detailed server capabilities |
| `GET` | `/api/server/info` | Brief server info summary |

---

## Configuration (key knobs)

For the full configuration reference, see `ADVANCED.md`. Key analysis/integration knobs include:

- `GITHUB_API_ENABLED`, `GITHUB_TOKEN`, `GH_TOKEN` (optional commit/release enrichment)
- `SKIP_HEAP_ENUM`, `SKIP_SYNC_BLOCKS` (heap/sync-block enumeration safety valve)
- `DEBUGGERMCP_SOURCE_CONTEXT_ROOTS` (local source context snippet roots)
- `DATADOG_TRACE_SYMBOLS_ENABLED`, `DATADOG_TRACE_SYMBOLS_PAT`, `DATADOG_TRACE_SYMBOLS_CACHE_DIR`, `DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS`, `DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE` (Datadog symbol workflows)

---

## Contributing

See `CONTRIBUTING.md`.

## License

MIT.

## Support

- GitHub Issues: https://github.com/tonyredondo/debugger-mcp-server/issues

---

Note: This is a debugging tool. Use responsibly and only analyze dumps you have permission to access.

