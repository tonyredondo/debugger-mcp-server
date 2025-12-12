namespace DebuggerMcp.Tests;

/// <summary>
/// Test collection for cases that mutate process-wide environment variables.
/// </summary>
/// <remarks>
/// Environment variables are shared across all tests in the same process.
/// Any test that calls <see cref="Environment.SetEnvironmentVariable(string,string?)"/>
/// must be serialized to avoid flaky cross-test interference.
/// </remarks>
[CollectionDefinition("NonParallelEnvironment", DisableParallelization = true)]
public class NonParallelEnvironmentCollection
{
}

