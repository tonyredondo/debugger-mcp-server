using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Generates ASCII-based charts for text-based reports (Markdown, terminal).
/// </summary>
public static class AsciiCharts
{
    private const char FullBlock = '█';
    private const char LightBlock = '░';
    private const int DefaultBarWidth = 30;

    /// <summary>
    /// Generates a horizontal bar chart.
    /// </summary>
    /// <param name="data">Dictionary of labels to values.</param>
    /// <param name="title">Optional title for the chart.</param>
    /// <param name="barWidth">Width of the bar area in characters.</param>
    /// <param name="showPercentage">Whether to show percentage after each bar.</param>
    /// <param name="showValue">Whether to show the raw value.</param>
    /// <param name="valueFormatter">Optional formatter for values (e.g., bytes to MB).</param>
    /// <returns>ASCII representation of the bar chart.</returns>
    public static string HorizontalBarChart(
        Dictionary<string, long> data,
        string? title = null,
        int barWidth = DefaultBarWidth,
        bool showPercentage = true,
        bool showValue = true,
        Func<long, string>? valueFormatter = null)
    {
        if (data == null || data.Count == 0)
        {
            return "No data available";
        }

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
        }

        var maxValue = data.Values.Max();
        var total = data.Values.Sum();
        var maxLabelLength = data.Keys.Max(k => k.Length);

