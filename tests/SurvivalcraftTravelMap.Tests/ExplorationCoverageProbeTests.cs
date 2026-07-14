using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class ExplorationCoverageProbeTests
{
    [Fact]
    public void Lookup_failure_keeps_candidate_pending_continues_batch_and_deduplicates_warning()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 32, 1f);
        scheduler.ObserveFootprint(footprint);
        foreach (var chunk in footprint.ChunksNearestFirst)
            scheduler.MarkCompleted(chunk);

        var failing = footprint.CenterChunk;
        var following = footprint.ChunksNearestFirst.First(chunk => chunk != failing);
        var lookupCalls = new List<TerrainChunkCoordinate>();
        var warnings = new List<string>();
        var failingState = 0;
        var reporter = new ExplorationFailureReporter(warnings.Add);
        var probe = new ExplorationCoverageProbe(
            chunk =>
            {
                lookupCalls.Add(chunk);
                if (chunk == failing)
                {
                    if (failingState == 0)
                        throw new IOException("coverage read failed");

                    return failingState == 2;
                }

                return true;
            },
            reporter);

        Assert.Equal(2, scheduler.ReconcileCoverage(probe.IsFullyExplored, maximumChecks: 2));
        Assert.Equal([failing, following], lookupCalls);
        Assert.Equal(failing, Assert.Single(scheduler.GetPendingAttempts(2)));
        Assert.Single(warnings);
        Assert.Contains("coverage lookup failed", warnings[0], StringComparison.Ordinal);
        Assert.Contains($"({failing.X}, {failing.Z})", warnings[0], StringComparison.Ordinal);

        lookupCalls.Clear();
        scheduler.ReconcileCoverage(probe.IsFullyExplored, maximumChecks: 2);
        Assert.Equal(failing, lookupCalls[0]);
        Assert.Single(warnings);
        Assert.Equal(failing, Assert.Single(scheduler.GetPendingAttempts(2)));

        failingState = 1;
        scheduler.ReconcileCoverage(probe.IsFullyExplored, maximumChecks: 2);
        Assert.Equal(failing, Assert.Single(scheduler.GetPendingAttempts(2)));

        failingState = 2;
        scheduler.ReconcileCoverage(probe.IsFullyExplored, maximumChecks: 2);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Record_and_coverage_failures_use_distinct_deduplication_operations()
    {
        var chunk = new TerrainChunkCoordinate(-2, 3);
        var warnings = new List<string>();
        var reporter = new ExplorationFailureReporter(warnings.Add);
        var exception = new IOException("same failure");

        reporter.Report(chunk, ExplorationFailureOperation.CoverageLookup, exception);
        reporter.Report(chunk, ExplorationFailureOperation.CoverageLookup, exception);
        reporter.Report(chunk, ExplorationFailureOperation.Record, exception);
        reporter.Report(chunk, ExplorationFailureOperation.Record, exception);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("coverage lookup failed", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("exploration failed", StringComparison.Ordinal));
    }
}
