using DebuggerMcp;

namespace DebuggerMcp.Tests.TestDoubles;

/// <summary>
/// Minimal fake implementation of <see cref="IDebuggerManager"/> for unit tests.
/// </summary>
public sealed class FakeDebuggerManager : IDebuggerManager
{
    public bool IsInitialized { get; set; } = true;
    public bool IsDumpOpen { get; set; } = true;
    public string? CurrentDumpPath { get; set; }
    public string DebuggerType { get; set; } = "Fake";
    public bool IsSosLoaded { get; set; } = true;
    public bool IsDotNetDump { get; set; } = true;

    public Func<string, string> CommandHandler { get; set; } = _ => string.Empty;

    public List<string> ExecutedCommands { get; } = new();

    public List<string> ConfiguredSymbolPaths { get; } = new();

    public Task InitializeAsync()
    {
        IsInitialized = true;
        return Task.CompletedTask;
    }

    public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
    public void CloseDump() => throw new NotSupportedException();
    public string ExecuteCommand(string command)
    {
        ExecutedCommands.Add(command);
        return CommandHandler(command);
    }
    public void LoadSosExtension() => throw new NotSupportedException();
    public void ConfigureSymbolPath(string symbolPath)
    {
        ConfiguredSymbolPaths.Add(symbolPath);
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
