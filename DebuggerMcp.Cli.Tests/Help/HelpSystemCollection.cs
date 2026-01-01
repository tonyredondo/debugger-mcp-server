using Xunit;

namespace DebuggerMcp.Cli.Tests.Help;

/// <summary>
/// Serializes tests that touch <see cref="DebuggerMcp.Cli.Help.HelpSystem"/> because some tests
/// temporarily mutate its global dictionaries (e.g., to cover legacy help fallbacks).
/// </summary>
[CollectionDefinition("HelpSystem", DisableParallelization = true)]
public sealed class HelpSystemCollection;

