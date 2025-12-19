#nullable enable

using System.Text;
using Markdig;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;
using SpectreMarkup = Spectre.Console.Markup;

namespace DebuggerMcp.Cli.Display;

internal sealed class LlmResponseRenderer
{
    internal sealed record Options
    {
        public int? ConsoleWidth { get; init; }
        public int MaxMarkdownChars { get; init; } = 200_000;
        public int MaxCodeBlockChars { get; init; } = 50_000;
        public int MaxListItems { get; init; } = 200;
        public int MaxTableRows { get; init; } = 40;
        public int MaxTableColumns { get; init; } = 6;
        public int MaxTableCellWidth { get; init; } = 40;
    }

    private readonly MarkdownPipeline _pipeline;
    private readonly Options _options;

    internal LlmResponseRenderer(Options? options = null)
    {
        _options = options ?? new Options();
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .Build();
    }

    internal IReadOnlyList<IRenderable> Render(string? text, int? consoleWidth = null)
    {
        text ??= string.Empty;
        var width = consoleWidth ?? _options.ConsoleWidth ?? 120;
        width = Math.Clamp(width, 40, 400);

        var result = new List<IRenderable>();

        if (text.Length == 0)
        {
            result.Add(new Markup(string.Empty));
            return result;
        }

        if (text.Length > _options.MaxMarkdownChars)
        {
            result.Add(new Markup(AnsiToSpectreMarkup.Convert(text)));
            return result;
        }

        MarkdownDocument? doc = null;
        try
        {
            doc = Markdown.Parse(text, _pipeline);
        }
        catch
        {
            result.Add(new Markup(AnsiToSpectreMarkup.Convert(text)));
            return result;
        }

        foreach (var block in doc)
        {
            RenderBlock(block, result, width, listItemBudget: _options.MaxListItems);
        }

        if (result.Count == 0)
        {
            result.Add(new Markup(AnsiToSpectreMarkup.Convert(text)));
        }

        return result;
    }

    private void RenderBlock(Block block, List<IRenderable> output, int consoleWidth, int listItemBudget, int indent = 0)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(heading, output);
                return;

            case ParagraphBlock paragraph:
                RenderParagraph(paragraph, output, indent);
                return;

            case ListBlock list:
                RenderList(list, output, consoleWidth, listItemBudget, indent);
                return;

            case FencedCodeBlock fenced:
                RenderCodeBlock(fenced, output);
                return;

            case CodeBlock code:
                RenderCodeBlock(code, output);
                return;

            case QuoteBlock quote:
                RenderQuote(quote, output, consoleWidth, listItemBudget, indent);
                return;

            case ThematicBreakBlock:
                output.Add(new Rule());
                return;

            case MdTable table:
                RenderTable(table, output, consoleWidth);
                return;

