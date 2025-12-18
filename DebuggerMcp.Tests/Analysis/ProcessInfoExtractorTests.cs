using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for the ProcessInfoExtractor class that extracts argv and envp from Linux core dumps.
/// </summary>
public class ProcessInfoExtractorTests
{
    private readonly ProcessInfoExtractor _extractor;

    public ProcessInfoExtractorTests()
    {
        _extractor = new ProcessInfoExtractor();
    }


    [Fact]
    public void FindMainFrame_WithDotnetMain_ExtractsArgcAndArgv()
    {
        // Arrange
        var backtraceOutput = @"
* thread #1, name = 'dotnet', stop reason = SIGABRT
    frame #46: 0x0000f7558fc7f1a4
    frame #47: 0x0000c5f644a76f8c dotnet`exe_start(argc=<unavailable>, argv=<unavailable>) at corehost.cpp:254:18 [opt]
    frame #48: 0x0000c5f644a77244 dotnet`main(argc=2, argv=0x0000ffffefcba618) at corehost.cpp:322:21 [opt]
    frame #49: 0x0000f7558ffad6a4 libstdc++.so.6
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert
        Assert.Equal(2, argc);
        Assert.Equal("0x0000ffffefcba618", argvAddress);
    }

    [Fact]
    public void FindMainFrame_WithCorehostMain_ExtractsArgcAndArgv()
    {
        // Arrange
        var backtraceOutput = @"
    frame #42: corehost_main(argc=5, argv=0x00007ffd12345678) at hostpolicy.cpp:426
    frame #43: some_other_function
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert
        Assert.Equal(5, argc);
        Assert.Equal("0x00007ffd12345678", argvAddress);
    }

    [Fact]
    public void FindMainFrame_WithHostfxrMain_ExtractsArgcAndArgv()
    {
        // Arrange
        var backtraceOutput = @"
    frame #10: hostfxr_main(argc=3, argv=0x00007fffffffe000) at hostfxr.cpp:100
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert
        Assert.Equal(3, argc);
        Assert.Equal("0x00007fffffffe000", argvAddress);
    }

    [Fact]
    public void FindMainFrame_WithExeStart_ExtractsArgcAndArgv()
    {
        // Arrange
        var backtraceOutput = @"
    frame #47: dotnet`exe_start(argc=10, argv=0x00007fff11111111) at corehost.cpp:254:18
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert
        Assert.Equal(10, argc);
        Assert.Equal("0x00007fff11111111", argvAddress);
    }

    [Fact]
    public void FindMainFrame_WithGenericMain_ExtractsArgcAndArgv()
    {
        // Arrange
        var backtraceOutput = @"
    frame #5: 0x12345678 myapp`main(argc=1, argv=0x7ffd00001234) at main.c:10
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert
        Assert.Equal(1, argc);
        Assert.Equal("0x7ffd00001234", argvAddress);
    }

    [Fact]
    public void FindMainFrame_WithUnavailableParameters_ReturnsNull()
    {
        // Arrange
        var backtraceOutput = @"
    frame #48: dotnet`main(argc=<unavailable>, argv=<unavailable>) at corehost.cpp:322:21 [opt]
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert - should fallback and try to find argv only pattern, which won't match <unavailable>
        Assert.Null(argc);
        Assert.Null(argvAddress);
    }

    [Fact]
    public void FindMainFrame_NoMainFound_ReturnsNull()
    {
        // Arrange
        var backtraceOutput = @"
    frame #0: 0x12345678 libc.so`abort
    frame #1: 0x87654321 libcoreclr.so`some_function
";

        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame(backtraceOutput);

        // Assert
        Assert.Null(argc);
        Assert.Null(argvAddress);
    }

    [Fact]
    public void FindMainFrame_EmptyOutput_ReturnsNull()
    {
        // Act
        var (argc, argvAddress) = _extractor.FindMainFrame("");

        // Assert
        Assert.Null(argc);
        Assert.Null(argvAddress);
    }



