using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace DebuggerMcp.Tests.Controllers;

/// <summary>
/// Integration tests for SymbolController.
/// Uses TestWebApplicationFactory to test HTTP endpoints with a real HTTP pipeline.
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

    // ========== Upload Endpoint Tests ==========

    [Fact]
    public async Task UploadSymbol_NoFile_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-dump-id"), "dumpId");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert - ASP.NET model binding returns BadRequest when file is missing
        // The exact error format may vary (RFC 9110 ProblemDetails vs custom error)
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadSymbol_InvalidDumpId_ReturnsBadRequest()
    {
        // Arrange
        var pdbContent = CreateValidPortablePdbHeader();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("../../../etc/passwd"), "dumpId"); // Path traversal attempt
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "test.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("path traversal", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadSymbol_InvalidFormat_ReturnsBadRequest()
    {
        // Arrange - Create an invalid file (not a valid symbol)
        var invalidContent = Encoding.UTF8.GetBytes("This is not a valid symbol file");

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(invalidContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "test.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid symbol file format", body);
    }

    [Fact]
    public async Task UploadSymbol_FileTooSmall_ReturnsBadRequest()
    {
        // Arrange - Create a file that's too small
        var smallContent = new byte[2]; // Too small to be valid

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(smallContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "test.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("too small", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadSymbol_ValidPortablePdb_ReturnsOk()
    {
        // Arrange
        var pdbContent = CreateValidPortablePdbHeader();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("valid-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "MyApp.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("dumpId", out var dumpId));
        Assert.Equal("valid-dump-id", dumpId.GetString());
        Assert.True(result.TryGetProperty("fileName", out var fileName));
        Assert.Equal("MyApp.pdb", fileName.GetString());
        Assert.True(result.TryGetProperty("format", out var format));
        Assert.Contains("Portable PDB", format.GetString());
    }

    [Fact]
    public async Task UploadSymbol_FileNameContainsPathSegments_ReturnsSanitizedBasename()
    {
        // Arrange
        var pdbContent = CreateValidPortablePdbHeader();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("sanitize-name-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", @"C:\temp\sym.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("fileName", out var fileName));
        Assert.Equal("sym.pdb", fileName.GetString());
    }

    [Fact]
    public async Task UploadSymbol_ValidWindowsPdb_ReturnsOk()
    {
        // Arrange
        var pdbContent = CreateValidWindowsPdbHeader();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("windows-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "Native.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("format", out var format));
        Assert.Contains("Windows PDB", format.GetString());
    }

    [Fact]
    public async Task UploadSymbol_ValidElfSymbol_ReturnsOk()
    {
        // Arrange
        var elfContent = CreateValidElfHeader();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("linux-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(elfContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "libmyapp.so");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("format", out var format));
        Assert.Contains("ELF", format.GetString());
    }

    [Fact]
    public async Task UploadSymbol_ValidMachOSymbol_ReturnsOk()
    {
        // Arrange
        var machoContent = CreateValidMachOHeader();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("macos-dump-id"), "dumpId");
        var fileContent = new ByteArrayContent(machoContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "MyApp.dylib");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("format", out var format));
        Assert.Contains("Mach-O", format.GetString());
    }

    // ========== Batch Upload Endpoint Tests ==========

    [Fact]
    public async Task UploadSymbolBatch_NoFiles_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-dump-id"), "dumpId");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No files provided", body);
    }

    [Fact]
    public async Task UploadSymbolBatch_MultipleValidFiles_ReturnsOk()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("batch-dump-id"), "dumpId");

        // Add first PDB
        var pdb1Content = CreateValidPortablePdbHeader();
        var file1Content = new ByteArrayContent(pdb1Content);
        file1Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file1Content, "files", "App1.pdb");

        // Add second PDB
        var pdb2Content = CreateValidPortablePdbHeader();
        var file2Content = new ByteArrayContent(pdb2Content);
        file2Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file2Content, "files", "App2.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("filesUploaded", out var filesUploaded));
        Assert.Equal(2, filesUploaded.GetInt32());

        Assert.True(result.TryGetProperty("files", out var files));
        var fileNames = files.EnumerateArray()
            .Select(f => f.GetProperty("fileName").GetString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        Assert.Contains("App1.pdb", fileNames);
        Assert.Contains("App2.pdb", fileNames);
    }

    [Fact]
    public async Task UploadSymbolBatch_FileNamesContainPathSegments_ReturnsSanitizedBasenames()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("batch-sanitize-dump-id"), "dumpId");

        var pdb1Content = CreateValidPortablePdbHeader();
        var file1Content = new ByteArrayContent(pdb1Content);
        file1Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file1Content, "files", "../App1.pdb");

        var pdb2Content = CreateValidPortablePdbHeader();
        var file2Content = new ByteArrayContent(pdb2Content);
        file2Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file2Content, "files", @"C:\x\App2.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("files", out var files));
        var fileNames = files.EnumerateArray()
            .Select(f => f.GetProperty("fileName").GetString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        Assert.Contains("App1.pdb", fileNames);
        Assert.Contains("App2.pdb", fileNames);
    }

    [Fact]
    public async Task UploadSymbolBatch_InvalidDumpId_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("../../../etc"), "dumpId");

        var pdbContent = CreateValidPortablePdbHeader();
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "files", "App1.pdb");

        // Act
        var response = await _client.PostAsync("/api/symbols/upload-batch", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ========== List Symbols Endpoint Tests ==========
    // Note: Route is /api/symbols/dump/{dumpId}

    [Fact]
    public async Task ListSymbols_NoSymbols_Returns404()
    {
        // Act - the endpoint returns 404 when no symbols exist
        var response = await _client.GetAsync("/api/symbols/dump/nonexistent-dump-id");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSymbols_InvalidDumpId_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync("/api/symbols/dump/..%2F..%2Fetc");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListSymbols_WithUploadedSymbols_ReturnsList()
    {
        // Arrange - Upload a symbol first with unique ID
        var uniqueDumpId = $"list-symbols-{Guid.NewGuid():N}";
        var pdbContent = CreateValidPortablePdbHeader();
        var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent(uniqueDumpId), "dumpId");
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadContent.Add(fileContent, "file", "Test.pdb");

        await _client.PostAsync("/api/symbols/upload", uploadContent);

        // Act
        var response = await _client.GetAsync($"/api/symbols/dump/{uniqueDumpId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("symbols", out var symbols));
        Assert.True(symbols.GetArrayLength() >= 1);
    }

    // ========== Symbol Exists Endpoint Tests ==========
    // Note: Route is /api/symbols/dump/{dumpId}/exists (checks if ANY symbols exist)

    [Fact]
    public async Task CheckSymbolExists_NoneExist_ReturnsFalse()
    {
        // Act
        var response = await _client.GetAsync("/api/symbols/dump/some-dump-no-symbols/exists");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("hasSymbols", out var hasSymbols));
        Assert.False(hasSymbols.GetBoolean());
    }

    [Fact]
    public async Task CheckSymbolExists_SymbolsExist_ReturnsTrue()
    {
        // Arrange - Upload a symbol first with unique ID
        var uniqueDumpId = $"exists-check-{Guid.NewGuid():N}";
        var pdbContent = CreateValidPortablePdbHeader();
        var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent(uniqueDumpId), "dumpId");
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadContent.Add(fileContent, "file", "ExistingFile.pdb");

        var uploadResponse = await _client.PostAsync("/api/symbols/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);

        // Act
        var response = await _client.GetAsync($"/api/symbols/dump/{uniqueDumpId}/exists");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("hasSymbols", out var hasSymbols));
        Assert.True(hasSymbols.GetBoolean());
    }

    [Fact]
    public async Task CheckSymbolExists_InvalidDumpId_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync("/api/symbols/dump/..%2Fetc/exists");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ========== Delete Symbol Endpoint Tests ==========
    // Note: Route is /api/symbols/dump/{dumpId} (deletes ALL symbols for dump)

    [Fact]
    public async Task DeleteSymbols_NoSymbols_Returns404()
    {
        // Act
        var response = await _client.DeleteAsync("/api/symbols/dump/some-dump-no-symbols");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSymbols_SymbolsExist_ReturnsOk()
    {
        // Arrange - Upload a symbol first with unique ID
        var uniqueDumpId = $"delete-symbol-{Guid.NewGuid():N}";
        var pdbContent = CreateValidPortablePdbHeader();
        var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent(uniqueDumpId), "dumpId");
        var fileContent = new ByteArrayContent(pdbContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadContent.Add(fileContent, "file", "ToDelete.pdb");

        var uploadResponse = await _client.PostAsync("/api/symbols/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);

        // Act
        var response = await _client.DeleteAsync($"/api/symbols/dump/{uniqueDumpId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify it's actually deleted
        var existsResponse = await _client.GetAsync($"/api/symbols/dump/{uniqueDumpId}/exists");
        var body = await existsResponse.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(result.GetProperty("hasSymbols").GetBoolean());
    }

    [Fact]
    public async Task DeleteSymbols_InvalidDumpId_ReturnsBadRequest()
    {
        // Act
        var response = await _client.DeleteAsync("/api/symbols/dump/..%2Fetc");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ========== Symbol Servers Endpoint Tests ==========

    [Fact]
    public async Task GetSymbolServers_ReturnsServerList()
    {
        // Act
        var response = await _client.GetAsync("/api/symbols/servers");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(result.TryGetProperty("servers", out var servers));
        Assert.True(servers.GetArrayLength() > 0);
    }

    // ========== Helper Methods ==========

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
    /// The signature is: "Microsoft C/C++ MSF 7.00\r\n\x1ADS" (29 bytes).
    /// </summary>
    private static byte[] CreateValidWindowsPdbHeader()
    {
        // Exact MSF 7.0 signature bytes
        var header = new byte[64];
        byte[] signature = {
            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // "Microsof"
            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // "t C/C++ "
            0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30, // "MSF 7.00"
            0x0D, 0x0A, 0x1A, 0x44, 0x53                    // "\r\n\x1ADS"
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
        header[0] = 0x7F; // DEL
        header[1] = 0x45; // E
        header[2] = 0x4C; // L
        header[3] = 0x46; // F
        header[4] = 0x02; // 64-bit
        header[5] = 0x01; // Little endian
        return header;
    }

    /// <summary>
    /// Creates a valid Mach-O header.
    /// </summary>
    private static byte[] CreateValidMachOHeader()
    {
        var header = new byte[64];
        // 64-bit Mach-O magic (little endian)
        header[0] = 0xCF;
        header[1] = 0xFA;
        header[2] = 0xED;
        header[3] = 0xFE;
        return header;
    }
}
