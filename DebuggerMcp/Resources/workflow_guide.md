# Debugger MCP Server - Workflow Guide

## 🎯 Complete Workflow for Analyzing Memory Dumps

This guide explains the complete workflow for using the Debugger MCP Server to analyze memory dumps. The server supports WinDbg on Windows and LLDB on macOS/Linux.

> MCP tool names note: the server exposes a compact 11-tool MCP surface. The canonical list is in `debugger://mcp-tools`.

### Prerequisites

- **HTTP API Server**: Must be running on the configured port (default: 5000)
- **MCP Server**: Connected via Claude Desktop, Cline, or another MCP client
- **Dump File**: A memory dump file (.dmp on Windows, .core on Linux/macOS)
- **API Key** (if enabled): The `X-API-Key` header value if `API_KEY` environment variable is set

---

## 📋 Step-by-Step Workflow

### Step 1: Upload Dump File via HTTP API

**IMPORTANT**: Dump files MUST be uploaded via the HTTP API first. The MCP protocol does not support large binary file uploads.

**Endpoint**: `POST /api/dumps/upload`

**Supported Dump Formats**:
- Windows Minidump (MDMP)
- Windows Full/Kernel Dump (PAGE)
- Linux ELF Core Dump
- macOS Mach-O Core Dump

**Using curl** (with API key authentication):
```bash
curl -X POST http://localhost:5000/api/dumps/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@/path/to/crash.dmp" \
  -F "userId=your-user-id" \
  -F "description=Production crash on 2024-01-15"
```

**Using curl** (without authentication - development mode):
```bash
curl -X POST http://localhost:5000/api/dumps/upload \
  -F "file=@/path/to/crash.dmp" \
  -F "userId=your-user-id" \
  -F "description=Production crash on 2024-01-15"
```

**Using PowerShell**:
```powershell
$headers = @{ "X-API-Key" = "your-api-key" }
$form = @{
    file = Get-Item -Path "C:\dumps\crash.dmp"
    userId = "your-user-id"
    description = "Production crash on 2024-01-15"
}
Invoke-RestMethod -Uri "http://localhost:5000/api/dumps/upload" `
    -Method Post -Form $form -Headers $headers
```

**Response**:
```json
{
  "dumpId": "abc123-456def-789ghi",
  "userId": "your-user-id",
  "fileName": "crash.dmp",
  "size": 524288000,
  "uploadedAt": "2024-01-15T10:30:00Z",
  "description": "Production crash on 2024-01-15",
  "dumpFormat": "Windows Minidump"
}
```

> **Note**: The server validates that the uploaded file is a valid dump format. Invalid files will be rejected.

**SAVE THE `dumpId`** - You will need it in the next steps!

---

### Step 2: Upload Symbol Files (Optional, HTTP API)

For better stack traces and debugging, upload your application's symbol files.

**Supported Symbol Formats**:
- `.pdb` (Windows)
- `.so`, `.dylib` (Linux/macOS)
- `.dwarf`, `.sym`, `.debug`, `.dbg`, `.dSYM`

**Upload a single symbol file**:
```bash
curl -X POST http://localhost:5000/api/symbols/upload \
  -H "X-API-Key: your-api-key" \
  -F "file=@/path/to/MyApp.pdb" \
  -F "dumpId=abc123-456def-789ghi"
```

**Upload multiple symbol files (batch)**:
```bash
curl -X POST http://localhost:5000/api/symbols/upload-batch \
  -H "X-API-Key: your-api-key" \
  -F "files=@/path/to/MyApp.pdb" \
  -F "files=@/path/to/MyLibrary.pdb" \
  -F "dumpId=abc123-456def-789ghi"