    [Fact]
    public void ParseMemoryBlock_64Bit_ValidOutput_ExtractsPointers()
    {
        // Arrange
        var memoryOutput = @"
0xffffefcba618: 0x0000ffffefcbbb24 0x0000ffffefcbbb2b
0xffffefcba628: 0x0000000000000000 0x0000ffffefcbbb8f
0xffffefcba638: 0x0000ffffefcbbb9c 0x0000ffffefcbbbc0
";

        // Act
        var pointers = _extractor.ParseMemoryBlock(memoryOutput, 64);

        // Assert
        Assert.Equal(6, pointers.Count);
        Assert.Equal("0x0000ffffefcbbb24", pointers[0]);
        Assert.Equal("0x0000ffffefcbbb2b", pointers[1]);
        Assert.Equal("0x0000000000000000", pointers[2]);
        Assert.Equal("0x0000ffffefcbbb8f", pointers[3]);
    }

    [Fact]
    public void ParseMemoryBlock_32Bit_ValidOutput_ExtractsPointers()
    {
        // Arrange
        var memoryOutput = @"
0xbfffe000: 0x12345678 0x87654321
0xbfffe008: 0x00000000 0xdeadbeef
";

        // Act
        var pointers = _extractor.ParseMemoryBlock(memoryOutput, 32);

        // Assert
        Assert.Equal(4, pointers.Count);
        Assert.Equal("0x12345678", pointers[0]);
        Assert.Equal("0x87654321", pointers[1]);
        Assert.Equal("0x00000000", pointers[2]);
        Assert.Equal("0xdeadbeef", pointers[3]);
    }

    [Fact]
    public void ParseMemoryBlock_EmptyOutput_ReturnsEmptyList()
    {
        // Act
        var pointers = _extractor.ParseMemoryBlock("", 64);

        // Assert
        Assert.Empty(pointers);
    }

    [Fact]
    public void ParseMemoryBlock_NoValidPointers_ReturnsEmptyList()
    {
        // Arrange
        var memoryOutput = "No memory data available";

        // Act
        var pointers = _extractor.ParseMemoryBlock(memoryOutput, 64);

        // Assert
        Assert.Empty(pointers);
    }



    [Fact]
    public void SplitByNullSentinel_ValidPointers_SeparatesArgvEnvp()
    {
        // Arrange - simulates: argv[0], argv[1], NULL, envp[0], envp[1], NULL
        var pointers = new List<string>
        {
            "0x0000ffffefcbbb24", // argv[0]
            "0x0000ffffefcbbb2b", // argv[1]
            "0x0000000000000000", // NULL (end of argv)
            "0x0000ffffefcbbb8f", // envp[0]
            "0x0000ffffefcbbb9c", // envp[1]
            "0x0000000000000000"  // NULL (end of envp)
        };

        // Act
        var (argv, envp) = _extractor.SplitByNullSentinel(pointers, 64);

        // Assert
        Assert.Equal(2, argv.Count);
        Assert.Equal(2, envp.Count);
        Assert.Equal("0x0000ffffefcbbb24", argv[0]);
        Assert.Equal("0x0000ffffefcbbb2b", argv[1]);
        Assert.Equal("0x0000ffffefcbbb8f", envp[0]);
        Assert.Equal("0x0000ffffefcbbb9c", envp[1]);
    }

    [Fact]
    public void SplitByNullSentinel_OnlyArgv_ReturnsEmptyEnvp()
    {
        // Arrange - only argv, no envp
        var pointers = new List<string>
        {
            "0x0000ffffefcbbb24",
            "0x0000000000000000" // NULL (end of argv, no envp follows)
        };

        // Act
        var (argv, envp) = _extractor.SplitByNullSentinel(pointers, 64);

        // Assert
        Assert.Single(argv);
        Assert.Empty(envp);
    }

    [Fact]
    public void SplitByNullSentinel_SkipsInvalidPointers()
    {
        // Arrange - includes invalid pointers (too low, kernel space)
        var pointers = new List<string>
        {
            "0x0000ffffefcbbb24", // valid
            "0x0000000000000100", // too low, skip
            "0x0000ffffefcbbb2b", // valid
            "0x0000000000000000", // NULL
            "0xffff800000000000", // kernel space, skip (64-bit)
            "0x0000ffffefcbbb8f", // valid
            "0x0000000000000000"  // NULL
        };

        // Act
        var (argv, envp) = _extractor.SplitByNullSentinel(pointers, 64);

        // Assert
        Assert.Equal(2, argv.Count);
        Assert.Single(envp);
    }

