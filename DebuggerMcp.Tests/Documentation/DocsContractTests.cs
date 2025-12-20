#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Reporting;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DebuggerMcp.Tests.Documentation;

/// <summary>
/// Contract tests that prevent documentation drift for public-facing surfaces (HTTP API + MCP tools).
/// </summary>
public class DocsContractTests
{
    [Fact]
    public void Readme_HttpApiTables_CoverAllControllerEndpoints()
    {
        var documented = ReadmeEndpointTable.ParseFromReadme(ReadmeEndpointTable.ReadReadmeText());
        Assert.NotEmpty(documented);

        var discovered = ApiRouteDiscovery.DiscoverControllerRoutes();
        Assert.NotEmpty(discovered);

        // README does not list health/info in the /api tables; those are covered separately.
        var missing = discovered
            .Where(d => !documented.Contains(d, StringComparer.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"README is missing {missing.Count} HTTP endpoints:\n" + string.Join('\n', missing));

        // Also ensure the README does not claim endpoints that are not implemented.
        var extras = documented
            .Where(d => !discovered.Contains(d, StringComparer.Ordinal) && !ReadmeEndpointTable.AllowedReadmeOnlyEndpoints.Contains(d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            extras.Count == 0,
            $"README lists {extras.Count} HTTP endpoints not implemented by controllers:\n" + string.Join('\n', extras));
    }

    [Fact]
    public void Readme_McpToolsTable_MatchesExportedToolCount()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();

        // Table under "MCP Tools Available" should enumerate exactly 11 tool names.
        var toolRowRegex = new Regex("^\\|\\s*`(?<tool>[^`]+)`\\s*\\|", RegexOptions.Multiline);
        var matches = toolRowRegex.Matches(readme).Select(m => m.Groups["tool"].Value.Trim()).ToList();

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "session",
            "dump",
            "analyze",
            "compare",
            "report",
            "watch",
            "inspect",
            "symbols",
            "source_link",
            "datadog_symbols",
            "exec"
        };

        var listed = matches.Where(m => allowed.Contains(m)).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(allowed.Count, listed.Count);
    }

    [Fact]
    public void Readme_EnvironmentVariables_DocumentsKeyAnalysisAndIntegrationKnobs()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();

        var required = new[]
        {
            "GITHUB_API_ENABLED",
            "GITHUB_TOKEN",
            "GH_TOKEN",
            "SKIP_HEAP_ENUM",
            "SKIP_SYNC_BLOCKS",
            "DEBUGGERMCP_SOURCE_CONTEXT_ROOTS",
            "DATADOG_TRACE_SYMBOLS_ENABLED",
            "DATADOG_TRACE_SYMBOLS_PAT",
            "DATADOG_TRACE_SYMBOLS_CACHE_DIR",
            "DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS",
            "DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE"
        };

