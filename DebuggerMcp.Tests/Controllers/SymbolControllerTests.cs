using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace DebuggerMcp.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="DebuggerMcp.Controllers.SymbolController"/>.
/// These tests exercise the HTTP pipeline with user-scoped dump ownership.
/// </summary>
public class SymbolControllerTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SymbolControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [Fact]
    public async Task UploadSymbol_NoFile_ReturnsBadRequest()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("user-one"), "userId" },
            { new StringContent("some-dump-id"), "dumpId" }
        };

        var response = await _client.PostAsync("/api/symbols/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadSymbol_InvalidDumpId_ReturnsBadRequest()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("user-one"), "userId" },
            { new StringContent("../../../etc/passwd"), "dumpId" }
        };
        content.Add(CreateFileContent(CreateValidPortablePdbHeader()), "file", "test.pdb");

        var response = await _client.PostAsync("/api/symbols/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("path traversal", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadSymbol_InvalidFormat_ReturnsBadRequest()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("user-one"), "userId" },
            { new StringContent("some-dump-id"), "dumpId" }
        };
        content.Add(CreateFileContent(Encoding.UTF8.GetBytes("This is not a valid symbol file")), "file", "test.pdb");

        var response = await _client.PostAsync("/api/symbols/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid symbol file format", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSymbol_FileTooSmall_ReturnsBadRequest()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("user-one"), "userId" },
            { new StringContent("some-dump-id"), "dumpId" }
        };
        content.Add(CreateFileContent(new byte[2]), "file", "test.pdb");

        var response = await _client.PostAsync("/api/symbols/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("too small", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadSymbol_WhenDumpDoesNotExist_ReturnsBadRequest()
    {
        var response = await UploadSymbolAsync(
            "missing-user",
            "missing-dump",
            CreateValidPortablePdbHeader(),
            "MyApp.pdb");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Upload the dump before uploading symbols", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadSymbol_ValidPortablePdb_ReturnsOkAndStoresInUserScopedDirectory()
    {
        const string userId = "portable-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await UploadSymbolAsync(userId, dumpId, CreateValidPortablePdbHeader(), "MyApp.pdb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Equal(dumpId, result.GetProperty("dumpId").GetString());
        Assert.Equal("MyApp.pdb", result.GetProperty("fileName").GetString());
        Assert.Contains("Portable PDB", result.GetProperty("format").GetString(), StringComparison.Ordinal);

        var scopedSymbolPath = Path.Combine(_factory.TempDirectory, userId, $".symbols_{dumpId}", "MyApp.pdb");
        var rootLevelSymbolDirectory = Path.Combine(_factory.TempDirectory, $".symbols_{dumpId}");
        Assert.True(File.Exists(scopedSymbolPath));
        Assert.False(Directory.Exists(rootLevelSymbolDirectory));
    }

    [Fact]
    public async Task UploadSymbol_FileNameContainsPathSegments_ReturnsSanitizedBasename()
    {
        const string userId = "sanitize-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await UploadSymbolAsync(userId, dumpId, CreateValidPortablePdbHeader(), @"C:\temp\sym.pdb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Equal("sym.pdb", result.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task UploadSymbol_ValidWindowsPdb_ReturnsOk()
    {
        const string userId = "windows-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await UploadSymbolAsync(userId, dumpId, CreateValidWindowsPdbHeader(), "Native.pdb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Contains("Windows PDB", result.GetProperty("format").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSymbol_ValidElfSymbol_ReturnsOk()
    {
        const string userId = "linux-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await UploadSymbolAsync(userId, dumpId, CreateValidElfHeader(), "libmyapp.so");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Contains("ELF", result.GetProperty("format").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSymbol_ValidMachOSymbol_ReturnsOk()
    {
        const string userId = "macos-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await UploadSymbolAsync(userId, dumpId, CreateValidMachOHeader(), "MyApp.dylib");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Contains("Mach-O", result.GetProperty("format").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSymbolBatch_NoFiles_ReturnsBadRequest()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("user-one"), "userId" },
            { new StringContent("some-dump-id"), "dumpId" }
        };

        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No files provided", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSymbolBatch_MultipleValidFiles_ReturnsOk()
    {
        const string userId = "batch-user";
        var dumpId = await UploadDumpAsync(userId);

        var content = new MultipartFormDataContent
        {
            { new StringContent(userId), "userId" },
            { new StringContent(dumpId), "dumpId" }
        };
        content.Add(CreateFileContent(CreateValidPortablePdbHeader()), "files", "App1.pdb");
        content.Add(CreateFileContent(CreateValidPortablePdbHeader()), "files", "App2.pdb");

        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Equal(2, result.GetProperty("filesUploaded").GetInt32());
        var fileNames = result.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("fileName").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        Assert.Contains("App1.pdb", fileNames);
        Assert.Contains("App2.pdb", fileNames);
    }

    [Fact]
    public async Task UploadSymbolBatch_FileNamesContainPathSegments_ReturnsSanitizedBasenames()
    {
        const string userId = "batch-sanitize-user";
        var dumpId = await UploadDumpAsync(userId);

        var content = new MultipartFormDataContent
        {
            { new StringContent(userId), "userId" },
            { new StringContent(dumpId), "dumpId" }
        };
        content.Add(CreateFileContent(CreateValidPortablePdbHeader()), "files", "../App1.pdb");
        content.Add(CreateFileContent(CreateValidPortablePdbHeader()), "files", @"C:\x\App2.pdb");

        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        var fileNames = result.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("fileName").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        Assert.Contains("App1.pdb", fileNames);
        Assert.Contains("App2.pdb", fileNames);
    }

    [Fact]
    public async Task UploadSymbolBatch_InvalidDumpId_ReturnsBadRequest()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("user-one"), "userId" },
            { new StringContent("../../../etc"), "dumpId" }
        };
        content.Add(CreateFileContent(CreateValidPortablePdbHeader()), "files", "App1.pdb");

        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListSymbols_NoSymbolsForUserScopedDump_Returns404()
    {
        const string userId = "list-empty-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await _client.GetAsync(GetDumpSymbolsRoute(userId, dumpId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSymbols_InvalidDumpId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/symbols/user/test-user/dump/..%2F..%2Fetc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListSymbols_WithUploadedSymbols_ReturnsList()
    {
        const string userId = "list-user";
        var dumpId = await UploadDumpAsync(userId);
        await UploadSymbolAsync(userId, dumpId, CreateValidPortablePdbHeader(), "Test.pdb");

        var response = await _client.GetAsync(GetDumpSymbolsRoute(userId, dumpId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Contains("Test.pdb", result.GetProperty("symbols").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task ListSymbols_WrongUserScope_DoesNotLeakSymbols()
    {
        const string ownerUserId = "owner-user";
        const string otherUserId = "other-user";
        var dumpId = await UploadDumpAsync(ownerUserId);
        await UploadSymbolAsync(ownerUserId, dumpId, CreateValidPortablePdbHeader(), "OwnerOnly.pdb");

        var response = await _client.GetAsync(GetDumpSymbolsRoute(otherUserId, dumpId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CheckSymbolExists_NoneExist_ReturnsFalse()
    {
        const string userId = "exists-empty-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await _client.GetAsync(GetDumpSymbolsExistsRoute(userId, dumpId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.False(result.GetProperty("hasSymbols").GetBoolean());
    }

    [Fact]
    public async Task CheckSymbolExists_SymbolsExist_ReturnsTrue()
    {
        const string userId = "exists-user";
        var dumpId = await UploadDumpAsync(userId);
        await UploadSymbolAsync(userId, dumpId, CreateValidPortablePdbHeader(), "ExistingFile.pdb");

        var response = await _client.GetAsync(GetDumpSymbolsExistsRoute(userId, dumpId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.True(result.GetProperty("hasSymbols").GetBoolean());
    }

    [Fact]
    public async Task CheckSymbolExists_InvalidDumpId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/symbols/user/test-user/dump/..%2Fetc/exists");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSymbols_NoSymbols_Returns404()
    {
        const string userId = "delete-empty-user";
        var dumpId = await UploadDumpAsync(userId);

        var response = await _client.DeleteAsync(GetDumpSymbolsRoute(userId, dumpId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSymbols_SymbolsExist_ReturnsOk()
    {
        const string userId = "delete-user";
        var dumpId = await UploadDumpAsync(userId);
        await UploadSymbolAsync(userId, dumpId, CreateValidPortablePdbHeader(), "ToDelete.pdb");

        var response = await _client.DeleteAsync(GetDumpSymbolsRoute(userId, dumpId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var existsResponse = await _client.GetAsync(GetDumpSymbolsExistsRoute(userId, dumpId));
        var result = await ReadJsonAsync(existsResponse);
        Assert.False(result.GetProperty("hasSymbols").GetBoolean());
    }

    [Fact]
    public async Task DeleteSymbols_InvalidDumpId_ReturnsBadRequest()
    {
        var response = await _client.DeleteAsync("/api/symbols/user/test-user/dump/..%2Fetc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSymbolServers_ReturnsServerList()
    {
        var response = await _client.GetAsync("/api/symbols/servers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.True(result.GetProperty("servers").GetArrayLength() > 0);
    }

    /// <summary>
    /// Uploads a real dump through the API so symbol tests use the same persisted dump ownership
    /// shape as production code.
    /// </summary>
    private async Task<string> UploadDumpAsync(string userId)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(userId), "userId" }
        };
        content.Add(CreateFileContent(CreateValidWindowsDumpHeader()), "file", "test.dmp");

        var response = await _client.PostAsync("/api/dumps/upload", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await ReadJsonAsync(response);
        return result.GetProperty("dumpId").GetString()!;
    }

    /// <summary>
    /// Uploads a single symbol file using the user-scoped symbol endpoint.
    /// </summary>
    private async Task<HttpResponseMessage> UploadSymbolAsync(string userId, string dumpId, byte[] contentBytes, string fileName)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(userId), "userId" },
            { new StringContent(dumpId), "dumpId" }
        };
        content.Add(CreateFileContent(contentBytes), "file", fileName);
        return await _client.PostAsync("/api/symbols/upload", content);
    }

    /// <summary>
    /// Creates a file content object with the binary media type used by these upload endpoints.
    /// </summary>
    private static ByteArrayContent CreateFileContent(byte[] bytes)
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return fileContent;
    }

    /// <summary>
    /// Reads a JSON response body as a <see cref="JsonElement"/>.
    /// </summary>
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    /// <summary>
    /// Builds the user-scoped route for listing or deleting dump symbols.
    /// </summary>
    private static string GetDumpSymbolsRoute(string userId, string dumpId)
    {
        return $"/api/symbols/user/{Uri.EscapeDataString(userId)}/dump/{Uri.EscapeDataString(dumpId)}";
    }

    /// <summary>
    /// Builds the user-scoped route for checking whether a dump has symbols.
    /// </summary>
    private static string GetDumpSymbolsExistsRoute(string userId, string dumpId)
    {
        return $"{GetDumpSymbolsRoute(userId, dumpId)}/exists";
    }

    /// <summary>
    /// Creates a valid Portable PDB header (BSJB signature).
    /// </summary>
    private static byte[] CreateValidPortablePdbHeader()
    {
        var header = new byte[64];
        header[0] = 0x42; // B
        header[1] = 0x53; // S
        header[2] = 0x4A; // J
        header[3] = 0x42; // B
        return header;
    }

    /// <summary>
    /// Creates a valid Windows PDB header (MSF 7.0 signature).
    /// </summary>
    private static byte[] CreateValidWindowsPdbHeader()
    {
        var header = new byte[64];
        byte[] signature =
        {
            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66,
            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20,
            0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30,
            0x0D, 0x0A, 0x1A, 0x44, 0x53
        };
        Array.Copy(signature, header, signature.Length);
        return header;
    }

    /// <summary>
    /// Creates a valid ELF header.
    /// </summary>
    private static byte[] CreateValidElfHeader()
    {
        var header = new byte[64];
        header[0] = 0x7F;
        header[1] = 0x45;
        header[2] = 0x4C;
        header[3] = 0x46;
        header[4] = 0x02;
        header[5] = 0x01;
        return header;
    }

    /// <summary>
    /// Creates a valid Mach-O header.
    /// </summary>
    private static byte[] CreateValidMachOHeader()
    {
        var header = new byte[64];
        header[0] = 0xCF;
        header[1] = 0xFA;
        header[2] = 0xED;
        header[3] = 0xFE;
        return header;
    }

    /// <summary>
    /// Creates a valid Windows minidump header.
    /// </summary>
    private static byte[] CreateValidWindowsDumpHeader()
    {
        var header = new byte[64];
        header[0] = 0x4D;
        header[1] = 0x44;
        header[2] = 0x4D;
        header[3] = 0x50;
        return header;
    }
}
