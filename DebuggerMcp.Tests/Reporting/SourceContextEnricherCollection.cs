using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Serializes tests that mutate <see cref="DebuggerMcp.Reporting.SourceContextEnricher"/> static state.
/// </summary>
[CollectionDefinition("SourceContextEnricher", DisableParallelization = true)]
public sealed class SourceContextEnricherCollection;