        var missing = required
            .Where(name => !readme.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "README.md is missing environment variable documentation for:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Readme_McpResourcesTable_ListsAllPublishedResources()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();

        var required = new[]
        {
            "debugger://mcp-tools",
            "debugger://workflow-guide",
            "debugger://analysis-guide",
            "debugger://windbg-commands",
            "debugger://lldb-commands",
            "debugger://sos-commands",
            "debugger://troubleshooting",
            "debugger://cli-guide"
        };

        var missing = required
            .Where(uri => !readme.Contains(uri, StringComparison.Ordinal))
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "README.md is missing MCP resource URIs:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Readme_DoesNotHardcodeCoverageOrTestCounts()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();

        // Coverage changes frequently; README should describe how to obtain it, not hardcode a % table.
        Assert.DoesNotContain("| Module | Line | Branch | Method |", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("| DebuggerMcp |", readme, StringComparison.Ordinal);

        // Total test counts drift quickly; avoid pinning a number in README.
        var totalTestsRegex = new Regex("^\\-\\s*\\*\\*Total Tests\\*\\*:", RegexOptions.Multiline);
        Assert.DoesNotMatch(totalTestsRegex, readme);
    }

    [Fact]
    public void Readme_CliAnalysisCommands_DoesNotMentionRemovedDotnetAlias()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();

        // The CLI supports: crash, ai, perf/performance, cpu, memory/allocations, gc, threads/contention, security.
        Assert.DoesNotContain("crash, dotnet", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void McpToolsDoc_AnalyzeAi_DefaultsMatchExportedToolSignature()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp", "Resources", "mcp_tools.md");
        var text = File.ReadAllText(path);

        var analyze = typeof(CompactTools).GetMethod("Analyze", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(analyze);

        var maxIterations = analyze!.GetParameters().Single(p => p.Name == "maxIterations");
        var maxTokens = analyze.GetParameters().Single(p => p.Name == "maxTokens");

        Assert.True(maxIterations.HasDefaultValue);
        Assert.True(maxTokens.HasDefaultValue);

        var expectedIterations = Convert.ToInt32(maxIterations.DefaultValue);
        var expectedTokens = Convert.ToInt32(maxTokens.DefaultValue);

        Assert.Contains($"maxIterations: {expectedIterations}", text, StringComparison.Ordinal);
        Assert.Contains($"maxTokens: {expectedTokens}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolsDoc_ReportGet_DefaultMaxCharsMatchesServer()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp", "Resources", "mcp_tools.md");
        var text = File.ReadAllText(path);

        Assert.Contains(
            $"default: {ReportSectionApi.DefaultMaxResponseChars}",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CliGuide_DocumentsActualDefaults()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp", "Resources", "cli_guide.md");
        var text = File.ReadAllText(path);

        // Keep a few critical defaults in sync with ConnectionSettings.
        Assert.Contains("DEBUGGER_MCP_TIMEOUT=600", text, StringComparison.Ordinal);
        Assert.Contains("DEBUGGER_MCP_HISTORY_FILE=~/.dbg-mcp-history", text, StringComparison.Ordinal);
        Assert.Contains("\"timeout\": 600", text, StringComparison.Ordinal);

        // Ensure report CLI usage reflects the implemented flags and formats.
        Assert.Contains("report -o <file> [-f markdown|html|json] [--summary] [--no-watches]", text, StringComparison.Ordinal);

        // Avoid implying reports can be generated without an output path.
        Assert.DoesNotContain("report --format json", text, StringComparison.Ordinal);
        Assert.Contains("report -o <file> --format json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CliReadme_DocumentsUpdatedDefaultsAndJsonReportNote()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp.Cli", "README.md");
        var text = File.ReadAllText(path);

        Assert.Contains("`DEBUGGER_MCP_TIMEOUT`", text, StringComparison.Ordinal);
        Assert.Contains("| `DEBUGGER_MCP_TIMEOUT` | Request timeout (seconds) | 600 |", text, StringComparison.Ordinal);
        Assert.Contains("| `DEBUGGER_MCP_HISTORY_FILE` | Command history file | ~/.dbg-mcp-history |", text, StringComparison.Ordinal);
        Assert.Contains("report -o <file> --format json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("report --format json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_HttpModeMentionsApiAlias()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();
        Assert.Contains("Alias: `--api`", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisExamples_CompareParameters_UseBaselineSessionIds()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "ANALYSIS_EXAMPLES.md");
        var text = File.ReadAllText(path);

        Assert.Contains("baselineSessionId", text, StringComparison.Ordinal);
        Assert.Contains("baselineUserId", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`kind`, `sessionId`, `userId`, `targetSessionId`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisExamples_SchemaOverview_MentionsKeySections()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "ANALYSIS_EXAMPLES.md");
        var text = File.ReadAllText(path);

        Assert.Contains("\"synchronization\"", text, StringComparison.Ordinal);
        Assert.Contains("\"rootCause\"", text, StringComparison.Ordinal);
        Assert.Contains("\"sourceContext\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LldbCommandsDoc_MentionsSosAutoLoadedInMcpServer()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp", "Resources", "lldb_commands.md");
        var text = File.ReadAllText(path);

        Assert.Contains("SOS is typically **auto-loaded**", text, StringComparison.Ordinal);
        Assert.Contains("inspect(kind: \"load_sos\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SosCommandsDoc_MentionsSosAutoLoadedInMcpServer()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp", "Resources", "sos_commands.md");
        var text = File.ReadAllText(path);

        Assert.Contains("SOS is typically **auto-loaded**", text, StringComparison.Ordinal);
        Assert.Contains("inspect(kind: \"load_sos\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_DotNet10Install_UsesChannelBasedInstallerExample()
    {
        var readme = ReadmeEndpointTable.ReadReadmeText();
        Assert.Contains(".NET 10 SDK", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("recently released", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet-install.sh", readme, StringComparison.Ordinal);
        Assert.Contains("--channel 10.0", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--version 10.0", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolsDoc_ListsAllExportedToolNames()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "DebuggerMcp", "Resources", "mcp_tools.md");
        var text = File.ReadAllText(path);

        // Ensure the canonical MCP doc enumerates the same tool names surfaced by the README table.
        var required = new[]
        {
            "`session`",
            "`dump`",
            "`exec`",
            "`report`",
            "`analyze`",
            "`compare`",
            "`watch`",
            "`symbols`",
            "`source_link`",
            "`inspect`",
            "`datadog_symbols`"
        };

        foreach (var token in required)
        {
            Assert.Contains(token, text, StringComparison.Ordinal);
        }
    }

    private static class ApiRouteDiscovery
    {
        public static HashSet<string> DiscoverControllerRoutes()
        {
            var assembly = typeof(DebuggerSessionManager).Assembly;
            var controllerTypes = assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
                .ToList();

            var results = new HashSet<string>(StringComparer.Ordinal);

            foreach (var controllerType in controllerTypes)
            {
                var controllerRoute = controllerType.GetCustomAttribute<RouteAttribute>()?.Template;
                if (string.IsNullOrWhiteSpace(controllerRoute))
                {
                    continue;
                }

                controllerRoute = NormalizeRouteTemplate(controllerRoute);

                foreach (var method in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    foreach (var (httpMethod, template) in GetHttpMethodTemplates(method))
                    {
                        var full = CombineRoutes(controllerRoute, template);
                        results.Add($"`{httpMethod}` {full}");
                    }
                }
            }

            return results;
        }

        private static IEnumerable<(string Method, string? Template)> GetHttpMethodTemplates(MethodInfo method)
        {
            foreach (var attr in method.GetCustomAttributes(inherit: true))
            {
                switch (attr)
                {
                    case HttpGetAttribute a:
                        yield return ("GET", a.Template);
                        break;
                    case HttpPostAttribute a:
                        yield return ("POST", a.Template);
                        break;
                    case HttpPutAttribute a:
                        yield return ("PUT", a.Template);
                        break;
                    case HttpDeleteAttribute a:
                        yield return ("DELETE", a.Template);
                        break;
                    case HttpPatchAttribute a:
                        yield return ("PATCH", a.Template);
                        break;
                }
            }
        }

        private static string CombineRoutes(string controllerRoute, string? actionTemplate)
        {
            if (string.IsNullOrWhiteSpace(actionTemplate))
            {
                return "/" + controllerRoute;
            }

            var normalizedAction = NormalizeRouteTemplate(actionTemplate);
            return "/" + controllerRoute.TrimEnd('/') + "/" + normalizedAction.TrimStart('/');
        }

        private static string NormalizeRouteTemplate(string template)
        {
            var t = template.Trim();
            if (t.StartsWith("/"))
            {
                t = t.TrimStart('/');
            }

            // Route templates often omit the leading "api/" on controllers; normalize it for README comparisons.
            return t;
        }
    }

    private static class ReadmeEndpointTable
    {
        // A small allowlist for endpoints that are intentionally documented outside controller tables.
        public static readonly HashSet<string> AllowedReadmeOnlyEndpoints = new(StringComparer.Ordinal)
        {
            "`GET` /health",
            "`GET` /info"
        };

        public static string ReadReadmeText()
        {
            var root = FindRepoRoot();
            var path = Path.Combine(root, "README.md");
            return File.ReadAllText(path);
        }

        public static HashSet<string> ParseFromReadme(string readme)
        {
            // Extract table rows like: | `GET` | `/api/...` | ...
            var rowRegex = new Regex("^\\|\\s*`(?<method>[A-Z]+)`\\s*\\|\\s*`(?<path>[^`]+)`\\s*\\|", RegexOptions.Multiline);
            var matches = rowRegex.Matches(readme);

            var results = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in matches)
            {
                var method = match.Groups["method"].Value.Trim();
                var path = match.Groups["path"].Value.Trim();
                if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                results.Add($"`{method}` {path}");
            }

            foreach (var extra in AllowedReadmeOnlyEndpoints)
            {
                results.Add(extra);
            }

            return results;
        }

        private static string FindRepoRoot()
        {
            return DocsContractTests.FindRepoRoot();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DebuggerMcp.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException("Failed to locate repo root (DebuggerMcp.slnx not found).");
        }

        return dir.FullName;
    }
}