            default:
            {
                if (block is ContainerBlock container)
                {
                    foreach (var child in container)
                    {
                        RenderBlock(child, output, consoleWidth, listItemBudget, indent);
                    }

                    return;
                }

                var fallback = block.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    output.Add(new Markup(IndentLines(AnsiToSpectreMarkup.Convert(fallback.TrimEnd()), indent)));
                }
                return;
            }
        }
    }

    private static void RenderHeading(HeadingBlock heading, List<IRenderable> output)
    {
        var title = heading.Inline == null
            ? string.Empty
            : RenderInlineToMarkup(heading.Inline);

        title = title.Trim();
        if (title.Length == 0)
        {
            return;
        }

        if (heading.Level <= 2)
        {
            output.Add(new Rule($"[bold cyan]{title}[/]")
            {
                Justification = Justify.Left,
                Style = Style.Parse("cyan")
            });
            return;
        }

        output.Add(new Markup($"[bold]{title}[/]"));
    }

    private static void RenderParagraph(ParagraphBlock paragraph, List<IRenderable> output, int indent)
    {
        if (paragraph.Inline == null)
        {
            return;
        }

        var markup = RenderInlineToMarkup(paragraph.Inline).TrimEnd();
        if (markup.Length == 0)
        {
            return;
        }

        output.Add(new Markup(IndentLines(markup, indent)));
    }

    private void RenderList(ListBlock list, List<IRenderable> output, int consoleWidth, int listItemBudget, int indent)
    {
        var ordered = list.IsOrdered;
        var index = ordered ? ParseOrderedStart(list) : 0;
        var remaining = listItemBudget;

        foreach (var item in list)
        {
            if (remaining-- <= 0)
            {
                output.Add(new Markup($"[dim]{new string(' ', indent * 2)}... (list truncated)[/]"));
                break;
            }

            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var prefix = ordered ? $"{index}." : "-";
            var baseIndent = new string(' ', indent * 2);
            var renderedFirst = false;

            foreach (var child in listItem)
            {
                if (!renderedFirst && child is ParagraphBlock p && p.Inline != null)
                {
                    var itemText = RenderInlineToMarkup(p.Inline).TrimEnd();
                    output.Add(new Markup($"{baseIndent}{prefix} {itemText}"));
                    renderedFirst = true;
                    continue;
                }

                RenderBlock(child, output, consoleWidth, listItemBudget: remaining, indent: indent + 1);
            }

            if (!renderedFirst)
            {
                output.Add(new Markup($"{baseIndent}{prefix}"));
            }

            if (ordered)
            {
                index++;
            }
        }
    }

    private static int ParseOrderedStart(ListBlock list)
    {
        try
        {
            // Markdig has used different types for OrderedStart across versions.
            // Use reflection so we remain compatible with the resolved Markdig package.
            var prop = list.GetType().GetProperty("OrderedStart");
            var value = prop?.GetValue(list);
            return value switch
            {
                int i => i,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 1
            };
        }
        catch
        {
            return 1;
        }
    }

    private void RenderQuote(QuoteBlock quote, List<IRenderable> output, int consoleWidth, int listItemBudget, int indent)
    {
        var inner = new List<IRenderable>();
        foreach (var child in quote)
        {
            RenderBlock(child, inner, consoleWidth, listItemBudget, indent: 0);
        }

        IRenderable content;
        if (inner.Count > 0)
        {
            content = new Rows(inner);
        }
        else
        {
            var fallback = quote.ToString() ?? string.Empty;
            fallback = fallback.Trim();
            if (fallback.Length == 0)
            {
                return;
            }
            content = new Markup(AnsiToSpectreMarkup.Convert(fallback));
        }

        output.Add(new Panel(content)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0)
        });
    }

    private void RenderCodeBlock(CodeBlock codeBlock, List<IRenderable> output)
    {
        var sb = new StringBuilder();
        foreach (var line in codeBlock.Lines.Lines)
        {
            sb.Append(line.ToString());
        }

        var code = sb.ToString();
        if (code.Length > _options.MaxCodeBlockChars)
        {
            code = code[.._options.MaxCodeBlockChars] + "\n... (truncated)\n";
        }

        var title = codeBlock is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info)
            ? fenced.Info.Trim()
            : "code";
        title = SpectreMarkup.Escape(title);

        var panel = new Panel(new Markup(AnsiToSpectreMarkup.Convert(code.TrimEnd())))
        {
            Header = new PanelHeader(title, Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0)
        };

        output.Add(panel);
    }

    private void RenderTable(MdTable table, List<IRenderable> output, int consoleWidth)
    {
        var (rows, columns) = ExtractTable(table);
        if (rows.Count == 0 || columns == 0)
        {
            output.Add(new Markup(AnsiToSpectreMarkup.Convert(table.ToString() ?? string.Empty)));
            return;
        }

        var budget = Math.Max(40, consoleWidth - 4);

        // MaxTableRows refers to body rows; always keep the header row if present.
        var cappedRows = new List<List<string>>(capacity: Math.Min(rows.Count, 1 + _options.MaxTableRows));
        cappedRows.Add(rows[0]);
        var bodyRowsAvailable = Math.Max(0, rows.Count - 1);
        var bodyRowsToTake = Math.Min(bodyRowsAvailable, _options.MaxTableRows);
        cappedRows.AddRange(rows.Skip(1).Take(bodyRowsToTake));
        var remainingBodyRows = bodyRowsAvailable - bodyRowsToTake;

        var estimated = EstimateTableWidth(cappedRows, columns, maxCellWidth: _options.MaxTableCellWidth);
        var canUseSpectreTable = columns <= _options.MaxTableColumns && estimated <= budget;

        if (!canUseSpectreTable)
        {
            // Fallback: simple panel with escaped markdown-ish table text.
            output.Add(new Panel(new Markup(AnsiToSpectreMarkup.Convert(RenderTableAsPlainText(cappedRows, columns))))
            {
                Header = new PanelHeader("table", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Grey),
                Padding = new Padding(1, 0)
            });

            if (remainingBodyRows > 0)
            {
                output.Add(new Markup($"[dim]... ({remainingBodyRows} more rows)[/]"));
            }

            return;
        }

        var spectre = new Spectre.Console.Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey);

        var header = cappedRows[0];
        for (var c = 0; c < columns; c++)
        {
            var headerText = c < header.Count ? header[c] : string.Empty;
            // Avoid wrapping in extra markup tags; headerText may already contain markup spans.
            spectre.AddColumn(new TableColumn(new Markup(headerText)));
        }

        foreach (var row in cappedRows.Skip(1))
        {
            var cells = new IRenderable[columns];
            for (var c = 0; c < columns; c++)
            {
                var cellText = c < row.Count ? row[c] : string.Empty;
                cells[c] = new Markup(cellText);
            }

            spectre.AddRow(cells);
        }

        output.Add(spectre);

        if (remainingBodyRows > 0)
        {
            output.Add(new Markup($"[dim]... ({remainingBodyRows} more rows)[/]"));
        }
    }

    private static (List<List<string>> rows, int columns) ExtractTable(MdTable table)
    {
        var header = new List<string>();
        var body = new List<List<string>>();
        var columns = 0;

        foreach (var child in table)
        {
            if (child is not MdTableRow row)
            {
                continue;
            }

            var cells = new List<string>();
            foreach (var cell in row)
            {
                if (cell is not MdTableCell tc)
                {
                    continue;
                }

                var cellText = new StringBuilder();
                foreach (var cellBlock in tc)
                {
                    if (cellBlock is ParagraphBlock p && p.Inline != null)
                    {
                        if (cellText.Length > 0)
                        {
                            cellText.Append('\n');
                        }
                        cellText.Append(RenderInlineToMarkup(p.Inline));
                    }
                    else
                    {
                        var fallback = cellBlock.ToString();
                        if (!string.IsNullOrWhiteSpace(fallback))
                        {
                            if (cellText.Length > 0)
                            {
                                cellText.Append('\n');
                            }
                            cellText.Append(AnsiToSpectreMarkup.Convert(fallback.TrimEnd()));
                        }
                    }
                }

                cells.Add(cellText.ToString().TrimEnd());
            }

            columns = Math.Max(columns, cells.Count);
            if (row.IsHeader)
            {
                if (header.Count == 0)
                {
                    header = cells;
                }
            }
            else
            {
                body.Add(cells);
            }
        }

        // Ensure there is at least a header row, even if Markdig didn't mark it.
        if (header.Count == 0 && body.Count > 0)
        {
            header = body[0];
            body.RemoveAt(0);
        }

        if (header.Count == 0)
        {
            return ([], 0);
        }

        var rows = new List<List<string>>(capacity: 1 + body.Count) { header };
        rows.AddRange(body);
        return (rows, columns);
    }

    private static int EstimateTableWidth(List<List<string>> rows, int columns, int maxCellWidth)
    {
        var maxLens = new int[columns];
        foreach (var row in rows)
        {
            for (var c = 0; c < columns; c++)
            {
                var cell = c < row.Count ? row[c] : string.Empty;
                var visible = VisibleLength(cell);
                maxLens[c] = Math.Max(maxLens[c], Math.Min(visible, maxCellWidth));
            }
        }

        var separators = Math.Max(0, columns - 1) * 3;
        return maxLens.Sum() + separators;
    }

    private static int VisibleLength(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var withoutAnsi = AnsiToSpectreMarkup.StripAnsi(text);
        var withoutMarkup = StripSpectreMarkup(withoutAnsi);
        var firstLine = withoutMarkup.Split('\n').FirstOrDefault() ?? string.Empty;
        return firstLine.Length;
    }

    private static string RenderTableAsPlainText(List<List<string>> rows, int columns)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < columns; c++)
            {
                if (c > 0)
                {
                    sb.Append(" | ");
                }
                sb.Append(StripSpectreMarkup(AnsiToSpectreMarkup.StripAnsi(c < row.Count ? row[c] : string.Empty)).Replace('\n', ' '));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderInlineToMarkup(ContainerInline container)
    {
        var sb = new StringBuilder();
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            AppendInline(sb, inline);
        }
        return sb.ToString();
    }

    private static void AppendInline(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(AnsiToSpectreMarkup.Convert(lit.Content.ToString()));
                return;

            case LineBreakInline br:
                sb.Append(br.IsHard ? "\n" : " ");
                return;

            case EmphasisInline em:
            {
                var inner = new StringBuilder();
                for (var child = em.FirstChild; child != null; child = child.NextSibling)
                {
                    AppendInline(inner, child);
                }

                var style = em.DelimiterCount >= 2 ? "bold" : "italic";
                sb.Append('[').Append(style).Append(']');
                sb.Append(inner);
                sb.Append("[/]");
                return;
            }

            case CodeInline code:
                sb.Append("[grey]");
                sb.Append(SpectreMarkup.Escape(code.Content));
                sb.Append("[/]");
                return;

            case LinkInline link:
            {
                var label = new StringBuilder();
                for (var child = link.FirstChild; child != null; child = child.NextSibling)
                {
                    AppendInline(label, child);
                }
                var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
                url = url.Trim();

                if (link.IsImage)
                {
                    // Avoid literal '['/']' here; we're building Spectre markup, not Markdown.
                    sb.Append("[dim]image[/] ");
                    sb.Append(label);
                    if (url.Length > 0)
                    {
                        sb.Append(" (");
                        sb.Append(SpectreMarkup.Escape(url));
                        sb.Append(')');
                    }
                    return;
                }

                if (label.Length == 0)
                {
                    sb.Append(SpectreMarkup.Escape(url));
                    return;
                }

                sb.Append(label);
                if (url.Length > 0)
                {
                    sb.Append(" (");
                    sb.Append(SpectreMarkup.Escape(url));
                    sb.Append(')');
                }
                return;
            }

            default:
            {
                if (inline is ContainerInline ci)
                {
                    for (var child = ci.FirstChild; child != null; child = child.NextSibling)
                    {
                        AppendInline(sb, child);
                    }
                    return;
                }

                var fallback = inline.ToString();
                if (!string.IsNullOrEmpty(fallback))
                {
                    sb.Append(AnsiToSpectreMarkup.Convert(fallback));
                }
                return;
            }
        }
    }

    private static string IndentLines(string text, int indent)
    {
        if (indent <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var prefix = new string(' ', indent * 2);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = prefix + lines[i];
        }
        return string.Join('\n', lines);
    }

    private static string StripSpectreMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Mirrors ConsoleOutput.StripSpectreMarkup, but local to avoid visibility issues.
        const string open = "\u0001";
        const string close = "\u0002";
        text = text.Replace("[[", open, StringComparison.Ordinal).Replace("]]", close, StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\[[^\\[\\]]+\\]", string.Empty);
        return text
            .Replace(open, "[", StringComparison.Ordinal)
            .Replace(close, "]", StringComparison.Ordinal)
            .TrimEnd();
    }
}

