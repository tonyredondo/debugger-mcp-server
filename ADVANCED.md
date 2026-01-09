# Debugger MCP Server — Advanced Guide

This document contains the deeper reference material that used to live in the root `README.md`:

- Server modes (stdio / HTTP / MCP-over-HTTP)
- Docker (multi-platform variants)
- Claude Desktop / Cline MCP configuration
- Full environment variable reference + security configuration
- Workflow examples (curl uploads, symbols)
- Platform command cheat-sheets
- Architecture overview
- Testing + coverage

If you’re just getting started, begin with `README.md`.

---

## Server Modes

The server can run in several modes depending on how you want to connect:

### MCP Mode (stdio) — local use

Runs the MCP server over stdin/stdout (useful for Claude Desktop / editor integrations).

```bash
cd DebuggerMcp
dotnet run
```

### HTTP API Mode — upload API only

Runs only the HTTP API (uploads, symbols, reports).

```bash
cd DebuggerMcp
dotnet run -- --http
```

Alias: `--api`

### MCP HTTP Mode — unified (MCP + upload API)

Runs MCP over HTTP/SSE **and** the HTTP API in the same process.

```bash
cd DebuggerMcp
dotnet run -- --mcp-http
```

This mode runs both the MCP server (via HTTP/SSE) and the Upload API in the same process on port 5000 by default.

---

## Docker (Multi-Platform)

The `docker-compose.yml` provides multiple server variants for cross-platform dump analysis:

| Service | Port | Architecture | Distribution |
|---------|------|--------------|--------------|
| `debugger-mcp-server` | 5000 | arm64 | Debian |
| `debugger-mcp-server-alpine` | 5001 | arm64 | Alpine |
| `debugger-mcp-server-x64` | 5002 | x64 | Debian |
| `debugger-mcp-server-alpine-x64` | 5003 | x64 | Alpine |

```bash
# Start all servers
docker-compose up -d

# Initialize CLI config for all servers
dbg-mcp server init

# List servers and their capabilities
dbg-mcp server list
```

Why multiple servers?
- Architecture matching: ARM64 dumps require ARM64 servers, x64 dumps require x64 servers
- Alpine vs Debian: Alpine-based .NET dumps often require Alpine servers for correct symbol/runtime matching (musl vs glibc)
- x64 safety valves: the x64 variants can enable heap-enum safety valves under emulation (see env vars below)

---

## Claude Desktop / Cline MCP Configuration

Add to your MCP configuration file:

### Local Mode (stdio)

Windows:
```json
{
  "mcpServers": {
    "debugger-local": {
      "command": "C:\\path\\to\\DebuggerMcp.exe",
      "args": []
    }
  }
}
```

Linux/macOS:
```json
{
  "mcpServers": {
    "debugger-local": {
      "command": "/path/to/DebuggerMcp",
      "args": []
    }
  }
}
```

### Remote/Docker Mode (HTTP/SSE)

```json
{
  "mcpServers": {
    "debugger-remote": {
      "transport": {
        "type": "http",
        "url": "http://localhost:5000/mcp"
      }
    }
  }
}
```

For Docker containers, replace `localhost` with the container’s IP/hostname.

---

## Workflow Examples (HTTP API)

### 1) Upload a dump

Windows (with API key auth):
```bash
curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@C:\\dumps\\crash.dmp" \
  -F "userId=user123"
```

Linux/macOS:
```bash
curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@/tmp/core.12345" \
  -F "userId=user123"
```

### 2) Upload symbols (optional)

Single file:
```bash
curl -X POST http://localhost:5000/api/symbols/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@/path/to/MyApp.pdb" \
  -F "dumpId=abc123-def456-ghi789"
```

Batch:
```bash
curl -X POST http://localhost:5000/api/symbols/upload-batch \
  -H "X-API-Key: your-api-key" \
  -F "files=@/path/to/MyApp.pdb" \
  -F "files=@/path/to/MyLibrary.pdb" \
  -F "dumpId=abc123-def456-ghi789"
```

