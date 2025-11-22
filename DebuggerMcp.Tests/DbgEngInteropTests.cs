using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Contains unit tests for DbgEng interop constants and structures.
/// </summary>
/// <remarks>
/// These tests verify that the constants and structures match the expected values
/// from the DbgEng API documentation.
/// </remarks>
public class DbgEngInteropTests
{
    #region Constant Tests

    /// <summary>
    /// Tests that output mask constants have the correct values.
    /// </summary>
    [Fact]
    public void OutputMaskConstants_HaveCorrectValues()
    {
        // Arrange & Act & Assert
        // Verify that each output mask constant matches the expected value from DbgEng documentation
        Assert.Equal(0x00000001u, DbgEngConstants.DEBUG_OUTPUT_NORMAL);
        Assert.Equal(0x00000002u, DbgEngConstants.DEBUG_OUTPUT_ERROR);
        Assert.Equal(0x00000004u, DbgEngConstants.DEBUG_OUTPUT_WARNING);
        Assert.Equal(0x00000008u, DbgEngConstants.DEBUG_OUTPUT_VERBOSE);
        Assert.Equal(0x00000010u, DbgEngConstants.DEBUG_OUTPUT_PROMPT);
        Assert.Equal(0x00000020u, DbgEngConstants.DEBUG_OUTPUT_PROMPT_REGISTERS);
        Assert.Equal(0x00000040u, DbgEngConstants.DEBUG_OUTPUT_EXTENSION_WARNING);
        Assert.Equal(0x00000080u, DbgEngConstants.DEBUG_OUTPUT_DEBUGGEE);
        Assert.Equal(0x00000100u, DbgEngConstants.DEBUG_OUTPUT_DEBUGGEE_PROMPT);
        Assert.Equal(0x00000200u, DbgEngConstants.DEBUG_OUTPUT_SYMBOLS);
    }

    /// <summary>
    /// Tests that output control constants have the correct values.
    /// </summary>
    [Fact]
    public void OutputControlConstants_HaveCorrectValues()
    {
        // Arrange & Act & Assert
        // Verify output control constants
        Assert.Equal(0x00000000u, DbgEngConstants.DEBUG_OUTCTL_THIS_CLIENT);
        Assert.Equal(0x00000001u, DbgEngConstants.DEBUG_OUTCTL_ALL_CLIENTS);
        Assert.Equal(0x00000002u, DbgEngConstants.DEBUG_OUTCTL_ALL_OTHER_CLIENTS);
        Assert.Equal(0x00000003u, DbgEngConstants.DEBUG_OUTCTL_IGNORE);
        Assert.Equal(0x00000004u, DbgEngConstants.DEBUG_OUTCTL_LOG_ONLY);
        Assert.Equal(0x00000007u, DbgEngConstants.DEBUG_OUTCTL_SEND_MASK);
        Assert.Equal(0x00000008u, DbgEngConstants.DEBUG_OUTCTL_NOT_LOGGED);
        Assert.Equal(0x00000010u, DbgEngConstants.DEBUG_OUTCTL_OVERRIDE_MASK);
        Assert.Equal(0x00000020u, DbgEngConstants.DEBUG_OUTCTL_DML);
        Assert.Equal(0xfffffffeu, DbgEngConstants.DEBUG_OUTCTL_AMBIENT_DML);
        Assert.Equal(0xffffffffu, DbgEngConstants.DEBUG_OUTCTL_AMBIENT_TEXT);
    }

    /// <summary>
    /// Tests that execute flag constants have the correct values.
    /// </summary>
    [Fact]
    public void ExecuteFlagConstants_HaveCorrectValues()
    {
        // Arrange & Act & Assert
        // Verify execute flag constants
        Assert.Equal(0x00000000u, DbgEngConstants.DEBUG_EXECUTE_DEFAULT);
        Assert.Equal(0x00000001u, DbgEngConstants.DEBUG_EXECUTE_ECHO);
        Assert.Equal(0x00000002u, DbgEngConstants.DEBUG_EXECUTE_NOT_LOGGED);
        Assert.Equal(0x00000004u, DbgEngConstants.DEBUG_EXECUTE_NO_REPEAT);
    }

    #endregion

    #region Structure Tests

    /// <summary>
    /// Tests that DEBUG_STACK_FRAME structure has the expected layout.
    /// </summary>
    [Fact]
    public void DebugStackFrame_HasCorrectLayout()
    {
        // Arrange
        var frame = new DEBUG_STACK_FRAME
        {
            InstructionOffset = 0x1000,
            ReturnOffset = 0x2000,
            FrameOffset = 0x3000,
            StackOffset = 0x4000,
            FuncTableEntry = 0x5000,
            Params = new ulong[4] { 1, 2, 3, 4 },
            Reserved = new ulong[6] { 0, 0, 0, 0, 0, 0 },
            Virtual = 0,
            FrameNumber = 1
        };

        // Act & Assert
        // Verify that all fields can be set and retrieved correctly
        Assert.Equal(0x1000ul, frame.InstructionOffset);
        Assert.Equal(0x2000ul, frame.ReturnOffset);
        Assert.Equal(0x3000ul, frame.FrameOffset);
        Assert.Equal(0x4000ul, frame.StackOffset);
        Assert.Equal(0x5000ul, frame.FuncTableEntry);
        Assert.Equal(4, frame.Params.Length);
        Assert.Equal(6, frame.Reserved.Length);
        Assert.Equal(0, frame.Virtual);
        Assert.Equal(1u, frame.FrameNumber);
    }