        foreach (var (label, value) in data.OrderByDescending(kv => kv.Value))
        {
            var paddedLabel = label.PadRight(maxLabelLength);
            var ratio = maxValue > 0 ? (double)value / maxValue : 0;
            var percentage = total > 0 ? (double)value / total * 100 : 0;
            var filledWidth = (int)(ratio * barWidth);
            var emptyWidth = barWidth - filledWidth;

            var bar = new string(FullBlock, filledWidth) + new string(LightBlock, emptyWidth);

            var suffix = "";
            if (showPercentage)
            {
                suffix += $" {percentage,5:F1}%";
            }
            if (showValue)
            {
                var formattedValue = valueFormatter?.Invoke(value) ?? value.ToString("N0");
                suffix += $" ({formattedValue})";
            }

            sb.AppendLine($"{paddedLabel} {bar}{suffix}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates a simple pie chart representation using percentages.
    /// </summary>
    /// <param name="data">Dictionary of labels to values.</param>
    /// <param name="title">Optional title for the chart.</param>
    /// <returns>ASCII representation showing distribution.</returns>
    public static string PieChartText(
        Dictionary<string, long> data,
        string? title = null)
    {
        if (data == null || data.Count == 0)
        {
            return "No data available";
        }

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
        }

        var total = data.Values.Sum();
        var maxLabelLength = data.Keys.Max(k => k.Length);

        foreach (var (label, value) in data.OrderByDescending(kv => kv.Value))
        {
            var paddedLabel = label.PadRight(maxLabelLength);
            var percentage = total > 0 ? (double)value / total * 100 : 0;
            var blocks = (int)(percentage / 5); // Each block = 5%
            var visual = new string('●', blocks) + new string('○', 20 - blocks);

            sb.AppendLine($"{paddedLabel} {visual} {percentage,5:F1}%");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates a memory usage summary with visual representation.
    /// </summary>
    /// <param name="gen0">Generation 0 size in bytes.</param>
    /// <param name="gen1">Generation 1 size in bytes.</param>
    /// <param name="gen2">Generation 2 size in bytes.</param>
    /// <param name="loh">Large Object Heap size in bytes.</param>
    /// <param name="poh">Pinned Object Heap size in bytes (optional).</param>
    /// <returns>ASCII representation of heap generations.</returns>
    public static string HeapGenerationsChart(long gen0, long gen1, long gen2, long loh, long poh = 0)
    {
        var data = new Dictionary<string, long>
        {
            ["Gen 0"] = gen0,
            ["Gen 1"] = gen1,
            ["Gen 2"] = gen2,
            ["LOH  "] = loh
        };

        if (poh > 0)
        {
            data["POH  "] = poh;
        }

        return HorizontalBarChart(
            data,
            "Heap Generation Sizes",
            barWidth: 25,
            showPercentage: true,
            showValue: true,
            valueFormatter: FormatBytes);
    }

    /// <summary>
    /// Generates a thread state distribution chart.
    /// </summary>
    /// <param name="threadStates">Dictionary of state names to counts.</param>
    /// <returns>ASCII representation of thread states.</returns>
    public static string ThreadStateChart(Dictionary<string, int> threadStates)
    {
        var data = threadStates.ToDictionary(kv => kv.Key, kv => (long)kv.Value);

        return HorizontalBarChart(
            data,
            "Thread States",
            barWidth: 20,
            showPercentage: true,
            showValue: true);
    }

    /// <summary>
    /// Generates a simple table in ASCII format.
    /// </summary>
    /// <param name="headers">Column headers.</param>
    /// <param name="rows">Data rows.</param>
    /// <param name="title">Optional title.</param>
    /// <returns>ASCII table.</returns>
    public static string Table(string[] headers, List<string[]> rows, string? title = null)
    {
        if (headers == null || headers.Length == 0)
        {
            return "No data available";
        }

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine(title);
            sb.AppendLine();
        }

        // Calculate column widths
        var colWidths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            colWidths[i] = headers[i].Length;
        }
        foreach (var row in rows)
        {
            for (int i = 0; i < Math.Min(row.Length, colWidths.Length); i++)
            {
                colWidths[i] = Math.Max(colWidths[i], (row[i] ?? "").Length);
            }
        }

        // Build header
        var headerLine = "| " + string.Join(" | ", headers.Select((h, i) => h.PadRight(colWidths[i]))) + " |";
        var separatorLine = "|" + string.Join("|", colWidths.Select(w => new string('-', w + 2))) + "|";

        sb.AppendLine(headerLine);
        sb.AppendLine(separatorLine);

        // Build rows
        foreach (var row in rows)
        {
            var cells = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                cells[i] = (i < row.Length ? row[i] ?? "" : "").PadRight(colWidths[i]);
            }
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates a progress bar.
    /// </summary>
    /// <param name="value">Current value.</param>
    /// <param name="maxValue">Maximum value.</param>
    /// <param name="width">Width of the bar.</param>
    /// <param name="label">Optional label.</param>
    /// <returns>ASCII progress bar.</returns>
    public static string ProgressBar(long value, long maxValue, int width = 30, string? label = null)
    {
        var ratio = maxValue > 0 ? (double)value / maxValue : 0;
        var percentage = ratio * 100;
        var filledWidth = (int)(ratio * width);
        var emptyWidth = width - filledWidth;

        var bar = $"[{new string(FullBlock, filledWidth)}{new string(LightBlock, emptyWidth)}] {percentage:F1}%";

        if (!string.IsNullOrEmpty(label))
        {
            bar = $"{label}: {bar}";
        }

        return bar;
    }

    /// <summary>
    /// Generates a sparkline-style mini chart.
    /// </summary>
    /// <param name="values">Series of values.</param>
    /// <returns>Sparkline string.</returns>
    public static string Sparkline(IEnumerable<double> values)
    {
        var sparkChars = new[] { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
        var list = values.ToList();

        if (list.Count == 0)
        {
            return "";
        }

        var min = list.Min();
        var max = list.Max();
        var range = max - min;

        if (range == 0)
        {
            return new string(sparkChars[4], list.Count);
        }

        return new string(list.Select(v =>
        {
            var normalized = (v - min) / range;
            var index = (int)(normalized * (sparkChars.Length - 1));
            return sparkChars[Math.Clamp(index, 0, sparkChars.Length - 1)];
        }).ToArray());
    }

    /// <summary>
    /// Formats bytes into human-readable format.
    /// </summary>
    /// <param name="bytes">Number of bytes.</param>
    /// <returns>Formatted string (e.g., "1.5 GB").</returns>
    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double value = bytes;

        while (value >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            value /= 1024;
        }

        return $"{value:F1} {suffixes[order]}";
    }

    /// <summary>
    /// Generates a severity indicator.
    /// </summary>
    /// <param name="level">Severity level (0-4, where 4 is most severe).</param>
    /// <returns>Visual indicator.</returns>
    public static string SeverityIndicator(int level)
    {
        return level switch
        {
            0 => "✓ OK",
            1 => "ℹ️ Info",
            2 => "⚠️ Warning",
            3 => "🔶 High",
            4 => "🔴 Critical",
            _ => "❓ Unknown"
        };
    }

    /// <summary>
    /// Generates a warning/alert box.
    /// </summary>
    /// <param name="message">Alert message.</param>
    /// <param name="level">Severity level.</param>
    /// <returns>Formatted alert box.</returns>
    public static string AlertBox(string message, int level = 2)
    {
        var indicator = level switch
        {
            0 => "✓",
            1 => "ℹ",
            2 => "⚠",
            3 => "⚡",
            4 => "🔴",
            _ => "●"
        };

        var border = new string('─', message.Length + 4);
        return $"┌{border}┐\n│ {indicator} {message} │\n└{border}┘";
    }
}

