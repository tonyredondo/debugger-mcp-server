#nullable enable

using System.Text.Json;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class ReportSectionApiTests
{
    [Fact]
    public void GetSection_WhenCursorIsInvalid_ReturnsInvalidCursorWithRecoveryPayload()
    {
        var reportJson = JsonSerializer.Serialize(new
        {
            metadata = new { dumpId = "d1", generatedAt = "2025-01-01T00:00:00Z" },
            analysis = new
            {
                threads = new
                {
                    all = new[]
                    {
                        new { threadId = "1" },
                        new { threadId = "2" }
                    }
                }
            }
        });

        var result = ReportSectionApi.GetSection(
            reportJson: reportJson,
            path: "analysis.threads.all",
            limit: 1,
            cursor: "not-a-cursor",
            maxChars: 200_000);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.Equal("analysis.threads.all", root.GetProperty("path").GetString());
        Assert.Equal("invalid_cursor", root.GetProperty("error").GetProperty("code").GetString());

        Assert.True(root.TryGetProperty("extra", out var extra));
        Assert.Equal(JsonValueKind.Object, extra.ValueKind);

        Assert.True(extra.TryGetProperty("cursorContract", out var contract));
        Assert.Equal(JsonValueKind.Array, contract.ValueKind);
        Assert.NotEmpty(contract.EnumerateArray());

        var recovery = extra.GetProperty("recovery");
        Assert.Equal(JsonValueKind.Object, recovery.ValueKind);
        var retry = recovery.GetProperty("retryWithoutCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(retry));
        Assert.Contains("report_get(path=\"analysis.threads.all\"", retry, StringComparison.Ordinal);
    }
}

