using System;
using System.Collections.Generic;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Unit tests for deterministic pure helper functions inside <see cref="DotNetCrashAnalyzer"/>.
/// These tests avoid any debugger or dump dependencies.
/// </summary>
public class DotNetCrashAnalyzerPureHelpersTests
{
    [Theory]
    [InlineData("0x1234", "1234")]
    [InlineData("1234", "1234")]
    [InlineData("0x1234 (System.String)", "1234")]
    [InlineData("  0xABCDEF  (Foo.Bar)", "ABCDEF")]
    public void ExtractHexAddress_WithValidFormats_ReturnsHexDigits(string input, string expected)
    {
        Assert.Equal(expected, DotNetCrashAnalyzer.ExtractHexAddress(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0x")]
    [InlineData("0xZZZ")]
    [InlineData("hello")]
    public void ExtractHexAddress_WithInvalidFormats_ReturnsNull(string? input)
    {
        Assert.Null(DotNetCrashAnalyzer.ExtractHexAddress(input));
    }

    [Theory]
    [InlineData("abcd", true)]
    [InlineData("ABCDEF", true)]
    [InlineData("1234", true)]
    [InlineData("", false)]
    [InlineData("0x1234", false)]
    [InlineData("123G", false)]
    public void IsValidHexString_ValidatesCorrectly(string input, bool expected)
    {
        Assert.Equal(expected, DotNetCrashAnalyzer.IsValidHexString(input));
    }

    [Theory]
    [InlineData("0xABCD", "abcd")]
    [InlineData("ABCD", "abcd")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeAddress_NormalizesAsExpected(string? input, string expected)
    {
        Assert.Equal(expected, DotNetCrashAnalyzer.NormalizeAddress(input));
    }

    [Theory]
    [InlineData("80131513", "0x80131513")]
    [InlineData("0x80131513", "0x80131513")]
    [InlineData("00000001", "0x00000001")]
    [InlineData("0", "0x00000000")]
    [InlineData(null, null)]
    public void FormatHResult_FormatsWith0xAndPadding(string? input, string? expected)
    {
        Assert.Equal(expected, DotNetCrashAnalyzer.FormatHResult(input));
    }

    [Fact]
    public void IsSosErrorOutput_RecognizesSosNotLoaded()
    {
        Assert.True(DotNetCrashAnalyzer.IsSosErrorOutput("SOS is not loaded"));
    }

    [Fact]
    public void IsSosErrorOutput_DoesNotFalsePositiveOnMissingMethodExceptionMessage()
    {
        Assert.False(DotNetCrashAnalyzer.IsSosErrorOutput("Method not found: 'System.Void Foo.Bar()'"));
    }

    [Theory]
    [InlineData("System.String", "System")]
    [InlineData("MyNamespace.MyType", "MyNamespace")]
    [InlineData("NoDots", null)]
    [InlineData(null, null)]
    public void ExtractModuleName_ExtractsNamespace(string? typeName, string? expected)
    {
        Assert.Equal(expected, DotNetCrashAnalyzer.ExtractModuleName(typeName));
    }

    [Fact]
    public void ParseStackPointer_ParsesCommonFormats()
    {
        Assert.Equal((ulong)0x1234, DotNetCrashAnalyzer.ParseStackPointer("0x1234"));
        Assert.Equal((ulong)0x1234, DotNetCrashAnalyzer.ParseStackPointer("[SP=0x1234]"));
        Assert.Equal((ulong)0x1234, DotNetCrashAnalyzer.ParseStackPointer("0000000000001234"));
        Assert.Null(DotNetCrashAnalyzer.ParseStackPointer("not-a-sp"));
    }

    [Fact]
    public void ParseRegisterOutput_ExtractsRegistersAndStrips0x()
    {
        var output = "rax = 0x0000000000001234\nrbx = 0xABCDEF";
        var regs = DotNetCrashAnalyzer.ParseRegisterOutput(output);

        Assert.Equal("0000000000001234", regs["rax"]);
        Assert.Equal("ABCDEF", regs["rbx"]);
    }

    [Fact]
    public void ParseBacktraceForSPs_ExtractsFrameIndexAndSp()
    {
        var bt = "* frame #0: 0x0000 foo SP=0x0000000000001111\n  frame #1: 0x0000 bar SP=0x2222";
        var frames = DotNetCrashAnalyzer.ParseBacktraceForSPs(bt);

        Assert.Equal(2, frames.Count);
        Assert.Equal((0, (ulong)0x1111), frames[0]);
        Assert.Equal((1, (ulong)0x2222), frames[1]);
    }

    [Fact]
    public void ParseExceptionForTypeAndMember_MethodNotFound_ExtractsTypeAndMemberAndSignature()
    {
        var msg = "Method not found: 'System.Void MyNamespace.MyType.Method(System.Int32)'";
        var (typeName, member, signature) = DotNetCrashAnalyzer.ParseExceptionForTypeAndMember("System.MissingMethodException", msg);

        Assert.Equal("MyNamespace.MyType", typeName);
        Assert.Equal("Method", member);
        Assert.Equal("System.Void Method(System.Int32)", signature);
    }

    [Fact]
    public void ParseExceptionForTypeAndMember_FieldNotFound_ExtractsTypeAndField()
    {
        var msg = "Field not found: 'MyNamespace.MyType.MyField'";
        var (typeName, member, signature) = DotNetCrashAnalyzer.ParseExceptionForTypeAndMember("System.MissingFieldException", msg);

        Assert.Equal("MyNamespace.MyType", typeName);
        Assert.Equal("MyField", member);
        Assert.Equal("MyField", signature);
    }

    [Fact]
    public void ParseMethodDescriptors_ParsesDumpmtOutput()
    {
        var output = @"
MethodDesc Table
   Entry       MethodDesc    JIT     Name
00007FFD00000001 00007FFD00000002 PreJIT 0000000000000000 System.Object.ToString()
00007FFD00000003 00007FFD00000004 NONE  0000000000000001 System.Collections.Generic.List`1.Add(!0)
";

        var methods = DotNetCrashAnalyzer.ParseMethodDescriptors(output);

        Assert.Equal(2, methods.Count);
        Assert.Equal("ToString", methods[0].Name);
        Assert.Equal(0, methods[0].Slot);
        Assert.Equal("Add", methods[1].Name);
        Assert.Equal(1, methods[1].Slot);
    }

    [Fact]
    public void GenerateResolutionDiagnosis_UsesFieldWhenExceptionIsMissingField()
    {
        var diagnosis = DotNetCrashAnalyzer.GenerateResolutionDiagnosis(
            memberName: "MyField",
            exactMatch: false,
            similarCount: 0,
            totalMethods: 10,
            exceptionType: "System.MissingFieldException");

        Assert.Contains("Field", diagnosis);
        Assert.Contains("MyField", diagnosis);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("clrjit", true)]
    [InlineData("libclrjit", true)]
    public void HasJitCompiler_ReturnsTrueWhenUnknownOrPresent(string modulesOutput, bool expected)
    {
        Assert.Equal(expected, DotNetCrashAnalyzer.HasJitCompiler(modulesOutput));
    }

    [Fact]
    public void HasJitCompiler_ReturnsFalseWhenMissing()
    {
        Assert.False(DotNetCrashAnalyzer.HasJitCompiler("System.Private.CoreLib"));
    }

    [Fact]
    public void GenerateTrimmingRecommendation_IncludesGeneralAdvice()
    {
        var rec = DotNetCrashAnalyzer.GenerateTrimmingRecommendation("Foo.Bar()", "MissingMethodException");
        Assert.Contains("DynamicDependency", rec);
        Assert.Contains("trimming warnings", rec, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectReflectionUsage_FindsKnownPatternAcrossThreadsAndException()
    {
        var result = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "1",
                        CallStack = new List<StackFrame>
                        {
                            new() { Module = "MyApp", Function = "System.Type.GetMethod" }
                        }
                    }
                }
            },
            Exception = new ExceptionDetails
            {
                Type = "System.Exception",
                StackTrace = new List<StackFrame>
                {
                    new() { Module = "MyApp", Function = "Activator.CreateInstance" }
                }
            }
        };

        var usage = DotNetCrashAnalyzer.DetectReflectionUsage(result);

        Assert.NotEmpty(usage);
        Assert.Contains(usage, u => u.Target == "System.Type.GetMethod");
        Assert.Contains(usage, u => u.Target == "Activator.CreateInstance");
    }

    [Fact]
    public void ParseGenericInstantiation_ForNonGenericType_ReturnsNull()
    {
        Assert.Null(DotNetCrashAnalyzer.ParseGenericInstantiation("System.String", ""));
    }

    [Fact]
    public void ParseGenericInstantiation_ExtractsDefinitionAndArguments()
    {
        var typeName = "System.Collections.Generic.Dictionary`2";
        var dumpmt = "[[System.String, System.Private.CoreLib],[System.Int32, System.Private.CoreLib]]";

        var info = DotNetCrashAnalyzer.ParseGenericInstantiation(typeName, dumpmt);

        Assert.NotNull(info);
        Assert.True(info!.IsGenericType);
        Assert.Equal("Dictionary`2", info.TypeDefinition);
        Assert.Equal(new[] { "System.String", "System.Int32" }, info.TypeArguments);
    }

    [Fact]
    public void ParseGenericInstantiation_WhenArgumentsMissing_FillsWithUnknownTypeParameters()
    {
        var info = DotNetCrashAnalyzer.ParseGenericInstantiation("MyNamespace.MyType`3", dumpmtOutput: "");

        Assert.NotNull(info);
        Assert.Equal("MyType`3", info!.TypeDefinition);
        Assert.Equal(new[] { "T0", "T1", "T2" }, info.TypeArguments);
    }

    [Fact]
    public void DetectNativeAotFromModules_FindsPatternAndAbsenceIndicator()
    {
        var modules = "System.Private.CoreLib.Native\nRuntime.WorkstationGC\n";
        var indicators = DotNetCrashAnalyzer.DetectNativeAotFromModules(modules);

        Assert.Contains(indicators, i => i.Pattern == "System.Private.CoreLib.Native");
        Assert.Contains(indicators, i => i.Pattern == "Runtime.WorkstationGC");
        Assert.Contains(indicators, i => i.Source == "module:absence");
    }

    [Fact]
    public void DetectNativeAotFromStackFrames_FindsMostSpecificPatternOnce()
    {
        var result = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "1",
                        CallStack = new List<StackFrame>
                        {
                            new() { Module = "MyApp", Function = "RhpNewArray" },
                            new() { Module = "MyApp", Function = "RhpAssignRef" }
                        }
                    }
                }
            }
        };

        var indicators = DotNetCrashAnalyzer.DetectNativeAotFromStackFrames(result);

        Assert.Contains(indicators, i => i.Pattern == "RhpNewArray");
        Assert.Contains(indicators, i => i.Pattern == "RhpAssignRef");
    }

