using System.Net;
using DebuggerMcp.Configuration;
using DebuggerMcp.Security;
using DebuggerMcp.Watches;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.Controllers;

/// <summary>
/// Tests for dump upload size limit behavior.
/// </summary>
[Collection("NonParallelEnvironment")]
public class DumpControllerUploadLimitTests : IDisposable
{
    private readonly string? _originalMaxRequestBodySizeGb;

    public DumpControllerUploadLimitTests()
    {
        _originalMaxRequestBodySizeGb = Environment.GetEnvironmentVariable(
            DebuggerMcp.Configuration.EnvironmentConfig.MaxRequestBodySizeGb);
    }

    [Fact]
    public async Task UploadDump_FileOverMaxRequestBodySize_ReturnsBadRequest()
    {
        // Arrange
        Environment.SetEnvironmentVariable(EnvironmentConfig.MaxRequestBodySizeGb, "1");

        var tempDir = Path.Combine(Path.GetTempPath(), $"DumpUploadLimitTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sessionManager = new DebuggerSessionManager(tempDir);
            var symbolManager = new SymbolManager(dumpStorageBasePath: tempDir);
            var watchStore = new WatchStore(tempDir);
            var controller = new DebuggerMcp.Controllers.DumpController(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<DebuggerMcp.Controllers.DumpController>.Instance,
                NullLoggerFactory.Instance);

            var file = new FakeFormFile(
                new MemoryStream(CreateValidWindowsDumpHeader()),
                fileName: "big.dmp",
                length: (1L * 1024 * 1024 * 1024) + 1);

            // Act
            var result = await controller.UploadDump(file, "testuser");

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("File size exceeds", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            EnvironmentConfig.MaxRequestBodySizeGb,
            _originalMaxRequestBodySizeGb);
    }

    private static byte[] CreateValidWindowsDumpHeader()
    {
        // Windows minidump signature: "MDMP".
        return new byte[] { 0x4D, 0x44, 0x4D, 0x50, 0x00, 0x00, 0x00, 0x00 };
    }

    private sealed class FakeFormFile(Stream stream, string fileName, long length) : Microsoft.AspNetCore.Http.IFormFile
    {
        public string ContentType { get; set; } = "application/octet-stream";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; } = length;
        public string Name { get; set; } = "file";
        public string FileName { get; } = fileName;

        public Stream OpenReadStream() => stream;

        public void CopyTo(Stream target) => stream.CopyTo(target);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            stream.CopyToAsync(target, cancellationToken);
    }
}
