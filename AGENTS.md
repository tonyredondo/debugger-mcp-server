# Agent Guidelines for Debugger MCP Server

This document provides guidelines for AI agents working on this repository. Following these guidelines ensures code quality, consistency, and maintainability.

## Project Overview

This is a cross-platform MCP (Model Context Protocol) server for controlling debuggers (WinDbg/LLDB) and analyzing memory dumps. The codebase includes:

- **DebuggerMcp**: Main server with MCP tools, HTTP API controllers, analyzers, and core functionality
- **DebuggerMcp.Cli**: Command-line interface client
- **DebuggerMcp.Tests**: Unit tests for the main server
- **DebuggerMcp.Cli.Tests**: Unit tests for the CLI

## Development Workflow

### Interaction Cadence
- Prefer batching meaningful edits before pausing; avoid frequent stop/start updates after only 1–2 trivial changes.
- Surface interim status only at natural checkpoints (e.g., end of a batch, before running build/test, or when blocked/at risk).
- Always validate (build/test) after a batch, not after every micro-edit, unless a change obviously risks stability.

### 1. Implementation Review (Mandatory)

After implementing and completing a feature or plan, you **must review your implementation at least 3 times**, focusing exclusively on finding issues and bugs:

```
Review 1: Logic and correctness
- Does the code do what it's supposed to do?
- Are there any edge cases not handled?
- Are there any potential null reference exceptions?

Review 2: Security and robustness
- Are inputs validated and sanitized?
- Are there any path traversal vulnerabilities?
- Is error handling comprehensive?

Review 3: Code quality and consistency
- Does it follow existing patterns in the codebase?
- Are there any code duplications that should be refactored?
- Is the code properly documented with XML comments?
```

### 2. Testing Requirements (Mandatory)

Every implementation **must include tests**. This project uses xUnit for testing.

#### Test Location
- Server tests: `DebuggerMcp.Tests/`
- CLI tests: `DebuggerMcp.Cli.Tests/`

#### Test Patterns
Follow existing test patterns in the codebase:

```csharp
public class MyFeatureTests
{
    [Fact]
    public void MethodName_Scenario_ExpectedBehavior()
    {
        // Arrange
        // Act
        // Assert
    }

    [Theory]
    [InlineData("input1", "expected1")]
    [InlineData("input2", "expected2")]
    public void MethodName_WithVariousInputs_ReturnsExpected(string input, string expected)
    {
        // Test implementation
    }
}
```

#### What to Test
- Happy path scenarios
- Edge cases and boundary conditions
- Error handling and exceptions
- Input validation
- Integration between components (where applicable)

### 3. Building the Solution (Mandatory)

After working on a feature, **always build the entire solution** to catch compilation errors:

```bash
# Build with network access (required for NuGet restore)
dotnet build
```

**Important**: Building requires network access for NuGet package restoration. Use the following permission when running terminal commands:

```
required_permissions: ["network"]
```

If you encounter permission errors, use:
```
required_permissions: ["all"]
```

**Codex CLI note (sandboxing)**: When running `dotnet build` via the agent tooling, prefer running it outside the workspace sandbox so the build can freely use the global NuGet cache and system temp locations:

```
sandbox_permissions: require_escalated
required_permissions: ["network"]
```

### 4. Running Tests (Mandatory)

**Always run the tests** before considering any work complete:

```bash
# Run all tests
dotnet test

# Run tests for a specific project
dotnet test DebuggerMcp.Tests/
dotnet test DebuggerMcp.Cli.Tests/

# Run specific test class
dotnet test --filter "FullyQualifiedName~MyFeatureTests"

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"
```

**Codex CLI note (sandboxing)**: When running `dotnet test` via the agent tooling, request an escalated (unsandboxed) run to avoid failures/hangs related to writing diagnostics, temp files, and NuGet caches:

```
sandbox_permissions: require_escalated
required_permissions: ["network"]
```

**All tests must pass before committing.** If tests fail:
1. Analyze the failure
2. Fix the issue
3. Run tests again
4. Repeat until all tests pass

### 5. Committing Changes (Mandatory)

If everything works correctly (build passes, all tests pass), **commit your changes with a detailed commit message**:

#### Commit Message Format
```
<type>: <short summary>

<detailed description of changes>

<list of specific changes if applicable>
```

#### Commit Types
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `test`: Adding or updating tests
- `refactor`: Code refactoring without functional changes
- `chore`: Maintenance tasks

#### Example Commit Message
```
feat: Add object inspection tool for .NET objects

Implement deep inspection of .NET objects in memory dumps with:
- Recursive field enumeration up to configurable depth
- Circular reference detection with [this] and [seen] markers
- Array element expansion with configurable limits
- Value type handling with dumpvc fallback

Added tests:
- ObjectInspectorTests with 15 test cases
- CollectionTypeDetectorTests for type detection
```

#### Committing with Git

**Important**: Committing requires network access for git hooks. Use:

```bash
# Stage and commit
git add <files>
git commit -m "commit message"
```

With permissions:
```
required_permissions: ["git_write", "network"]
```

Or if that fails:
```
required_permissions: ["all"]
```

## Code Style and Conventions

### C# Conventions
- Use primary constructors for dependency injection
- Use `readonly` for immutable fields
- Use nullable reference types (`#nullable enable`)
- Add XML documentation comments all methods and types
- Follow existing patterns for MCP tools, controllers, and analyzers

### MCP Tools Pattern
```csharp
[McpServerToolType]
public class MyTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<MyTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    [McpServerTool]
    [Description("Tool description for LLM consumption")]
    public async Task<string> MyTool(
        [Description("Parameter description")] string param)
    {
        // Implementation
    }
}
```

### Controller Pattern
```csharp
[ApiController]
[Route("api/myresource")]
[Authorize]
public class MyController : ControllerBase
{
    // Implementation
}
```

## Quick Reference

### Essential Commands
```bash
# Build
dotnet build

# Test
dotnet test

# Run server (stdio mode)
cd DebuggerMcp && dotnet run

# Run server (HTTP mode)
cd DebuggerMcp && dotnet run -- --mcp-http

# Run CLI
cd DebuggerMcp.Cli && dotnet run
```

### Project Structure
```
DebuggerMcp/
├── McpTools/           # MCP tool implementations
├── Controllers/        # HTTP API controllers
├── Analysis/           # Crash/performance analyzers
├── Security/           # Validators and sanitizers
├── Reporting/          # Report generators
├── ObjectInspection/   # .NET object inspection
├── Watches/            # Watch expression handling
└── SourceLink/         # Source Link resolution

DebuggerMcp.Tests/
├── McpTools/           # Tool tests
├── Controllers/        # Controller tests
├── Analysis/           # Analyzer tests
└── Security/           # Security component tests
```

## Checklist Before Completing Work

- [ ] Implementation complete
- [ ] Code reviewed 3 times for bugs/issues
- [ ] Tests added for new functionality
- [ ] Solution builds successfully
- [ ] All tests pass
- [ ] Changes committed with detailed message

---

**Remember**: Quality over speed. Taking time to review, test, and document properly prevents bugs and makes the codebase maintainable.
