# Debugger MCP Server

A cross-platform MCP (Model Context Protocol) server to control debuggers (WinDbg/LLDB) and analyze memory dumps programmatically, with multitenant support and hybrid MCP + HTTP API architecture.

## 🌟 Features

### Cross-Platform Support
- **Windows**: Uses WinDbg with DbgEng COM API for analyzing .dmp files
- **Linux/macOS**: Uses LLDB with process-based communication for analyzing core dumps
- **Automatic Detection**: Automatically selects the appropriate debugger based on the operating system

### Hybrid Architecture
- **MCP Server**:
  - **stdio mode**: For local debugging (Claude Desktop, Cline)
  - **HTTP/SSE mode**: For remote servers and Docker containers
- **HTTP API (REST)**: For dump uploads, symbol management, and report generation
- **Unified Process**: Both MCP and HTTP API can run in the same process
- **Multitenant**: Support for multiple simultaneous users
- **Session Management**: Up to 10 concurrent sessions per user (default; configurable via `MAX_SESSIONS_PER_USER`)

### Debugging Capabilities
- ✅ Opening and analyzing memory dumps (.dmp on Windows, .core on Linux/macOS)
- ✅ Executing debugger commands (WinDbg commands on Windows, LLDB commands on Linux/macOS)
- ✅ Full support for SOS (Son of Strike) for .NET analysis on all platforms
- ✅ **Automated Crash Analysis**: AI-powered analysis with structured JSON output
  - General crash analysis: exception type, call stack, thread info, recommendations
  - .NET specific analysis: CLR version, managed exceptions, heap stats
  - **Memory leak detection**: Identifies potential memory leaks and large object allocations
  - **Deadlock detection**: Detects thread synchronization issues and async deadlocks
  - Automatic command execution and output parsing
- ✅ **Dump Comparison/Diff Analysis**: Compare two memory dumps to identify differences
  - **Heap comparison**: Detect memory growth and potential leaks
  - **Thread comparison**: Identify new, terminated, and state-changed threads
  - **Module comparison**: Track loaded/unloaded modules and version changes
  - Automatic recommendations based on comparison results
- ✅ **Performance Profiling**: Comprehensive performance analysis tools
  - **CPU analysis**: Identify hot functions, runaway threads, and spin loops
  - **Allocation analysis**: Find top allocators, large objects, and potential memory leaks
  - **GC analysis**: Analyze garbage collection behavior, generations, and fragmentation
  - **Contention analysis**: Detect lock contention, waiting threads, and deadlocks
- ✅ **Watch Expressions / Bookmarks**: Track memory and variables across sessions
  - **Persistence**: Watches saved per-dump, survive session restarts
  - **Auto-detection**: Watch types automatically detected from patterns
  - **Analysis integration**: Watch results included in all analysis reports
  - **Insights**: Automatic detection of null pointers, uninitialized memory
- ✅ **Report Generation**: Create shareable analysis reports
  - **Multiple formats**: Markdown (ASCII charts), HTML (styled), JSON (structured)
  - **Visual charts**: Memory usage, thread states, heap distribution
  - **PDF support**: Generate HTML and print to PDF from browser
  - **Customizable**: Include/exclude sections, custom titles
- ✅ **Source Link Integration**: Click-through to source code
  - **Automatic resolution**: Extracts Source Link from Portable PDBs
  - **Multi-provider**: GitHub, GitLab, Azure DevOps, Bitbucket support
  - **Clickable links**: Stack frames link directly to source lines
  - **Report integration**: Links included in Markdown/HTML reports
- ✅ **Security Vulnerability Detection**: Identify security issues in crashes
  - **Buffer overflows**: Stack and heap overflow detection with canary checks
  - **Memory safety**: Use-after-free, double-free, null dereference detection
  - **Exploit patterns**: NOP sleds, shellcode, return address overwrites
  - **Memory protections**: ASLR, DEP/NX, stack canary verification
  - **CWE mappings**: Links to Common Weakness Enumeration
- ✅ **Object Inspection**: Deep inspection of .NET objects with recursive field expansion
  - Recursive field enumeration up to configurable depth
  - Circular reference detection with [this] and [seen] markers
  - Array element expansion with configurable limits
  - Automatic value type detection and handling
- ✅ Platform-specific integration:
  - Windows: DbgEng COM API
  - Linux/macOS: LLDB process with stdin/stdout redirection