    [Fact]
    public void FormatClrMdAsDumpObj_FormatsStringsArraysAndFields()
    {
        var stringObj = new ClrMdObjectInspection
        {
            Type = "System.String",
            MethodTable = "0x0000000000000001",
            Size = 24,
            IsString = true,
            Value = "hello"
        };

        var stringFormatted = DotNetCrashAnalyzer.FormatClrMdAsDumpObj(stringObj);
        Assert.Contains("Name:", stringFormatted);
        Assert.Contains("System.String", stringFormatted);
        Assert.Contains("String:", stringFormatted);
        Assert.Contains("hello", stringFormatted);

        var arrayObj = new ClrMdObjectInspection
        {
            Type = "System.Int32[]",
            MethodTable = "0x0000000000000002",
            Size = 64,
            IsArray = true,
            ArrayLength = 3,
            ArrayElementType = "System.Int32"
        };

        var arrayFormatted = DotNetCrashAnalyzer.FormatClrMdAsDumpObj(arrayObj);
        Assert.Contains("Array:", arrayFormatted);
        Assert.Contains("Number of elements 3", arrayFormatted);

        var fieldsObj = new ClrMdObjectInspection
        {
            Type = "MyNamespace.MyType",
            MethodTable = "0x0000000000000003",
            Size = 48,
            Fields = new List<ClrMdFieldInspection>
            {
                new() { Name = "_count", Offset = 0x10, Type = "System.Int32", Value = 42 },
                new() { Name = "_child", Offset = 0x18, Type = "MyNamespace.Child", NestedObject = new ClrMdObjectInspection { Address = "0xDEAD" } }
            }
        };

        var fieldsFormatted = DotNetCrashAnalyzer.FormatClrMdAsDumpObj(fieldsObj);
        Assert.Contains("Fields:", fieldsFormatted);
        Assert.Contains("_count", fieldsFormatted);
        Assert.Contains("42", fieldsFormatted);
        Assert.Contains("_child", fieldsFormatted);
        Assert.Contains("0xDEAD", fieldsFormatted);
    }
}
