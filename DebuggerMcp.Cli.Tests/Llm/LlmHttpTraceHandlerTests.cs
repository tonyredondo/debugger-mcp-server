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
                Content = new StringContent("{\"apiKey\":\"secret\",\"openai_api_key\":\"secret2\",\"x\":1}", Encoding.UTF8, "application/json")
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
            Assert.Contains("\"openai_api_key\": \"***\"", requestText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SendAsync_WritesMetaFiles_WithRedactedAuthorization()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);

            var inner = new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                });

            var trace = new LlmHttpTraceHandler(store, "openai") { InnerHandler = inner };
            using var http = new HttpClient(trace);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/chat/completions")
            {
                Content = new StringContent("{\"x\":1}", Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-123");

            var resp = await http.SendAsync(req);
            _ = await resp.Content.ReadAsStringAsync();

            var requestMetaFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.request.meta.json", StringComparison.OrdinalIgnoreCase));
            var requestMetaText = await File.ReadAllTextAsync(requestMetaFile);
            Assert.DoesNotContain("sk-123", requestMetaText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Bearer ***", requestMetaText, StringComparison.OrdinalIgnoreCase);

            var responseMetaFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.response.meta.json", StringComparison.OrdinalIgnoreCase));
            var responseMetaText = await File.ReadAllTextAsync(responseMetaFile);
            Assert.Contains("\"status\"", responseMetaText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SendAsync_RedactsOpenAiApiKeyEnvVarsAndRawKeyTokens()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);

            var inner = new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Incorrect API key provided: sk-123", Encoding.UTF8, "text/plain")
                });

            var trace = new LlmHttpTraceHandler(store, "openai") { InnerHandler = inner };
            using var http = new HttpClient(trace);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/chat/completions?api_key=sk-123")
            {
                Content = new StringContent("OPENAI_API_KEY=sk-123", Encoding.UTF8, "text/plain")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-123");

            var resp = await http.SendAsync(req);
            _ = await resp.Content.ReadAsStringAsync();

            var requestFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.request.json", StringComparison.OrdinalIgnoreCase));
            var requestText = await File.ReadAllTextAsync(requestFile);
            Assert.Contains("OPENAI_API_KEY=***", requestText, StringComparison.OrdinalIgnoreCase);

            var responseFile = Directory.GetFiles(temp).Single(f => f.EndsWith(".openai.response.json", StringComparison.OrdinalIgnoreCase));
            var responseText = await File.ReadAllTextAsync(responseFile);
            Assert.DoesNotContain("sk-123", responseText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sk-***", responseText, StringComparison.OrdinalIgnoreCase);

            var eventsText = await File.ReadAllTextAsync(Path.Combine(temp, "events.jsonl"));
            Assert.DoesNotContain("sk-123", eventsText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("api_key=***", eventsText, StringComparison.OrdinalIgnoreCase);
            Assert.True(eventsText.Contains("authorization", StringComparison.OrdinalIgnoreCase), eventsText);
            Assert.True(eventsText.Contains("Bearer ***", StringComparison.OrdinalIgnoreCase), eventsText);
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
    public async Task SendAsync_RedactsNonHexTokenValues()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);

            var inner = new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"token\":\"abc\"}", Encoding.UTF8, "application/json")
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
            Assert.Contains("\"token\": \"***\"", responseText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"token\": \"abc\"", responseText, StringComparison.OrdinalIgnoreCase);
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
