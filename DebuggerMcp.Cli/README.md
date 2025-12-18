# Debugger MCP CLI

A powerful command-line interface for the Debugger MCP Server, enabling remote crash dump analysis, debugging, and diagnostics.

## Features

- 🔌 **Remote Connection**: Connect to any Debugger MCP Server instance
- 📤 **File Upload**: Upload dump and symbol files with progress tracking
- 🔍 **Interactive Debugging**: Execute debugger commands in real-time
- 📊 **Crash Analysis**: Automated crash analysis for native and .NET applications
- 🤖 **AI Crash Analysis**: Deep, tool-driven analysis via MCP sampling (`analyze ai`)
- 🧠 **LLM + Agent Mode**: OpenRouter-backed chat and tool-using agent (`llm`, `llmagent`)
- ⚡ **Performance Profiling**: CPU, memory, GC, and thread contention analysis
- 🔐 **Security Scanning**: Detect potential vulnerabilities in crash dumps
- 📝 **Report Generation**: Generate comprehensive reports in Markdown, HTML, and JSON
- 🔗 **Source Link**: Link crash locations to source code repositories
- 👁️ **Watch Expressions**: Track values and expressions across sessions
- 📈 **Dump Comparison**: Compare two dumps to identify changes

## Installation

### As a .NET Global Tool

```bash
dotnet tool install -g DebuggerMcp.Cli
```

### From Source

```bash
git clone https://github.com/tonyredondo/debugger-mcp-server.git
cd debugger-mcp-server/DebuggerMcp.Cli
dotnet build
dotnet run
```

## Quick Start

```bash
# Start the CLI
dbg-mcp

# Connect to a server
dbg-mcp> connect http://localhost:5000

# Upload a dump file
dbg-mcp> dumps upload ./crash.dmp

# Open the dump (auto-creates session)
dbg-mcp> open <dumpId>

# Run crash analysis
dbg-mcp> analyze crash -o ./crash.json

# Execute debugger commands
dbg-mcp> exec !analyze -v
dbg-mcp> exec k

# Generate a report
dbg-mcp> report -o ./crash-report.md

# Exit
dbg-mcp> exit
```

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `DEBUGGER_MCP_URL` | Default server URL | - |
| `DEBUGGER_MCP_API_KEY` | Default API key | - |
| `DEBUGGER_MCP_USER_ID` | Default user ID | Current username |
| `DEBUGGER_MCP_TIMEOUT` | Request timeout (seconds) | 300 |
| `DEBUGGER_MCP_OUTPUT` | Output format (text/json) | text |
| `DEBUGGER_MCP_VERBOSE` | Enable verbose mode | false |
| `DEBUGGER_MCP_HISTORY_FILE` | Command history file | ~/.dbg-mcp/history |

#### LLM / OpenRouter

| Variable | Description | Default |
|----------|-------------|---------|
| `OPENROUTER_API_KEY` | OpenRouter API key (recommended) | - |
| `OPENROUTER_MODEL` | OpenRouter model id | openrouter/auto |
| `OPENROUTER_BASE_URL` | OpenRouter base URL | https://openrouter.ai/api/v1 |
| `OPENROUTER_TIMEOUT_SECONDS` | LLM request timeout | 120 |
| `DEBUGGER_MCP_OPENROUTER_API_KEY` | Alternate API key env var | - |
| `DEBUGGER_MCP_OPENROUTER_MODEL` | Alternate model env var | - |
| `DEBUGGER_MCP_LLM_AGENT_MODE` | Enable agent mode by default | false |
| `DEBUGGER_MCP_LLM_AGENT_CONFIRM` | Confirm each tool call in agent mode | true |

### Configuration File

Create `~/.dbg-mcp/config.json`:

```json
{
  "defaultServer": "http://localhost:5000",
  "apiKey": "your-api-key",
  "userId": "your-username",
  "timeout": 300,
  "outputFormat": "text",
  "historySize": 1000
}
```

