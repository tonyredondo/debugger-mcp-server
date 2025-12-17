using DebuggerMcp.SourceLink;

namespace DebuggerMcp.Tests.SourceLink;

public class PdbSearchPathBuilderTests
{
    [Fact]
    public void BuildExistingPaths_IncludesPerDumpSymbolsAndDatadog_WhenPresent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var dumpId = "abc123";
            var dumpPath = Path.Combine(tempRoot, $"{dumpId}.dmp");
            File.WriteAllText(dumpPath, "dummy");

            var symbolsDir = Path.Combine(tempRoot, $".symbols_{dumpId}");
            var datadogDir = Path.Combine(symbolsDir, ".datadog");

            Directory.CreateDirectory(symbolsDir);
            Directory.CreateDirectory(datadogDir);

            var paths = PdbSearchPathBuilder.BuildExistingPaths(dumpPath, dumpId, runtime: null);

            Assert.Contains(Path.GetFullPath(tempRoot), paths);
            Assert.Contains(Path.GetFullPath(symbolsDir), paths);
            Assert.Contains(Path.GetFullPath(datadogDir), paths);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void BuildExistingPaths_DeduplicatesPaths()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var dumpPath = Path.Combine(tempRoot, "x.dmp");
            File.WriteAllText(dumpPath, "dummy");

            var symbolsDir = Path.Combine(tempRoot, ".symbols_x");
            Directory.CreateDirectory(symbolsDir);

            var paths = PdbSearchPathBuilder.BuildExistingPaths(dumpPath, dumpId: null, runtime: null);

            Assert.Equal(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), paths.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}