internal static class AnsiToSpectreMarkup
{
    internal static string Convert(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 32);
        var state = new AnsiStyleState();
        var segment = new StringBuilder();
        string? currentTag = null;

        void Flush()
        {
            if (segment.Length == 0)
            {
                return;
            }

            var escaped = SpectreMarkup.Escape(segment.ToString());
            if (currentTag == null)
            {
                sb.Append(escaped);
            }
            else
            {
                sb.Append('[').Append(currentTag).Append(']');
                sb.Append(escaped);
                sb.Append("[/]");
            }

            segment.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\u001B')
            {
                if (TryParseSgr(text, i, out var sgrEnd, out var codes))
                {
                    Flush();
                    state.Apply(codes);
                    currentTag = state.ToSpectreTag();
                    i = sgrEnd;
                    continue;
                }

                if (TrySkipAnsiSequence(text, i, out var skipEnd))
                {
                    i = skipEnd;
                    continue;
                }

                continue;
            }

            segment.Append(ch);
        }

        Flush();
        return sb.ToString();
    }

    internal static string StripAnsi(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\u001B')
            {
                if (TryParseSgr(text, i, out var sgrEnd, out _))
                {
                    i = sgrEnd;
                    continue;
                }

                if (TrySkipAnsiSequence(text, i, out var skipEnd))
                {
                    i = skipEnd;
                    continue;
                }

                continue;
            }

            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool TrySkipAnsiSequence(string text, int start, out int endIndex)
    {
        endIndex = start;
        if (start + 1 >= text.Length)
        {
            return false;
        }

        var next = text[start + 1];

        // CSI: ESC [ ... <final>
        if (next == '[')
        {
            for (var i = start + 2; i < text.Length; i++)
            {
                var c = text[i];
                if (c is >= '@' and <= '~')
                {
                    endIndex = i;
                    return true;
                }
            }

            endIndex = text.Length - 1;
            return true;
        }

        // OSC: ESC ] ... BEL or ESC \
        if (next == ']')
        {
            for (var i = start + 2; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\a')
                {
                    endIndex = i;
                    return true;
                }

                if (c == '\u001B' && i + 1 < text.Length && text[i + 1] == '\\')
                {
                    endIndex = i + 1;
                    return true;
                }
            }

            endIndex = text.Length - 1;
            return true;
        }

        return false;
    }

    private static bool TryParseSgr(string text, int start, out int endIndex, out List<int> codes)
    {
        // Parse ESC [ ... m
        endIndex = start;
        codes = [];

        if (start + 1 >= text.Length || text[start] != '\u001B')
        {
            return false;
        }

        if (text[start + 1] != '[')
        {
            return false;
        }

        var i = start + 2;
        var num = 0;
        var hasNum = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == 'm')
            {
                if (hasNum)
                {
                    codes.Add(num);
                }
                else if (codes.Count == 0)
                {
                    codes.Add(0);
                }

                endIndex = i;
                return true;
            }

            if (c == ';')
            {
                if (hasNum)
                {
                    codes.Add(num);
                    num = 0;
                    hasNum = false;
                }
                else
                {
                    codes.Add(0);
                }

                i++;
                continue;
            }

            if (c is >= '0' and <= '9')
            {
                hasNum = true;
                num = (num * 10) + (c - '0');
                i++;
                continue;
            }

            // Not a supported SGR sequence
            return false;
        }

        return false;
    }

    private sealed class AnsiStyleState
    {
        private string? _foreground;
        private string? _background;
        private bool _bold;
        private bool _italic;
        private bool _underline;

        public void Apply(List<int> codes)
        {
            if (codes.Count == 0)
            {
                Reset();
                return;
            }

            foreach (var code in codes)
            {
                switch (code)
                {
                    case 0:
                        Reset();
                        break;
                    case 1:
                        _bold = true;
                        break;
                    case 3:
                        _italic = true;
                        break;
                    case 4:
                        _underline = true;
                        break;
                    case 22:
                        _bold = false;
                        break;
                    case 23:
                        _italic = false;
                        break;
                    case 24:
                        _underline = false;
                        break;
                    case 39:
                        _foreground = null;
                        break;
                    case 49:
                        _background = null;
                        break;
                    case >= 30 and <= 37:
                        _foreground = MapAnsiColor(code - 30, bright: false);
                        break;
                    case >= 90 and <= 97:
                        _foreground = MapAnsiColor(code - 90, bright: true);
                        break;
                    case >= 40 and <= 47:
                        _background = MapAnsiColor(code - 40, bright: false);
                        break;
                    case >= 100 and <= 107:
                        _background = MapAnsiColor(code - 100, bright: true);
                        break;
                }
            }
        }

        public string? ToSpectreTag()
        {
            if (_foreground == null && _background == null && !_bold && !_italic && !_underline)
            {
                return null;
            }

            var sb = new StringBuilder();
            if (_bold)
            {
                sb.Append("bold");
            }
            if (_italic)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("italic");
            }
            if (_underline)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("underline");
            }
            if (_foreground != null)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(_foreground);
            }
            if (_background != null)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("on ").Append(_background);
            }
            return sb.ToString();
        }

        private void Reset()
        {
            _foreground = null;
            _background = null;
            _bold = false;
            _italic = false;
            _underline = false;
        }

        private static string MapAnsiColor(int idx, bool bright)
        {
            // Keep to Spectre's common color names.
            // "bright black" is better represented as grey.
            return (idx, bright) switch
            {
                (0, false) => "black",
                (1, false) => "red",
                (2, false) => "green",
                (3, false) => "yellow",
                (4, false) => "blue",
                (5, false) => "magenta",
                (6, false) => "cyan",
                (7, false) => "white",
                (0, true) => "grey",
                (1, true) => "red",
                (2, true) => "green",
                (3, true) => "yellow",
                (4, true) => "blue",
                (5, true) => "magenta",
                (6, true) => "cyan",
                (7, true) => "white",
                _ => "white"
            };
        }
    }
}