```

Symbols are automatically configured when you open the dump.

---

### Step 3: Create a Debugging Session (MCP)

Use the MCP tool `session` with `action: "create"` to create a new debugging session.

**Tool**: `session`

**Parameters**:
- `userId`: Your user identifier (same as used in Step 1)
- `action`: `"create"`

**Example**:
```
session(action: "create", userId: "your-user-id")
```

**Response**:
```
Session created successfully. SessionId: session-xyz-789. Use this sessionId in all subsequent operations.
```

**SAVE THE `sessionId`** - You will need it for all subsequent operations!

---

### Step 4: Open the Dump File (MCP)

Open the dump file using the `dumpId` from Step 1. The debugging engine is automatically initialized when opening the first dump.

**Tool**: `dump`

**Parameters**:
- `sessionId`: The session ID from Step 3
- `userId`: Your user identifier
- `dumpId`: The dump ID from Step 1
- `action`: `"open"`

**Example**:
```
dump(
    action: "open",
    sessionId: "session-xyz-789",
    userId: "your-user-id",
    dumpId: "abc123-456def-789ghi"
)
```

**What happens automatically**:
- ✅ Debugger engine initialized (WinDbg on Windows, LLDB on Linux/macOS)
- ✅ Microsoft Symbol Server configured
- ✅ Dump-specific symbols configured (if uploaded in Step 2)
- ✅ Symbol cache checked and downloaded if needed

---

### Step 5: Execute Debugger Commands (MCP)

Now you can execute debugger commands on the open dump.

**Tool**: `exec`

**Parameters**:
- `sessionId`: The session ID from Step 3
- `userId`: Your user identifier
- `command`: The debugger command to execute

**Common Commands (Windows - WinDbg)**:
```
# Display call stack
exec(sessionId, userId, command: "k")

# Verbose crash analysis
exec(sessionId, userId, command: "!analyze -v")

# List all threads
exec(sessionId, userId, command: "~*k")

# List loaded modules
exec(sessionId, userId, command: "lm")
```

**Common Commands (Linux/macOS - LLDB)**:
```
# Display backtrace
exec(sessionId, userId, command: "bt")

# List all threads
exec(sessionId, userId, command: "thread list")

# List loaded images
exec(sessionId, userId, command: "image list")
```

---

### Step 6: Automated Crash Analysis (MCP)

Use the automated analysis tools for structured output.

**Tool**: `analyze` (`kind: "crash"`)

Returns JSON with:
- Crash type and exception information
- Call stack analysis
- Thread information
- **Memory leak indicators**
- **Deadlock detection**
- Recommendations

```
analyze(kind: "crash", sessionId: "session-xyz-789", userId: "your-user-id")
```

---

### Step 7: .NET-Specific Analysis (MCP)

For .NET dumps, SOS is **automatically loaded** when `dump(action: "open")` detects a .NET runtime.
If detection fails, you can load SOS manually with `inspect(kind: "load_sos", ...)`.

Use .NET-specific analysis tools:

**Tool**: `analyze` (`kind: "dotnet_crash"`)

Returns JSON with:
- CLR version and runtime info
- Managed exceptions with stack traces
- Heap statistics and large object allocations
- **.NET-specific memory leak detection**
- **Async deadlock detection** (Tasks, SemaphoreSlim, etc.)
- GC and finalization queue analysis

```
analyze(kind: "dotnet_crash", sessionId: "session-xyz-789", userId: "your-user-id")
```

**Common SOS Commands**:
```
# List managed threads
exec(sessionId, userId, command: "!threads")

# Show heap statistics
exec(sessionId, userId, command: "!dumpheap -stat")

# Display current exception
exec(sessionId, userId, command: "!pe")

