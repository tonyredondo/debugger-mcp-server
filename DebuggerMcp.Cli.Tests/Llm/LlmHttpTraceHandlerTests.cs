#nullable enable

using System.Net;
using System.Text;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public sealed class LlmHttpTraceHandlerTests
{
    [Fact]
    public async Task SendAsync_WritesRequestAndResponseFiles_AndPreservesResponseBody()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 2_000_000);

            var inner = new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                });

            var trace = new LlmHttpTraceHandler(store, "openai") { InnerHandler = inner };
            using var http = new HttpClient(trace);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/chat/completions")
            {
                Content = new StringContent("{\"apiKey\":\"secret\",\"x\":1}", Encoding.UTF8, "application/json")
            };

            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal("{\"ok\":true}", body);

            var files = Directory.GetFiles(temp).Select(Path.GetFileName).ToList();
            Assert.Contains(files, f => f != null && f.EndsWith(".openai.request.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => f != null && f.EndsWith(".openai.response.json", StringComparison.OrdinalIgnoreCase));

            var requestFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.request.json", StringComparison.OrdinalIgnoreCase));
            var requestText = await File.ReadAllTextAsync(requestFile);
            Assert.Contains("\"apiKey\": \"***\"", requestText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SendAsync_DoesNotRedactDebuggerMethodTokens()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);

            var inner = new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tool\":\"sos token2ee\",\"token\":\"0x06000001\"}", Encoding.UTF8, "application/json")
                });

            var trace = new LlmHttpTraceHandler(store, "openai") { InnerHandler = inner };
            using var http = new HttpClient(trace);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/chat/completions")
            {
                Content = new StringContent("{\"x\":1}", Encoding.UTF8, "application/json")
            };

            var resp = await http.SendAsync(req);
            _ = await resp.Content.ReadAsStringAsync();

            var responseFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.response.json", StringComparison.OrdinalIgnoreCase));
            var responseText = await File.ReadAllTextAsync(responseFile);
            Assert.Contains("0x06000001", responseText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SendAsync_PreservesNonUtf8ResponseBody()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);

            var latin1 = Encoding.GetEncoding("iso-8859-1");
            var inner = new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"olá\"}", latin1, "application/json")
                });

            var trace = new LlmHttpTraceHandler(store, "openai") { InnerHandler = inner };
            using var http = new HttpClient(trace);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/chat/completions")
            {
                Content = new StringContent("{\"x\":1}", Encoding.UTF8, "application/json")
            };

            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal("{\"text\":\"olá\"}", body);

            var responseFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.response.json", StringComparison.OrdinalIgnoreCase));
            var responseText = await File.ReadAllTextAsync(responseFile);
            Assert.Contains("olá", responseText, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