ZIP upload:
```bash
curl -X POST http://localhost:5000/api/symbols/upload-zip \
  -H "X-API-Key: your-api-key" \
  -F "file=@/path/to/symbols.zip" \
  -F "dumpId=abc123-def456-ghi789"
```

### 3) Analyze using MCP tools

See:
- `debugger://workflow-guide`
- `debugger://analysis-guide`
- `debugger://mcp-tools`
- `ANALYSIS_EXAMPLES.md`

---

## Platform Command Cheat-Sheets

### Windows (WinDbg)

```
!threads          - List .NET threads
!dumpheap         - Dump managed heap
k                 - Call stack
lm                - List modules
r                 - Show registers
.ecxr             - Exception context
!analyze -v       - Verbose crash analysis
!locks            - Show lock information
```

### Linux/macOS (LLDB)

```
# SOS (.NET) commands:
# - You can use WinDbg-style SOS commands (prefixed with '!') even on LLDB; the server strips the leading '!' for LLDB sessions.
!clrthreads       - List .NET threads (SOS)
!clrstack -a      - Managed call stack with args/locals (SOS)
!dumpheap -stat   - Managed heap statistics (SOS)

# Native LLDB commands:
bt                - Backtrace (call stack)
image list        - List loaded images/modules
register read     - Show registers
thread list       - List all threads
```

For full command references:
- `debugger://windbg-commands`
- `debugger://lldb-commands`
- `debugger://sos-commands`

---

## Configuration Reference

Tip: `DebuggerMcp/Configuration/EnvironmentConfig.cs` is the server-side source of truth for configuration knobs (names + defaults).

### Environment variables

```bash
# HTTP API binding
export ASPNETCORE_URLS="http://localhost:5000"

# Storage directories
export DUMP_STORAGE_PATH="/custom/path/dumps"
export SYMBOL_STORAGE_PATH="/custom/path/symbols"
export SESSION_STORAGE_PATH="/custom/path/sessions"
export LOG_STORAGE_PATH="/custom/path/logs"

# Optional auth + CORS + rate limits
export API_KEY="your-secret-api-key"
export CORS_ALLOWED_ORIGINS="https://app.example.com,https://admin.example.com"
export RATE_LIMIT_REQUESTS_PER_MINUTE=120

# SOS (optional, for non-standard installs)
export SOS_PLUGIN_PATH="/custom/path/to/libsosplugin.so"

# Optional override path for dotnet-symbol tool
export DOTNET_SYMBOL_TOOL_PATH="/custom/path/to/dotnet-symbol"

# Enable Swagger UI (default: enabled in development)
export ENABLE_SWAGGER="true"

# Maximum dump upload size in GB (default: 5)
export MAX_REQUEST_BODY_SIZE_GB=5

# Session cleanup settings
export SESSION_CLEANUP_INTERVAL_MINUTES=5
export SESSION_INACTIVITY_THRESHOLD_MINUTES=1440

# Session limits (defaults: 10 per user, 50 total)
export MAX_SESSIONS_PER_USER=10
export MAX_TOTAL_SESSIONS=50

# GitHub commit enrichment for assemblies / Source Link release resolver (optional)
export GITHUB_API_ENABLED=true
export GITHUB_TOKEN="..."
export GH_TOKEN="..."

# Heap/sync-block enumeration safety valve (optional)
export SKIP_HEAP_ENUM=false
export SKIP_SYNC_BLOCKS=false

# Local source context roots (optional; ';' separator, ':' also supported on Linux/macOS)
export DEBUGGERMCP_SOURCE_CONTEXT_ROOTS="/path/to/repo1;/path/to/repo2"

# Datadog trace symbols (used by the `datadog_symbols` MCP tool)
export DATADOG_TRACE_SYMBOLS_ENABLED=true
export DATADOG_TRACE_SYMBOLS_PAT="..."
export DATADOG_TRACE_SYMBOLS_CACHE_DIR="/path/to/cache"
export DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS=120
export DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE=524288000

# Optional: skip post-upload dump analysis
export SKIP_DUMP_ANALYSIS="true"

# Optional: symbol download timeout for dotnet-symbol
export SYMBOL_DOWNLOAD_TIMEOUT_MINUTES=10

# Optional: AI sampling trace (analyze ai / MCP sampling debugging)
export DEBUGGER_MCP_AI_SAMPLING_TRACE=true
export DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES=true
export DEBUGGER_MCP_AI_SAMPLING_TRACE_MAX_FILE_BYTES=2000000
export DEBUGGER_MCP_AI_SAMPLING_CHECKPOINT_EVERY_ITERATIONS=4

# Convenience only: used in startup messages (HTTP binding is controlled by ASP.NET Core, e.g. ASPNETCORE_URLS)
export PORT=5000
```

