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
- **Session Management**: Up to 5 concurrent sessions per user

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
- ✅ **ZIP archive support**: Upload ZIP files containing symbol directories (preserves structure)
- ✅ **Organized by dump**: Symbols stored in dump-specific directories for easy management
- ✅ **Remote symbol servers**: Microsoft Symbol Server, NuGet Symbol Server, custom servers
- ✅ **Multiple formats**: .pdb (Windows), .so/.dylib (Linux/macOS), .dwarf, .sym, .debug, .dbg, .dSYM
- ✅ **Symbol file validation**: File type and size validation (up to 500MB per file)
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
- 🔒 **Dump File Validation**: Magic byte validation ensures only valid dump files are uploaded
- 🔒 **Symbol File Validation**: Validates symbol file formats (PDB, ELF, Mach-O) before storage
- 🔒 **Path Traversal Prevention**: All user identifiers are sanitized to prevent directory traversal attacks
- 🔒 **Secure Responses**: Internal file paths are never exposed in API responses
- 🔒 Session isolation per user
- 🔒 Session ownership validation
- 📊 Maximum 5 sessions per user
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
- **.NET 10** (recently released)

#### Windows-Specific
- **Debugging Tools for Windows** (part of Windows SDK)
- Download from: https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/

#### Linux/macOS-Specific
- **LLDB** (Low Level Debugger)
- **libsosplugin.so** (SOS plugin for LLDB, included with .NET SDK)

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
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --version 10.0
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
1. create_session(userId="user123") → Get sessionId
2. open_dump(sessionId, userId, dumpId="abc123") → Open dump
   ✅ Symbols configured automatically:
      - Microsoft Symbol Server
      - Dump-specific symbols (if uploaded)
   ✅ SOS auto-loaded for .NET dumps
3. execute_command(sessionId, userId, command="!threads") → List .NET threads
4. execute_command(sessionId, userId, command="k") → Show call stack (with symbols!)
5. execute_command(sessionId, userId, command="!analyze -v") → Analyze crash
6. close_session(sessionId, userId) → Close and cleanup
```

**Automated Analysis:**
```
# General crash analysis with memory leak and deadlock detection
analyze_crash(sessionId, userId) → Returns JSON with:
  - Crash type and exception info
  - Call stack analysis
  - Thread information
  - Memory leak indicators
  - Deadlock detection
  - Security vulnerabilities
  - Watch expression results
  - Recommendations

# .NET specific analysis (SOS auto-loaded by open_dump)
analyze_dot_net_crash(sessionId, userId) → Returns JSON with:
  - CLR version and runtime info
  - Managed exceptions with stack traces
  - Heap statistics and large object allocations
  - .NET-specific memory leak detection
  - Async deadlock detection (Tasks, SemaphoreSlim, etc.)
  - GC and finalization queue analysis
```

📝 **See [ANALYSIS_EXAMPLES.md](ANALYSIS_EXAMPLES.md) for detailed examples and JSON output formats.**

**Optional - Add additional symbol servers:**
```
configure_additional_symbols(sessionId, userId, additionalPaths="https://symbols.nuget.org/download/symbols")
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
clrthreads        - List .NET threads (after loading SOS)
dumpheap          - Dump managed heap (after loading SOS)
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

| Module | Line | Branch | Method |
|--------|------|--------|--------|
| DebuggerMcp | 59.55% | 56.11% | 73.3% |

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
- **Total Tests**: ~1170 across 59 test files
- **Pass Rate**: 100%
- **Coverage Areas**:
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

### Session & Dump Management

| Tool | Description | Parameters |
|------|-------------|------------|
| `create_session` | Create a new debugging session | `userId` |
| `open_dump` | Open a dump file (auto-configures symbols, auto-loads SOS for .NET) | `sessionId`, `userId`, `dumpId` |
| `close_dump` | Close the currently open dump | `sessionId`, `userId` |
| `execute_command` | Execute a debugger command (WinDbg/LLDB syntax) | `sessionId`, `userId`, `command` |
| `load_sos` | Load SOS extension for .NET (usually auto-loaded) | `sessionId`, `userId` |
| `close_session` | Close and release all session resources | `sessionId`, `userId` |
| `list_sessions` | List all active sessions for a user | `userId` |
| `get_debugger_info` | Get debugger type, OS, and status | `sessionId`, `userId` |
| `restore_session` | Restore/attach to a persisted session (after server restart) | `sessionId`, `userId` |

### Symbol Management

| Tool | Description | Parameters |
|------|-------------|------------|
| `configure_additional_symbols` | Add additional symbol paths (optional) | `sessionId`, `userId`, `additionalPaths` |
| `get_symbol_servers` | List common symbol servers | - |
| `clear_symbol_cache` | Clear downloaded symbols for a dump | `userId`, `dumpId` |
| `reload_symbols` | Reload symbols after uploading new files | `sessionId`, `userId` |

