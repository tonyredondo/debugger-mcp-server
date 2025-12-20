# Contributing

Thanks for helping improve Debugger MCP Server.

## Development prerequisites

- .NET 10 SDK
- Platform debugger:
  - Windows: WinDbg / Debugging Tools for Windows
  - Linux/macOS: LLDB

## Repository layout

- `DebuggerMcp/`: MCP server + HTTP upload API
- `DebuggerMcp.Cli/`: `dbg-mcp` interactive CLI
- `DebuggerMcp.Tests/`: server unit tests (xUnit)
- `DebuggerMcp.Cli.Tests/`: CLI unit tests (xUnit)

## Workflow

1. Make focused changes and keep docs in sync.
2. Add/adjust tests for new behavior or public contracts.
3. Build and run tests before committing:
   - `dotnet build DebuggerMcp.slnx -c Release`
   - `dotnet test DebuggerMcp.slnx -c Release`

## Code quality

- Follow existing patterns and naming.
- Prefer fixing root causes over patching symptoms.
- Keep public-facing behavior stable; when it changes, update docs and add contract tests under `DebuggerMcp.Tests/Documentation/`.