# Analyze async state machines
exec(sessionId, userId, command: "!dumpasync")
```

---

### Step 8 (Optional): AI Crash Analysis (MCP Sampling)

Use AI-assisted analysis to drive additional evidence gathering via tools.

**Tool**: `analyze` (`kind: "ai"`)

```
analyze(kind: "ai", sessionId: "session-xyz-789", userId: "your-user-id")
```

Notes:
- Requires an MCP client that supports sampling (`sampling/createMessage`) with tools enabled.
  - The `dbg-mcp` CLI supports this when an LLM provider is configured (`OPENROUTER_API_KEY` or `OPENAI_API_KEY` + `llm provider openai`).
- For managed object inspection during AI sampling, prefer the sampling tool `inspect(address: "0x...")` over `exec "sos dumpobj ..."` when the AI requests object details.

Debugging:
- Enable `DEBUGGER_MCP_AI_SAMPLING_TRACE=true` and `DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES=true`.
- Trace files are written under `LOG_STORAGE_PATH/ai-sampling`.

---

### Step 9: Close the Dump (MCP)

When done analyzing the current dump, close it.

**Tool**: `dump` (`action: "close"`)

```
dump(action: "close", sessionId: "session-xyz-789", userId: "your-user-id")
```

**Note**: You can open another dump in the same session after closing.

---

### Step 10: Close the Session (MCP)

When completely done with debugging, close the session to release resources.

**Tool**: `session` (`action: "close"`)

```
session(action: "close", sessionId: "session-xyz-789", userId: "your-user-id")
```

---

## 🔄 Alternative Workflows

### Analyzing Multiple Dumps in One Session

```
1. session(action="create")
2. dump(action="open", dump1) - auto-initializes debugger
3. analyze(kind="crash") or exec (analyze dump1)
4. dump(action="close")
5. dump(action="open", dump2)
6. analyze(kind="crash") or exec (analyze dump2)
7. dump(action="close")
8. session(action="close")
```

### Comparing .NET Memory Leaks

```
1. session(action="create")
2. dump(action="open", baseline dump)  # SOS auto-loaded for .NET dumps
3. analyze(kind="dotnet_crash") → Save heap statistics
4. dump(action="close")
5. dump(action="open", leak dump)  # SOS auto-loaded
6. analyze(kind="dotnet_crash") → Compare heap statistics
7. session(action="close")
```

### Managing Multiple Concurrent Sessions

```
User can have up to 10 concurrent sessions:

Session 1: Analyzing production crash
Session 2: Analyzing memory leak
Session 3: Comparing two dumps
...

Use session(action="list", userId) to see all active sessions (JSON).
```

### Comparing Two Dumps (Memory Leak Detection)

```
1. session(action="create", userId) → session1
2. session(action="create", userId) → session2
3. dump(action="open", session1, userId, baseline-dumpId)
4. dump(action="open", session2, userId, comparison-dumpId)
5. compare(kind="dumps", baselineSessionId=session1, baselineUserId=userId, targetSessionId=session2, targetUserId=userId)
   → Returns heap changes, thread changes, module changes
6. compare(kind="heaps", baselineSessionId=session1, baselineUserId=userId, targetSessionId=session2, targetUserId=userId)
   → Detailed memory growth analysis
7. session(action="close", sessionId=session1, userId)
8. session(action="close", sessionId=session2, userId)
```

### Using Watch Expressions

```
1. dump(action="open", ...)
2. watch(action="add", sessionId, userId, expression="0x12345678", description="Suspicious pointer")
3. watch(action="add", sessionId, userId, expression="g_AppState", description="Global state variable")
4. watch(action="evaluate_all", sessionId, userId)
   → Returns current values with insights (null pointers, freed memory, etc.)
5. analyze(kind="crash", sessionId, userId)
   → Watch results included in analysis
```

### Generating Reports

```
1. dump(action="open", ...)  # SOS auto-loaded for .NET dumps
2. report(action="full", sessionId, userId, format="html", includeWatches=true)
   → Returns comprehensive HTML report
3. report(action="summary", sessionId, userId, format="json")
   → Quick summary with key findings
```

### Security Analysis

```
1. dump(action="open", ...)
2. analyze(kind="security", sessionId, userId)
   → Returns vulnerabilities, memory protections, exploit patterns
3. analyze(kind="security", action="capabilities")
   → Lists detectable vulnerability types