### Symbol Support
- ✅ **Automatic symbol configuration**: Microsoft Symbol Server configured automatically when opening dumps
- ✅ **Dump-specific symbols**: Upload multiple symbol files per dump (batch upload supported)
- ✅ **ZIP archive support**: Upload ZIP files containing symbol directories (preserves structure for extracted symbol entries; non-symbol entries are ignored)
- ✅ **Organized by dump**: Symbols stored in dump-specific directories for easy management
- ✅ **Remote symbol servers**: Microsoft Symbol Server, NuGet Symbol Server, custom servers
- ✅ **Common formats**: .pdb (Windows), .so/.dylib (Linux/macOS), .dwarf, .sym, .debug, .dbg, .so.dbg, .dSYM (DWARF) (validated by file signatures)
- ✅ **Symbol file validation**: Single/batch uploads validate headers + enforce file size limit (500 MiB per file)
- ✅ **Zero configuration**: Just upload symbols and open dump - symbols are configured automatically

### MCP Resources
The server exposes documentation and guides as MCP resources for easy access:
- 📖 **Workflow Guide**: Complete workflow for analyzing memory dumps
- 📖 **Analysis Guide**: Crash analysis, .NET analysis, and dump comparison features
- 📖 **WinDbg Commands Reference**: Common WinDbg commands for crash analysis
- 📖 **LLDB Commands Reference**: Common LLDB commands for macOS/Linux debugging
- 📖 **.NET SOS Commands Reference**: SOS commands for .NET application debugging
- 📖 **Troubleshooting Guide**: Solutions to common issues
- 📖 **CLI Guide**: Using the dbg-mcp command-line client

### Security Features
- 🔒 **API Key Authentication**: Optional authentication via `X-API-Key` header
- 🔒 **CORS Configuration**: Configurable allowed origins for production deployments
- 🔒 **Rate Limiting**: Fixed-window per-IP limiter (default: 120 requests/min, configurable via `RATE_LIMIT_REQUESTS_PER_MINUTE`)
- 🔒 **Dump File Validation**: Magic byte validation ensures only valid dump files are uploaded
- 🔒 **Symbol File Validation**: Single/batch uploads validate symbol headers (PDB, ELF, Mach-O, etc.) before storage
- 🔒 **ZIP Extraction Hardening**: ZIP uploads extract only symbol-related entries and apply ZipSlip + zip bomb defenses
- 🔒 **Path Traversal Prevention**: User identifiers and uploaded symbol file names are sanitized to prevent directory traversal attacks
- 🔒 **Secure Responses**: Internal file paths are never exposed in API responses
- 🔒 Session isolation per user
- 🔒 Session ownership validation
- 📊 Maximum 10 sessions per user (default; configurable via `MAX_SESSIONS_PER_USER`)
- 📊 Maximum 50 total sessions in the system
- 📊 Maximum dump size: 5GB by default (configurable via `MAX_REQUEST_BODY_SIZE_GB`)
- 🧹 Automatic cleanup of inactive sessions (default: 24 hours, configurable via `SESSION_INACTIVITY_THRESHOLD_MINUTES`)

## 📋 Requirements

### Operating System
- **Windows**: Windows 10/11 or Windows Server 2016+
- **Linux**: Any modern distribution with LLDB installed
- **macOS**: macOS 10.15+ with LLDB (included with Xcode Command Line Tools)

### Software

#### All Platforms
- **.NET 10 SDK**

#### Windows-Specific
- **Debugging Tools for Windows** (part of Windows SDK)
- Download from: https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/

#### Linux/macOS-Specific
- **LLDB** (Low Level Debugger)
- **libsosplugin.so** (SOS plugin for LLDB; typically available with .NET runtime/SDK installs, or can be provided via `SOS_PLUGIN_PATH`)

##### Installing LLDB on Linux:
```bash
# Ubuntu/Debian
sudo apt-get install lldb

# Fedora/RHEL
sudo dnf install lldb

# Arch Linux
sudo pacman -S lldb
```

##### Installing LLDB on macOS:
```bash
# LLDB comes with Xcode Command Line Tools
xcode-select --install
```

## 🚀 Installation

### 1. Install .NET 10

```bash
# Download and install from:
https://dotnet.microsoft.com/download/dotnet/10.0

# Or use the install script (Linux/macOS):
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
```

### 2. Install Platform-Specific Debugger

