using DebuggerMcp.ObjectInspection;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for <see cref="PrimitiveResolver"/>.
/// </summary>
public class PrimitiveResolverTests
{
    [Theory]
    [InlineData("System.Int32", true)]
    [InlineData("Int32", true)]
    [InlineData("System.Guid", true)]
    [InlineData("System.String", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPrimitiveType_KnownValues_ReturnsExpected(string? typeName, bool expected)
    {
        Assert.Equal(expected, PrimitiveResolver.IsPrimitiveType(typeName!));
    }

    [Theory]
    [InlineData("System.Bool...", true)]
    [InlineData("System.DateT...", true)]
    [InlineData("System.Str...", false)]
    public void IsPrimitiveType_TruncatedNames_ReturnsExpected(string typeName, bool expected)
    {
        Assert.Equal(expected, PrimitiveResolver.IsPrimitiveType(typeName));
    }

    [Theory]
    [InlineData("System.Int32", "123", 123)]
    [InlineData("System.Int32", "7B", 123)]
    [InlineData("System.UInt32", "7B", 123u)]
    [InlineData("System.Int16", "7B", (short)123)]
    [InlineData("System.UInt16", "7B", (ushort)123)]
    [InlineData("System.Int64", "7B", 123L)]
    [InlineData("System.UInt64", "7B", 123UL)]
    [InlineData("System.Byte", "7B", (byte)123)]
    public void ResolvePrimitiveValue_HexAndDecimalNumbers_ReturnsNumericValue(string typeName, string rawValue, object expected)
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue(typeName, rawValue);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("System.Boolean", "1", true)]
    [InlineData("System.Boolean", "0", false)]
    [InlineData("System.Boolean", "true", true)]
    [InlineData("System.Boolean", "false", false)]
    public void ResolvePrimitiveValue_BooleanValues_ReturnsBool(string typeName, string rawValue, bool expected)
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue(typeName, rawValue);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("(null)", true)]
    [InlineData("0", true)]
    [InlineData("0x0", true)]
    [InlineData("0000", true)]
    [InlineData("0x000000", true)]
    [InlineData("0x123", false)]
    [InlineData("123", false)]
    public void IsNullAddress_CommonForms_ReturnsExpected(string address, bool expected)
    {
        Assert.Equal(expected, PrimitiveResolver.IsNullAddress(address));
    }

    [Theory]
    [InlineData("0x0000000000000000", "0")]
    [InlineData("000000001234", "1234")]
    [InlineData("0x000000001234", "1234")]
    [InlineData("0x1234 (System.String)", "1234")]
    [InlineData("1234 (System.String)", "1234")]
    [InlineData("1234 System.String", "1234")]
    public void NormalizeAddress_SupportedFormats_ReturnsNormalized(string input, string expected)
    {
        Assert.Equal(expected, PrimitiveResolver.NormalizeAddress(input));
    }
}

