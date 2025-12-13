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
    [InlineData("System.Single", "3.5", 3.5f)]
    [InlineData("System.Double", "2.25", 2.25d)]
    public void ResolvePrimitiveValue_FloatingPointAndDecimal_ReturnsNumeric(string typeName, string rawValue, object expected)
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue(typeName, rawValue);

        if (expected is float expectedFloat)
        {
            Assert.IsType<float>(result);
            Assert.Equal(expectedFloat, (float)result!, precision: 3);
        }
        else if (expected is double expectedDouble)
        {
            Assert.IsType<double>(result);
            Assert.Equal(expectedDouble, (double)result!, precision: 3);
        }
        else if (expected is decimal expectedDecimal)
        {
            Assert.IsType<decimal>(result);
            Assert.Equal(expectedDecimal, (decimal)result!);
        }
        else
        {
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void ResolvePrimitiveValue_Decimal_ReturnsDecimal()
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue("System.Decimal", "1234.56");

        Assert.IsType<decimal>(result);
        Assert.Equal(1234.56m, (decimal)result!);
    }

    [Theory]
    [InlineData("System.Char", "A", "A")]
    [InlineData("System.Char", "65", "A")]
    [InlineData("System.Char", "999999", "999999")]
    public void ResolvePrimitiveValue_CharValues_ReturnsExpected(string typeName, string rawValue, string expected)
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue(typeName, rawValue);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("System.IntPtr", "0000", "0x0")]
    [InlineData("System.IntPtr", "7b", "0x7b")]
    [InlineData("System.UIntPtr", "000000001234", "0x1234")]
    public void ResolvePrimitiveValue_PointerTypes_ReturnsHexFormatted(string typeName, string rawValue, string expected)
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue(typeName, rawValue);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePrimitiveValue_DateTimeTicks_ReturnsIsoString()
    {
        var dt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var result = PrimitiveResolver.ResolvePrimitiveValue("System.DateTime", dt.Ticks.ToString());

        Assert.Equal(dt.ToString("o"), result);
    }

    [Fact]
    public void ResolvePrimitiveValue_TimeSpanTicks_ReturnsToString()
    {
        var ts = TimeSpan.FromMilliseconds(1234);
        var result = PrimitiveResolver.ResolvePrimitiveValue("System.TimeSpan", ts.Ticks.ToString());

        Assert.Equal(ts.ToString(), result);
    }

    [Theory]
    [InlineData("d2719b9c-0900-4ce4-a4d7-9a3a8411f79a")]
    [InlineData("d2719b9c09004ce4a4d79a3a8411f79a")]
    public void ResolvePrimitiveValue_GuidValues_ReturnsGuidString(string rawValue)
    {
        var result = PrimitiveResolver.ResolvePrimitiveValue("System.Guid", rawValue);

        Assert.True(Guid.TryParse(result?.ToString(), out _));
    }

    [Fact]
    public void ResolvePrimitiveValue_WhenRawValueEmpty_ReturnsNull()
    {
        Assert.Null(PrimitiveResolver.ResolvePrimitiveValue("System.Int32", ""));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", true, false)]
    [InlineData("System.Int32", true, false)]
    [InlineData("System.Enum", true, true)]
    [InlineData("MyNamespace.MyEnum", true, true)]
    [InlineData("System.Nullable`1", true, false)]
    [InlineData("System.ValueTuple`2", true, false)]
    public void IsPotentialEnumType_CommonCases_ReturnsExpected(string? typeName, bool isValueType, bool expected)
    {
        Assert.Equal(expected, PrimitiveResolver.IsPotentialEnumType(typeName!, isValueType));
    }

    [Fact]
    public void FormatEnumValue_WithName_FormatsAsNameAndNumeric()
    {
        var formatted = PrimitiveResolver.FormatEnumValue("MyEnum", "2", enumName: "Two");
        Assert.Equal("Two (2)", formatted);
    }

    [Fact]
    public void FormatEnumValue_WithNumericValue_ReturnsTypedDictionary()
    {
        var formatted = PrimitiveResolver.FormatEnumValue("MyEnum", "42", enumName: null);

        var dict = Assert.IsType<Dictionary<string, object>>(formatted);
        Assert.Equal(42L, dict["_value"]);
        Assert.Equal("MyEnum", dict["_type"]);
    }

    [Fact]
    public void FormatEnumValue_WithNonNumericValue_ReturnsRawValue()
    {
        var formatted = PrimitiveResolver.FormatEnumValue("MyEnum", "not-a-number", enumName: null);
        Assert.Equal("not-a-number", formatted);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeAddress_WhenNullOrEmpty_ReturnsInput(string? input)
    {
        Assert.Equal(input, PrimitiveResolver.NormalizeAddress(input!));
    }
}