### Multi-Server Configuration

For managing multiple servers, use `servers.json` (in the same directory as the CLI binary):

```json
{
  "servers": [
    { "url": "http://localhost:5000" },
    { "url": "http://localhost:5001" },
    { "url": "https://prod.example.com", "apiKey": "prod-key" }
  ]
}
```

Use `server init` to create a default configuration for docker-compose setups.

## Command Reference

### Connection Commands

| Command | Description |
|---------|-------------|
| `connect <url>` | Connect to a Debugger MCP Server |
| `disconnect` | Disconnect from the current server |
| `status` | Show connection and session status |
| `health [url]` | Check server health |

**Examples:**
```bash
connect http://localhost:5000
connect http://localhost:5000 --api-key my-secret-key
health http://localhost:5000
```

### Server Management Commands

Manage multiple server connections for cross-platform dump analysis.

| Command | Description |
|---------|-------------|
| `server list` | List all configured servers with capabilities |
| `server add <url>` | Add a new server to configuration |
| `server remove <url\|name>` | Remove a server by URL or name |
| `server switch <url\|name>` | Switch to a different server |
| `server init` | Create default config for docker-compose setup |

**Examples:**
```bash
# Initialize config with localhost servers (ports 5000-5003)
server init

# List all servers with their architecture and distro
server list

# Add a new server
server add http://debugger.prod.example.com
server add http://localhost:5004 --api-key my-secret

# Switch to a specific server
server switch alpine-x64           # By auto-generated name
server switch http://localhost:5001  # By URL

# Remove a server
server remove alpine-arm64
```

**Auto-generated Server Names:**
Servers are automatically named based on their capabilities:
- `debian-arm64` - Debian/glibc on ARM64
- `alpine-x64` - Alpine on x64
- `debian-x64` - Debian/glibc on x64

**Dump-Server Matching:**
When opening a dump, the CLI automatically checks if the server matches the dump's requirements:
- Architecture (arm64 vs x64)
- Distribution (Alpine vs Debian/glibc for proper symbol resolution)

If there's a mismatch, you'll be prompted to switch to a compatible server.

### File Operations

| Command | Description |
|---------|-------------|
| `dumps upload <file>` | Upload dump file with progress |
| `dumps list` | List available dumps |
| `dumps info <id>` | Get dump details |
| `dumps delete <id>` | Delete a dump |
| `symbols upload <file>` | Upload symbol file(s) |
| `symbols list` | List symbols for current dump |
| `stats` | Show server statistics |

**Examples:**
```bash
dumps upload ./crash.dmp
dumps upload ./crash.dmp --description "Production crash"
dumps list
symbols upload ./app.pdb
symbols upload *.pdb              # Wildcard support!
symbols upload ./bin/**/*.pdb     # Recursive wildcard!
```

### Session Management

| Command | Description |
|---------|-------------|
| `session create` | Create a new debugging session |
| `session list` | List all your sessions |
| `session use <id>` | Attach to existing session |
| `session close <id>` | Close a session |
| `session info <id>` | Get debugger info |

**Examples:**
```bash
session create
session list
session use d03              # Partial ID matching!
session close d0307dc3
```

### Debugging Commands

| Command | Description |
|---------|-------------|
| `open <dumpId>` | Open a dump file in debugger |
| `close` | Close current dump |
| `exec <cmd>` | Execute debugger command |
| `cmd` | Enter multi-line command mode (run debugger commands without typing `exec`) |
| `showobj <address>` | Inspect .NET object as JSON (ClrMD) |

**Examples:**
```bash
open abc123
exec !analyze -v
exec k
exec !dumpheap -stat
cmd
showobj 0x7f8a2b3c4d50
```

### Analysis Commands

