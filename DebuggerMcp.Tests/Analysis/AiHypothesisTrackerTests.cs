#nullable enable

using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiHypothesisTrackerTests
{
    [Fact]
    public void Register_AssignsId_AndCopiesNotesAndEvidenceLinks()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);
        ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Id = "E1", Source = "report_get(path=\"analysis.exception.type\")", Finding = "System.MissingMethodException" }
        ]);

        var tracker = new AiHypothesisTracker(ledger, maxHypotheses: 10);

        var result = tracker.Register(
        [
            new AiHypothesis
            {
                Hypothesis = "Assembly mismatch caused MissingMethodException",
                Confidence = "medium",
                SupportsEvidenceIds = ["E1"],
                ContradictsEvidenceIds = ["E999"],
                Notes = "Initial hypothesis based on exception type",
                Unknowns = ["Need to confirm assembly versions"],
                TestsToRun = ["report_get(path=\"analysis.assemblies.items\", limit=25)"]
            }
        ]);

        Assert.Equal(["H1"], result.AddedIds);
        Assert.Single(tracker.Hypotheses);

        var h = tracker.Hypotheses[0];
        Assert.Equal("H1", h.Id);
        Assert.Equal("medium", h.Confidence);
        Assert.Equal("Initial hypothesis based on exception type", h.Notes);
        Assert.Equal(["E1"], h.SupportsEvidenceIds);
        Assert.Null(h.ContradictsEvidenceIds);
        Assert.Equal(["Need to confirm assembly versions"], h.Unknowns);
        Assert.Equal(["report_get(path=\"analysis.assemblies.items\", limit=25)"], h.TestsToRun);
    }

    [Fact]
    public void Update_ValidatesEvidenceIds_AndTracksUnknownEvidence()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);
        ledger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem { Id = "E1", Source = "s1", Finding = "f1" },
            new AiEvidenceLedgerItem { Id = "E2", Source = "s2", Finding = "f2" }
        ]);

        var tracker = new AiHypothesisTracker(ledger, maxHypotheses: 10);
        tracker.Register(
        [
            new AiHypothesis { Hypothesis = "H", Confidence = "unknown" }
        ]);

        var update = tracker.Update(
        [
            new AiHypothesisUpdate
            {
                Id = "H1",
                Confidence = "high",
                SupportsEvidenceIds = ["E1", "E999"],
                ContradictsEvidenceIds = ["E2"],
                Notes = "Updated after reviewing evidence"
            }
        ]);

        Assert.Equal(["H1"], update.UpdatedIds);
        Assert.Contains("E999", update.UnknownEvidenceIds);

        var h = tracker.Hypotheses[0];
        Assert.Equal("high", h.Confidence);
        Assert.Equal(["E1"], h.SupportsEvidenceIds);
        Assert.Equal(["E2"], h.ContradictsEvidenceIds);
        Assert.Equal("Updated after reviewing evidence", h.Notes);
    }

    [Fact]
    public void Register_WhenUpdatingId_ChangesDedupeKey_AllowsOldHypothesisAgain()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);
        var tracker = new AiHypothesisTracker(ledger, maxHypotheses: 10);

        tracker.Register(
        [
            new AiHypothesis { Id = "H1", Hypothesis = "Hypothesis A", Confidence = "low" }
        ]);

        tracker.Register(
        [
            new AiHypothesis { Id = "H1", Hypothesis = "Hypothesis B", Confidence = "low" }
        ]);

        var result = tracker.Register(
        [
            new AiHypothesis { Hypothesis = "Hypothesis A", Confidence = "low" }
        ]);

        Assert.Equal(["H2"], result.AddedIds);
        Assert.Equal(2, tracker.Hypotheses.Count);
    }

    [Fact]
    public void Register_WhenProvidingNewIdForDuplicateHypothesis_IgnoresDuplicate()
    {
        var ledger = new AiEvidenceLedger(maxItems: 10);
        var tracker = new AiHypothesisTracker(ledger, maxHypotheses: 10);

        tracker.Register(
        [
            new AiHypothesis { Hypothesis = "Hypothesis A", Confidence = "low" }
        ]);

        var result = tracker.Register(
        [
            new AiHypothesis { Id = "H99", Hypothesis = "Hypothesis A", Confidence = "low" }
        ]);

        Assert.Empty(result.AddedIds);
        Assert.Equal(1, result.IgnoredDuplicates);
        Assert.Contains("H1", result.IgnoredDuplicateIds);
        Assert.Single(tracker.Hypotheses);
    }
}
