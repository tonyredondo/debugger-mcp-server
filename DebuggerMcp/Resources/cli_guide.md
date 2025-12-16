# Debugger MCP CLI Guide

The `dbg-mcp` CLI provides a powerful command-line interface for remote crash dump analysis.

## Installation

```bash
# As a .NET global tool
dotnet tool install -g DebuggerMcp.Cli

# Or run from source
cd DebuggerMcp.Cli
dotnet run
```

## Quick Start

```bash
# Start the CLI
dbg-mcp

# Connect to server
dbg-mcp> connect http://localhost:5000

# Upload a dump
dbg-mcp> dumps upload ./crash.dmp

# Open and analyze
dbg-mcp> open <dumpId>
dbg-mcp> analyze crash

# Generate report
dbg-mcp> report -o ./report.md
```

## Command Categories

### Connection (`help connection`)
- `connect <url>` - Connect to server
- `disconnect` - Disconnect
- `status` - Show current status
- `health` - Check server health

### File Operations (`help files`)
- `dumps upload <file>` - Upload dump with progress
- `dumps list/info/delete` - Manage dumps
- `symbols upload <file>` - Upload symbols (wildcards supported: `*.pdb`)
- `stats` - Server statistics

### Session Management (`help session`)
- `session create` - Create session
- `session list` - List sessions
- `session use <id>` - Attach to session (partial IDs work!)
- `session close <id>` - Close session

### Debugging (`help debugging`)
- `open <dumpId>` - Open dump
- `close` - Close dump
- `exec <cmd>` - Execute command
- `sos` - Load .NET SOS
- `threads` / `stack` - Quick commands

### Analysis (`help analysis`)
- `analyze crash` - Crash analysis
- `analyze dotnet` - .NET analysis
- `analyze perf/cpu/memory/gc/contention` - Performance
- `analyze security` - Security scan
- `compare <s1> <s2>` - Compare dumps

### Advanced (`help advanced`)
- `watch add/list/eval/remove/clear` - Watch expressions
- `report -o <file> [--format html|json]` - Generate reports (output file required)
- `sourcelink <path>` - Resolve to source URL

## Interactive Features

### Dynamic Prompt
```
dbg-mcp>                                    # Not connected
dbg-mcp [localhost:5000]>                   # Connected
dbg-mcp [localhost:5000] session:d0307dc3>  # With session
dbg-mcp [localhost:5000] session:d0307dc3 dump:abc123>  # With dump
```

### Keyboard Shortcuts
- `↑/↓` - History navigation
- `Tab` - Auto-completion
- `Ctrl+C` - Cancel
- `Ctrl+L` - Clear screen

### Tab Completion
Commands, subcommands, file paths, dump IDs, and session IDs are all auto-completed.

### Partial ID Matching
Like Docker, use partial IDs:
```bash
session use d           # Matches d0307dc3-...
compare heap d03 a8b    # Partial session IDs
```

## Configuration

### Environment Variables
```bash
export DEBUGGER_MCP_URL=http://localhost:5000
export DEBUGGER_MCP_API_KEY=your-key
export DEBUGGER_MCP_VERBOSE=true
```

### Config File (`~/.dbg-mcp/config.json`)
```json
{
  "defaultServer": "http://localhost:5000",
  "apiKey": "your-key",
  "timeout": 300
}
```

### Multi-Server Config (`servers.json` next to CLI binary)
```json
{
  "servers": [
    { "url": "http://localhost:5000" },
    { "url": "http://localhost:5001", "apiKey": "key" }
  ]
}
```

Use `server init` to create default config, `server list` to view all servers.

## Common Workflows

### Analyze a Crash
```bash
connect http://localhost:5000
dumps upload ./crash.dmp
open <dumpId>
analyze crash
analyze dotnet
report -o ./crash-report.md
```

### Compare Two Dumps
```bash
connect http://localhost:5000
session create                    # Session 1
open <dumpId1>
session create                    # Session 2
open <dumpId2>
compare heap <session1> <session2>
```

### Track Values with Watches
```bash
watch add @rsp --name "stack pointer"
watch add !dumpheap -stat
watch eval                        # Evaluate all
report -o ./report.html --format html   # Include in report
```

## Troubleshooting

### Connection Issues
- Ensure server is running with `--mcp-http`
- Check firewall settings
- Verify URL and API key

### Debug Mode
```bash
set verbose true    # Enable verbose output
```

For more details, see `help <command>` in the CLI.
