using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for <see cref="LldbManager"/> module-list helpers.
/// </summary>
public class LldbManagerModuleFilterTests
{
    [Fact]
    public void FilterNativeModulesFromVerifyCore_FiltersSoAndExcludesDebugFiles()
    {
        var input = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            ["libc.so.6"] = 0x1000,
            ["libcoreclr.so"] = 0x2000,
            ["libssl.so.1.1"] = 0x3000,
            ["libc.so.6.dbg"] = 0x4000,
            ["libfoo.so.debug"] = 0x5000,
            ["System.Private.CoreLib.dll"] = 0x6000,
            ["not-a-module"] = 0x7000
        };

        var filtered = LldbManager.FilterNativeModulesFromVerifyCore(input);

        Assert.Contains(filtered, kv => kv.Key == "libc.so.6" && kv.Value == 0x1000);
        Assert.Contains(filtered, kv => kv.Key == "libcoreclr.so" && kv.Value == 0x2000);
        Assert.Contains(filtered, kv => kv.Key == "libssl.so.1.1" && kv.Value == 0x3000);

        Assert.DoesNotContain(filtered, kv => kv.Key.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(filtered, kv => kv.Key.EndsWith(".debug", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(filtered, kv => kv.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetMissingCachedFiles_ReturnsMissingRelativePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerModuleFilterTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "a"));
            File.WriteAllText(Path.Combine(tempDir, "a", "present.txt"), "ok");

            var cached = new List<string> { "a/present.txt", "missing.txt" };
            var missing = LldbManager.GetMissingCachedFiles(cached, tempDir);

            Assert.Single(missing);
            Assert.Equal("missing.txt", missing[0]);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}