### Crash Analysis

| Tool | Description | Parameters |
|------|-------------|------------|
| `analyze_crash` | Comprehensive crash analysis with security, watches, and recommendations | `sessionId`, `userId`, `includeWatches` (default: true) |
| `analyze_dot_net_crash` | .NET specific analysis with CLR info, managed exceptions, heap stats | `sessionId`, `userId`, `includeWatches` (default: true) |

### Dump Comparison

| Tool | Description | Parameters |
|------|-------------|------------|
| `compare_dumps` | Full comparison (heap, threads, modules) | `baselineSessionId`, `baselineUserId`, `comparisonSessionId`, `comparisonUserId` |
| `compare_heaps` | Compare heap/memory allocations for leak detection | `baselineSessionId`, `baselineUserId`, `comparisonSessionId`, `comparisonUserId` |
| `compare_threads` | Compare thread states for deadlock detection | `baselineSessionId`, `baselineUserId`, `comparisonSessionId`, `comparisonUserId` |
| `compare_modules` | Compare loaded modules for version changes | `baselineSessionId`, `baselineUserId`, `comparisonSessionId`, `comparisonUserId` |

#### Dump Comparison Use Cases

The dump comparison tools help identify:
- **Memory Leaks**: Compare dumps taken at different times to identify growing allocations
- **State Changes**: Find what changed between a working and broken state
- **Regression Analysis**: Compare dumps from different versions
- **Deadlock Detection**: Identify threads that started waiting on locks

**Example Comparison Workflow:**
```
1. create_session(userId="user1") → Get baselineSessionId
2. create_session(userId="user1") → Get comparisonSessionId
3. open_dump(baselineSessionId, "user1", "baseline-dump-id")
4. open_dump(comparisonSessionId, "user1", "comparison-dump-id")
5. compare_dumps(baselineSessionId, "user1", comparisonSessionId, "user1") → Returns:
   - Memory growth analysis
   - New/terminated threads
   - Loaded/unloaded modules
   - Recommendations
```

**HTTP API Alternative:**
```bash
# Compare dumps via HTTP API
curl -X POST http://localhost:5000/api/dumps/compare \
  -H "Content-Type: application/json" \
  -H "X-API-Key: your-api-key" \
  -d '{
    "baselineUserId": "user1",
    "baselineDumpId": "dump-before",
    "comparisonUserId": "user1",
    "comparisonDumpId": "dump-after"
  }'
```

### Performance Profiling

| Tool | Description | Parameters |
|------|-------------|------------|
| `analyze_performance` | Comprehensive analysis (CPU, memory, GC, contention) with watches | `sessionId`, `userId`, `includeWatches` (default: true) |
| `analyze_cpu_usage` | Identify hot functions, runaway threads, spin loops | `sessionId`, `userId` |
| `analyze_allocations` | Top allocators, large objects, potential memory leaks | `sessionId`, `userId` |
| `analyze_gc` | GC behavior, heap generations, fragmentation, finalizer queue | `sessionId`, `userId` |
| `analyze_contention` | Lock contention, waiting threads, deadlock detection | `sessionId`, `userId` |

### Watch Expressions / Bookmarks

| Tool | Description | Parameters |
|------|-------------|------------|
| `add_watch` | Add a watch expression (type auto-detected) | `sessionId`, `userId`, `expression`, `description` (optional) |
| `list_watches` | List all watch expressions for the current dump | `sessionId`, `userId` |
| `evaluate_watches` | Evaluate all watches and return values with insights | `sessionId`, `userId` |
| `evaluate_watch` | Evaluate a specific watch expression by ID | `sessionId`, `userId`, `watchId` |
| `remove_watch` | Remove a watch expression by ID | `sessionId`, `userId`, `watchId` |
| `clear_watches` | Clear all watches for the current dump | `sessionId`, `userId` |

### Report Generation

| Tool | Description | Parameters |
|------|-------------|------------|
| `generate_report` | Generate comprehensive crash analysis report | `sessionId`, `userId`, `format` (markdown/html/json), `includeWatches`, `includeSecurity`, `maxStackFrames` |
| `generate_summary_report` | Generate brief summary with key findings | `sessionId`, `userId`, `format` (markdown/html/json) |

#### Report Features

Generate shareable reports from crash analysis in multiple formats:

- **Markdown** (default): ASCII charts, works in any text viewer, GitHub-friendly
- **HTML**: Beautiful styled reports with CSS charts, opens in browser
- **JSON**: Structured data for programmatic consumption

