using DebuggerMcp.ObjectInspection;

namespace DebuggerMcp.Tests.ObjectInspection;

public class DumpVcParserTests
{
    [Fact]
    public void Parse_EmptyOutput_ReturnsError()
    {
        var result = DumpVcParser.Parse(string.Empty);

        Assert.False(result.Success);
        Assert.True(result.IsValueType);
        Assert.Equal("Empty output", result.ErrorMessage);
    }

    [Theory]
    [InlineData("Usage: dumpvc")]
    [InlineData("Invalid address")]
    [InlineData("Error: something")]
    public void Parse_ErrorOutput_ReturnsError(string output)
    {
        var result = DumpVcParser.Parse(output);

        Assert.False(result.Success);
        Assert.True(result.IsValueType);
        Assert.Equal("Invalid value type or address", result.ErrorMessage);
    }

    [Fact]
    public void Parse_ValidOutput_ParsesNameMethodTableSizeFileAndFields()
    {
        var output = string.Join("\n", new[]
        {
            "Name: MyNamespace.MyStruct",
            "MethodTable: 00007ff9b4d21000",
            "Size: 16(0x10) bytes",
            "File: /tmp/MyAssembly.dll",
            "Fields:",
            "MT               Field            Offset                 Type VT     Attr            Value Name",
            "00007ff9b4d21000 4000001          0008        System.Int32  1 instance              123 _x",
            "00007ff9b4d21000 4000002          0010        System.Int32  1 TLstatic          _threadLocal"
        });

        var result = DumpVcParser.Parse(output);

        Assert.True(result.Success);
        Assert.True(result.IsValueType);
        Assert.Equal("MyNamespace.MyStruct", result.Name);
        Assert.Equal("00007ff9b4d21000", result.MethodTable);
        Assert.Equal(16, result.Size);
        Assert.Equal("/tmp/MyAssembly.dll", result.File);

        Assert.NotNull(result.Fields);
        Assert.Equal(2, result.Fields.Count);

        Assert.Equal("_x", result.Fields[0].Name);
        Assert.Equal("123", result.Fields[0].Value);
        Assert.False(result.Fields[0].IsStatic);

        Assert.Equal("_threadLocal", result.Fields[1].Name);
        Assert.Equal(string.Empty, result.Fields[1].Value);
        Assert.True(result.Fields[1].IsStatic);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Usage: dumpvc", true)]
    [InlineData("Invalid MethodTable", true)]
    [InlineData("Invalid address", true)]
    [InlineData("MyType is not a value class", true)]
    [InlineData("Error: foo", true)]
    [InlineData("Error: MethodTable 00007ff...", false)]
    [InlineData("Name: System.InvalidOperationException\nMethodTable: 00007ff...", false)]
    public void IsFailedOutput_DetectsFailureWithoutFalsePositives(string output, bool expected)
    {
        Assert.Equal(expected, DumpVcParser.IsFailedOutput(output));
    }
}

