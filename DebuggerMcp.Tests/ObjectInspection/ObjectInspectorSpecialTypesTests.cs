using DebuggerMcp.ObjectInspection;
using DebuggerMcp.ObjectInspection.Models;
using DebuggerMcp.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for special-type formatting in <see cref="ObjectInspector"/>.
/// </summary>
public class ObjectInspectorSpecialTypesTests
{
    [Fact]
    public async Task InspectAsync_Guid_SetsFormattedValue()
    {
        ObjectInspector.ClearCache();

        var expected = new Guid(0x00112233, 0x4455, 0x6677, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff).ToString();

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        System.Guid
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        16(0x10) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd0003 4000001 00000000        System.Int32  1 instance 1122867 _a
00007ff9abcd0003 4000002 00000004        System.Int16  1 instance 17493 _b
00007ff9abcd0003 4000003 00000006        System.Int16  1 instance 26231 _c
00007ff9abcd0003 4000004 00000008         System.Byte  1 instance 136 _d
00007ff9abcd0003 4000005 00000009         System.Byte  1 instance 153 _e
00007ff9abcd0003 4000006 0000000a         System.Byte  1 instance 170 _f
00007ff9abcd0003 4000007 0000000b         System.Byte  1 instance 187 _g
00007ff9abcd0003 4000008 0000000c         System.Byte  1 instance 204 _h
00007ff9abcd0003 4000009 0000000d         System.Byte  1 instance 221 _i
00007ff9abcd0003 400000a 0000000e         System.Byte  1 instance 238 _j
00007ff9abcd0003 400000b 0000000f         System.Byte  1 instance 255 _k
""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x1111");

        Assert.NotNull(inspected);
        Assert.Equal("System.Guid", inspected.Type);
        Assert.Equal(expected, inspected.FormattedValue);
    }

    [Fact]
    public async Task InspectAsync_TimeSpan_SetsFormattedValue()
    {
        ObjectInspector.ClearCache();

        var expected = new TimeSpan(0, 1, 2, 3, 456).ToString(@"h\:mm\:ss\.fff");
        var ticks = new TimeSpan(0, 1, 2, 3, 456).Ticks;

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
                {
                    return $"""
Name:        System.TimeSpan
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        8(0x8) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd0003 4000001 00000000        System.Int64  1 instance {ticks} _ticks
""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x2222");

        Assert.NotNull(inspected);
        Assert.Equal("System.TimeSpan", inspected.Type);
        Assert.Equal(expected, inspected.FormattedValue);
    }

    [Fact]
    public async Task InspectAsync_DateOnly_SetsFormattedValue()
    {
        ObjectInspector.ClearCache();

        var expectedDate = new DateOnly(2024, 1, 2);
        var dayNumber = expectedDate.DayNumber;

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
                {
                    return $"""
Name:        System.DateOnly
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        4(0x4) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd0003 4000001 00000000        System.Int32  1 instance {dayNumber} _dayNumber
""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x3333");

        Assert.NotNull(inspected);
        Assert.Equal("System.DateOnly", inspected.Type);
        Assert.Equal(expectedDate.ToString("O"), inspected.FormattedValue);
    }

    [Fact]
    public async Task InspectAsync_TimeOnly_SetsFormattedValue()
    {
        ObjectInspector.ClearCache();

        var expectedTime = new TimeOnly(13, 45, 12, 123);
        var ticks = expectedTime.Ticks;

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
                {
                    return $"""
Name:        System.TimeOnly
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        8(0x8) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd0003 4000001 00000000        System.Int64  1 instance {ticks} _ticks
""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x4444");

        Assert.NotNull(inspected);
        Assert.Equal("System.TimeOnly", inspected.Type);
        Assert.Equal(expectedTime.ToString("O"), inspected.FormattedValue);
    }

    [Fact]
    public async Task InspectAsync_DateTimeOffset_SetsFormattedValueWithOffset()
    {
        ObjectInspector.ClearCache();

        var dt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dateData = (ulong)dt.Ticks | ((ulong)DateTimeKind.Utc << 62);
        var expected = $"{dt.ToString("O")} (+01:30)";

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.Equals("dumpobj 0x5555", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        System.DateTimeOffset
MethodTable: 00007ff9abcd9000
EEClass:     00007ff9abcd9001
Size:        16(0x10) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd1111 4000001 00000000      System.DateTime  1 instance 0000000000006000 _dateTime
00007ff9abcd2222 4000002 00000008        System.Int16  1 instance 90 _offsetMinutes
""";
                }

                if (command.Equals("dumpobj 0x6000", StringComparison.OrdinalIgnoreCase))
                {
                    return $"""
Name:        System.DateTime
MethodTable: 00007ff9abcd1111
EEClass:     00007ff9abcd3333
Size:        8(0x8) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd4444 4000001 00000000       System.UInt64  1 instance {dateData} _dateData
""";
                }

                if (command.StartsWith("dumpvc 00007ff9abcd1111", StringComparison.OrdinalIgnoreCase) &&
                    command.Contains("6000", StringComparison.OrdinalIgnoreCase))
                {
                    return $"""
Name:        System.DateTime
MethodTable: 00007ff9abcd1111
EEClass:     00007ff9abcd3333
Size:        8(0x8) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd4444 4000001 00000000       System.UInt64  1 instance {dateData} _dateData
""";
                }

                if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x5555", maxDepth: 3);

        Assert.NotNull(inspected);
        Assert.Equal("System.DateTimeOffset", inspected.Type);
        Assert.NotNull(inspected.Fields);

        var dateTimeField = Assert.Single(inspected.Fields, f => f.Name == "_dateTime");
        var offsetMinutesField = Assert.Single(inspected.Fields, f => f.Name == "_offsetMinutes");

        var dtObj = Assert.IsType<InspectedObject>(dateTimeField.Value);
        Assert.Equal("System.DateTime", dtObj.Type);
        Assert.NotNull(dtObj.Fields);
        Assert.Contains(dtObj.Fields, f => f.Name == "_dateData");
        Assert.Equal("90", offsetMinutesField.Value?.ToString());

        Assert.Equal(expected, inspected.FormattedValue);
    }
}
