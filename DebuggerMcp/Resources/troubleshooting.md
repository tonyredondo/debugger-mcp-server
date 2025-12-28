# Troubleshooting Guide

Solutions to common issues when using the Debugger MCP Server.

---

## 🔐 Authentication Issues

### "401 Unauthorized" Error

**Cause**: API key authentication is enabled but not provided.

**Solution**:
1. Include the `X-API-Key` header in your requests:
   ```bash
   curl -H "X-API-Key: your-api-key" http://localhost:5000/api/dumps/user/user123
   ```
2. Verify the key matches the `API_KEY` environment variable on the server.

### CORS Errors in Browser

**Cause**: Origin not allowed by CORS configuration.

**Solution**:
1. Set `CORS_ALLOWED_ORIGINS` environment variable:
   ```bash
   export CORS_ALLOWED_ORIGINS="https://your-app.com,https://admin.your-app.com"
   ```
2. For development, leave `CORS_ALLOWED_ORIGINS` unset to allow any origin.

---

## 📁 Dump Upload Issues

### "Invalid dump file format" Error

**Cause**: The uploaded file is not a valid memory dump.

**Solution**:
- Verify the file is a valid dump format:
  - **Windows**: `.dmp` files with MDMP or PAGE signature
  - **Linux**: ELF core dumps (verify with `file dump.core`)
  - **macOS**: Mach-O core dumps
- The server validates magic bytes at the start of the file.

### "File size exceeds maximum" Error

**Cause**: Dump file is larger than the configured maximum upload size (default: 5GB).

**Solution**:
- Compress large dumps before analysis
- For Windows, create a minidump instead of full dump
- Consider using selective memory dump options

If you control the server, you can increase the limit via `MAX_REQUEST_BODY_SIZE_GB`.

### Upload Timeout

**Cause**: Large file upload taking too long.

**Solution**:
- Increase client timeout settings
- Use a faster network connection
- Consider uploading during off-peak hours

---

## 🔍 Session Issues

### "Dump file not found" Error

**Causes**:
- Dump was not uploaded via HTTP API
- Incorrect `dumpId`
- Incorrect `userId`

**Solutions**:
1. Ensure the dump was uploaded first via `/api/dumps/upload`
2. Verify the `dumpId` from the upload response
3. Ensure `userId` matches the one used during upload

### "Session not found" Error

**Causes**:
- Invalid `sessionId`
- Session was closed or cleaned up

**Solutions**:
1. Verify the `sessionId` from `CreateSession` response
2. Use `ListSessions(userId)` to see active sessions
3. Sessions auto-cleanup after 24 hours of inactivity by default (configurable via `SESSION_INACTIVITY_THRESHOLD_MINUTES`)

### "User does not have access to session" Error

**Cause**: `userId` doesn't match session owner.

**Solution**:
- Use the same `userId` that created the session
- Sessions are isolated per user for security

### "Maximum sessions reached" Error

**Cause**: User has reached the per-user session limit (default: 10).

**Solution**:
- Close unused sessions with `CloseSession`
- Use `ListSessions` to see all active sessions
- Wait for automatic cleanup (24 hours by default)
- If you control the server, increase the limit via `MAX_SESSIONS_PER_USER`

---

## 🧠 Crash Analysis Safety Valves

### SIGSEGV / native crash during heap or sync-block analysis

**Cause**: Some heap walks (sync blocks / object enumeration) can crash in cross-architecture or emulation scenarios (for example, analyzing an `x64` dump on an `arm64` host).

**Solution**:
- Set `SKIP_HEAP_ENUM=true` (or legacy: `SKIP_SYNC_BLOCKS=true`) on the server to skip heap/sync-block enumeration.
- Prefer using a server that matches the dump architecture and libc (e.g., Alpine/musl dumps on Alpine images).

This reduces leak/deadlock detail in the JSON report but keeps the rest of the analysis running.

---

## 🔧 Debugger Issues

### Windows: "Unable to load DbgEng.dll"

**Cause**: Debugging Tools for Windows not installed.

**Solution**:
```bash
# Install Windows SDK
winget install Microsoft.WindowsSDK

# Or download from Microsoft
# https://developer.microsoft.com/windows/downloads/windows-sdk/
```

### Windows: SOS Extension Not Found

**Cause**: .NET SDK not installed or wrong version.

**Solutions**:
1. Verify .NET SDK is installed:
   ```bash
   dotnet --list-sdks
   ```
2. Use correct SOS load command:
   ```
   .loadby sos coreclr    # .NET Core/.NET 5+
   .loadby sos clr        # .NET Framework
   ```
3. Install dotnet-sos:
   ```bash
   dotnet tool install -g dotnet-sos
   dotnet-sos install
   ```

### Linux: "lldb: command not found"

**Solution**:
```bash
# Ubuntu/Debian
sudo apt-get install lldb

# Fedora/RHEL
sudo dnf install lldb

# Arch Linux
sudo pacman -S lldb
```

### Linux/macOS: "libsosplugin.so not found"

**Cause**: SOS plugin not found in expected location.

**Solutions**:
1. Find the plugin:
   ```bash
   find /usr -name "libsosplugin.so" 2>/dev/null      # Linux
   find /usr -name "libsosplugin.dylib" 2>/dev/null   # macOS
   ```
2. Set custom path:
   ```bash
   export SOS_PLUGIN_PATH="/path/to/libsosplugin.so"
   ```
3. If you’re using the Debugger MCP Server, avoid manually loading SOS during normal flows; `dump(action="open")` auto-loads SOS for .NET dumps. If you have explicit evidence SOS is not loaded, use `inspect(kind: "load_sos", sessionId: "...", userId: "...")`.
4. Install dotnet-sos:
   ```bash
   dotnet tool install -g dotnet-sos
   dotnet-sos install
   ```

