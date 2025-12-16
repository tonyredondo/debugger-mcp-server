using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

[Collection("SourceContextEnricher")]
public class JsonDerivedReportRenderingTests
{
    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public void GenerateMarkdownReport_WhenFaultingFrameHasSourceContext_RendersInlineSnippet()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\n", Encoding.UTF8, "text/plain")
        });

        var originalFactory = SourceContextEnricher.HttpClientFactory;
        var originalRoots = SourceContextEnricher.LocalSourceRoots;
        try
        {
            SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);
            SourceContextEnricher.LocalSourceRoots = Array.Empty<string>();

            var analysis = new CrashAnalysisResult
            {
                Summary = new AnalysisSummary { Description = "Found 1 threads (1 total frames, 1 in faulting thread), 0 modules." },
                Threads = new ThreadsInfo
                {
                    All =
                    [
                        new ThreadInfo
                        {
                            ThreadId = "t1",
                            IsFaulting = true,
                            CallStack =
                            [
                                new StackFrame
                                {
                                    FrameNumber = 0,
                                    InstructionPointer = "0x1",
                                    Module = "MyApp.Core",
                                    Function = "MyApp.Core.Foo.Bar()",
                                    IsManaged = true,
                                    SourceFile = "file.cs",
                                    LineNumber = 3,
                                    SourceRawUrl = "https://raw.githubusercontent.com/org/repo/sha/file.cs",
                                    SourceUrl = "https://github.com/org/repo/blob/sha/file.cs#L3"
                                }
                            ]
                        }
                    ]
                },
                Modules = []
            };

            CrashAnalysisResultFinalizer.Finalize(analysis);

            var service = new ReportService();
            var markdown = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Markdown },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            Assert.Contains("## At a glance", markdown);
            Assert.Contains("## Root cause", markdown);
            Assert.Contains("## Findings", markdown);
            Assert.Contains("Faulting thread", markdown);
            Assert.Contains("Source context", markdown);
            Assert.Contains("line 3", markdown);
            Assert.True(handler.CallCount >= 1);
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }

    [Fact]
    public void GenerateHtmlReport_WhenFaultingFrameHasSourceContext_RendersInlineSnippet()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\n", Encoding.UTF8, "text/plain")
        });

        var originalFactory = SourceContextEnricher.HttpClientFactory;
        var originalRoots = SourceContextEnricher.LocalSourceRoots;
        try
        {
            SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);
            SourceContextEnricher.LocalSourceRoots = Array.Empty<string>();

            var analysis = new CrashAnalysisResult
            {
                Summary = new AnalysisSummary { Description = "Found 1 threads (1 total frames, 1 in faulting thread), 0 modules." },
                Threads = new ThreadsInfo
                {
                    All =
                    [
                        new ThreadInfo
                        {
                            ThreadId = "t1",
                            IsFaulting = true,
                            CallStack =
                            [
                                new StackFrame
                                {
                                    FrameNumber = 0,
                                    InstructionPointer = "0x1",
                                    Module = "MyApp.Core",
                                    Function = "MyApp.Core.Foo.Bar()",
                                    IsManaged = true,
                                    SourceFile = "file.cs",
                                    LineNumber = 3,
                                    SourceRawUrl = "https://raw.githubusercontent.com/org/repo/sha/file.cs",
                                    SourceUrl = "https://github.com/org/repo/blob/sha/file.cs#L3"
                                }
                            ]
                        }
                    ]
                },
                Modules = []
            };

            CrashAnalysisResultFinalizer.Finalize(analysis);

            var service = new ReportService();
            var html = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Html },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            Assert.Contains("id=\"at-a-glance\"", html);
            Assert.Contains("id=\"root-cause\"", html);
            Assert.Contains("id=\"findings\"", html);
            Assert.Contains("Faulting thread", html);
            Assert.Contains("Source context", html);
            Assert.Contains("line 3", html);
            Assert.True(handler.CallCount >= 1);
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }
}
