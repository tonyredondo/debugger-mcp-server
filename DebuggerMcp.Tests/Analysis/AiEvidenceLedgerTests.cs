#nullable enable

using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiEvidenceLedgerTests
{
    [Fact]
    public void AddOrUpdate_WhenAddingItemsWithoutId_AssignsSequentialIds()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);

        var result = ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Source = "report_get(path=\"analysis.exception.type\")", Finding = "System.MissingMethodException" },
            new AiEvidenceLedgerItem { Source = "report_get(path=\"analysis.exception.message\")", Finding = "Method not found ..." }
        ]);

        Assert.Equal(["E1", "E2"], result.AddedIds);
        Assert.Equal(0, result.InvalidItems);
        Assert.Equal(2, ledger.Items.Count);
        Assert.Equal("E1", ledger.Items[0].Id);
        Assert.Equal("E2", ledger.Items[1].Id);
    }

    [Fact]
    public void AddOrUpdate_WhenAddingDuplicateSourceAndFinding_IgnoresDuplicate()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);

        ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Source = "report_get(path=\"analysis.exception.type\")", Finding = "System.MissingMethodException" }
        ]);

        var result = ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Source = "  report_get(path=\"analysis.exception.type\")  ", Finding = "system.missingmethodexception" }
        ]);

        Assert.Empty(result.AddedIds);
        Assert.Equal(1, result.IgnoredDuplicates);
        Assert.Single(ledger.Items);
    }

    [Fact]
    public void AddOrUpdate_WhenProvidingExistingId_UpdatesItem()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);

        ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Id = "E10", Source = "exec(command=\"ip2md 0x123\")", Finding = "MethodDesc: 0xabc" }
        ]);

        var result = ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Id = "E10", Source = "exec(command=\"ip2md 0x123\")", Finding = "MethodDesc: 0xdef" }
        ]);

        Assert.Equal(["E10"], result.UpdatedIds);
        Assert.Single(ledger.Items);
        Assert.Equal("MethodDesc: 0xdef", ledger.Items[0].Finding);
        Assert.True(ledger.ContainsEvidenceId("E10"));
        Assert.True(ledger.ContainsEvidenceId("e010"));
    }
}