| Command | Description |
|---------|-------------|
| `analyze crash -o <file>` | Crash analysis (saves JSON) |
| `analyze dotnet -o <file>` | .NET crash analysis (saves JSON) |
| `analyze ai -o <file>` | AI-assisted crash analysis via MCP sampling (saves JSON) |
| `analyze perf -o <file>` | Comprehensive performance profiling (saves JSON) |
| `analyze cpu -o <file>` | CPU usage and hot function analysis (saves JSON) |
| `analyze memory -o <file>` | Memory allocation analysis (saves JSON) |
| `analyze gc -o <file>` | Garbage collection behavior analysis (saves JSON) |
| `analyze contention -o <file>` | Thread contention and lock analysis (saves JSON) |
| `analyze security -o <file>` | Security vulnerability scan with CWE mappings (saves JSON) |

**Examples:**
```bash
analyze crash -o ./crash.json
analyze dotnet -o ./dotnet.json
analyze ai -o ./ai.json
analyze perf -o ./perf.json
analyze security
```

**AI analysis note**: `analyze ai` uses MCP sampling (`sampling/createMessage`). When using `dbg-mcp` as the connected MCP client, configure OpenRouter first (e.g., `OPENROUTER_API_KEY=...`).

### LLM Commands

| Command | Description |
|---------|-------------|
| `llm <prompt>` | Ask an OpenRouter-backed LLM using your CLI transcript as context |
| `llm set-key <key>` | Persist an OpenRouter API key to `~/.dbg-mcp/config.json` |
| `llm model <openrouter-model-id>` | Set the model |
| `llm set-agent <true|false>` | Enable/disable tool-using agent mode for `llm` |
| `llm set-agent-confirm <true|false>` | Confirm each tool call in agent mode |
| `llm reset` | Clear LLM context (conversation + transcript context) for the current session/dump |
| `llm reset conversation` | Clear only LLM conversation (keep CLI context) |
| `llmagent` | Interactive agent mode (no `llm` prefix required) |

`llmagent` slash commands:
```text
/help
/tools
/reset
/reset conversation
/exit
```

### Comparison Commands

| Command | Description |
|---------|-------------|
| `compare <s1> <s2>` | Full dump comparison |
| `compare heap <s1> <s2>` | Heap comparison |
| `compare threads <s1> <s2>` | Thread comparison |
| `compare modules <s1> <s2>` | Module comparison |

**Examples:**
```bash
compare session1 session2
compare heap d03 a8b          # Partial IDs work!
compare threads s1 s2
```

### Watch Commands

| Command | Description |
|---------|-------------|
| `watch add <expr>` | Add watch expression |
| `watch list` | List all watches |
| `watch eval` | Evaluate all watches |
| `watch eval <id>` | Evaluate specific watch |
| `watch remove <id>` | Remove a watch |
| `watch clear` | Clear all watches |

**Examples:**
```bash
watch add 0x7fff1234           # Memory address
watch add !dumpheap -stat      # Debugger command
watch add @rsp --name stack    # Named watch
watch list
watch eval
watch remove w1
```

### Report Commands

| Command | Description |
|---------|-------------|
| `report -o <file>` | Generate report (required) |
| `report -o <file> --format html` | Generate HTML report |
| `report -o <file> --format json` | Generate JSON report |
| `report --summary` | Generate summary only |

**Examples:**
```bash
report -o ./crash-report.md
report -o ./crash-report.html --format html
report -o ./crash-report.json --format json
report -o ./summary.json --summary --format json
```

### Source Link Commands

| Command | Description |
|---------|-------------|
| `sourcelink <path>` | Resolve source to URL |
| `sourcelink <path> <line>` | With line number |
| `sourcelink info` | Show Source Link config |

**Examples:**
```bash
sourcelink /src/Program.cs
sourcelink /src/Program.cs 42
sl info
```

### General Commands