Reports include:
- Executive summary and crash information
- Call stack with source references
- Memory/heap analysis with visual charts
- Thread state distribution
- .NET-specific information (CLR version, managed exceptions)
- Watch expression results and insights
- Recommendations for fixing issues

**TIP**: For PDF output, generate HTML and print from browser (File > Print > Save as PDF)

### Source Link

| Tool | Description | Parameters |
|------|-------------|------------|
| `resolve_source_link` | Resolve source file to browsable URL (GitHub, etc.) | `sessionId`, `userId`, `sourceFile`, `lineNumber` (optional) |
| `get_source_link_info` | Get Source Link configuration and symbol paths | `sessionId`, `userId` |

#### Source Link Features

Automatically link stack frames to source code using Source Link metadata from Portable PDBs:

- **Automatic Resolution**: Extracts Source Link JSON from PDB files
- **Provider Support**: GitHub, GitLab, Azure DevOps, Bitbucket
- **Clickable Links**: Stack frames in reports link directly to source lines
- **Version Accurate**: Links point to the exact commit that was compiled

**Example:** A stack frame like `MyApp.dll!ProcessData` with source at `Controllers/HomeController.cs:42` becomes:
```
https://github.com/myorg/myapp/blob/v1.2.3/Controllers/HomeController.cs#L42
```

**Prerequisites:**
- PDB files must be Portable PDBs (not Windows PDBs)
- PDBs must have embedded Source Link JSON
- Symbol files should be uploaded via the symbol API

### Security Analysis

| Tool | Description | Parameters |
|------|-------------|------------|
| `analyze_security` | Comprehensive security vulnerability analysis | `sessionId`, `userId` |
| `get_security_check_capabilities` | List detectable vulnerability types and capabilities | - |

### Object Inspection

| Tool | Description | Parameters |
|------|-------------|------------|
| `inspect_object` | Inspect a .NET object with recursive field expansion | `sessionId`, `userId`, `address`, `methodTable` (optional), `maxDepth`, `maxArrayElements`, `maxStringLength` |

#### Object Inspection Features

Deep inspection of .NET objects in memory dumps with:

- **Recursive field enumeration**: Expand nested objects up to configurable depth (default: 5)
- **Circular reference detection**: Marks self-references with [this] and previously seen objects with [seen]
- **Array support**: Shows first N elements (configurable) with type information
- **Value type handling**: Falls back to `dumpvc` when `dumpobj` fails with method table
- **String truncation**: Long strings truncated to configurable length (default: 1024)

**Example output:**
```json
{
  "address": "f7158ec79b48",
  "type": "MyNamespace.MyClass",
  "mt": "0000f755890ba770",
  "isValueType": false,
  "fields": [
    { "name": "_count", "type": "System.Int32", "isStatic": false, "value": 42 },
    { "name": "_name", "type": "System.String", "isStatic": false, "value": "Hello" }
  ]
}
```

#### Security Vulnerability Detection

Identify potential security vulnerabilities in crash dumps:

| Vulnerability Type | Detection Method | CWE |
|-------------------|------------------|-----|
| **Stack Buffer Overflow** | Stack canary/cookie corruption | CWE-121 |
| **Heap Buffer Overflow** | Heap metadata corruption | CWE-122 |
| **Use-After-Free** | Access to freed memory regions | CWE-416 |
| **Double-Free** | Heap state analysis | CWE-415 |
| **Null Dereference** | Access violation at low addresses | CWE-476 |
| **Integer Overflow** | Suspicious allocation sizes | CWE-190 |
| **Code Execution** | Execution in non-executable regions | CWE-94 |

#### Memory Protection Analysis

- **ASLR**: Address Space Layout Randomization status
- **DEP/NX**: Data Execution Prevention status
- **Stack Canaries**: /GS (Windows) or __stack_chk (Unix) protection
- **SafeSEH**: Structured Exception Handling protection (Windows)
- **Modules Analysis**: Identify modules without security features

#### Exploit Pattern Detection

Searches memory for common exploitation indicators:
- NOP sleds (0x90909090)
- Common overflow patterns (0x41414141, 0x42424242)
- Windows heap markers (0xFEEEFEEE, 0xBAADF00D)
- Return address overwrite patterns

#### Watch Expression Features

Watch expressions allow you to bookmark and track specific memory locations, variables, or debugger expressions across sessions:

- **Persistence**: Watches are stored per-dump and survive session restarts
- **Auto-detection**: Watch types are automatically detected from expression patterns
- **Analysis Integration**: Watch results are included in crash/performance analysis reports
- **Insights**: Automatic insights for null pointers, uninitialized memory, etc.