    [Fact]
    public void SplitByNullSentinel_32Bit_HandlesCorrectly()
    {
        // Arrange
        var pointers = new List<string>
        {
            "0x12345678",
            "0x00000000", // NULL
            "0x87654321",
            "0x00000000"  // NULL
        };

        // Act
        var (argv, envp) = _extractor.SplitByNullSentinel(pointers, 32);

        // Assert
        Assert.Single(argv);
        Assert.Single(envp);
    }



    [Theory]
    [InlineData("0x0000000000000000", 64, true)]
    [InlineData("0x00000000", 32, true)]
    [InlineData("0x0", 64, true)]
    [InlineData("0x0", 32, true)]
    [InlineData("0x0000ffffefcbbb24", 64, false)]
    [InlineData("0x12345678", 32, false)]
    public void IsNullPointer_VariousInputs_ReturnsCorrectly(string ptr, int pointerSize, bool expected)
    {
        // Act
        var result = ProcessInfoExtractor.IsNullPointer(ptr, pointerSize);

        // Assert
        Assert.Equal(expected, result);
    }



    [Theory]
    [InlineData("0x0000000000000000", 64, false)] // NULL
    [InlineData("0x0000000000000100", 64, false)] // Too low
    [InlineData("0x0000ffffefcbbb24", 64, true)]  // Valid ARM64 user space
    [InlineData("0x00007ffd12345678", 64, true)]  // Valid x64 user space
    [InlineData("0xffff800000000000", 64, false)] // Kernel space (64-bit)
    [InlineData("0x0001000000000000", 64, false)] // Above user space limit
    [InlineData("0x00000000", 32, false)]         // NULL (32-bit)
    [InlineData("0x00000100", 32, false)]         // Too low (32-bit)
    [InlineData("0x12345678", 32, true)]          // Valid (32-bit)
    [InlineData("0x7fffffff", 32, true)]          // Valid high user space (32-bit)
    [InlineData("0xc0000000", 32, false)]         // Kernel space (32-bit)
    public void IsValidUserSpacePointer_VariousAddresses_ReturnsCorrectly(string ptr, int pointerSize, bool expected)
    {
        // Act
        var result = ProcessInfoExtractor.IsValidUserSpacePointer(ptr, pointerSize);

        // Assert
        Assert.Equal(expected, result);
    }



    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("hello\\nworld", "hello\nworld")]
    [InlineData("tab\\there", "tab\there")]
    [InlineData("carriage\\rreturn", "carriage\rreturn")]
    [InlineData("quote\\\"test", "quote\"test")]
    [InlineData("backslash\\\\path", "backslash\\path")]
    [InlineData("combined\\n\\t\\r", "combined\n\t\r")]
    [InlineData("null\\0char", "null\0char")]
    [InlineData("hex\\x41escape", "hexAescape")]      // \x41 = 'A'
    [InlineData("hex\\x00null", "hex\0null")]         // \x00 = null char
    [InlineData("hex\\x0anewline", "hex\nnewline")]   // \x0a = newline
    [InlineData("", "")]
    public void UnescapeString_HandlesEscapes(string input, string expected)
    {
        // Act
        var result = ProcessInfoExtractor.UnescapeString(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void UnescapeString_ComplexEscapeSequence_HandlesCorrectly()
    {
        // Arrange - path with backslash followed by 'n' (not a newline)
        var input = "C:\\\\Users\\\\name";

        // Act
        var result = ProcessInfoExtractor.UnescapeString(input);

        // Assert - should be C:\Users\name
        Assert.Equal("C:\\Users\\name", result);
    }



    [Fact]
    public async Task ExtractProcessInfoAsync_NonLLDB_ReturnsNull()
    {
        // Arrange
        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("WinDbg");

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_NoMainFrame_ReturnsNull()
    {
        // Arrange
        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #0: 0x12345678 libc.so`abort");

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_WithPlatformInfo_UsesCorrectPointerSize()
    {
        // Arrange
        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #48: dotnet`main(argc=2, argv=0x0000ffffefcba618)");
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read"))))
            .Returns(@"0xffffefcba618: 0x0000ffffefcbbb24 0x0000ffffefcbbb2b
0xffffefcba628: 0x0000000000000000 0x0000ffffefcbbb8f
0xffffefcba638: 0x0000000000000000");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcbbb24"))
            .Returns("(char *) $1 = 0x0000ffffefcbbb24 \"dotnet\"");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcbbb2b"))
            .Returns("(char *) $2 = 0x0000ffffefcbbb2b \"/app/MyApp.dll\"");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcbbb8f"))
            .Returns("(char *) $3 = 0x0000ffffefcbbb8f \"PATH=/usr/bin\"");

        var platformInfo = new PlatformInfo { PointerSize = 64 };

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Argc);
        Assert.Equal("0x0000ffffefcba618", result.ArgvAddress);
        Assert.Equal(2, result.Arguments.Count);
        Assert.Equal("dotnet", result.Arguments[0]);
        Assert.Equal("/app/MyApp.dll", result.Arguments[1]);
        Assert.Single(result.EnvironmentVariables);
        Assert.Equal("PATH=/usr/bin", result.EnvironmentVariables[0]);

        // Verify memory read used correct pointer size
        mockDebugger.Verify(d => d.ExecuteCommand(It.Is<string>(s => s.Contains("-s8"))), Times.Once);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_NullPlatformInfo_DefaultsTo64Bit()
    {
        // Arrange
        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #48: dotnet`main(argc=1, argv=0x0000ffffefcba618)");
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read"))))
            .Returns(@"0xffffefcba618: 0x0000ffffefcbbb24
0xffffefcba620: 0x0000000000000000
0xffffefcba628: 0x0000000000000000");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcbbb24"))
            .Returns("(char *) $1 = 0x0000ffffefcbbb24 \"dotnet\"");

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, null);

        // Assert
        Assert.NotNull(result);
        // Verify 64-bit (8 bytes) was used
        mockDebugger.Verify(d => d.ExecuteCommand(It.Is<string>(s => s.Contains("-s8"))), Times.Once);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_32Bit_UsesCorrectPointerSize()
    {
        // Arrange
        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #48: dotnet`main(argc=1, argv=0xbfffe100)");
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read"))))
            .Returns(@"0xbfffe100: 0x12345678
0xbfffe104: 0x00000000
0xbfffe108: 0x00000000");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x12345678"))
            .Returns("(char *) $1 = 0x12345678 \"dotnet\"");

        var platformInfo = new PlatformInfo { PointerSize = 32 };

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo);

        // Assert
        Assert.NotNull(result);
        // Verify 32-bit (4 bytes) was used
        mockDebugger.Verify(d => d.ExecuteCommand(It.Is<string>(s => s.Contains("-s4"))), Times.Once);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_UsesProvidedBacktraceOutput()
    {
        // Arrange
        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read"))))
            .Returns(@"0xffffefcba618: 0x0000ffffefcbbb24
0xffffefcba620: 0x0000000000000000
0xffffefcba628: 0x0000000000000000");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcbbb24"))
            .Returns("(char *) $1 = 0x0000ffffefcbbb24 \"dotnet\"");

        var preProvidedBacktrace = "frame #48: dotnet`main(argc=1, argv=0x0000ffffefcba618)";

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, null, preProvidedBacktrace);

        // Assert
        Assert.NotNull(result);
        // Should NOT have called bt all since we provided backtrace
        mockDebugger.Verify(d => d.ExecuteCommand("bt all"), Times.Never);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_NoMainFrame_StackScanFallback_CanExtractArgsAndEnv()
    {
        // Arrange
        var stackStart = 0x0000ffffefcb0000UL;
        var stackEnd = 0x0000ffffefcc0000UL;

        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #0: 0x12345678 libc.so`abort");

        mockDebugger.Setup(d => d.ExecuteCommand("memory region --all"))
            .Returns($"[0x{stackStart:X}-0x{stackEnd:X}) rw- [stack]");

        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -fx", StringComparison.Ordinal))))
            .Returns(
                $"0x{stackStart:X}: 0x0000ffffefcb1000 0x0000ffffefcb1010\n" +
                $"0x{stackStart + 0x10:X}: 0x0000000000000000 0x0000ffffefcb1100\n" +
                $"0x{stackStart + 0x20:X}: 0x0000ffffefcb1110 0x0000000000000000\n");

        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcb1000"))
            .Returns("(char *) $1 = 0x0000ffffefcb1000 \"dotnet\"");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcb1010"))
            .Returns("(char *) $2 = 0x0000ffffefcb1010 \"/app/MyApp.dll\"");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcb1100"))
            .Returns("(char *) $3 = 0x0000ffffefcb1100 \"DD_API_KEY=secret\"");
        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcb1110"))
            .Returns("(char *) $4 = 0x0000ffffefcb1110 \"PATH=/usr/bin\"");

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo: null, backtraceOutput: null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result!.Argc);
        Assert.Contains("dotnet", result.Arguments);
        Assert.Contains("/app/MyApp.dll", result.Arguments);
        Assert.True(result.SensitiveDataFiltered);
        Assert.Contains("DD_API_KEY=<redacted>", result.EnvironmentVariables);
        Assert.Contains("PATH=/usr/bin", result.EnvironmentVariables);

        mockDebugger.Verify(d => d.ExecuteCommand("memory region --all"), Times.Once);
        mockDebugger.Verify(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -fx", StringComparison.Ordinal))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_NoMainFrame_StringScanFallback_CanExtractArgsAndEnv()
    {
        // Arrange
        var stackStart = 0x0000ffffefcb0000UL;
        var stackEnd = 0x0000ffffefcc0000UL;

        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #0: 0x12345678 libc.so`abort");

        mockDebugger.Setup(d => d.ExecuteCommand("memory region --all"))
            .Returns($"[0x{stackStart:X}-0x{stackEnd:X}) rw- [stack]");

        // Pointer scan returns pointers, but we don't provide any readable argv[0] string for them,
        // so the extractor will fall back to the legacy string scan.
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -fx", StringComparison.Ordinal))))
            .Returns(
                $"0x{stackStart:X}: 0x0000ffffefcb2000 0x0000ffffefcb2010\n" +
                $"0x{stackStart + 0x10:X}: 0x0000000000000000 0x0000000000000000\n");

        // String scan memory read (no -fx).
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -c", StringComparison.Ordinal))))
            .Returns(
                "0x0000ffffefcbff00: " +
                "2f 61 70 70 2f 4d 79 41 70 70 2e 64 6c 6c 00 " + // /app/MyApp.dll\0
                "2d 2d 68 65 6c 70 00 " +                         // --help\0
                "44 44 5f 41 50 49 5f 4b 45 59 3d 73 65 63 72 65 74 00 " + // DD_API_KEY=secret\0
                "50 41 54 48 3d 2f 75 73 72 2f 62 69 6e 00 \n"); // PATH=/usr/bin\0 (note trailing space for regex)

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo: null, backtraceOutput: null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result!.Argc);
        Assert.Contains("/app/MyApp.dll", result.Arguments);
        Assert.Contains("--help", result.Arguments);
        Assert.True(result.SensitiveDataFiltered);
        Assert.Contains("DD_API_KEY=<redacted>", result.EnvironmentVariables);
        Assert.Contains("PATH=/usr/bin", result.EnvironmentVariables);

        mockDebugger.Verify(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -c", StringComparison.Ordinal))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_MainFrameWithNullArgv_FallsBackToStackScan()
    {
        // Arrange
        var stackStart = 0x0000ffffefcb0000UL;
        var stackEnd = 0x0000ffffefcc0000UL;

        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #48: dotnet`main(argc=1, argv=0x0000000000000000)");

        mockDebugger.Setup(d => d.ExecuteCommand("memory region --all"))
            .Returns($"[0x{stackStart:X}-0x{stackEnd:X}) rw- [stack]");

        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -fx", StringComparison.Ordinal))))
            .Returns(
                $"0x{stackStart:X}: 0x0000ffffefcb1000 0x0000000000000000\n" +
                $"0x{stackStart + 0x10:X}: 0x0000000000000000\n");

        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcb1000"))
            .Returns("(char *) $1 = 0x0000ffffefcb1000 \"dotnet\"");

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo: null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result!.Argc);
        Assert.Equal("dotnet", result.Arguments[0]);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_StringScan_IgnoresTooShortArguments()
    {
        // Arrange
        var stackStart = 0x0000ffffefcb0000UL;
        var stackEnd = 0x0000ffffefcc0000UL;

        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #0: 0x12345678 libc.so`abort");

        mockDebugger.Setup(d => d.ExecuteCommand("memory region --all"))
            .Returns($"[0x{stackStart:X}-0x{stackEnd:X}) rw- [stack]");

        // Pointer scan: no readable argv[0], triggers string scan fallback.
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -fx", StringComparison.Ordinal))))
            .Returns($"0x{stackStart:X}: 0x0000ffffefcb2000 0x0000ffffefcb2010\n0x{stackStart + 0x10:X}: 0x0000000000000000 0x0000000000000000\n");

        // String scan output includes a short "-v" argument which should be rejected.
        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s => s.StartsWith("memory read --force -c", StringComparison.Ordinal))))
            .Returns(
                "0x0000ffffefcbff00: " +
                "2f 61 70 70 2f 4d 79 41 70 70 2e 64 6c 6c 00 " + // /app/MyApp.dll\0
                "2d 76 00 " +                                       // -v\0
                "50 41 54 48 3d 2f 75 73 72 2f 62 69 6e 00 \n");   // PATH=/usr/bin\0

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo: null);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result!.Arguments);
        Assert.Equal("/app/MyApp.dll", result.Arguments[0]);
        Assert.Contains("PATH=/usr/bin", result.EnvironmentVariables);
    }

    [Fact]
    public async Task ExtractProcessInfoAsync_StackRegionWithoutAnnotation_ChoosesHighestRwCandidate()
    {
        // Arrange
        var lowStart = 0x0000000000400000UL;
        var lowEnd = 0x0000000000500000UL;
        var highStart = 0x0000ffffefcb0000UL;
        var highEnd = 0x0000ffffefcc0000UL;

        var mockDebugger = new Mock<IDebuggerManager>();
        mockDebugger.Setup(d => d.DebuggerType).Returns("LLDB");
        mockDebugger.Setup(d => d.ExecuteCommand("bt all"))
            .Returns("frame #0: 0x12345678 libc.so`abort");

        mockDebugger.Setup(d => d.ExecuteCommand("memory region --all"))
            .Returns($"[0x{lowStart:X}-0x{lowEnd:X}) rw-\n[0x{highStart:X}-0x{highEnd:X}) rw-\n");

        mockDebugger.Setup(d => d.ExecuteCommand(It.Is<string>(s =>
                s.StartsWith("memory read --force -fx", StringComparison.Ordinal) &&
                s.Contains($"0x{highStart:X}", StringComparison.OrdinalIgnoreCase))))
            .Returns(
                $"0x{highStart:X}: 0x0000ffffefcb1000 0x0000000000000000\n" +
                $"0x{highStart + 0x10:X}: 0x0000000000000000\n");

        mockDebugger.Setup(d => d.ExecuteCommand("expr -- (char*)0x0000ffffefcb1000"))
            .Returns("(char *) $1 = 0x0000ffffefcb1000 \"dotnet\"");

        // Act
        var result = await _extractor.ExtractProcessInfoAsync(mockDebugger.Object, platformInfo: null);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("dotnet", result!.Arguments);
    }



    [Theory]
    [InlineData("DD_API_KEY=abc123secret", "DD_API_KEY=<redacted>", true)]
    [InlineData("DD_LOGGER_DD_API_KEY=secret_api_key_value_here", "DD_LOGGER_DD_API_KEY=<redacted>", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY=mysecret", "AWS_SECRET_ACCESS_KEY=<redacted>", true)]
    [InlineData("DATABASE_PASSWORD=pass123", "DATABASE_PASSWORD=<redacted>", true)]
    [InlineData("MY_APP_TOKEN=token123", "MY_APP_TOKEN=<redacted>", true)]
    [InlineData("AUTH_TOKEN=bearer123", "AUTH_TOKEN=<redacted>", true)]
    [InlineData("PRIVATE_KEY=-----BEGIN RSA", "PRIVATE_KEY=<redacted>", true)]
    [InlineData("CONNECTION_STRING=Server=localhost;Password=secret", "CONNECTION_STRING=<redacted>", true)]
    [InlineData("JWT_TOKEN=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", "JWT_TOKEN=<redacted>", true)]
    [InlineData("STRIPE_SECRET_KEY=sk_live_xxx", "STRIPE_SECRET_KEY=<redacted>", true)]
    public void RedactSensitiveValue_RedactsSensitiveKeys(string input, string expectedOutput, bool expectedRedacted)
    {
        // Act
        var (result, wasRedacted) = ProcessInfoExtractor.RedactSensitiveValue(input);

        // Assert
        Assert.Equal(expectedOutput, result);
        Assert.Equal(expectedRedacted, wasRedacted);
    }

    [Theory]
    [InlineData("PATH=/usr/bin:/usr/local/bin", "PATH=/usr/bin:/usr/local/bin", false)]
    [InlineData("HOME=/root", "HOME=/root", false)]
    [InlineData("DOTNET_ROOT=/usr/share/dotnet", "DOTNET_ROOT=/usr/share/dotnet", false)]
    [InlineData("ASPNETCORE_ENVIRONMENT=Production", "ASPNETCORE_ENVIRONMENT=Production", false)]
    [InlineData("MY_APP_NAME=MyApplication", "MY_APP_NAME=MyApplication", false)]
    [InlineData("TOKENIZER_PATH=/app/models", "TOKENIZER_PATH=/app/models", false)] // Contains TOKEN but not as a key pattern
    [InlineData("KEY_COUNT=5", "KEY_COUNT=5", false)] // Contains KEY but not sensitive
    public void RedactSensitiveValue_DoesNotRedactNonSensitiveKeys(string input, string expectedOutput, bool expectedRedacted)
    {
        // Act
        var (result, wasRedacted) = ProcessInfoExtractor.RedactSensitiveValue(input);

        // Assert
        Assert.Equal(expectedOutput, result);
        Assert.Equal(expectedRedacted, wasRedacted);
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("NOEQUALS", "NOEQUALS", false)]
    [InlineData("=VALUE_ONLY", "=VALUE_ONLY", false)]
    public void RedactSensitiveValue_HandlesEdgeCases(string input, string expectedOutput, bool expectedRedacted)
    {
        // Act
        var (result, wasRedacted) = ProcessInfoExtractor.RedactSensitiveValue(input);

        // Assert
        Assert.Equal(expectedOutput, result);
        Assert.Equal(expectedRedacted, wasRedacted);
    }

    [Fact]
    public void RedactSensitiveValue_IsCaseInsensitive()
    {
        // Act & Assert - various case combinations should all be redacted
        var (result1, redacted1) = ProcessInfoExtractor.RedactSensitiveValue("api_key=secret");
        var (result2, redacted2) = ProcessInfoExtractor.RedactSensitiveValue("API_KEY=secret");
        var (result3, redacted3) = ProcessInfoExtractor.RedactSensitiveValue("Api_Key=secret");
        var (result4, redacted4) = ProcessInfoExtractor.RedactSensitiveValue("MY_API_KEY=secret");

        Assert.True(redacted1);
        Assert.True(redacted2);
        Assert.True(redacted3);
        Assert.True(redacted4);
        Assert.Equal("api_key=<redacted>", result1);
        Assert.Equal("API_KEY=<redacted>", result2);
        Assert.Equal("Api_Key=<redacted>", result3);
        Assert.Equal("MY_API_KEY=<redacted>", result4);
    }

    [Fact]
    public void RedactSensitiveValue_PreservesKeyName()
    {
        // Arrange
        var envVar = "VERY_LONG_SECRET_KEY_NAME_HERE=some_secret_value_12345";

        // Act
        var (result, wasRedacted) = ProcessInfoExtractor.RedactSensitiveValue(envVar);

        // Assert
        Assert.True(wasRedacted);
        Assert.Equal("VERY_LONG_SECRET_KEY_NAME_HERE=<redacted>", result);
        Assert.DoesNotContain("some_secret_value", result);
    }

}
