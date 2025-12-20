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
dbg-mcp> analyze crash -o ./crash.json

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
- `symbols datadog <subcommand>` - Download/load Datadog tracer symbols (optional)
- `stats` - Server statistics

#### Symbol Upload Notes
- Symbol files are stored under `.symbols_<dumpId>/` on the server.
- Uploaded symbol file names are normalized to a safe basename (any directory components are stripped).
- If you upload a `.zip`, the server extracts only symbol-related entries and ignores other files; see `debugger://workflow-guide` for the ZIP extraction rules and limits.

#### Datadog Trace Symbols (Optional)
If your dump includes Datadog tracer components (e.g., `Datadog.Trace.dll`), you can download and load matching symbols:
```bash
symbols datadog prepare
symbols datadog download --force-version
symbols datadog config
```

### Session Management (`help session`)
- `session create` - Create session
- `session list` - List sessions
- `session use <id>` - Attach to session (partial IDs work!)
- `session close <id>` - Close session

### Debugging (`help debugging`)
- `open <dumpId>` - Open dump
- `close` - Close dump
- `exec <cmd>` - Execute command
- `cmd` - Multi-line debugger command mode
- `showobj <address>` - Inspect .NET object as JSON (ClrMD)

### Analysis (`help analysis`)
- `analyze <type> -o <file>` - Run analysis and save output (e.g., `analyze crash -o ./crash.json`)
- `analyze ai -o <file>` - AI-powered deep crash analysis (requires MCP sampling / LLM config)
- `analyze perf|cpu|memory|gc|contention -o <file>` - Performance analyses (save JSON)
- `analyze security -o <file>` - Security scan (save JSON)
- `compare <s1> <s2>` - Compare dumps

### LLM (`help llm`)
- `llm <prompt>` - Ask a configured LLM (OpenRouter/OpenAI/Anthropic) using your CLI transcript as context
- `llm set-agent <true|false>` - Enable/disable tool-using agent mode for `llm`
- `llm reasoning-effort <low|medium|high|unset>` - Set reasoning effort for the current provider/model
- `llm reset` - Clear LLM context (conversation + transcript context) for the current session/dump
- `llmagent` - Interactive agent mode (no `llm` prefix required)

In `llmagent` mode:
- Exit with `exit`/Ctrl+C or `/exit`
- Use `/help`, `/tools`, `/reset`, `/reset conversation`
- Tool confirmations are disabled by default for the duration of the session (the previous setting is restored on exit)

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

# LLM provider (required for llm / llmagent, and for analyze ai when using the dbg-mcp CLI as the sampling client)
export OPENROUTER_API_KEY="..."
# Or:
export OPENAI_API_KEY="..."
# Or:
export ANTHROPIC_API_KEY="..."

# Optional:
export DEBUGGER_MCP_LLM_PROVIDER="openrouter"   # or "openai" or "anthropic"
export OPENROUTER_MODEL="openrouter/auto"
export OPENAI_MODEL="gpt-4o-mini"
export ANTHROPIC_MODEL="claude-3-5-sonnet-20240620"
export DEBUGGER_MCP_LLM_REASONING_EFFORT="medium"   # low|medium|high|unset

# If provider is "openai" and no API key is configured, the CLI can fall back to ~/.codex/auth.json (expects OPENAI_API_KEY).
# Override the Codex auth file path with:
export DEBUGGER_MCP_CODEX_AUTH_PATH="/path/to/auth.json"
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
analyze crash -o ./crash.json
report -o ./crash-report.md
```
Note: `analyze crash`, `analyze ai`, and `report --format json` all use the same canonical JSON report schema (`{ "metadata": { ... }, "analysis": { ... } }`).

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