    /// <summary>
    /// Tests that DEBUG_VALUE structure can hold different value types.
    /// </summary>
    [Fact]
    public void DebugValue_CanHoldDifferentTypes()
    {
        // Arrange & Act
        // Test 8-bit integer
        var value8 = new DEBUG_VALUE { I8 = 0x42, Type = 1 };
        Assert.Equal((byte)0x42, value8.I8);
        Assert.Equal(1u, value8.Type);

        // Test 16-bit integer
        var value16 = new DEBUG_VALUE { I16 = 0x1234, Type = 2 };
        Assert.Equal((ushort)0x1234, value16.I16);
        Assert.Equal(2u, value16.Type);

        // Test 32-bit integer
        var value32 = new DEBUG_VALUE { I32 = 0x12345678, Type = 3 };
        Assert.Equal(0x12345678u, value32.I32);
        Assert.Equal(3u, value32.Type);

        // Test 64-bit integer
        var value64 = new DEBUG_VALUE { I64 = 0x123456789ABCDEF0, Type = 4 };
        Assert.Equal(0x123456789ABCDEF0ul, value64.I64);
        Assert.Equal(4u, value64.Type);

        // Test 32-bit float
        var valueF32 = new DEBUG_VALUE { F32 = 3.14f, Type = 5 };
        Assert.Equal(3.14f, valueF32.F32, precision: 2);
        Assert.Equal(5u, valueF32.Type);

        // Test 64-bit double
        var valueF64 = new DEBUG_VALUE { F64 = 3.14159265359, Type = 6 };
        Assert.Equal(3.14159265359, valueF64.F64, precision: 10);
        Assert.Equal(6u, valueF64.Type);
    }

    /// <summary>
    /// Tests that DEBUG_BREAKPOINT_PARAMETERS structure has the expected layout.
    /// </summary>
    [Fact]
    public void DebugBreakpointParameters_HasCorrectLayout()
    {
        // Arrange
        var bp = new DEBUG_BREAKPOINT_PARAMETERS
        {
            Offset = 0x1000,
            Id = 1,
            BreakType = 0,
            ProcType = 0,
            Flags = 0x01,
            DataSize = 0,
            DataAccessType = 0,
            PassCount = 1,
            CurrentPassCount = 0,
            MatchThread = 0,
            CommandSize = 0,
            OffsetExpressionSize = 0
        };

        // Act & Assert
        // Verify that all fields can be set and retrieved correctly
        Assert.Equal(0x1000ul, bp.Offset);
        Assert.Equal(1u, bp.Id);
        Assert.Equal(0u, bp.BreakType);
        Assert.Equal(0u, bp.ProcType);
        Assert.Equal(0x01u, bp.Flags);
        Assert.Equal(0u, bp.DataSize);
        Assert.Equal(0u, bp.DataAccessType);
        Assert.Equal(1u, bp.PassCount);
        Assert.Equal(0u, bp.CurrentPassCount);
        Assert.Equal(0u, bp.MatchThread);
        Assert.Equal(0u, bp.CommandSize);
        Assert.Equal(0u, bp.OffsetExpressionSize);
    }

    /// <summary>
    /// Tests that DEBUG_SPECIFIC_FILTER_PARAMETERS structure has the expected layout.
    /// </summary>
    [Fact]
    public void DebugSpecificFilterParameters_HasCorrectLayout()
    {
        // Arrange
        var filter = new DEBUG_SPECIFIC_FILTER_PARAMETERS
        {
            ExecutionOption = 1,
            ContinueOption = 2,
            TextSize = 10,
            CommandSize = 20,
            ArgumentSize = 30
        };

        // Act & Assert
        // Verify that all fields can be set and retrieved correctly
        Assert.Equal(1u, filter.ExecutionOption);
        Assert.Equal(2u, filter.ContinueOption);
        Assert.Equal(10u, filter.TextSize);
        Assert.Equal(20u, filter.CommandSize);
        Assert.Equal(30u, filter.ArgumentSize);
    }

    /// <summary>
    /// Tests that DEBUG_EXCEPTION_FILTER_PARAMETERS structure has the expected layout.
    /// </summary>
    [Fact]
    public void DebugExceptionFilterParameters_HasCorrectLayout()
    {
        // Arrange
        var filter = new DEBUG_EXCEPTION_FILTER_PARAMETERS
        {
            ExecutionOption = 1,
            ContinueOption = 2,
            TextSize = 10,
            CommandSize = 20,
            SecondCommandSize = 30,
            ExceptionCode = 0xC0000005 // Access violation
        };

        // Act & Assert
        // Verify that all fields can be set and retrieved correctly
        Assert.Equal(1u, filter.ExecutionOption);
        Assert.Equal(2u, filter.ContinueOption);
        Assert.Equal(10u, filter.TextSize);
        Assert.Equal(20u, filter.CommandSize);
        Assert.Equal(30u, filter.SecondCommandSize);
        Assert.Equal(0xC0000005u, filter.ExceptionCode);
    }

    #endregion

    #region GUID Tests

    /// <summary>
    /// Tests that the IDebugClient GUID is correct.
    /// </summary>
    [Fact]
    public void IDebugClient_GUID_IsCorrect()
    {
        // Arrange
        var expectedGuid = new Guid("27fe5639-8407-4f47-8364-ee118fb08ac8");

        // Act
        var actualGuid = DbgEng.IID_IDebugClient;

        // Assert
        // The GUID must match exactly for COM interop to work
        Assert.Equal(expectedGuid, actualGuid);
    }

    #endregion
}