### macOS: "xcrun: error: unable to find utility 'lldb'"

**Solution**:
```bash
xcode-select --install
```

---

## 📊 Symbol Issues

### "No symbols loaded" / Poor Stack Traces

**Causes**:
- Symbol files not uploaded
- Symbol server not accessible
- Symbol mismatch
- ZIP upload contained no symbol entries (non-symbol files are ignored)

**Solutions**:

1. **Upload application symbols**:
   ```bash
   curl -X POST http://localhost:5000/api/symbols/upload \
     -F "file=@MyApp.pdb" \
     -F "dumpId=abc123"
   ```

   If you upload a ZIP archive, only symbol-related entries are extracted (other entries are ignored). If the ZIP is very large or suspiciously compressed, the server may reject it with a `400` error.

2. **Verify symbol server access** (Windows/WinDbg):
   ```
   .symfix
   .reload
   !sym noisy
   ```

3. **Check symbol path**:
   ```
   .sympath
   ```

4. **Force reload symbols** (Windows):
   ```
   .reload /f
   ```

### Slow Symbol Loading

**Cause**: Downloading from symbol servers.

**Solutions**:
- Pre-cache symbols locally
- Use local symbol server
- Add local cache path:
  ```
  .symfix+ C:\LocalSymbols
  ```

---

## 🤖 AI / LLM Sampling Issues

### "AI analysis failed: empty sampling response."

**Cause**: The connected MCP client returned no assistant content/tool calls during sampling (often due to an upstream LLM/provider error).

**Solutions**:
- Ensure your MCP client supports sampling (`sampling/createMessage`) with tools enabled.
  - The `dbg-mcp` CLI supports sampling when an LLM provider is configured (`OPENROUTER_API_KEY` or `OPENAI_API_KEY` + `llm provider openai`).
- Try a different model/provider (some providers have stricter tool-call requirements).

### "I can't see what was sent to the LLM" / "Sampling is truncated"

Enable server-side sampling traces (may include sensitive debugger output):
```bash
export DEBUGGER_MCP_AI_SAMPLING_TRACE=true
export DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES=true
export DEBUGGER_MCP_AI_SAMPLING_TRACE_MAX_FILE_BYTES=2000000
# Optional: override how often the sampling loop checkpoints/prunes context (default: 4).
export DEBUGGER_MCP_AI_SAMPLING_CHECKPOINT_EVERY_ITERATIONS=4
```

Trace files are written under:
- `LOG_STORAGE_PATH/ai-sampling`
  - In `docker-compose.yml`, each service mounts a different host logs directory (e.g., `./logs`, `./logs-alpine`, `./logs-x64`). Trace files appear under the corresponding directory in `ai-sampling/`.

### "The AI keeps using dumpobj instead of inspect"

**Cause**: The model defaults to familiar SOS commands.

**Solution**: Prefer the first-class sampling tool:
- Use `inspect(address: "0x...", maxDepth: 3)` for managed object inspection (more complete and safer than `exec "sos dumpobj ..."`).

---

## 💾 Memory Issues

### Out of Memory During Analysis

**Cause**: Large dump exceeds available memory.

**Solutions**:
- Use 64-bit debugging tools
- Increase system RAM or page file
- Use selective analysis commands instead of full heap dumps
- Close other applications

### Analysis Hangs

**Causes**:
- Very large dump file
- Complex symbol resolution
- Network issues with symbol servers

**Solutions**:
- Wait for completion (can take several minutes for large dumps)
- Check network connectivity
- Use local symbols to avoid network delays

---

## 🌐 Network Issues

### Cannot Connect to Server

**Causes**:
- Server not running
- Wrong port
- Firewall blocking

**Solutions**:
1. Verify server is running:
   ```bash
   curl http://localhost:5000/swagger
   ```
2. Check port configuration:
   ```bash
   export ASPNETCORE_URLS="http://localhost:5000"
   ```
3. Check firewall settings

### Docker Connection Issues

**Solutions**:
1. Ensure port mapping is correct:
   ```bash
   docker run -p 5000:5000 debugger-mcp-server
   ```
2. Use correct host:
   - From host: `localhost:5000`
   - From another container: `container-name:5000`

---

## 🔄 MCP Issues

### MCP Client Not Connecting

**Solutions**:
1. For stdio mode, verify command path in config:
   ```json
   {
     "mcpServers": {
       "debugger": {
         "command": "/full/path/to/DebuggerMcp"
       }
     }
   }
   ```

2. For HTTP mode, verify URL:
   ```json
   {
     "mcpServers": {
       "debugger": {
         "transport": {
           "type": "http",
           "url": "http://localhost:5000/mcp"
         }
       }
     }
   }
   ```

---

## 📞 Getting Help

### Diagnostic Information to Collect

When reporting issues, include:

1. **Server version and platform**:
   ```bash
   dotnet --version
   uname -a  # Linux/macOS
   ```

2. **Error messages** (full text)

3. **Dump file info**:
   ```bash
   file dump.dmp  # Linux/macOS
   ```

4. **Server logs** (if available)

5. **Steps to reproduce**

### Resources

- **MCP Resources**: Use `debugger://troubleshooting` in MCP client
- **Swagger UI**: `http://localhost:5000/swagger`
- **WinDbg Docs**: https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/
- **LLDB Docs**: https://lldb.llvm.org/
- **SOS Docs**: https://learn.microsoft.com/en-us/dotnet/framework/tools/sos-dll-sos-debugging-extension