#### Windows
```bash
# Install Windows SDK (includes Debugging Tools)
# Download from: https://developer.microsoft.com/windows/downloads/windows-sdk/

# Or install via winget:
winget install Microsoft.WindowsSDK
```

#### Linux
```bash
# Install LLDB
sudo apt-get install lldb  # Ubuntu/Debian
```

#### macOS
```bash
# Install Xcode Command Line Tools (includes LLDB)
xcode-select --install
```

### 3. Clone and Build

```bash
git clone https://github.com/tonyredondo/debugger-mcp-server.git
cd debugger-mcp-server
dotnet build -c Release
```

## 🎯 Usage

### Running the Server

#### MCP Mode (stdio) - For Local Use
```bash
cd DebuggerMcp
dotnet run
```

#### HTTP API Mode - Upload API Only
```bash
cd DebuggerMcp
dotnet run -- --http
```
Alias: `--api`

#### MCP HTTP Mode - Unified (MCP + Upload API)
```bash
cd DebuggerMcp
dotnet run -- --mcp-http
```

This mode runs both the MCP server (via HTTP/SSE) and the Upload API in the same process on port 5000.

#### Docker Mode
```bash
# Build and run with Docker Compose (all 4 platform variants)
docker-compose up -d

# Or build and run manually
docker build -t debugger-mcp-server .
docker run -p 5000:5000 -v $(pwd)/dumps:/app/dumps debugger-mcp-server
```

#### Multi-Platform Docker Setup

The `docker-compose.yml` provides 4 server variants for cross-platform dump analysis:

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

**Why Multiple Servers?**
- **Architecture matching**: ARM64 dumps require ARM64 servers, x64 dumps require x64 servers
- **Alpine vs Debian**: Alpine-based .NET dumps require Alpine servers for proper symbol resolution (musl vs glibc)
- **x64 safety valves (docker-compose defaults)**: the x64 variants enable `SKIP_HEAP_ENUM=true` / `SKIP_SYNC_BLOCKS=true` by default to avoid flaky heap walks under emulation; disable if you need full heap/sync-block analysis.

The CLI automatically detects dump/server mismatches and prompts you to switch to a compatible server.

### Configuration for Claude Desktop / Cline

Add to your MCP configuration file:

#### Local Mode (stdio)

**Windows:**
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

**Linux/macOS:**
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

#### Remote/Docker Mode (HTTP/SSE)

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

For Docker containers, replace `localhost` with the container's IP or hostname.

### Workflow Example

#### 1. Upload a Dump File (HTTP API)

```bash
# Windows (with API key authentication)
curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@C:\dumps\crash.dmp" \
  -F "userId=user123"

# Linux/macOS
curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@/tmp/core.12345" \
  -F "userId=user123"
```

Response:
```json
{
  "dumpId": "abc123-def456-ghi789",
  "userId": "user123",
  "fileName": "crash.dmp",
  "size": 524288000,
  "uploadedAt": "2024-01-15T10:30:00Z",
  "dumpFormat": "Windows Minidump",
  "isAlpineDump": false,
  "runtimeVersion": "8.0.10",
  "architecture": "x64"
}
```

> **Note**: The response includes dump format detection, Alpine/musl detection (for Linux dumps), .NET runtime version, and processor architecture. This information is used by the CLI for dump/server matching.

#### 2. Upload Symbol Files (Optional, HTTP API)

**Upload symbols for a specific dump:**

```bash
# Upload a single symbol file
curl -X POST http://localhost:5000/api/symbols/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@/path/to/MyApp.pdb" \
  -F "dumpId=abc123-def456-ghi789"

# Upload multiple symbol files (batch)
curl -X POST http://localhost:5000/api/symbols/upload-batch \
  -H "X-API-Key: your-api-key" \
  -F "files=@/path/to/MyApp.pdb" \
  -F "files=@/path/to/MyLibrary.pdb" \
  -F "files=@/path/to/ThirdParty.pdb" \
  -F "dumpId=abc123-def456-ghi789"

# Upload a ZIP archive with symbol directories
curl -X POST http://localhost:5000/api/symbols/upload-zip \
  -H "X-API-Key: your-api-key" \
  -F "file=@/path/to/symbols.zip" \
  -F "dumpId=abc123-def456-ghi789"
```

