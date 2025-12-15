using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

public class DateAgeFormattingTests
{
    [Fact]
    public void FormatUtcDateTimeWithAge_WhenLessThanOneDay_ShowsLessThanOneDayAgo()
    {
        var nowUtc = new DateTime(2025, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var timestamp = nowUtc.AddHours(-3);

        var formatted = DebuggerMcp.Cli.Program.FormatUtcDateTimeWithAge(timestamp, nowUtc, "yyyy-MM-dd HH:mm:ss");

        Assert.Contains("(<1 day ago)", formatted);
    }

    [Fact]
    public void FormatUtcDateTimeWithAge_WhenOneDay_ShowsOneDayAgo()
    {
        var nowUtc = new DateTime(2025, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var timestamp = nowUtc.AddDays(-1).AddMinutes(-1);

        var formatted = DebuggerMcp.Cli.Program.FormatUtcDateTimeWithAge(timestamp, nowUtc, "yyyy-MM-dd HH:mm:ss");

        Assert.Contains("(1 day ago)", formatted);
    }

    [Fact]
    public void FormatUtcDateTimeWithAge_WhenMultipleDays_ShowsDaysAgo()
    {
        var nowUtc = new DateTime(2025, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var timestamp = nowUtc.AddDays(-2).AddHours(-6);

        var formatted = DebuggerMcp.Cli.Program.FormatUtcDateTimeWithAge(timestamp, nowUtc, "yyyy-MM-dd HH:mm:ss");

        Assert.Contains("(2 days ago)", formatted);
    }
}

