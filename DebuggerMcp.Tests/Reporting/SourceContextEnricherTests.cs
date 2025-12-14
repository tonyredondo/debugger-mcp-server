using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class SourceContextEnricherTests
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
    public void GenerateJsonReport_WhenSourceRawUrlPresent_IncludesSourceContext()
    {
        var file = new StringBuilder();
        for (var i = 1; i <= 20; i++)
        {
            file.AppendLine($"line {i}");
        }

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(file.ToString(), Encoding.UTF8, "text/plain")
        });

        var originalFactory = SourceContextEnricher.HttpClientFactory;
        try
        {
            SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);

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
                                    LineNumber = 10,
                                    SourceUrl = "https://github.com/org/repo/blob/sha/file.cs#L10",
                                    SourceRawUrl = "https://raw.githubusercontent.com/org/repo/sha/file.cs"
                                }
                            ]
                        }
                    ]
                },
                Modules = []
            };

            CrashAnalysisResultFinalizer.Finalize(analysis);

            var service = new ReportService();
            var content = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Json },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            Assert.True(handler.CallCount >= 1);

            using var doc = JsonDocument.Parse(content);
            var sourceContext = doc.RootElement.GetProperty("analysis").GetProperty("sourceContext");
            Assert.Equal(JsonValueKind.Array, sourceContext.ValueKind);
            Assert.True(sourceContext.GetArrayLength() >= 1);

            var entry = sourceContext[0];
            Assert.Equal("remote", entry.GetProperty("status").GetString());
            Assert.Equal(7, entry.GetProperty("lines").GetArrayLength()); // ±3 window around line 10
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenSourceUrlHasQuery_DoesNotFetchRemoteSource()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("line 1\nline 2\n", Encoding.UTF8, "text/plain")
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
                                    Module = "m",
                                    Function = "f",
                                    IsManaged = true,
                                    SourceFile = "file.cs",
                                    LineNumber = 1,
                                    SourceRawUrl = "https://raw.githubusercontent.com/org/repo/sha/file.cs?token=abc"
                                }
                            ]
                        }
                    ]
                },
                Modules = []
            };

            CrashAnalysisResultFinalizer.Finalize(analysis);

            var service = new ReportService();
            var content = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Json },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            Assert.Equal(0, handler.CallCount);

            using var doc = JsonDocument.Parse(content);
            var sourceContext = doc.RootElement.GetProperty("analysis").GetProperty("sourceContext");
            Assert.Equal(JsonValueKind.Array, sourceContext.ValueKind);
            Assert.True(sourceContext.GetArrayLength() >= 1);
            Assert.NotEqual("remote", sourceContext[0].GetProperty("status").GetString());
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenSourceFileIsUnderAllowedRoot_ReadsLocalSourceWithoutFetching()
    {
        var tempRoot = Directory.CreateTempSubdirectory("source-context-root-");
        try
        {
            var filePath = Path.Combine(tempRoot.FullName, "file.cs");
            File.WriteAllText(filePath, "line 1\nline 2\nline 3\nline 4\nline 5\n");

            var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("should-not-fetch", Encoding.UTF8, "text/plain")
            });

            var originalFactory = SourceContextEnricher.HttpClientFactory;
            var originalRoots = SourceContextEnricher.LocalSourceRoots;
            try
            {
                SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);
                SourceContextEnricher.LocalSourceRoots = new[] { tempRoot.FullName };

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
                                        Module = "m",
                                        Function = "f",
                                        IsManaged = true,
                                        SourceFile = filePath,
                                        LineNumber = 3
                                    }
                                ]
                            }
                        ]
                    },
                    Modules = []
                };

                CrashAnalysisResultFinalizer.Finalize(analysis);

                var service = new ReportService();
                var content = service.GenerateReport(
                    analysis,
                    new ReportOptions { Format = ReportFormat.Json },
                    new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

                Assert.Equal(0, handler.CallCount);

                using var doc = JsonDocument.Parse(content);
                var entry = doc.RootElement.GetProperty("analysis").GetProperty("sourceContext")[0];
                Assert.Equal("local", entry.GetProperty("status").GetString());
                Assert.Equal(5, entry.GetProperty("lines").GetArrayLength());
            }
            finally
            {
                SourceContextEnricher.HttpClientFactory = originalFactory;
                SourceContextEnricher.LocalSourceRoots = originalRoots;
            }
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