### Security configuration

API key authentication:
```bash
export API_KEY="my-secure-api-key-12345"

curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: my-secure-api-key-12345" \
  -F "file=@dump.dmp" \
  -F "userId=user123"
```

CORS:
```bash
export CORS_ALLOWED_ORIGINS="https://myapp.com,https://admin.myapp.com"
```

---

## Troubleshooting

Start with the canonical troubleshooting guide:
- `debugger://troubleshooting` (also `DebuggerMcp/Resources/troubleshooting.md`)

Common platform prerequisites:

Windows:
```bash
winget install Microsoft.WindowsSDK
```

Linux:
```bash
sudo apt-get install lldb
```

macOS:
```bash
xcode-select --install
```

---

## Architecture

### Component diagram

```
┌─────────────────────────────────────────────┐
│            Client (LLM/User)                │
└──────────────┬──────────────────────────────┘
               │
       ┌───────┴────────┐
       │                │
┌──────▼──────┐  ┌──────▼──────┐
│ MCP Server  │  │  HTTP API   │
│  (stdio)    │  │  (Port 5000)│
└──────┬──────┘  └──────┬──────┘
       │                │
       └────────┬───────┘
                │
    ┌───────────▼────────────┐
    │   SessionManager       │
    │   (Multitenant)        │
    └───────────┬────────────┘
                │
        ┌───────┴────────┐
        │                │
  ┌─────▼──────┐  ┌──────▼──────┐
  │ Session 1  │  │  Session 2  │
  │ User A     │  │  User B     │
  └─────┬──────┘  └──────┬──────┘
        │                │
  ┌─────▼──────┐  ┌──────▼──────┐
  │DebuggerMgr │  │DebuggerMgr  │
  └─────┬──────┘  └──────┬──────┘
        │                │
  ┌─────▼──────────────────▼──────┐
  │    DebuggerFactory            │
  │  (OS Detection)               │
  └─────┬──────────────────┬──────┘
        │                  │
  ┌─────▼──────┐    ┌──────▼──────┐
  │ WinDbgMgr  │    │  LldbMgr    │
  │ (Windows)  │    │(Linux/macOS)│
  └─────┬──────┘    └──────┬──────┘
        │                  │
  ┌─────▼──────┐    ┌──────▼──────┐
  │ DbgEng COM │    │LLDB Process │
  │    API     │    │ (stdin/out) │
  └────────────┘    └─────────────┘
```

### Key components

1. `IDebuggerManager`: common interface for all debuggers
2. `WinDbgManager`: Windows implementation using DbgEng COM API
3. `LldbManager`: Linux/macOS implementation using LLDB process
4. `DebuggerFactory`: creates the correct debugger based on OS/arch
5. `DebuggerSessionManager`: manages concurrent debugging sessions
6. Controllers: HTTP API for dumps/symbols/server capabilities

---

## Testing and Coverage

```bash
# Run all tests
dotnet test

# Run with code coverage
dotnet test --collect:"XPlat Code Coverage" --settings ./coverlet.runsettings

# Run specific test class
dotnet test --filter "FullyQualifiedName~DebuggerFactoryTests"

# Generate HTML coverage report (requires reportgenerator)
./coverage.sh
```