**Notes (storage + safety)**:
- Symbol files are stored under `.symbols_<dumpId>/`.
- Uploaded symbol file names are normalized to a safe basename (any directory components are stripped).
- ZIP uploads extract only symbol-related entries (other entries are ignored) and apply defensive limits:
  - Max entries: 25,000
  - Max extracted bytes (total): 2 GiB
  - Max extracted bytes (per entry): 512 MiB
  - Compression ratio guard: entries ≥ 10 MiB with ratio > 200 are rejected
  - Paths must be relative (no absolute paths or `..` segments)

Response (batch upload):
```json
{
  "dumpId": "abc123-def456-ghi789",
  "filesUploaded": 3,
  "files": [
    { "fileName": "MyApp.pdb", "size": 2048000 },
    { "fileName": "MyLibrary.pdb", "size": 1024000 },
    { "fileName": "ThirdParty.pdb", "size": 512000 }
  ]
}
```

**Note**: Symbols are organized by dumpId and automatically configured when you open the dump.

#### 3. Use MCP Tools to Analyze

```
1. session(action="create", userId="user123") → Get sessionId
2. dump(action="open", sessionId, userId, dumpId="abc123") → Open dump
   ✅ Symbols configured automatically:
      - Microsoft Symbol Server
      - Dump-specific symbols (if uploaded)
   ✅ SOS auto-loaded for .NET dumps
3. exec(sessionId, userId, command="!threads") → (WinDbg) List .NET threads
   exec(sessionId, userId, command="!clrthreads") → (LLDB) List .NET threads (SOS)
4. exec(sessionId, userId, command="k") → (WinDbg) Show call stack (with symbols!)
   exec(sessionId, userId, command="bt") → (LLDB) Show call stack (with symbols!)
5. analyze(kind="crash", sessionId, userId) → Analyze crash
6. session(action="close", sessionId, userId) → Close and cleanup
```

**Automated Analysis:**
```
# .NET crash analysis (SOS auto-loaded when opening .NET dumps)
analyze(kind="crash", sessionId, userId) → Returns the canonical JSON report document (same schema as `report(format="json")`):
  - metadata (dumpId/userId/generatedAt/debuggerType/serverVersion)
  - analysis (summary/exception/environment/threads/memory/assemblies/modules/async/security/watches/…)

# AI-assisted crash analysis (requires MCP sampling support in the connected client)
analyze(kind="ai", sessionId, userId) → Returns the same report enriched with:
  - analysis.aiAnalysis.rootCause / confidence / reasoning
  - analysis.aiAnalysis.summary (and overwrites analysis.summary.description / analysis.summary.recommendations)
  - analysis.aiAnalysis.threadNarrative (and populates analysis.threads.summary.description)
  - analysis.aiAnalysis.commandsExecuted (tools/commands the AI requested; prefer `inspect` over raw `dumpobj` when possible)

Tip: To debug sampling prompts/responses on the server, enable `DEBUGGER_MCP_AI_SAMPLING_TRACE` and `DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES` (writes to `LOG_STORAGE_PATH/ai-sampling`).
```

📝 **See [ANALYSIS_EXAMPLES.md](ANALYSIS_EXAMPLES.md) for detailed examples and JSON output formats.**

**Optional - Add additional symbol servers:**
```
symbols(action="configure_additional", sessionId, userId, additionalPaths="https://symbols.nuget.org/download/symbols")
```

**Note**: Symbol configuration is automatic! Just upload symbols (step 2 above) and open the dump.

### Platform-Specific Commands

#### Windows (WinDbg)
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

#### Linux/macOS (LLDB)
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

## 🏗️ Architecture

### Component Diagram

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

### Key Components

1. **IDebuggerManager**: Common interface for all debuggers
2. **WinDbgManager**: Windows implementation using DbgEng COM API
3. **LldbManager**: Linux/macOS implementation using LLDB process
4. **DebuggerFactory**: Automatically creates the correct debugger based on OS
5. **DebuggerSessionManager**: Manages multiple concurrent debugging sessions
6. **DumpController**: HTTP API for dump file uploads and management
7. **SymbolController**: HTTP API for symbol file management (single, batch, ZIP)
8. **ServerController**: HTTP API for server capabilities and info (for dump/server matching)
9. **CrashAnalyzer**: Automated crash analysis with structured output
10. **DotNetCrashAnalyzer**: .NET-specific crash analysis (memory leaks, deadlocks)
11. **PerformanceAnalyzer**: CPU, memory, GC, and contention analysis
12. **SecurityAnalyzer**: Security vulnerability detection with CWE mappings
13. **DumpComparer**: Dump comparison for memory/thread/module changes
14. **WatchStore**: Persistent watch expression storage per dump
15. **SourceLinkResolver**: Source code URL resolution from Portable PDBs
16. **ObjectInspector**: Deep .NET object inspection with recursive field expansion

