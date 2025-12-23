using System.Text.Json;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiAnalysisResultTests
{
    [Fact]
    public void RemoveCommandTraces_RemovesCommandsExecutedFromRootAndNestedResults()
    {
        var ai = new AiAnalysisResult
        {
            RootCause = "x",
            CommandsExecuted =
            [
                new ExecutedCommand
                {
                    Tool = "report_get",
                    Output = "o",
                    Iteration = 1
                }
            ],
            Summary = new AiSummaryResult
            {
                Description = "d",
                CommandsExecuted =
                [
                    new ExecutedCommand
                    {
                        Tool = "exec",
                        Output = "o2",
                        Iteration = 1
                    }
                ]
            },
            ThreadNarrative = new AiThreadNarrativeResult
            {
                Description = "t",
                CommandsExecuted =
                [
                    new ExecutedCommand
                    {
                        Tool = "get_thread_stack",
                        Output = "o3",
                        Iteration = 1
                    }
                ]
            }
        };

        ai.RemoveCommandTraces();

        Assert.Null(ai.CommandsExecuted);
        Assert.NotNull(ai.Summary);
        Assert.Null(ai.Summary!.CommandsExecuted);
        Assert.NotNull(ai.ThreadNarrative);
        Assert.Null(ai.ThreadNarrative!.CommandsExecuted);
    }

    [Fact]
    public void RemoveCommandTraces_WhenSerialized_OmitsCommandsExecuted()
    {
        var ai = new AiAnalysisResult
        {
            RootCause = "x",
            CommandsExecuted =
            [
                new ExecutedCommand
                {
                    Tool = "report_get",
                    Output = "o",
                    Iteration = 1
                }
            ]
        };

        ai.RemoveCommandTraces();

        var json = JsonSerializer.Serialize(ai);
        Assert.DoesNotContain("\"commandsExecuted\"", json, StringComparison.Ordinal);
    }
}