```

---

## 📊 HTTP API Endpoints Reference

### Authentication
When `API_KEY` environment variable is set, include `X-API-Key` header in all requests.

### Upload Dump
- **POST** `/api/dumps/upload`
- **Headers**: `X-API-Key` (if enabled)
- **Form Data**: `file`, `userId`, `description` (optional)
- **Returns**: `dumpId`, `dumpFormat`, upload metadata

### Upload Symbols
- **POST** `/api/symbols/upload`
- **Headers**: `X-API-Key` (if enabled)
- **Form Data**: `file`, `dumpId`
- **Returns**: Upload confirmation

### Upload Symbols (Batch)
- **POST** `/api/symbols/upload-batch`
- **Headers**: `X-API-Key` (if enabled)
- **Form Data**: `files[]`, `dumpId`
- **Returns**: Upload confirmation with file list

### Get Dump Info
- **GET** `/api/dumps/{userId}/{dumpId}`
- **Headers**: `X-API-Key` (if enabled)
- **Returns**: Dump metadata

### List User Dumps
- **GET** `/api/dumps/user/{userId}`
- **Headers**: `X-API-Key` (if enabled)
- **Returns**: Array of dump metadata

### Delete Dump
- **DELETE** `/api/dumps/{userId}/{dumpId}`
- **Headers**: `X-API-Key` (if enabled)
- **Returns**: Success message

### Get Session Statistics
- **GET** `/api/dumps/stats`
- **Headers**: `X-API-Key` (if enabled)
- **Returns**: Active sessions, storage used, uptime

### Compare Dumps
- **POST** `/api/dumps/compare`
- **Headers**: `X-API-Key` (if enabled)
- **Body**: `{ baselineUserId, baselineDumpId, comparisonUserId, comparisonDumpId }`
- **Returns**: Heap, thread, and module comparison results

### Generate Report
- **GET** `/api/dumps/{userId}/{dumpId}/report?format=markdown`
- **Headers**: `X-API-Key` (if enabled)
- **Query Params**: `format` (markdown, html, json)
- **Returns**: Analysis report as downloadable file

### Upload Symbol ZIP
- **POST** `/api/symbols/upload-zip`
- **Headers**: `X-API-Key` (if enabled)
- **Form Data**: `file` (ZIP archive), `dumpId`
- **Returns**: Extraction result with file counts

### Health Check
- **GET** `/health`
- **Returns**: `{ status: "healthy", timestamp }`

### Server Info
- **GET** `/info`
- **Returns**: OS, architecture, .NET runtimes, Docker status

---

## ⚠️ Important Notes

1. **Upload First**: Always upload dumps via HTTP API before using MCP tools
2. **Save IDs**: Save both `dumpId` and `sessionId` for use in subsequent operations
3. **Session Limits**: Each user can have up to 5 concurrent sessions
4. **System Limits**: Maximum 50 total concurrent sessions across all users
5. **File Size**: Maximum dump file size is 5GB by default (configurable via `MAX_REQUEST_BODY_SIZE_GB`)
6. **Cleanup**: Inactive sessions are automatically cleaned up after 24 hours by default (configurable via `SESSION_INACTIVITY_THRESHOLD_MINUTES`)
7. **Security**: Use API key authentication in production environments
8. **File Validation**: Only valid dump files are accepted (magic byte validation)

---

## 🔒 Security Features

- **API Key Authentication**: Set `API_KEY` env var to require `X-API-Key` header
- **CORS Configuration**: Set `CORS_ALLOWED_ORIGINS` env var to restrict allowed origins
- **File Validation**: Uploads are validated to be genuine dump files
- **Path Sanitization**: User identifiers are sanitized to prevent path traversal
- **Session Isolation**: Users can only access their own sessions

---

## 🆘 Troubleshooting

### "401 Unauthorized" Error
- Include `X-API-Key` header if API key authentication is enabled
- Verify the API key matches the `API_KEY` environment variable

### "Invalid dump file format" Error
- Ensure you're uploading a valid memory dump file
- Supported: Windows MDMP/PAGE, Linux ELF core, macOS Mach-O core

### "Dump file not found" Error
- Ensure the dump was uploaded via HTTP API first
- Verify the `dumpId` is correct
- Check that the `userId` matches

### "Session not found" Error
- Verify the `sessionId` is correct
- Check if the session was already closed or cleaned up
- Use `ListSessions` to see active sessions

### "User does not have access to session" Error
- Ensure the `userId` matches the one used to create the session
- Sessions are isolated per user for security

---

## 📚 Additional Resources

- **MCP Resources**: Use `debugger://windbg-commands`, `debugger://lldb-commands`, `debugger://sos-commands` for command references
- [WinDbg Command Reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/)
- [SOS Debugging Extension](https://learn.microsoft.com/en-us/dotnet/framework/tools/sos-dll-sos-debugging-extension)
- [LLDB Documentation](https://lldb.llvm.org/)
- **Swagger UI**: `http://localhost:5000/swagger` - Interactive API documentation