### Security Components

1. **ApiKeyAuthenticationHandler**: Optional API key-based authentication
2. **DumpFileValidator**: Validates dump file magic bytes
3. **SymbolFileValidator**: Validates symbol file formats (PDB, ELF, Mach-O, etc.)
4. **PathSanitizer**: Prevents path traversal attacks

## 🧪 Testing

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

### Code Coverage

Coverage is collected via Coverlet (see `coverlet.runsettings`) and printed as a per-test-project summary during `dotnet test`.

To generate HTML reports:
```bash
# Install ReportGenerator (one time)
dotnet tool install -g dotnet-reportgenerator-globaltool

# Run coverage script
./coverage.sh

# Open the report
open ./TestResults/coverage-report/index.html  # macOS
xdg-open ./TestResults/coverage-report/index.html  # Linux
```

### Test Statistics
The repository includes extensive xUnit coverage for both the server and CLI, including:
  - Automated crash analysis (CrashAnalyzer, DotNetCrashAnalyzer)
  - Security components (PathSanitizer, DumpFileValidator, SymbolFileValidator)
  - Session management (DebuggerSessionManager)
  - Symbol management (SymbolManager)
  - Watch expressions (WatchStore, WatchEvaluator)
  - Performance analysis (PerformanceAnalyzer)
  - Dump comparison (DumpComparer)
  - Report generation (MarkdownReportGenerator, HtmlReportGenerator)
  - Source Link resolution (SourceLinkResolver)
  - Security analysis (SecurityAnalyzer)
  - Object inspection (ObjectInspector)

## 📚 MCP Tools Available

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
```
1. session(action="create", userId="user1") → sessionId
2. dump(action="open", sessionId, userId="user1", dumpId="abc123")
3. analyze(kind="crash", sessionId, userId="user1")
4. report(action="full", sessionId, userId="user1", format="html")
5. session(action="close", sessionId, userId="user1")
```

## 📚 MCP Resources Available

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

## 🌐 HTTP API Endpoints

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

## 💻 CLI Client

The `dbg-mcp` CLI provides a powerful command-line interface for remote crash dump analysis.

### Installation

```bash
# Run from source (recommended for now)
cd DebuggerMcp.Cli && dotnet run

# Or as a .NET global tool (when published to NuGet)
dotnet tool install -g DebuggerMcp.Cli
```

### Quick Start

```bash
# Start the CLI
dbg-mcp

# Connect to server
dbg-mcp> connect http://localhost:5000

# Upload and analyze a dump
dbg-mcp> dumps upload ./crash.dmp
dbg-mcp> open <dumpId>
dbg-mcp> analyze crash -o ./crash.json

# Generate report
dbg-mcp> report -o ./crash-report.md
```

### Key Features

| Feature | Description |
|---------|-------------|
| **Interactive Shell** | Rich prompt with context, history, tab completion |
| **File Operations** | Upload dumps/symbols with progress, wildcard support |
| **Session Management** | Create, list, attach, close sessions (partial ID matching) |
| **Analysis Commands** | crash, ai, perf, cpu, memory, gc, threads, security |
| **Dump Comparison** | Compare heaps, threads, modules between dumps |
| **Watch Expressions** | Track memory/variables across sessions |
| **Report Generation** | Markdown, HTML, JSON formats |
| **Source Link** | Resolve source files to repository URLs |
| **Multi-Server** | Manage multiple servers for cross-platform dump analysis |
| **LLM + Agent Mode** | OpenRouter/OpenAI/Anthropic chat + tool-using agent (`llm`, `llmagent`) |
| **AI Crash Analysis** | `analyze ai` via MCP sampling (LLM-driven evidence gathering) |

### Command Categories

```bash
help connection    # connect, disconnect, status, health, server
help files         # dumps, symbols, stats
help session       # session create/list/use/close
help debugging     # open, close, exec, cmd, showobj/inspect
help analysis      # analyze, compare
help llm           # llm, llmagent
help advanced      # watch, report, sourcelink
help general       # help, history, set, exit
```

### LLM + Agent Mode (OpenRouter / OpenAI / Anthropic)

The CLI can chat with a configured LLM provider (OpenRouter, OpenAI, or Anthropic) and (optionally) run as a tool-using agent against the currently connected server/session/dump.

