using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DebuggerMcp.Tests.Controllers;

/// <summary>
/// Integration tests for DumpController.
/// Uses TestWebApplicationFactory to test HTTP endpoints with a real HTTP pipeline.
/// </summary>
public class DumpControllerTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    
    public DumpControllerTests(TestWebApplicationFactory factory)
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
    public async Task UploadDump_NoFile_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("testuser"), "userId");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert - ASP.NET model binding returns BadRequest when file is missing
        // The exact error format may vary (RFC 9110 ProblemDetails vs custom error)
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadDump_EmptyFile_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("testuser"), "userId");
        content.Add(new ByteArrayContent(Array.Empty<byte>()), "file", "test.dmp");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No file provided", body);
    }

    [Fact]
    public async Task UploadDump_InvalidUserId_ReturnsBadRequest()
    {
        // Arrange - Create a valid Windows minidump header (MDMP)
        var dumpHeader = CreateValidWindowsDumpHeader();
        
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("../../../etc/passwd"), "userId"); // Path traversal attempt
        var fileContent = new ByteArrayContent(dumpHeader);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "test.dmp");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("path traversal", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadDump_InvalidDumpFormat_ReturnsBadRequest()
    {
        // Arrange - Create an invalid file (not a valid dump)
        var invalidContent = Encoding.UTF8.GetBytes("This is not a valid dump file");
        
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("testuser"), "userId");
        var fileContent = new ByteArrayContent(invalidContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "test.dmp");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid dump file format", body);
    }

    [Fact]
    public async Task UploadDump_ValidWindowsDump_ReturnsOk()
    {
        // Arrange - Create a valid Windows minidump header
        var dumpContent = CreateValidWindowsDumpHeader();
        
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("testuser"), "userId");
        content.Add(new StringContent("Test dump description"), "description");
        var fileContent = new ByteArrayContent(dumpContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "crash.dmp");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        
        Assert.True(result.TryGetProperty("dumpId", out var dumpId));
        Assert.False(string.IsNullOrEmpty(dumpId.GetString()));
        Assert.True(result.TryGetProperty("userId", out var userId));
        Assert.Equal("testuser", userId.GetString());
        Assert.True(result.TryGetProperty("dumpFormat", out var format));
        Assert.Contains("Windows", format.GetString());
    }

    [Fact]
    public async Task UploadDump_ValidLinuxElfCore_ReturnsOk()
    {
        // Arrange - Create a valid Linux ELF core header
        var dumpContent = CreateValidLinuxElfCoreHeader();
        
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("linuxuser"), "userId");
        var fileContent = new ByteArrayContent(dumpContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "core.12345");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        
        Assert.True(result.TryGetProperty("dumpFormat", out var format));
        Assert.Contains("Linux", format.GetString());
    }

    [Fact]
    public async Task UploadDump_ValidMacOSCore_ReturnsOk()
    {
        // Arrange - Create a valid macOS Mach-O core header
        var dumpContent = CreateValidMachOCoreHeader();
        
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("macuser"), "userId");
        var fileContent = new ByteArrayContent(dumpContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "core.12345");

        // Act
        var response = await _client.PostAsync("/api/dumps/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        
        Assert.True(result.TryGetProperty("dumpFormat", out var format));
        Assert.Contains("macOS", format.GetString());
    }

    // ========== GetDumpInfo Endpoint Tests ==========

    [Fact]
    public async Task GetDumpInfo_NotFound_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/dumps/testuser/nonexistent-dump-id");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDumpInfo_InvalidUserId_ReturnsBadRequest()
    {
        // Act - Using URL encoded path traversal
        var response = await _client.GetAsync("/api/dumps/..%2F..%2Fetc/dump-id");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetDumpInfo_ExistingDump_ReturnsOk()
    {
        // Arrange - First upload a dump
        var dumpContent = CreateValidWindowsDumpHeader();
        var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent("getinfouser"), "userId");
        var fileContent = new ByteArrayContent(dumpContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadContent.Add(fileContent, "file", "test.dmp");

        var uploadResponse = await _client.PostAsync("/api/dumps/upload", uploadContent);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        var uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadBody);
        var dumpId = uploadResult.GetProperty("dumpId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/dumps/getinfouser/{dumpId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        
        Assert.True(result.TryGetProperty("dumpId", out var resultDumpId));
        Assert.Equal(dumpId, resultDumpId.GetString());
        Assert.True(result.TryGetProperty("size", out _));
    }

    // ========== ListUserDumps Endpoint Tests ==========

    [Fact]
    public async Task ListUserDumps_NoUser_ReturnsEmptyList()
    {
        // Act
        var response = await _client.GetAsync("/api/dumps/user/nonexistentuser");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public async Task ListUserDumps_WithDumps_ReturnsList()
    {
        // Arrange - Upload two dumps with unique user ID
        var uniqueUserId = $"listuser_{Guid.NewGuid():N}";
        for (int i = 0; i < 2; i++)
        {
            var dumpContent = CreateValidWindowsDumpHeader();
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(uniqueUserId), "userId");
            var fileContent = new ByteArrayContent(dumpContent);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", $"dump{i}.dmp");
            await _client.PostAsync("/api/dumps/upload", content);
        }

        // Act
        var response = await _client.GetAsync($"/api/dumps/user/{uniqueUserId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(2, result.GetArrayLength());
    }

    [Fact]
    public async Task ListUserDumps_InvalidUserId_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync("/api/dumps/user/..%2F..%2Fetc");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ========== DeleteDump Endpoint Tests ==========

    [Fact]
    public async Task DeleteDump_NotFound_Returns404()
    {
        // Act
        var response = await _client.DeleteAsync("/api/dumps/testuser/nonexistent-dump-id");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDump_ExistingDump_ReturnsOk()
    {
        // Arrange - First upload a dump
        var dumpContent = CreateValidWindowsDumpHeader();
        var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent("deleteuser"), "userId");
        var fileContent = new ByteArrayContent(dumpContent);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadContent.Add(fileContent, "file", "test.dmp");

        var uploadResponse = await _client.PostAsync("/api/dumps/upload", uploadContent);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        var uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadBody);
        var dumpId = uploadResult.GetProperty("dumpId").GetString();

        // Act
        var response = await _client.DeleteAsync($"/api/dumps/deleteuser/{dumpId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        // Verify it's actually deleted
        var getResponse = await _client.GetAsync($"/api/dumps/deleteuser/{dumpId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteDump_InvalidDumpId_ReturnsBadRequest()
    {
        // Act
        var response = await _client.DeleteAsync("/api/dumps/testuser/..%2F..%2Fetc");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ========== Stats Endpoint Tests ==========

    [Fact]
    public async Task GetStats_ReturnsOk()
    {
        // Act
        var response = await _client.GetAsync("/api/dumps/stats");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(body);
        
        // The stats endpoint returns TotalSessions and MaxSessionsPerUser (camelCase)
        Assert.True(result.TryGetProperty("totalSessions", out _));
        Assert.True(result.TryGetProperty("maxSessionsPerUser", out _));
    }

    // ========== Health Endpoint Tests ==========

    [Fact]
    public async Task HealthEndpoint_ReturnsOk()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ========== Helper Methods ==========

    /// <summary>
    /// Creates a valid Windows minidump file header (MDMP signature).
    /// </summary>
    private static byte[] CreateValidWindowsDumpHeader()
    {
        // MDMP signature (4D 44 4D 50) + padding to meet minimum size
        var header = new byte[64];
        header[0] = 0x4D; // M
        header[1] = 0x44; // D
        header[2] = 0x4D; // M
        header[3] = 0x50; // P
        // Rest is zeros (valid minidump structure)
        return header;
    }

    /// <summary>
    /// Creates a valid Linux ELF core file header.
    /// </summary>
    private static byte[] CreateValidLinuxElfCoreHeader()
    {
        // ELF signature (7F 45 4C 46) + padding
        var header = new byte[64];
        header[0] = 0x7F; // DEL
        header[1] = 0x45; // E
        header[2] = 0x4C; // L
        header[3] = 0x46; // F
        header[4] = 0x02; // 64-bit
        header[5] = 0x01; // Little endian
        header[16] = 0x04; // e_type = ET_CORE
        return header;
    }

    /// <summary>
    /// Creates a valid macOS Mach-O core file header.
    /// </summary>
    private static byte[] CreateValidMachOCoreHeader()
    {
        // Mach-O 64-bit magic (0xFEEDFACF) in little endian
        var header = new byte[64];
        header[0] = 0xCF;
        header[1] = 0xFA;
        header[2] = 0xED;
        header[3] = 0xFE;
        return header;
    }
}