| Command | Description |
|---------|-------------|
| `help` | Show help overview |
| `help <category>` | Show category commands |
| `help <command>` | Show command details |
| `help all` | List all commands |
| `history` | Show command history |
| `history <n>` | Show last n commands |
| `history search <term>` | Search history |
| `history clear` | Clear history |
| `clear` | Clear screen |
| `set <key> <value>` | Set configuration |
| `tools` | List MCP tools |
| `version` | Show version |
| `exit` | Exit CLI |

**Help Categories:**
- `connection` - Server connection commands
- `files` - File upload and management
- `session` - Session management
- `debugging` - Debugger commands
- `analysis` - Analysis commands
- `advanced` - Watch, report, sourcelink
- `general` - Help and configuration

## Interactive Shell

### Dynamic Prompt

The prompt shows your current context:

```
dbg-mcp>                                           # Not connected
dbg-mcp [localhost:5000]>                          # Connected
dbg-mcp [localhost:5000] session:d0307dc3>         # With session
dbg-mcp [localhost:5000] session:d0307dc3 dump:abc123 (WinDbg)>  # With dump
```

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `↑/↓` | Navigate command history |
| `Tab` | Auto-complete commands |
| `Ctrl+C` | Cancel current operation |
| `Ctrl+L` | Clear screen |
| `Ctrl+U` | Clear line before cursor |
| `Ctrl+K` | Clear line after cursor |
| `Ctrl+W` | Delete word before cursor |
| `Home/End` | Move to line start/end |

### Tab Completion

The CLI provides intelligent tab completion for:
- Command names
- Subcommands
- File paths (for upload/symbols)
- Dump IDs
- Session IDs (with partial matching)
- Analysis types
- Report formats

### Partial ID Matching

Like Docker, you can use partial IDs:
```bash
session use d           # If only one session starts with 'd'
session close d03       # Matches d0307dc3-5256-4eae-...
compare heap d03 a8b    # Compare with partial session IDs
```

## Output Formats

### Text (Default)

Human-readable formatted output with colors and tables.

### JSON

Machine-readable JSON output for scripting:
```bash
set output json
dumps list
```

## Scripting

Use environment variables and non-interactive mode:

```bash
# Set environment
export DEBUGGER_MCP_URL=http://localhost:5000
export DEBUGGER_MCP_API_KEY=my-key

# Run commands
echo "connect $DEBUGGER_MCP_URL
dumps upload ./crash.dmp
exit" | dbg-mcp
```

## Architecture

```
┌─────────────────┐              ┌────────────────────┐
│   CLI Client    │   HTTP/MCP   │  Debugger MCP      │
│                 │ ───────────► │     Server         │
│  • Spectre.UI   │              │  • WinDbg (Win)    │
│  • System.Cmd   │              │  • LLDB (Lin/Mac)  │
└─────────────────┘              └────────────────────┘
```

**Communication:**
- **HTTP API**: File uploads with progress, health checks
- **MCP over SSE**: All debugger operations via Model Context Protocol

## Troubleshooting

### Common Issues

**Cannot connect to server**
```
Error: Connection refused to http://localhost:5000
```
- Ensure the Debugger MCP Server is running with `--mcp-http`
- Check the URL is correct
- Verify firewall settings

**Authentication failed**
```
Error: Authentication failed. Please check your API key.
```
- Check your API key is correct
- Ensure the `API_KEY` environment variable is set on the server

**MCP operations fail**
```
Error: MCP not connected
```
- The server must be started with `--mcp-http` flag
- Check that MCP SSE endpoint is accessible

### Debug Mode

Enable verbose output for troubleshooting:
```bash
set verbose true
```

Or via environment:
```bash
export DEBUGGER_MCP_VERBOSE=true
dbg-mcp
```

### Network Retries

The CLI automatically retries failed requests:
- 5 retries with exponential backoff
- Retries on timeout, 5xx errors, and network failures
- Progress shown in terminal

## Contributing

See the main repository [CONTRIBUTING.md](../CONTRIBUTING.md) for guidelines.

## License

MIT License - see [LICENSE](../LICENSE) for details.
