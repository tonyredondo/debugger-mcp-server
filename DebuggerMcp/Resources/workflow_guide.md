# Debugger MCP Server - Workflow Guide

## 🎯 Complete Workflow for Analyzing Memory Dumps

This guide explains the complete workflow for using the Debugger MCP Server to analyze memory dumps. The server supports WinDbg on Windows and LLDB on macOS/Linux.

> MCP tool names note: the server exposes a compact 11-tool MCP surface. The canonical list is in `debugger://mcp-tools`.
> If you see older examples referencing tools like `create_session` or `open_dump`, translate them using that reference.

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

Use the MCP tool `create_session` to create a new debugging session.

**Tool**: `create_session`

**Parameters**:
- `userId`: Your user identifier (same as used in Step 1)

**Example**:
```
create_session(userId: "your-user-id")
```

**Response**:
```
Session created successfully. Session ID: session-xyz-789

IMPORTANT: Save this session ID for subsequent operations.
```

**SAVE THE `sessionId`** - You will need it for all subsequent operations!

---

### Step 4: Open the Dump File (MCP)

Open the dump file using the `dumpId` from Step 1. The debugging engine is automatically initialized when opening the first dump.

**Tool**: `open_dump`

**Parameters**:
- `sessionId`: The session ID from Step 3
- `userId`: Your user identifier
- `dumpId`: The dump ID from Step 1

**Example**:
```
open_dump(
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

**Tool**: `execute_command`

**Parameters**:
- `sessionId`: The session ID from Step 3
- `userId`: Your user identifier
- `command`: The debugger command to execute

**Common Commands (Windows - WinDbg)**:
```
# Display call stack
execute_command(sessionId, userId, command: "k")

# Verbose crash analysis
execute_command(sessionId, userId, command: "!analyze -v")

# List all threads
execute_command(sessionId, userId, command: "~*k")

# List loaded modules
execute_command(sessionId, userId, command: "lm")
```

**Common Commands (Linux/macOS - LLDB)**:
```
# Display backtrace
execute_command(sessionId, userId, command: "bt")

# List all threads
execute_command(sessionId, userId, command: "thread list")

# List loaded images
execute_command(sessionId, userId, command: "image list")
```

---

### Step 6: Automated Crash Analysis (MCP)

Use the automated analysis tools for structured output.

**Tool**: `analyze_crash`

Returns JSON with:
- Crash type and exception information
- Call stack analysis
- Thread information
- **Memory leak indicators**
- **Deadlock detection**
- Recommendations

```
analyze_crash(sessionId: "session-xyz-789", userId: "your-user-id")
```

---

### Step 7: .NET-Specific Analysis (MCP)

For .NET dumps, SOS is **automatically loaded** when `open_dump` detects a .NET runtime.
You can verify this by checking the `open_dump` response which will say ".NET dump detected, SOS auto-loaded."

> **Note**: The `load_sos` command is still available for backwards compatibility or if auto-detection fails.

Use .NET-specific analysis tools:

**Tool**: `analyze_dot_net_crash`

Returns JSON with:
- CLR version and runtime info
- Managed exceptions with stack traces
- Heap statistics and large object allocations
- **.NET-specific memory leak detection**
- **Async deadlock detection** (Tasks, SemaphoreSlim, etc.)
- GC and finalization queue analysis

```
analyze_dot_net_crash(sessionId: "session-xyz-789", userId: "your-user-id")
```

**Common SOS Commands**:
```
# List managed threads
execute_command(sessionId, userId, command: "!threads")

# Show heap statistics
execute_command(sessionId, userId, command: "!dumpheap -stat")

# Display current exception
execute_command(sessionId, userId, command: "!pe")

# Analyze async state machines
execute_command(sessionId, userId, command: "!dumpasync")
```

---

### Step 9: Close the Dump (MCP)

When done analyzing the current dump, close it.

**Tool**: `close_dump`

```
close_dump(sessionId: "session-xyz-789", userId: "your-user-id")
```

**Note**: You can open another dump in the same session after closing.

---

### Step 10: Close the Session (MCP)

When completely done with debugging, close the session to release resources.

**Tool**: `close_session`

```
close_session(sessionId: "session-xyz-789", userId: "your-user-id")
```

---

## 🔄 Alternative Workflows

### Analyzing Multiple Dumps in One Session

```
1. create_session
2. open_dump (dump1) - auto-initializes debugger
3. analyze_crash or execute_command (analyze dump1)
4. close_dump
5. open_dump (dump2)
6. analyze_crash or execute_command (analyze dump2)
7. close_dump
8. close_session
```

### Comparing .NET Memory Leaks

```
1. create_session
2. open_dump (baseline dump)  # SOS auto-loaded
3. analyze_dot_net_crash → Save heap statistics
4. close_dump
5. open_dump (leak dump)  # SOS auto-loaded
6. analyze_dot_net_crash → Compare heap statistics
7. close_session
```

### Managing Multiple Concurrent Sessions

```
User can have up to 5 concurrent sessions:

Session 1: Analyzing production crash
Session 2: Analyzing memory leak
Session 3: Comparing two dumps
...

Use list_sessions(userId) to see all active sessions (JSON).
```

### Comparing Two Dumps (Memory Leak Detection)

```
1. create_session → session1
2. create_session → session2
3. open_dump(session1, userId, baseline-dumpId)
4. open_dump(session2, userId, comparison-dumpId)
5. compare_dumps(session1, userId, session2, userId)
   → Returns heap changes, thread changes, module changes
6. compare_heaps(session1, userId, session2, userId)
   → Detailed memory growth analysis
7. close_session(session1, userId)
8. close_session(session2, userId)
```

### Using Watch Expressions

```
1. open_dump
2. add_watch(sessionId, userId, "0x12345678", "Suspicious pointer")
3. add_watch(sessionId, userId, "g_AppState", "Global state variable")
4. evaluate_watches(sessionId, userId)
   → Returns current values with insights (null pointers, freed memory, etc.)
5. analyze_crash(sessionId, userId)
   → Watch results included in analysis
```

### Generating Reports

```
1. open_dump  # SOS auto-loaded for .NET dumps
2. generate_report(sessionId, userId, format="html", includeWatches=true)
   → Returns comprehensive HTML report
3. generate_summary_report(sessionId, userId)
   → Quick summary with key findings
```

### Security Analysis

```
1. open_dump
2. analyze_security(sessionId, userId)
   → Returns vulnerabilities, memory protections, exploit patterns
3. get_security_check_capabilities()
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
- **Query Params**: `format` (markdown, html, json), `includeRaw` (bool)
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