Configure an API key (recommended via env var):
```bash
export OPENROUTER_API_KEY="..."
# Optional:
export OPENROUTER_MODEL="openai/gpt-4o-mini"
```

To use OpenAI directly:
```bash
export OPENAI_API_KEY="..."
llm provider openai
llm model gpt-4o-mini
```

To use Anthropic directly:
```bash
export ANTHROPIC_API_KEY="..."
llm provider anthropic
llm model claude-3-5-sonnet-20240620
```

Examples:
```bash
llm Explain the faulting thread in the last report
llm set-agent true
llm set-agent-confirm false
llm reasoning-effort medium
llmagent
llmagent> /help
llmagent> Analyze the current dump and run whatever commands you need
```

Notes:
- `llmagent` supports `/help`, `/reset`, `/reset conversation`, `/tools`, `/exit`.
- `llm reset` clears LLM context for the current server/session/dump scope.
- When provider is `openai` and no API key is configured, the CLI will try to fall back to `~/.codex/auth.json` (expects `OPENAI_API_KEY`). Override with `DEBUGGER_MCP_CODEX_AUTH_PATH`.

### Configuration

Environment variables:
```bash
export DEBUGGER_MCP_URL=http://localhost:5000
export DEBUGGER_MCP_API_KEY=your-key
export DEBUGGER_MCP_VERBOSE=true
```

Config file (`~/.dbg-mcp/config.json`):
```json
{
  "defaultServer": "http://localhost:5000",
  "apiKey": "your-key"
}
```

Multi-server config (`servers.json` next to CLI binary):
```json
{
  "servers": [
    { "url": "http://localhost:5000" },
    { "url": "http://localhost:5001", "apiKey": "key" }
  ]
}
```

For complete documentation, see [DebuggerMcp.Cli/README.md](DebuggerMcp.Cli/README.md).

## 🔧 Configuration

### Environment Variables

```bash
# HTTP API port (default: 5000)
export ASPNETCORE_URLS="http://localhost:5000"

# Dump storage directory (default: {TempPath}/WinDbgDumps)
export DUMP_STORAGE_PATH="/custom/path/dumps"

# Symbol cache directory for remote symbol servers (default: platform-specific)
export SYMBOL_STORAGE_PATH="/custom/path/symbols"

# Persistent session storage directory (default: /app/sessions in containers)
export SESSION_STORAGE_PATH="/custom/path/sessions"

# Log storage directory (default: {AppContext.BaseDirectory}/logs; used for server logs and trace artifacts)
export LOG_STORAGE_PATH="/custom/path/logs"

# API Key for authentication (optional - when set, X-API-Key header is required)
export API_KEY="your-secret-api-key"

# CORS allowed origins (optional - comma-separated list)
# When not set, allows any origin (development mode)
export CORS_ALLOWED_ORIGINS="https://app.example.com,https://admin.example.com"

# Rate limiting (requests per minute per IP, default: 120)
export RATE_LIMIT_REQUESTS_PER_MINUTE=120

# Custom SOS plugin path (optional - for non-standard installations)
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

# Crash analysis / reporting knobs (optional)
#
# GitHub commit enrichment for assemblies (adds author/message metadata when commit hashes are detected).
# Set GITHUB_API_ENABLED=false to disable network calls.
export GITHUB_API_ENABLED=true
export GITHUB_TOKEN="..."  # optional; increases GitHub API rate limits (also used by Source Link release resolver)
export GH_TOKEN="..."      # optional alias (used by the GitHub releases resolver)

# Skip heap/sync-block enumeration (safety valve).
# Useful when analyzing cross-architecture dumps (e.g., x64 dump on arm64 host) or emulation, where heap walks can SIGSEGV.
export SKIP_HEAP_ENUM=false
export SKIP_SYNC_BLOCKS=false  # legacy alias for SKIP_HEAP_ENUM

# Local source context roots (optional).
# When a report includes sourceContext/sourcelink URLs, the server can fetch and include source snippets.
# Provide one or more repo roots (separated by ';' and also ':' on Linux/macOS).
export DEBUGGERMCP_SOURCE_CONTEXT_ROOTS="/path/to/repo1;/path/to/repo2"

# Datadog trace symbols (used by the `datadog_symbols` MCP tool).
export DATADOG_TRACE_SYMBOLS_ENABLED=true
export DATADOG_TRACE_SYMBOLS_PAT="..."                  # optional; Azure DevOps PAT for private access
export DATADOG_TRACE_SYMBOLS_CACHE_DIR="/path/to/cache" # optional; defaults under dump storage
export DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS=120
export DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE=524288000

# Optional: skip post-upload analysis (dotnet-symbol --verifycore + architecture detection)
# Useful in constrained environments and tests.
export SKIP_DUMP_ANALYSIS="true"

# Optional: symbol download timeout for dotnet-symbol
export SYMBOL_DOWNLOAD_TIMEOUT_MINUTES=10

# Optional: AI sampling trace (for analyze ai / MCP sampling debugging)
# WARNING: may contain sensitive data from debugger outputs.
# Note: docker-compose.yml enables these by default for debugging; set to false in production.
export DEBUGGER_MCP_AI_SAMPLING_TRACE=true
export DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES=true
export DEBUGGER_MCP_AI_SAMPLING_TRACE_MAX_FILE_BYTES=2000000

# Convenience only: used in startup messages (HTTP binding is controlled by ASP.NET Core, e.g. ASPNETCORE_URLS)
export PORT=5000
```