**Watch Types:**
- `MemoryAddress`: Display memory at an address (e.g., "0x12345678")
- `Variable`: Display a variable (e.g., "g_DataManager", "myModule!myVar")
- `Object`: Display a .NET object (uses `!do` command)
- `Expression`: Evaluate a debugger expression (e.g., "poi(esp+8)")

**Example Workflow:**
```
1. CreateSession(userId="user1") → sessionId
2. OpenDump(sessionId, "user1", "crash-dump")
3. AddWatch(sessionId, "user1", "0x12345678", "Suspicious pointer")
4. AddWatch(sessionId, "user1", "g_AppState", "Global state variable")
5. EvaluateWatches(sessionId, "user1") → All watch values + insights
6. AnalyzeCrash(sessionId, "user1") → Includes watch results!
```

#### Performance Profiling Use Cases

The performance profiling tools help identify:
- **CPU Hotspots**: Functions consuming excessive CPU time
- **Memory Issues**: Large allocations, potential leaks, LOH fragmentation
- **GC Pressure**: High Gen2/LOH usage, finalizer queue issues
- **Lock Contention**: Threads blocked on locks, deadlock detection
- **Spin Loops**: Threads spinning on locks or conditions

**Example Performance Analysis Workflow:**
```
1. CreateSession(userId="user1") → Get sessionId
2. OpenDump(sessionId, "user1", "perf-dump-id")  # SOS auto-loaded for .NET
3. AnalyzePerformance(sessionId, "user1") → Returns:
   - CPU analysis with hot functions
   - Memory allocation statistics
   - GC generation sizes and fragmentation
   - Lock contention and waiting threads
   - Recommendations for each category
```

**Drill-down Analysis:**
```
# If CPU issues detected:
AnalyzeCpuUsage(sessionId, "user1") → Detailed CPU breakdown

# If memory issues detected:
AnalyzeAllocations(sessionId, "user1") → Type-by-type allocations

# If GC issues detected:
AnalyzeGc(sessionId, "user1") → Detailed GC statistics

# If contention issues detected:
AnalyzeContention(sessionId, "user1") → Lock and wait analysis
```

## 📚 MCP Resources Available

| Resource URI | Name | Description |
|--------------|------|-------------|
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
dbg-mcp> analyze crash

# Generate report
dbg-mcp> report -o ./crash-report.md
```

### Key Features

| Feature | Description |
|---------|-------------|
| **Interactive Shell** | Rich prompt with context, history, tab completion |
| **File Operations** | Upload dumps/symbols with progress, wildcard support |
| **Session Management** | Create, list, attach, close sessions (partial ID matching) |
| **Analysis Commands** | crash, dotnet, perf, cpu, memory, gc, security |
| **Dump Comparison** | Compare heaps, threads, modules between dumps |
| **Watch Expressions** | Track memory/variables across sessions |
| **Report Generation** | Markdown, HTML, JSON formats |
| **Source Link** | Resolve source files to repository URLs |
| **Multi-Server** | Manage multiple servers for cross-platform dump analysis |

### Command Categories

```bash
help connection    # connect, disconnect, status, health, server
help files         # dumps, symbols, stats
help session       # session create/list/use/close
help debugging     # open, close, exec, sos, threads, stack
help analysis      # analyze, compare
help advanced      # watch, report, sourcelink
help general       # help, history, set, exit
```

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

# Dump storage directory (default: system temp directory)
export DUMP_STORAGE_PATH="/custom/path/dumps"

# API Key for authentication (optional - when set, X-API-Key header is required)
export API_KEY="your-secret-api-key"

# CORS allowed origins (optional - comma-separated list)
# When not set, allows any origin (development mode)
export CORS_ALLOWED_ORIGINS="https://app.example.com,https://admin.example.com"

# Custom SOS plugin path (optional - for non-standard installations)
export SOS_PLUGIN_PATH="/custom/path/to/libsosplugin.so"

# Enable Swagger UI (default: enabled in development)
export ENABLE_SWAGGER="true"

# Maximum dump upload size in GB (default: 5)
export MAX_REQUEST_BODY_SIZE_GB=5

# Session cleanup settings
export SESSION_CLEANUP_INTERVAL_MINUTES=5
export SESSION_INACTIVITY_THRESHOLD_MINUTES=1440

# Optional: skip post-upload analysis (dotnet-symbol --verifycore + architecture detection)
# Useful in constrained environments and tests.
export SKIP_DUMP_ANALYSIS="true"

# Optional: symbol download timeout for dotnet-symbol
export SYMBOL_DOWNLOAD_TIMEOUT_MINUTES=10
```

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
# Solution: Ensure .NET SDK is installed and SOS plugin is available
find /usr -name "libsosplugin.so" 2>/dev/null

# Or set custom path
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
- [Feature Proposals](FEATURE_PROPOSALS.md) - Proposed and implemented features with technical details
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
