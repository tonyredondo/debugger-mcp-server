namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Test collection for cases that mutate process-wide Console state.
/// </summary>
[Xunit.CollectionDefinition("NonParallelConsole", DisableParallelization = true)]
public class NonParallelConsoleCollection
{
}

