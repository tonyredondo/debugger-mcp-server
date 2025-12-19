#nullable enable

using System.ComponentModel;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// Compact MCP tool surface (intentionally small) that dispatches to underlying implementation
/// methods while keeping the exported tool count under common client limits.
/// </summary>
/// <remarks>
/// This type is the only MCP tool type that should be exported by the server. All other tool
/// classes in this namespace are implementation details and must not be annotated with MCP
/// tool attributes.
/// </remarks>
[McpServerToolType]
public sealed class CompactTools
{
    private readonly SessionTools _sessionTools;
    private readonly DumpTools _dumpTools;
    private readonly AnalysisTools _analysisTools;
    private readonly AiAnalysisTools _aiAnalysisTools;
    private readonly PerformanceTools _performanceTools;
    private readonly SecurityTools _securityTools;
    private readonly ComparisonTools _comparisonTools;
    private readonly WatchTools _watchTools;
    private readonly ReportTools _reportTools;
    private readonly SourceLinkTools _sourceLinkTools;
    private readonly SymbolTools _symbolTools;
    private readonly ObjectInspectionTools _objectInspectionTools;
    private readonly DatadogSymbolsTools _datadogSymbolsTools;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompactTools"/> class.
    /// </summary>
    public CompactTools(
        DebuggerSessionManager sessionManager,
        SymbolManager symbolManager,
        WatchStore watchStore,
        ILoggerFactory loggerFactory)
    {
        _sessionTools = new SessionTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<SessionTools>());
        _dumpTools = new DumpTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<DumpTools>());
        _analysisTools = new AnalysisTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<AnalysisTools>());
        _aiAnalysisTools = new AiAnalysisTools(sessionManager, symbolManager, watchStore, loggerFactory, loggerFactory.CreateLogger<AiAnalysisTools>());
        _performanceTools = new PerformanceTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<PerformanceTools>());
        _securityTools = new SecurityTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<SecurityTools>());
        _comparisonTools = new ComparisonTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<ComparisonTools>());
        _watchTools = new WatchTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<WatchTools>());
        _reportTools = new ReportTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<ReportTools>());
        _sourceLinkTools = new SourceLinkTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<SourceLinkTools>());
        _symbolTools = new SymbolTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<SymbolTools>());
        _objectInspectionTools = new ObjectInspectionTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<ObjectInspectionTools>());
        _datadogSymbolsTools = new DatadogSymbolsTools(sessionManager, symbolManager, watchStore, loggerFactory.CreateLogger<DatadogSymbolsTools>());
    }

    private static string Normalize(string value) => value.Trim().Replace('-', '_').ToLowerInvariant();

    private static string NormalizeRequired(string? value, string name)
        => Normalize(Require(value, name));

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required", name);
        }

        return value;
    }

    /// <summary>
    /// Manages sessions (create/list/close/restore/debugger_info).
    /// </summary>
    [McpServerTool(Name = "session")]
    [Description("Manage sessions: create | list | close | restore | debugger_info")]
    public string Session(
        [Description("Action: create | list | close | restore | debugger_info")] string action,
        [Description("User ID (session owner)")] string userId,
        [Description("Session ID (required for close/restore/debugger_info)")] string? sessionId = null)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "create" => _sessionTools.CreateSession(userId),
            "list" => _sessionTools.ListSessions(userId),
            "close" => _sessionTools.CloseSession(Require(sessionId, nameof(sessionId)), userId),
            "restore" => _sessionTools.RestoreSession(Require(sessionId, nameof(sessionId)), userId),
            "debugger_info" => _sessionTools.GetDebuggerInfo(Require(sessionId, nameof(sessionId)), userId),
            _ => throw new ArgumentException($"Unknown session action '{action}'", nameof(action)),
        };

    /// <summary>
    /// Opens and closes dumps (dump files) in a session.
    /// </summary>
    [McpServerTool(Name = "dump")]
    [Description("Manage dumps: open | close")]
    public Task<string> Dump(
        [Description("Action: open | close")] string action,
        [Description("Session ID")] string sessionId,
        [Description("User ID (session owner)")] string userId,
        [Description("Dump ID (required for open)")] string? dumpId = null)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "open" => _dumpTools.OpenDump(sessionId, userId, Require(dumpId, nameof(dumpId))),
            "close" => Task.FromResult(_dumpTools.CloseDump(sessionId, userId)),
            _ => throw new ArgumentException($"Unknown dump action '{action}'", nameof(action)),
        };

    /// <summary>
    /// Executes a raw debugger command. Use as a last resort.
    /// </summary>
    [McpServerTool(Name = "exec")]
    [Description("Execute a raw debugger command (last resort).")]
    public string Exec(
        [Description("Session ID")] string sessionId,
        [Description("User ID (session owner)")] string userId,
        [Description("Debugger command to execute")] string command)
        => _dumpTools.ExecuteCommand(sessionId, userId, command);

    /// <summary>
    /// Generates reports (full/summary) in json/markdown/html.
    /// </summary>
    [McpServerTool(Name = "report")]
    [Description("Generate reports: full | summary (formats: json | markdown | html). Returns report content.")]
    public Task<string> Report(
        [Description("Action: full | summary")] string action,
        [Description("Session ID")] string sessionId,
        [Description("User ID (session owner)")] string userId,
        [Description("Format: json | markdown | html (default: json)")] string format = "json",
        [Description("Include watches (full only, default: true)")] bool includeWatches = true,
        [Description("Include security (full only, default: true)")] bool includeSecurity = true,
        [Description("Maximum stack frames (full only, 0 = all)")] int maxStackFrames = 0)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "full" => _reportTools.GenerateReport(sessionId, userId, format, includeWatches, includeSecurity, maxStackFrames),
            "summary" => _reportTools.GenerateSummaryReport(sessionId, userId, format),
            _ => throw new ArgumentException($"Unknown report action '{action}'", nameof(action)),
        };

    /// <summary>
    /// Runs analysis and diagnostics on the currently open dump.
    /// </summary>
    [McpServerTool(Name = "analyze")]
    [Description("Analyze a dump: crash | ai | performance | cpu | allocations | gc | contention | security. For security capabilities: kind=security, action=capabilities.")]
    public Task<string> Analyze(
        McpServer server,
        [Description("Kind: crash | ai | performance | cpu | allocations | gc | contention | security")] string kind,
        [Description("Session ID (required for all kinds except security capabilities)")] string? sessionId = null,
        [Description("User ID (required for all kinds except security capabilities)")] string? userId = null,
        [Description("Optional action (security only): capabilities")] string? action = null,
        [Description("Include watches (default: true)")] bool includeWatches = true,
        [Description("Include security analysis (AI only, default: true)")] bool includeSecurity = true,
        [Description("Maximum AI iterations (AI only, default: 100)")] int maxIterations = 100,
        [Description("Maximum output tokens (AI only, default: 4096)")] int maxTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        return NormalizeRequired(kind, nameof(kind)) switch
        {
            "crash" => _analysisTools.AnalyzeCrash(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), includeWatches),
            // Back-compat: `dotnet_crash` was renamed to `crash` (the server now assumes .NET dumps).
            "dotnet_crash" => _analysisTools.AnalyzeCrash(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), includeWatches),
            "ai" => _aiAnalysisTools.AnalyzeCrashWithAiAsync(
                server,
                Require(sessionId, nameof(sessionId)),
                Require(userId, nameof(userId)),
                maxIterations: maxIterations,
                maxTokens: maxTokens,
                includeWatches: includeWatches,
                includeSecurity: includeSecurity,
                cancellationToken: cancellationToken),
            "performance" => _performanceTools.AnalyzePerformance(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), includeWatches),
            "cpu" or "cpu_usage" => _performanceTools.AnalyzeCpuUsage(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            "allocations" => _performanceTools.AnalyzeAllocations(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            "gc" => _performanceTools.AnalyzeGc(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            "contention" => _performanceTools.AnalyzeContention(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            "security" => (!string.IsNullOrWhiteSpace(action) && Normalize(action) == "capabilities")
                ? Task.FromResult(_securityTools.GetSecurityCheckCapabilities())
                : _securityTools.AnalyzeSecurity(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            _ => throw new ArgumentException($"Unknown analyze kind '{kind}'", nameof(kind)),
        };
    }

    /// <summary>
    /// Compares two sessions/dumps.
    /// </summary>
    [McpServerTool(Name = "compare")]
    [Description("Compare: dumps | heaps | threads | modules")]
    public Task<string> Compare(
        [Description("Kind: dumps | heaps | threads | modules")] string kind,
        [Description("Baseline session ID")] string baselineSessionId,
        [Description("Baseline user ID")] string baselineUserId,
        [Description("Target session ID")] string targetSessionId,
        [Description("Target user ID")] string targetUserId)
        => NormalizeRequired(kind, nameof(kind)) switch
        {
            "dumps" => _comparisonTools.CompareDumps(baselineSessionId, baselineUserId, targetSessionId, targetUserId),
            "heaps" => _comparisonTools.CompareHeaps(baselineSessionId, baselineUserId, targetSessionId, targetUserId),
            "threads" => _comparisonTools.CompareThreads(baselineSessionId, baselineUserId, targetSessionId, targetUserId),
            "modules" => _comparisonTools.CompareModules(baselineSessionId, baselineUserId, targetSessionId, targetUserId),
            _ => throw new ArgumentException($"Unknown compare kind '{kind}'", nameof(kind)),
        };

    /// <summary>
    /// Manages watch expressions.
    /// </summary>
    [McpServerTool(Name = "watch")]
    [Description("Manage watches: add | remove | list | clear | evaluate | evaluate_all")]
    public Task<string> Watch(
        [Description("Action: add | remove | list | clear | evaluate | evaluate_all")] string action,
        [Description("Session ID")] string sessionId,
        [Description("User ID (session owner)")] string userId,
        [Description("Watch ID (required for remove/evaluate)")] string? watchId = null,
        [Description("Watch expression (required for add)")] string? expression = null,
        [Description("Optional description (add)")] string? description = null)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "add" => _watchTools.AddWatch(sessionId, userId, Require(expression, nameof(expression)), description),
            "remove" => _watchTools.RemoveWatch(sessionId, userId, Require(watchId, nameof(watchId))),
            "list" => _watchTools.ListWatches(sessionId, userId),
            "clear" => _watchTools.ClearWatches(sessionId, userId),
            "evaluate" => _watchTools.EvaluateWatch(sessionId, userId, Require(watchId, nameof(watchId))),
            "evaluate_all" => _watchTools.EvaluateWatches(sessionId, userId),
            _ => throw new ArgumentException($"Unknown watch action '{action}'", nameof(action)),
        };

    /// <summary>
    /// Configures and manages symbols.
    /// </summary>
    [McpServerTool(Name = "symbols")]
    [Description("Manage symbols: configure_additional | get_servers | clear_cache | reload | verify_core_modules")]
    public string Symbols(
        [Description("Action: configure_additional | get_servers | clear_cache | reload | verify_core_modules")] string action,
        [Description("Session ID (required for configure_additional/reload/verify_core_modules)")] string? sessionId = null,
        [Description("User ID (required for configure_additional/clear_cache/reload/verify_core_modules)")] string? userId = null,
        [Description("Dump ID (clear_cache)")] string? dumpId = null,
        [Description("Additional symbol paths (configure_additional)")] string? additionalPaths = null,
        [Description("Module names (verify_core_modules)")] string? moduleNames = null)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "configure_additional" => _symbolTools.ConfigureAdditionalSymbols(
                Require(sessionId, nameof(sessionId)),
                Require(userId, nameof(userId)),
                Require(additionalPaths, nameof(additionalPaths))),
            "get_servers" => _symbolTools.GetSymbolServers(),
            "clear_cache" => _symbolTools.ClearSymbolCache(Require(userId, nameof(userId)), Require(dumpId, nameof(dumpId))),
            "reload" => _symbolTools.ReloadSymbols(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            "verify_core_modules" => _sessionTools.LoadVerifyCoreModules(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), moduleNames),
            _ => throw new ArgumentException($"Unknown symbols action '{action}'", nameof(action)),
        };

    /// <summary>
    /// Resolves Source Link URLs and reports Source Link configuration.
    /// </summary>
    [McpServerTool(Name = "source_link")]
    [Description("Resolve Source Link: resolve | info")]
    public string SourceLink(
        [Description("Action: resolve | info")] string action,
        [Description("Session ID (required)")] string? sessionId = null,
        [Description("User ID (required)")] string? userId = null,
        [Description("Source file path (resolve)")] string? sourceFile = null,
        [Description("Line number (resolve, optional)")] int? lineNumber = null)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "resolve" => _sourceLinkTools.ResolveSourceLink(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), Require(sourceFile, nameof(sourceFile)), lineNumber),
            "info" => _sourceLinkTools.GetSourceLinkInfo(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId))),
            _ => throw new ArgumentException($"Unknown source_link action '{action}'", nameof(action)),
        };

    /// <summary>
    /// Inspects managed objects, modules and managed call stacks (ClrMD/SOS helpers).
    /// </summary>
    [McpServerTool(Name = "inspect")]
    [Description("Inspect: object | module | modules | clr_stack | lookup_type | lookup_method | load_sos")]
    public string Inspect(
        [Description("Kind: object | module | modules | clr_stack | lookup_type | lookup_method | load_sos")] string kind,
        [Description("Session ID")] string sessionId,
        [Description("User ID")] string userId,
        [Description("Address (object)")] string? address = null,
        [Description("Method table (object, optional)")] string? methodTable = null,
        [Description("Max depth (object)")] int maxDepth = 5,
        [Description("Max array elements (object)")] int maxArrayElements = 10,
        [Description("Max string length (object)")] int maxStringLength = 1024,
        [Description("Type name (lookup_type/lookup_method)")] string? typeName = null,
        [Description("Module name filter (lookup_type)")] string? moduleName = "*",
        [Description("Include all modules in lookup results")] bool includeAllModules = false,
        [Description("Method name (lookup_method)")] string? methodName = null,
        [Description("Include method arguments (clr_stack)")] bool includeArguments = true,
        [Description("Include locals (clr_stack)")] bool includeLocals = true,
        [Description("Include registers (clr_stack)")] bool includeRegisters = true,
        [Description("Filter to OS thread ID (0 = all)")] uint threadId = 0)
        => NormalizeRequired(kind, nameof(kind)) switch
        {
            "object" => _objectInspectionTools.InspectObject(sessionId, userId, Require(address, nameof(address)), methodTable, maxDepth, maxArrayElements, maxStringLength),
            "module" => _objectInspectionTools.DumpModule(sessionId, userId, Require(address, nameof(address))),
            "modules" => _objectInspectionTools.ListModules(sessionId, userId),
            "clr_stack" => _objectInspectionTools.ClrStack(sessionId, userId, includeArguments, includeLocals, includeRegisters, threadId),
            "lookup_type" => _objectInspectionTools.Name2EE(sessionId, userId, Require(typeName, nameof(typeName)), moduleName, includeAllModules),
            "lookup_method" => _objectInspectionTools.Name2EEMethod(sessionId, userId, Require(typeName, nameof(typeName)), Require(methodName, nameof(methodName))),
            "load_sos" => _dumpTools.LoadSos(sessionId, userId),
            _ => throw new ArgumentException($"Unknown inspect kind '{kind}'", nameof(kind)),
        };

    /// <summary>
    /// Downloads and manages Datadog symbols.
    /// </summary>
    [McpServerTool(Name = "datadog_symbols")]
    [Description("Datadog symbols: prepare | download | list_artifacts | get_config | clear")]
    public Task<string> DatadogSymbols(
        [Description("Action: prepare | download | list_artifacts | get_config | clear")] string action,
        [Description("Session ID (required for prepare/download/clear)")] string? sessionId = null,
        [Description("User ID (required for prepare/download/clear)")] string? userId = null,
        [Description("Commit SHA (download/list_artifacts)")] string? commitSha = null,
        [Description("Target framework (download)")] string? targetFramework = null,
        [Description("Load into debugger (download/prepare)")] bool loadIntoDebugger = true,
        [Description("Force version fallback (download/prepare)")] bool forceVersion = false,
        [Description("Optional version (download)")] string? version = null,
        [Description("Clear API cache (clear, default: false)")] bool clearApiCache = false,
        [Description("Optional Azure Pipelines build ID (download)")] int? buildId = null)
        => NormalizeRequired(action, nameof(action)) switch
        {
            "prepare" => _datadogSymbolsTools.PrepareDatadogSymbols(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), loadIntoDebugger, forceVersion),
            "download" => _datadogSymbolsTools.DownloadDatadogSymbols(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), Require(commitSha, nameof(commitSha)), targetFramework, loadIntoDebugger, forceVersion, version, buildId),
            "list_artifacts" => _datadogSymbolsTools.ListDatadogArtifacts(Require(commitSha, nameof(commitSha))),
            "get_config" => Task.FromResult(_datadogSymbolsTools.GetDatadogSymbolsConfig()),
            "clear" => Task.FromResult(_datadogSymbolsTools.ClearDatadogSymbols(Require(sessionId, nameof(sessionId)), Require(userId, nameof(userId)), clearApiCache)),
            _ => throw new ArgumentException($"Unknown datadog_symbols action '{action}'", nameof(action)),
        };
}
