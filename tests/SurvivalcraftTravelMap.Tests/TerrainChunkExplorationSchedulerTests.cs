using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TerrainChunkExplorationSchedulerTests
{
    [Fact]
    public void Repeated_positions_in_one_chunk_enqueue_once()
    {
        var scheduler = new TerrainChunkExplorationScheduler();

        Assert.True(scheduler.ObservePlayerPosition(0, 0));
        Assert.False(scheduler.ObservePlayerPosition(15, 15));

        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal([new TerrainChunkCoordinate(0, 0)], scheduler.GetPendingAttempts(4));
    }

    [Theory]
    [InlineData(16, 15, 1, 0)]
    [InlineData(15, 16, 0, 1)]
    public void Crossing_one_boundary_enqueues_only_the_entered_chunk(
        int worldX,
        int worldZ,
        int enteredX,
        int enteredZ)
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        scheduler.ObservePlayerPosition(15, 15);

        Assert.True(scheduler.ObservePlayerPosition(worldX, worldZ));

        Assert.Equal(2, scheduler.PendingCount);
        Assert.Equal(
            [new TerrainChunkCoordinate(enteredX, enteredZ), new TerrainChunkCoordinate(0, 0)],
            scheduler.GetPendingAttempts(4));
    }

    [Fact]
    public void Newly_entered_current_chunk_is_attempted_before_older_pending_chunks()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        scheduler.ObservePlayerPosition(0, 0);
        scheduler.ObservePlayerPosition(16, 0);

        var attempts = scheduler.GetPendingAttempts(2);

        Assert.Equal(
            [new TerrainChunkCoordinate(1, 0), new TerrainChunkCoordinate(0, 0)],
            attempts);
    }

    [Fact]
    public void Reentered_pending_chunk_moves_back_to_the_front()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        scheduler.ObservePlayerPosition(0, 0);
        scheduler.ObservePlayerPosition(16, 0);
        scheduler.ObservePlayerPosition(32, 0);

        scheduler.ObservePlayerPosition(0, 0);

        Assert.Equal(
            [
                new TerrainChunkCoordinate(0, 0),
                new TerrainChunkCoordinate(2, 0),
                new TerrainChunkCoordinate(1, 0),
            ],
            scheduler.GetPendingAttempts(4));
    }

    [Fact]
    public void Current_chunk_is_retried_while_older_chunks_advance_round_robin()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        for (var chunkX = 0; chunkX < 6; chunkX++)
        {
            scheduler.ObservePlayerPosition(chunkX * TerrainChunkCoordinate.Size, 0);
        }

        var firstFrame = scheduler.GetPendingAttempts(4);
        var secondFrame = scheduler.GetPendingAttempts(4);

        Assert.Equal(
            [
                new TerrainChunkCoordinate(5, 0),
                new TerrainChunkCoordinate(4, 0),
                new TerrainChunkCoordinate(3, 0),
                new TerrainChunkCoordinate(2, 0),
            ],
            firstFrame);
        Assert.Equal(new TerrainChunkCoordinate(5, 0), secondFrame[0]);
        Assert.Equal(
            [
                new TerrainChunkCoordinate(0, 0),
                new TerrainChunkCoordinate(1, 0),
                new TerrainChunkCoordinate(2, 0),
                new TerrainChunkCoordinate(3, 0),
                new TerrainChunkCoordinate(4, 0),
            ],
            firstFrame.Skip(1).Concat(secondFrame.Skip(1)).Distinct().OrderBy(chunk => chunk.X));
        Assert.Equal(6, scheduler.PendingCount);
    }

    [Fact]
    public void MarkCompleted_removes_only_that_pending_chunk()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var older = new TerrainChunkCoordinate(0, 0);
        var current = new TerrainChunkCoordinate(1, 0);
        scheduler.ObservePlayerPosition(older.OriginX, older.OriginZ);
        scheduler.ObservePlayerPosition(current.OriginX, current.OriginZ);

        scheduler.MarkCompleted(current);

        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal([older], scheduler.GetPendingAttempts(4));
    }

    [Fact]
    public void Returning_to_a_completed_chunk_enqueues_it_again()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var chunkA = new TerrainChunkCoordinate(0, 0);
        var chunkB = new TerrainChunkCoordinate(1, 0);
        scheduler.ObservePlayerPosition(chunkA.OriginX, chunkA.OriginZ);
        scheduler.MarkCompleted(chunkA);
        scheduler.ObservePlayerPosition(chunkB.OriginX, chunkB.OriginZ);

        Assert.True(scheduler.ObservePlayerPosition(chunkA.OriginX, chunkA.OriginZ));

        Assert.Equal(2, scheduler.PendingCount);
        Assert.Equal([chunkA, chunkB], scheduler.GetPendingAttempts(2));
    }

    [Fact]
    public void Uncompleted_chunk_stays_pending_across_repeated_attempts()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        var chunk = new TerrainChunkCoordinate(-1, -1);
        scheduler.ObservePlayerPosition(-1, -1);

        Assert.Equal([chunk], scheduler.GetPendingAttempts(1));
        Assert.Equal([chunk], scheduler.GetPendingAttempts(1));
        Assert.Equal(1, scheduler.PendingCount);
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
    public void Clear_resets_current_identity_and_pending_state()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        scheduler.ObservePlayerPosition(3, 4);

        scheduler.Clear();

        Assert.Equal(0, scheduler.PendingCount);
        Assert.Empty(scheduler.GetPendingAttempts(4));
        Assert.True(scheduler.ObservePlayerPosition(3, 4));
        Assert.Equal([new TerrainChunkCoordinate(0, 0)], scheduler.GetPendingAttempts(4));
    }
}
