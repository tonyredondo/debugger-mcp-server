using DebuggerMcp.Reporting;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Tests for AsciiCharts utility class.
/// </summary>
public class AsciiChartsTests
{
    // ============================================================
    // HorizontalBarChart Tests
    // ============================================================

    [Fact]
    public void HorizontalBarChart_WithNullData_ReturnsNoDataMessage()
    {
        // Act
        var result = AsciiCharts.HorizontalBarChart(null!);

        // Assert
        Assert.Equal("No data available", result);
    }

    [Fact]
    public void HorizontalBarChart_WithEmptyData_ReturnsNoDataMessage()
    {
        // Arrange
        var data = new Dictionary<string, long>();

        // Act
        var result = AsciiCharts.HorizontalBarChart(data);

        // Assert
        Assert.Equal("No data available", result);
    }

    [Fact]
    public void HorizontalBarChart_WithSingleEntry_ReturnsChart()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Test"] = 100 };

        // Act
        var result = AsciiCharts.HorizontalBarChart(data);

        // Assert
        Assert.Contains("Test", result);
        Assert.Contains("█", result); // Full block
        Assert.Matches(@"100[,.]0%", result); // 100% of total (locale-independent)
    }

    [Fact]
    public void HorizontalBarChart_WithTitle_IncludesTitle()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Item"] = 50 };

        // Act
        var result = AsciiCharts.HorizontalBarChart(data, title: "My Chart");

        // Assert
        Assert.Contains("My Chart", result);
        Assert.Contains("---", result); // Title underline
    }

    [Fact]
    public void HorizontalBarChart_WithMultipleEntries_SortsByValueDescending()
    {
        // Arrange
        var data = new Dictionary<string, long>
        {
            ["Small"] = 10,
            ["Large"] = 100,
            ["Medium"] = 50
        };

        // Act
        var result = AsciiCharts.HorizontalBarChart(data);

        // Assert
        var largeIndex = result.IndexOf("Large");
        var mediumIndex = result.IndexOf("Medium");
        var smallIndex = result.IndexOf("Small");

        Assert.True(largeIndex < mediumIndex);
        Assert.True(mediumIndex < smallIndex);
    }

    [Fact]
    public void HorizontalBarChart_WithShowPercentageFalse_HidesPercentage()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Test"] = 100 };

        // Act
        var result = AsciiCharts.HorizontalBarChart(data, showPercentage: false);

        // Assert
        Assert.DoesNotContain("%", result);
    }

    [Fact]
    public void HorizontalBarChart_WithShowValueFalse_HidesValue()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Test"] = 12345 };

        // Act
        var result = AsciiCharts.HorizontalBarChart(data, showValue: false);

        // Assert
        Assert.DoesNotContain("12,345", result);
        Assert.DoesNotContain("(", result);
    }

    [Fact]
    public void HorizontalBarChart_WithCustomFormatter_UsesFormatter()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Test"] = 1024 };
        Func<long, string> formatter = v => $"{v} bytes";

        // Act
        var result = AsciiCharts.HorizontalBarChart(data, valueFormatter: formatter);

        // Assert
        Assert.Contains("1024 bytes", result);
    }

    [Fact]
    public void HorizontalBarChart_WithCustomBarWidth_AdjustsBarSize()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Test"] = 100 };

        // Act
        var result10 = AsciiCharts.HorizontalBarChart(data, barWidth: 10);
        var result50 = AsciiCharts.HorizontalBarChart(data, barWidth: 50);

        // Assert
        Assert.True(result50.Length > result10.Length);
    }

    [Fact]
    public void HorizontalBarChart_WithZeroValues_HandlesGracefully()
    {
        // Arrange
        var data = new Dictionary<string, long>
        {
            ["Zero"] = 0,
            ["NonZero"] = 100
        };

        // Act
        var result = AsciiCharts.HorizontalBarChart(data);

        // Assert
        Assert.Contains("Zero", result);
        Assert.Matches(@"0[,.]0%", result);
    }

    // ============================================================
    // PieChartText Tests
    // ============================================================

    [Fact]
    public void PieChartText_WithNullData_ReturnsNoDataMessage()
    {
        // Act
        var result = AsciiCharts.PieChartText(null!);

        // Assert
        Assert.Equal("No data available", result);
    }

    [Fact]
    public void PieChartText_WithEmptyData_ReturnsNoDataMessage()
    {
        // Arrange
        var data = new Dictionary<string, long>();

        // Act
        var result = AsciiCharts.PieChartText(data);

        // Assert
        Assert.Equal("No data available", result);
    }

    [Fact]
    public void PieChartText_WithData_ShowsPercentages()
    {
        // Arrange
        var data = new Dictionary<string, long>
        {
            ["A"] = 50,
            ["B"] = 50
        };

        // Act
        var result = AsciiCharts.PieChartText(data);

        // Assert
        Assert.Matches(@"50[,.]0%", result); // Locale-independent
        Assert.Contains("●", result); // Filled circles
        Assert.Contains("○", result); // Empty circles
    }

    [Fact]
    public void PieChartText_WithTitle_IncludesTitle()
    {
        // Arrange
        var data = new Dictionary<string, long> { ["Item"] = 100 };

        // Act
        var result = AsciiCharts.PieChartText(data, title: "Distribution");

        // Assert
        Assert.Contains("Distribution", result);
    }

    // ============================================================
    // HeapGenerationsChart Tests
    // ============================================================

    [Fact]
    public void HeapGenerationsChart_ReturnsFormattedChart()
    {
        // Act
        var result = AsciiCharts.HeapGenerationsChart(
            gen0: 1024 * 1024,
            gen1: 2048 * 1024,
            gen2: 4096 * 1024,
            loh: 8192 * 1024);

        // Assert
        Assert.Contains("Gen 0", result);
        Assert.Contains("Gen 1", result);
        Assert.Contains("Gen 2", result);
        Assert.Contains("LOH", result);
        Assert.Contains("Heap Generation Sizes", result);
        Assert.Contains("MB", result); // Formatted bytes
    }

    [Fact]
    public void HeapGenerationsChart_WithPoh_IncludesPoh()
    {
        // Act
        var result = AsciiCharts.HeapGenerationsChart(
            gen0: 1024,
            gen1: 2048,
            gen2: 4096,
            loh: 8192,
            poh: 16384);

        // Assert
        Assert.Contains("POH", result);
    }

    [Fact]
    public void HeapGenerationsChart_WithZeroPoh_ExcludesPoh()
    {
        // Act
        var result = AsciiCharts.HeapGenerationsChart(
            gen0: 1024,
            gen1: 2048,
            gen2: 4096,
            loh: 8192,
            poh: 0);

        // Assert
        Assert.DoesNotContain("POH", result);
    }

    // ============================================================
    // ThreadStateChart Tests
    // ============================================================

    [Fact]
    public void ThreadStateChart_ReturnsFormattedChart()
    {
        // Arrange
        var states = new Dictionary<string, int>
        {
            ["Running"] = 5,
            ["Waiting"] = 10,
            ["Blocked"] = 2
        };

        // Act
        var result = AsciiCharts.ThreadStateChart(states);

        // Assert
        Assert.Contains("Thread States", result);
        Assert.Contains("Running", result);
        Assert.Contains("Waiting", result);
        Assert.Contains("Blocked", result);
    }

    // ============================================================
    // Table Tests
    // ============================================================

    [Fact]
    public void Table_WithNullHeaders_ReturnsNoDataMessage()
    {
        // Act
        var result = AsciiCharts.Table(null!, new List<string[]>());

        // Assert
        Assert.Equal("No data available", result);
    }

    [Fact]
    public void Table_WithEmptyHeaders_ReturnsNoDataMessage()
    {
        // Act
        var result = AsciiCharts.Table(Array.Empty<string>(), new List<string[]>());

        // Assert
        Assert.Equal("No data available", result);
    }

    [Fact]
    public void Table_WithHeadersOnly_ReturnsHeaderRow()
    {
        // Arrange
        var headers = new[] { "Name", "Value" };
        var rows = new List<string[]>();

        // Act
        var result = AsciiCharts.Table(headers, rows);

        // Assert
        Assert.Contains("Name", result);
        Assert.Contains("Value", result);
        Assert.Contains("|", result);
        Assert.Contains("-", result);
    }

    [Fact]
    public void Table_WithData_ReturnsFormattedTable()
    {
        // Arrange
        var headers = new[] { "Name", "Age", "City" };
        var rows = new List<string[]>
        {
            new[] { "Alice", "30", "NYC" },
            new[] { "Bob", "25", "LA" }
        };

        // Act
        var result = AsciiCharts.Table(headers, rows);

        // Assert
        Assert.Contains("Alice", result);
        Assert.Contains("Bob", result);
        Assert.Contains("30", result);
        Assert.Contains("NYC", result);
    }

    [Fact]
    public void Table_WithTitle_IncludesTitle()
    {
        // Arrange
        var headers = new[] { "Col1" };
        var rows = new List<string[]> { new[] { "Data" } };

        // Act
        var result = AsciiCharts.Table(headers, rows, title: "My Table");

        // Assert
        Assert.Contains("My Table", result);
    }

    [Fact]
    public void Table_WithUnevenRows_HandlesGracefully()
    {
        // Arrange
        var headers = new[] { "A", "B", "C" };
        var rows = new List<string[]>
        {
            new[] { "1" }, // Short row
            new[] { "1", "2", "3", "4" } // Long row
        };

        // Act
        var result = AsciiCharts.Table(headers, rows);

        // Assert - Should not throw
        Assert.Contains("A", result);
    }

    [Fact]
    public void Table_WithNullCells_HandlesGracefully()
    {
        // Arrange
        var headers = new[] { "A", "B" };
        var rows = new List<string[]>
        {
            new[] { "Value", null! }
        };

        // Act
        var result = AsciiCharts.Table(headers, rows);

        // Assert - Should not throw
        Assert.Contains("Value", result);
    }

    // ============================================================
    // ProgressBar Tests
    // ============================================================

    [Fact]
    public void ProgressBar_At0Percent_ShowsEmptyBar()
    {
        // Act
        var result = AsciiCharts.ProgressBar(0, 100);

        // Assert
        Assert.Matches(@"0[,.]0%", result);
        Assert.Contains("░", result); // Light block
    }

    [Fact]
    public void ProgressBar_At100Percent_ShowsFullBar()
    {
        // Act
        var result = AsciiCharts.ProgressBar(100, 100);

        // Assert
        Assert.Matches(@"100[,.]0%", result);
        Assert.Contains("█", result); // Full block
    }

    [Fact]
    public void ProgressBar_At50Percent_ShowsHalfBar()
    {
        // Act
        var result = AsciiCharts.ProgressBar(50, 100);

        // Assert
        Assert.Matches(@"50[,.]0%", result);
    }

    [Fact]
    public void ProgressBar_WithLabel_IncludesLabel()
    {
        // Act
        var result = AsciiCharts.ProgressBar(50, 100, label: "Progress");

        // Assert
        Assert.StartsWith("Progress:", result);
    }

    [Fact]
    public void ProgressBar_WithCustomWidth_AdjustsSize()
    {
        // Act
        var result10 = AsciiCharts.ProgressBar(50, 100, width: 10);
        var result50 = AsciiCharts.ProgressBar(50, 100, width: 50);

        // Assert
        Assert.True(result50.Length > result10.Length);
    }

    [Fact]
    public void ProgressBar_WithZeroMax_HandlesGracefully()
    {
        // Act
        var result = AsciiCharts.ProgressBar(50, 0);

        // Assert
        Assert.Matches(@"0[,.]0%", result);
    }

    // ============================================================
    // Sparkline Tests
    // ============================================================

    [Fact]
    public void Sparkline_WithEmptyValues_ReturnsEmptyString()
    {
        // Act
        var result = AsciiCharts.Sparkline(Array.Empty<double>());

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void Sparkline_WithSingleValue_ReturnsMiddleChar()
    {
        // Act
        var result = AsciiCharts.Sparkline(new[] { 5.0 });

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public void Sparkline_WithConstantValues_ReturnsUniformLine()
    {
        // Act
        var result = AsciiCharts.Sparkline(new[] { 5.0, 5.0, 5.0, 5.0 });

        // Assert
        Assert.Equal(4, result.Length);
        Assert.True(result.All(c => c == result[0])); // All same char
    }

    [Fact]
    public void Sparkline_WithIncreasingValues_ShowsTrend()
    {
        // Act
        var result = AsciiCharts.Sparkline(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 });

        // Assert
        Assert.Equal(5, result.Length);
        // First char should be lower than last char
        Assert.True(result[0] < result[4]);
    }

    [Fact]
    public void Sparkline_WithDecreasingValues_ShowsTrend()
    {
        // Act
        var result = AsciiCharts.Sparkline(new[] { 5.0, 4.0, 3.0, 2.0, 1.0 });

        // Assert
        Assert.Equal(5, result.Length);
        // First char should be higher than last char
        Assert.True(result[0] > result[4]);
    }

    // ============================================================
    // FormatBytes Tests
    // ============================================================

    [Theory]
    [InlineData(0, "B")]
    [InlineData(100, "B")]
    [InlineData(1024, "KB")]
    [InlineData(1536, "KB")]
    [InlineData(1048576, "MB")]
    [InlineData(1073741824, "GB")]
    [InlineData(1099511627776, "TB")]
    public void FormatBytes_ReturnsCorrectFormat(long bytes, string expectedSuffix)
    {
        // Act
        var result = AsciiCharts.FormatBytes(bytes);

        // Assert (locale-independent - check suffix and pattern)
        Assert.EndsWith(expectedSuffix, result);
        Assert.Matches(@"^\d+[,.]?\d* \w+$", result);
    }

    // ============================================================
    // SeverityIndicator Tests
    // ============================================================

    [Theory]
    [InlineData(0, "✓ OK")]
    [InlineData(1, "ℹ️ Info")]
    [InlineData(2, "⚠️ Warning")]
    [InlineData(3, "🔶 High")]
    [InlineData(4, "🔴 Critical")]
    [InlineData(5, "❓ Unknown")]
    [InlineData(-1, "❓ Unknown")]
    public void SeverityIndicator_ReturnsCorrectIndicator(int level, string expected)
    {
        // Act
        var result = AsciiCharts.SeverityIndicator(level);

        // Assert
        Assert.Equal(expected, result);
    }

    // ============================================================
    // AlertBox Tests
    // ============================================================

    [Fact]
    public void AlertBox_CreatesBoxWithMessage()
    {
        // Act
        var result = AsciiCharts.AlertBox("Test message");

        // Assert
        Assert.Contains("Test message", result);
        Assert.Contains("┌", result);
        Assert.Contains("┐", result);
        Assert.Contains("└", result);
        Assert.Contains("┘", result);
        Assert.Contains("│", result);
    }

    [Theory]
    [InlineData(0, "✓")]
    [InlineData(1, "ℹ")]
    [InlineData(2, "⚠")]
    [InlineData(3, "⚡")]
    [InlineData(4, "🔴")]
    [InlineData(99, "●")]
    public void AlertBox_UsesCorrectIndicatorForLevel(int level, string expectedIndicator)
    {
        // Act
        var result = AsciiCharts.AlertBox("Message", level);

        // Assert
        Assert.Contains(expectedIndicator, result);
    }

    [Fact]
    public void AlertBox_DefaultLevel_IsWarning()
    {
        // Act
        var result = AsciiCharts.AlertBox("Warning message");

        // Assert
        Assert.Contains("⚠", result);
    }

    [Fact]
    public void AlertBox_BorderWidthMatchesMessage()
    {
        // Arrange
        var message = "Short";

        // Act
        var result = AsciiCharts.AlertBox(message);
        var lines = result.Split('\n');

        // Assert - border should be same length
        Assert.Equal(lines[0].Length, lines[2].Length);
    }
}

