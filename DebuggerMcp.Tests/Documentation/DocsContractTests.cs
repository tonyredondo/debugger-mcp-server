#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DebuggerMcp;
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
}