Tip: `DebuggerMcp/Configuration/EnvironmentConfig.cs` is the server-side source of truth for configuration knobs (names + defaults).

### Security Configuration

#### API Key Authentication

When the `API_KEY` environment variable is set, all HTTP API requests must include the `X-API-Key` header:

```bash
# Set the API key
export API_KEY="my-secure-api-key-12345"

# Make authenticated requests
curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: my-secure-api-key-12345" \
  -F "file=@dump.dmp" \
  -F "userId=user123"
```

When `API_KEY` is not set, authentication is disabled (suitable for development).

#### CORS Configuration

For production deployments, set `CORS_ALLOWED_ORIGINS` to restrict which domains can access the API:

```bash
# Production: Only allow specific origins
export CORS_ALLOWED_ORIGINS="https://myapp.com,https://admin.myapp.com"

# Development: Leave unset to allow any origin
```

## 🐛 Troubleshooting

### Windows

**Problem**: "Unable to load DbgEng.dll"
```bash
# Solution: Install Debugging Tools for Windows
winget install Microsoft.WindowsSDK
```

**Problem**: "SOS extension not found"
```bash
# Solution: Ensure .NET SDK is installed
dotnet --list-sdks
```

### Linux

**Problem**: "lldb: command not found"
```bash
# Solution: Install LLDB
sudo apt-get install lldb
```

**Problem**: "libsosplugin.so not found"
```bash
# Solution: Ensure SOS plugin is available (often part of .NET runtime/SDK installs)
find /usr -name "libsosplugin.so" 2>/dev/null

# Or set custom path (used by the server when auto-loading SOS)
export SOS_PLUGIN_PATH="/path/to/libsosplugin.so"
```

### macOS

**Problem**: "xcrun: error: unable to find utility 'lldb'"
```bash
# Solution: Install Xcode Command Line Tools
xcode-select --install
```

### API Errors

**Problem**: "401 Unauthorized"
```bash
# Solution: Include the X-API-Key header if API_KEY is set
curl -H "X-API-Key: your-api-key" http://localhost:5000/api/dumps/user/user123
```

**Problem**: "Invalid dump file format"
```bash
# Solution: Ensure you're uploading a valid memory dump file
# Supported formats: Windows MDMP/PAGE, Linux ELF core, macOS Mach-O core
```

## 📖 Documentation

- [Analysis Examples](ANALYSIS_EXAMPLES.md) - Crash analysis JSON output examples
- [CLI Documentation](DebuggerMcp.Cli/README.md) - Complete CLI client reference

## 🤝 Contributing

Contributions are welcome! Please ensure:
- All tests pass
- Code is documented with XML comments
- New features include tests
- Security-sensitive code is reviewed

## 📄 License

This project is licensed under the MIT License.

## 🙏 Acknowledgments

- Microsoft for DbgEng API and debugging tools
- LLVM project for LLDB
- Model Context Protocol team for the MCP specification
- .NET team for the excellent cross-platform runtime

## 📞 Support

For issues and questions:
- GitHub Issues: https://github.com/tonyredondo/debugger-mcp-server/issues
- Documentation: See the [📖 Documentation](#-documentation) section above

---

**Note**: This is a debugging tool. Use responsibly and only analyze dumps you have permission to access.
