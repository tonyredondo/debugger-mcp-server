using System.Linq;
using DebuggerMcp.ObjectInspection;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for <see cref="DumpObjParser"/>.
/// </summary>
public class DumpObjParserTests
{
    [Fact]
    public void Parse_WhenOutputEmpty_ReturnsErrorMessage()
    {
        var result = DumpObjParser.Parse(string.Empty);

        Assert.False(result.Success);
        Assert.Equal("Empty output", result.ErrorMessage);
    }

    [Theory]
    [InlineData("Invalid object address")]
    [InlineData("not a valid object")]
    [InlineData("<Note: this object has an invalid CLASS field>")]
    [InlineData("Error: something happened")]
    public void Parse_WhenOutputContainsErrorMarkers_ReturnsInvalidObjectError(string output)
    {
        var result = DumpObjParser.Parse(output);

        Assert.False(result.Success);
        Assert.Equal("Invalid object or address", result.ErrorMessage);
    }

    [Fact]
    public void Parse_WhenNameMissing_ReturnsParseError()
    {
        var output = """
        MethodTable: 00007fff12345678
        Size: 24(0x18) bytes
        """;

        var result = DumpObjParser.Parse(output);

        Assert.False(result.Success);
        Assert.Equal("Could not parse object name", result.ErrorMessage);
    }

    [Fact]
    public void Parse_WhenValidStringObject_ParsesFieldsAndStringValue()
    {
        var output = """
        Name: System.String
        MethodTable: 00007fff12345678
        Canonical MethodTable: 00007fff12345678
        Size: 24(0x18) bytes
        File: /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Private.CoreLib.dll
        String: hello world

        Fields:
        MT               Field   Offset                 Type VT     Attr            Value Name
        00007fff12345678 4000001 00000008 System.Int32 0 instance 000000000000000b _stringLength
        0000000000000000 4000c1d 00000010 SZARRAY 0 TLstatic t_safeWaitHandlesForRent
        """;

        var result = DumpObjParser.Parse(output);

        Assert.True(result.Success);
        Assert.Equal("System.String", result.Name);
        Assert.Equal("00007fff12345678", result.MethodTable);
        Assert.Equal("00007fff12345678", result.CanonicalMethodTable);
        Assert.Equal(24, result.Size);
        Assert.Equal("/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Private.CoreLib.dll", result.File);
        Assert.Equal("hello world", result.StringValue);

        Assert.NotNull(result.Fields);
        Assert.Equal(2, result.Fields.Count);

        var instanceField = result.Fields.Single(f => f.Name == "_stringLength");
        Assert.False(instanceField.IsStatic);
        Assert.Equal("System.Int32", instanceField.Type);
        Assert.Equal("000000000000000b", instanceField.Value);
        Assert.Equal(0x8, instanceField.Offset);

        var tlStaticField = result.Fields.Single(f => f.Name == "t_safeWaitHandlesForRent");
        Assert.True(tlStaticField.IsStatic);
        Assert.Equal(string.Empty, tlStaticField.Value);
    }

    [Fact]
    public void Parse_WhenArrayOutputPresent_ParsesArrayMetadata()
    {
        var output = """
        Name: System.String[]
        MethodTable: 00007fff12340000
        Size: 48(0x30) bytes
        Array: Rank 1, Number of elements 10, Type System.String
        Element Methodtable: 00007fff12345678
        Fields:
        None
        """;

        var result = DumpObjParser.Parse(output);

        Assert.True(result.Success);
        Assert.True(result.IsArray);
        Assert.Equal(10, result.ArrayLength);
        Assert.Equal("System.String", result.ArrayElementType);
        Assert.Equal("00007fff12345678", result.ArrayElementMethodTable);
        Assert.Empty(result.Fields);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Invalid object address", true)]
    [InlineData("is not a managed object", true)]
    [InlineData("Error: something", true)]
    [InlineData("Error: something\nMethodTable: 00007fff12345678", false)]
    public void IsFailedOutput_IdentifiesCommonFailureForms(string? output, bool expected)
    {
        var result = DumpObjParser.IsFailedOutput(output!);
        Assert.Equal(expected, result);
    }
}
