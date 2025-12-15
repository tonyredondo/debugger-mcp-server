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
    public void GenerateJsonReport_WhenMultipleFramesShareRemoteUrl_FetchesRemoteSourceOnce()
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
                Summary = new AnalysisSummary { Description = "Found 1 threads (2 total frames, 2 in faulting thread), 0 modules." },
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
                                    SourceRawUrl = "https://raw.githubusercontent.com/org/repo/sha/file.cs"
                                },
                                new StackFrame
                                {
                                    FrameNumber = 1,
                                    InstructionPointer = "0x2",
                                    Module = "MyApp.Core",
                                    Function = "MyApp.Core.Foo.Baz()",
                                    IsManaged = true,
                                    SourceFile = "file.cs",
                                    LineNumber = 6,
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
            _ = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Json },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            Assert.Equal(1, handler.CallCount);
            Assert.NotNull(analysis.Threads!.FaultingThread);
            Assert.NotNull(analysis.Threads.FaultingThread!.CallStack![0].SourceContext);
            Assert.NotNull(analysis.Threads.FaultingThread.CallStack![1].SourceContext);
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
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
                                    SourceRawUrl = "https://raw.githubusercontent.com/org/repo/sha/file.cs?q=1"
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

    [Fact]
    public void GenerateJsonReport_WhenSourceFileTraversesSymlinkedDirectory_DoesNotReadLocalSource()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Directory.CreateTempSubdirectory("source-context-root-");
        var externalRoot = Directory.CreateTempSubdirectory("source-context-external-");
        try
        {
            var externalFile = Path.Combine(externalRoot.FullName, "file.cs");
            File.WriteAllText(externalFile, "line 1\nline 2\nline 3\nline 4\nline 5\n");

            var linkPath = Path.Combine(tempRoot.FullName, "link");
            try
            {
                Directory.CreateSymbolicLink(linkPath, externalRoot.FullName);
            }
            catch
            {
                // If the environment doesn't allow symlinks, skip the assertion.
                return;
            }

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
                                        SourceFile = Path.Combine(linkPath, "file.cs"),
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
                Assert.Equal("unavailable", entry.GetProperty("status").GetString());
            }
            finally
            {
                SourceContextEnricher.HttpClientFactory = originalFactory;
                SourceContextEnricher.LocalSourceRoots = originalRoots;
            }
        }
        finally
        {
            externalRoot.Delete(recursive: true);
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenAzureDevOpsSourceUrlPresent_FetchesAndParsesContentJson()
    {
        var file = new StringBuilder();
        for (var i = 1; i <= 20; i++)
        {
            file.AppendLine($"line {i}");
        }

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"content\":{JsonSerializer.Serialize(file.ToString())}}}", Encoding.UTF8, "application/json")
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
                                    SourceFile = "/_/src/file.cs",
                                    LineNumber = 10,
                                    SourceUrl = "https://dev.azure.com/org/proj/_git/repo?path=%2Ffile.cs&version=GCabcdef"
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
            var entry = doc.RootElement.GetProperty("analysis").GetProperty("sourceContext")[0];
            Assert.Equal("remote", entry.GetProperty("status").GetString());
            Assert.Equal(7, entry.GetProperty("lines").GetArrayLength());
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenFaultingThreadHasManySourceFrames_EmbedsMoreThanTwoEntriesUnderFaultingThread()
    {
        var file = new StringBuilder();
        for (var i = 1; i <= 60; i++)
        {
            file.AppendLine($"line {i}");
        }

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(file.ToString(), Encoding.UTF8, "text/plain")
        });

        var originalFactory = SourceContextEnricher.HttpClientFactory;
        var originalRoots = SourceContextEnricher.LocalSourceRoots;
        try
        {
            SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);
            SourceContextEnricher.LocalSourceRoots = Array.Empty<string>();

            var rawUrl = "https://raw.githubusercontent.com/dotnet/dotnet/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/src/file.cs";

            var faulting = new ThreadInfo
            {
                ThreadId = "t1",
                IsFaulting = true,
                CallStack =
                [
                    // 10 native frames first (dominates the top of stack)
                    new StackFrame { FrameNumber = 0, InstructionPointer = "0x1", Module = "libcoreclr.so", Function = "native0", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 1, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 1, InstructionPointer = "0x2", Module = "libcoreclr.so", Function = "native1", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 2, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 2, InstructionPointer = "0x3", Module = "libcoreclr.so", Function = "native2", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 3, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 3, InstructionPointer = "0x4", Module = "libcoreclr.so", Function = "native3", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 4, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 4, InstructionPointer = "0x5", Module = "libcoreclr.so", Function = "native4", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 5, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 5, InstructionPointer = "0x6", Module = "libcoreclr.so", Function = "native5", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 6, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 6, InstructionPointer = "0x7", Module = "libcoreclr.so", Function = "native6", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 7, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 7, InstructionPointer = "0x8", Module = "libcoreclr.so", Function = "native7", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 8, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 8, InstructionPointer = "0x9", Module = "libcoreclr.so", Function = "native8", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 9, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 9, InstructionPointer = "0x10", Module = "libcoreclr.so", Function = "native9", IsManaged = false, SourceFile = "/_/src/file.cs", LineNumber = 10, SourceRawUrl = rawUrl },

                    // Managed frames appear later; selection should still include at least two.
                    new StackFrame { FrameNumber = 10, InstructionPointer = "0x11", Module = "System.Private.CoreLib", Function = "managed0", IsManaged = true, SourceFile = "/_/src/file.cs", LineNumber = 11, SourceRawUrl = rawUrl },
                    new StackFrame { FrameNumber = 11, InstructionPointer = "0x12", Module = "System.Private.CoreLib", Function = "managed1", IsManaged = true, SourceFile = "/_/src/file.cs", LineNumber = 12, SourceRawUrl = rawUrl },
                ]
            };

            var analysis = new CrashAnalysisResult
            {
                Summary = new AnalysisSummary { Description = "Found 1 threads (4 total frames, 4 in faulting thread), 0 modules." },
                Threads = new ThreadsInfo { All = [ faulting ] },
                Modules = []
            };

            CrashAnalysisResultFinalizer.Finalize(analysis);

            var service = new ReportService();
            var content = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Json },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            using var doc = JsonDocument.Parse(content);
            var faultingThread = doc.RootElement.GetProperty("analysis").GetProperty("threads").GetProperty("faultingThread");
            Assert.False(faultingThread.TryGetProperty("sourceContext", out _));

            var callStack = faultingThread.GetProperty("callStack");
            Assert.Equal(JsonValueKind.Array, callStack.ValueKind);

            var embeddedCount = 0;
            var managedCount = 0;
            foreach (var frame in callStack.EnumerateArray())
            {
                if (!frame.TryGetProperty("sourceContext", out _))
                {
                    continue;
                }

                embeddedCount++;
                if (frame.TryGetProperty("module", out var module) &&
                    string.Equals(module.GetString(), "System.Private.CoreLib", StringComparison.Ordinal))
                {
                    managedCount++;
                }
            }

            Assert.True(embeddedCount > 2);
            Assert.True(managedCount >= 2);

        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenFaultingThreadHasMoreThanTenEligibleFrames_EmbedsAllFaultingFramesButKeepsSummaryBounded()
    {
        var file = new StringBuilder();
        for (var i = 1; i <= 200; i++)
        {
            file.AppendLine($"line {i}");
        }

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(file.ToString(), Encoding.UTF8, "text/plain")
        });

        var originalFactory = SourceContextEnricher.HttpClientFactory;
        var originalRoots = SourceContextEnricher.LocalSourceRoots;
        try
        {
            SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);
            SourceContextEnricher.LocalSourceRoots = Array.Empty<string>();

            var rawUrl = "https://raw.githubusercontent.com/dotnet/dotnet/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/src/file.cs";

            var frames = new List<StackFrame>();
            for (var i = 0; i < 12; i++)
            {
                frames.Add(new StackFrame
                {
                    FrameNumber = i,
                    InstructionPointer = $"0x{i + 1:x}",
                    Module = i < 10 ? "libcoreclr.so" : "System.Private.CoreLib",
                    Function = $"f{i}",
                    IsManaged = i >= 10,
                    SourceFile = "/_/src/file.cs",
                    LineNumber = 10 + i,
                    SourceRawUrl = rawUrl
                });
            }

            var faulting = new ThreadInfo { ThreadId = "t1", IsFaulting = true, CallStack = frames };

            var analysis = new CrashAnalysisResult
            {
                Summary = new AnalysisSummary { Description = "Found 1 threads (12 total frames, 12 in faulting thread), 0 modules." },
                Threads = new ThreadsInfo { All = [ faulting ] },
                Modules = []
            };

            CrashAnalysisResultFinalizer.Finalize(analysis);

            var service = new ReportService();
            var content = service.GenerateReport(
                analysis,
                new ReportOptions { Format = ReportFormat.Json },
                new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch });

            using var doc = JsonDocument.Parse(content);
            var summary = doc.RootElement.GetProperty("analysis").GetProperty("sourceContext");
            Assert.Equal(10, summary.GetArrayLength());

            var faultingThread = doc.RootElement.GetProperty("analysis").GetProperty("threads").GetProperty("faultingThread");
            Assert.False(faultingThread.TryGetProperty("sourceContext", out _));

            var callStackElement = faultingThread.GetProperty("callStack");
            var embeddedCount = 0;
            foreach (var frame in callStackElement.EnumerateArray())
            {
                if (frame.TryGetProperty("sourceContext", out _))
                {
                    embeddedCount++;
                }
            }

            Assert.Equal(12, embeddedCount);
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenRemoteFileIsLargerThan256KbButUnder5Mb_FetchesSuccessfully()
    {
        // Previously, SourceContextEnricher capped remote fetches at 256KB.
        // This test ensures we can fetch larger source files (still bounded for safety).
        var sb = new StringBuilder();
        for (var i = 1; i <= 40000; i++)
        {
            sb.AppendLine("0123456789");
        }

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain")
        });

        var originalFactory = SourceContextEnricher.HttpClientFactory;
        var originalRoots = SourceContextEnricher.LocalSourceRoots;
        try
        {
            SourceContextEnricher.HttpClientFactory = () => new HttpClient(handler);
            SourceContextEnricher.LocalSourceRoots = Array.Empty<string>();

            var rawUrl = "https://raw.githubusercontent.com/dotnet/dotnet/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/src/file.cs";

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
                                    Module = "System.Private.CoreLib",
                                    Function = "managed",
                                    IsManaged = true,
                                    SourceFile = "/_/src/file.cs",
                                    LineNumber = 20000,
                                    SourceRawUrl = rawUrl
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
            var entry = doc.RootElement.GetProperty("analysis").GetProperty("sourceContext")[0];
            Assert.Equal("remote", entry.GetProperty("status").GetString());
            Assert.Equal(7, entry.GetProperty("lines").GetArrayLength());
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }

    [Fact]
    public void GenerateJsonReport_WhenFaultingFrameContextIsUnavailable_DoesNotEmitFrameSourceContext()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("should-not-fetch", Encoding.UTF8, "text/plain")
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
                                    IsManaged = false,
                                    SourceFile = "/_/src/file.c",
                                    LineNumber = 10
                                    // No sourceUrl/sourceRawUrl and local roots disabled -> unavailable.
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

            using var doc = JsonDocument.Parse(content);
            var frame = doc.RootElement.GetProperty("analysis")
                .GetProperty("threads")
                .GetProperty("faultingThread")
                .GetProperty("callStack")[0];

            Assert.False(frame.TryGetProperty("sourceContext", out _));
        }
        finally
        {
            SourceContextEnricher.HttpClientFactory = originalFactory;
            SourceContextEnricher.LocalSourceRoots = originalRoots;
        }
    }
}
