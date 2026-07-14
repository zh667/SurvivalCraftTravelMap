using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TerrainChunkExplorationSchedulerTests
{
    [Fact]
    public void First_observation_enqueues_the_full_footprint_center_first()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 64, 1f);

        Assert.True(scheduler.ObserveFootprint(footprint));
        Assert.Equal(footprint.ChunksNearestFirst.Count, scheduler.PendingCount);
        Assert.Equal(footprint.ChunksNearestFirst.Take(4), scheduler.GetPendingAttempts(4));
    }

    [Fact]
    public void Same_footprint_does_not_reenqueue_completed_chunks_but_retries_pending_chunks()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 32, 1f);
        scheduler.ObserveFootprint(footprint);
        var completed = footprint.CenterChunk;
        scheduler.MarkCompleted(completed);

        Assert.False(scheduler.ObserveFootprint(footprint));
        Assert.DoesNotContain(completed, scheduler.GetPendingAttempts(32));
        Assert.Equal(footprint.ChunksNearestFirst.Count - 1, scheduler.PendingCount);
    }

    [Fact]
    public void Leaving_removes_pending_chunks_and_reentering_enqueues_them_again()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var first = MinimapExplorationFootprint.Create(8f, 8f, 16, 1f);
        var second = MinimapExplorationFootprint.Create(40f, 8f, 16, 1f);
        scheduler.ObserveFootprint(first);
        scheduler.ObserveFootprint(second);

        Assert.DoesNotContain(first.CenterChunk, scheduler.GetPendingAttempts(32));
        Assert.True(scheduler.ObserveFootprint(first));
        Assert.Equal(first.CenterChunk, scheduler.GetPendingAttempts(1)[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_attempt_limit_throws(int maximumCount)
    {
        var scheduler = new TerrainChunkExplorationScheduler();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            scheduler.GetPendingAttempts(maximumCount);
        });
    }

    [Fact]
    public void Pending_chunks_advance_round_robin_while_the_center_is_retried()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 32, 1f);
        scheduler.ObserveFootprint(footprint);

        var firstFrame = scheduler.GetPendingAttempts(4);
        var secondFrame = scheduler.GetPendingAttempts(4);

        Assert.Equal(
            footprint.ChunksNearestFirst.Take(4),
            firstFrame);
        Assert.Equal(
            footprint.ChunksNearestFirst.Take(1).Concat(footprint.ChunksNearestFirst.Skip(4).Take(3)),
            secondFrame);
        Assert.Equal(footprint.ChunksNearestFirst.Count, scheduler.PendingCount);
    }

    [Fact]
    public void MarkCompleted_removes_only_that_pending_chunk()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 32, 1f);
        scheduler.ObserveFootprint(footprint);
        var completed = footprint.ChunksNearestFirst[1];

        scheduler.MarkCompleted(completed);

        Assert.Equal(footprint.ChunksNearestFirst.Count - 1, scheduler.PendingCount);
        Assert.DoesNotContain(completed, scheduler.GetPendingAttempts(32));
        Assert.Contains(footprint.CenterChunk, scheduler.GetPendingAttempts(32));
    }

    [Fact]
    public void Clear_resets_visible_identity_and_pending_state()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 32, 1f);
        scheduler.ObserveFootprint(footprint);

        scheduler.Clear();

        Assert.Equal(0, scheduler.PendingCount);
        Assert.Empty(scheduler.GetPendingAttempts(4));
        Assert.True(scheduler.ObserveFootprint(footprint));
        Assert.Equal(footprint.ChunksNearestFirst, scheduler.GetPendingAttempts(32));
    }

    [Fact]
    public void Null_footprint_is_rejected()
    {
        var scheduler = new TerrainChunkExplorationScheduler();

        Assert.Throws<ArgumentNullException>(() => scheduler.ObserveFootprint(null!));
    }
}
