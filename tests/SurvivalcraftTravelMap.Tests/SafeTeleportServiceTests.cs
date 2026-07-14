using System.Numerics;
using SurvivalcraftTravelMap.Teleport;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class SafeTeleportServiceTests
{
    [Fact]
    public async Task One_player_rejects_overlapping_transactions_and_releases_gate_after_completion()
    {
        var context = new TeleportTestContext();
        var service = context.Service;
        var enteredPostMoveValidation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePostMoveValidation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Terrain.SetSafeFeet(1, 65, 0);
        context.Clock.WaitForUpdate = async cancellationToken =>
        {
            enteredPostMoveValidation.TrySetResult();
            await releasePostMoveValidation.Task.WaitAsync(cancellationToken);
        };

        var first = service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), CancellationToken.None);
        await enteredPostMoveValidation.Task.WaitAsync(TestContext.Current.CancellationToken);
        var overlapping = await service.TeleportToSurfaceAsync(1, 0, TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Busy, overlapping);
        Assert.Single(context.Mover.Movements);

        releasePostMoveValidation.TrySetResult();
        Assert.Equal(TeleportResult.Success, await first);
        Assert.Equal(
            TeleportResult.Success,
            await service.TeleportToSurfaceAsync(1, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Legacy_and_ID61_entry_shapes_share_the_same_non_queueing_gate_in_both_directions()
    {
        foreach (var legacyStartsFirst in new[] { true, false })
        {
            var context = new TeleportTestContext();
            var service = context.Service;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Terrain.SetSafeFeet(0, 65, 0);
            context.Terrain.SetSafeFeet(1, 65, 0);
            context.Clock.WaitForUpdate = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            };

            var first = legacyStartsFirst
                ? service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), CancellationToken.None)
                : service.TeleportToSurfaceAsync(1, 0, CancellationToken.None);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var overlapping = legacyStartsFirst
                ? await service.TeleportToSurfaceAsync(1, 0, TestContext.Current.CancellationToken)
                : await service.TeleportToWaypointAsync(
                    new Vector3(0f, 65f, 0f),
                    TestContext.Current.CancellationToken);

            Assert.Equal(TeleportResult.Busy, overlapping);
            Assert.Single(context.Mover.Movements);
            release.TrySetResult();
            Assert.Equal(TeleportResult.Success, await first);
        }
    }

    [Fact]
    public async Task Cancellation_rolls_back_releases_the_gate_and_allows_the_next_transaction()
    {
        var context = new TeleportTestContext();
        var service = context.Service;
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        context.Clock.WaitForUpdate = _ => Task.CompletedTask;
        Assert.Single(context.Mover.RestoredSnapshots);
        Assert.Equal(
            TeleportResult.Success,
            await service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exception_releases_the_gate_and_allows_the_next_transaction()
    {
        var context = new TeleportTestContext();
        var service = context.Service;
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Terrain.ThrowOnReadNumber = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        context.Terrain.ThrowOnReadNumber = null;
        Assert.Equal(
            TeleportResult.Success,
            await service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Different_players_have_independent_teleport_transaction_gates()
    {
        var first = new TeleportTestContext();
        var second = new TeleportTestContext();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Terrain.SetSafeFeet(0, 65, 0);
        second.Terrain.SetSafeFeet(0, 65, 0);
        first.Clock.WaitForUpdate = async token =>
        {
            firstEntered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        second.Clock.WaitForUpdate = async token =>
        {
            secondEntered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var firstOperation = first.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            CancellationToken.None);
        var secondOperation = second.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            CancellationToken.None);
        await Task.WhenAll(firstEntered.Task, secondEntered.Task)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Single(first.Mover.Movements);
        Assert.Single(second.Mover.Movements);
        release.TrySetResult();
        Assert.Equal(TeleportResult.Success, await firstOperation);
        Assert.Equal(TeleportResult.Success, await secondOperation);
    }

    [Fact]
    public void Surface_candidates_use_radius_eight_squared_distance_then_coordinate_order()
    {
        var candidates = TeleportCandidate.GenerateSurface(10, -20).ToArray();

        Assert.Equal(17 * 17, candidates.Length);
        Assert.Equal(
            [
                new TeleportCandidate(10, null, -20),
                new TeleportCandidate(9, null, -20),
                new TeleportCandidate(10, null, -21),
                new TeleportCandidate(10, null, -19),
                new TeleportCandidate(11, null, -20),
                new TeleportCandidate(9, null, -21),
                new TeleportCandidate(9, null, -19),
                new TeleportCandidate(11, null, -21),
                new TeleportCandidate(11, null, -19),
            ],
            candidates.Take(9));
        Assert.All(candidates, candidate =>
            Assert.InRange(Math.Max(Math.Abs(candidate.X - 10), Math.Abs(candidate.Z + 20)), 0, 8));
        Assert.Equal(new TeleportCandidate(18, null, -12), candidates[^1]);
    }

    [Fact]
    public void Waypoint_candidates_exhaust_vertical_offsets_before_increasing_horizontal_distance()
    {
        var candidates = TeleportCandidate.GenerateWaypoint(10, 64, -20).ToArray();
        var expectedVerticalOffsets = new[]
        {
            0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8, -8,
        };

        Assert.Equal(17 * 17 * 17, candidates.Length);
        Assert.Equal(
            expectedVerticalOffsets.Select(offset => new TeleportCandidate(10, 64 + offset, -20)),
            candidates.Take(17));
        Assert.Equal(
            expectedVerticalOffsets.Select(offset => new TeleportCandidate(9, 64 + offset, -20)),
            candidates.Skip(17).Take(17));
        Assert.Equal(new TeleportCandidate(18, 56, -12), candidates[^1]);
    }

    [Fact]
    public async Task Surface_teleport_loads_radius_eight_then_moves_to_the_first_safe_centered_position()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(9, 65, 20);
        var expectedSnapshot = context.Mover.Snapshot;
        context.Clock.WaitForUpdate = _ =>
        {
            Assert.Equal(0, context.Chunks.Lease.DisposeCount);
            return Task.CompletedTask;
        };

        var result = await context.Service.TeleportToSurfaceAsync(10, 20, TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
        Assert.Equal([(10, 20, 8)], context.Chunks.Requests);
        var movement = Assert.Single(context.Mover.Movements);
        Assert.Equal(new Vector3(9.5f, 65f, 20.5f), movement.Position);
        Assert.Equal(expectedSnapshot.Rotation, movement.Rotation);
        Assert.Equal(Vector3.Zero, movement.LinearVelocity);
        Assert.Equal(Vector3.Zero, movement.AngularVelocity);
        Assert.Equal(0f, movement.FallDistance);
        Assert.False(movement.IsFalling);
        Assert.False(movement.HasPendingFallDamage);
        Assert.Equal(1, context.Clock.UpdateWaitCount);
        Assert.Empty(context.Mover.RestoredSnapshots);
        Assert.Equal(1, context.Chunks.Lease.DisposeCount);
    }

    [Fact]
    public async Task Successful_post_validation_notifies_the_authoritative_position_commit_once()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        var commitCount = 0;
        var service = CreateObservedService(context, () => commitCount++);

        var result = await service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
        Assert.Equal(1, commitCount);
    }

    [Fact]
    public async Task Chunk_load_failure_reports_the_exact_stage_and_exception_once()
    {
        var context = new TeleportTestContext();
        var failure = new IOException("chunk load failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        context.Chunks.Load = _ => Task.FromException<IChunkLoadLease>(failure);
        var service = CreateDiagnosticService(context, diagnostics);

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.ChunkLoad, failure);
    }

    [Fact]
    public async Task Candidate_search_failure_reports_the_exact_stage_and_exception_once()
    {
        var context = new TeleportTestContext();
        var failure = new InvalidDataException("terrain read failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Terrain.ThrowOnReadNumber = 1;
        context.Terrain.BlockReadException = failure;
        var service = CreateDiagnosticService(context, diagnostics);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.CandidateSearch, failure);
    }

    [Fact]
    public async Task Movement_snapshot_failure_reports_without_move_restore_or_sync()
    {
        var context = new TeleportTestContext();
        var failure = new InvalidOperationException("snapshot failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        var syncCount = 0;
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Mover.CaptureException = failure;
        var service = CreateDiagnosticService(context, diagnostics, () => syncCount++);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.MovementSnapshot, failure);
        Assert.Empty(context.Mover.Movements);
        Assert.Equal(0, context.Mover.RestoreAttempts);
        Assert.Equal(0, syncCount);
    }

    [Fact]
    public async Task Position_write_failure_reports_after_one_safe_restore_and_without_sync()
    {
        var context = new TeleportTestContext();
        var failure = new InvalidOperationException("position write failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        var syncCount = 0;
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Mover.MoveException = failure;
        var service = CreateDiagnosticService(context, diagnostics, () => syncCount++);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.PositionWrite, failure);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
        Assert.Equal(0, syncCount);
    }

    [Fact]
    public async Task Post_move_validation_failure_reports_after_one_safe_restore_and_without_sync()
    {
        var context = new TeleportTestContext();
        var failure = new InvalidOperationException("post-move validation failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        var syncCount = 0;
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = _ => throw failure;
        var service = CreateDiagnosticService(context, diagnostics, () => syncCount++);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.PostMoveValidation, failure);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
        Assert.Equal(0, syncCount);
    }

    [Fact]
    public async Task Rollback_failure_reports_the_rollback_stage_and_public_exception_once()
    {
        var context = new TeleportTestContext();
        var originalFailure = new InvalidOperationException("post-move validation failed");
        var restoreFailure = new IOException("restore failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = _ => throw originalFailure;
        context.Mover.RestoreException = restoreFailure;
        var service = CreateDiagnosticService(context, diagnostics);

        var thrown = await Assert.ThrowsAsync<TeleportRollbackException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(originalFailure, thrown.OriginalFailure);
        Assert.Same(restoreFailure, thrown.RestoreFailure);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.Rollback, thrown);
    }

    [Fact]
    public async Task Position_sync_failure_rolls_back_reports_once_and_never_returns_success()
    {
        var context = new TeleportTestContext();
        var failure = new InvalidOperationException("position sync failed");
        var diagnostics = new List<TeleportFailureDiagnostic>();
        var syncAttempts = 0;
        context.Terrain.SetSafeFeet(0, 65, 0);
        var service = CreateDiagnosticService(
            context,
            diagnostics,
            () =>
            {
                syncAttempts++;
                throw failure;
            });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        AssertDiagnostic(diagnostics, TeleportExecutionStage.PositionSync, failure);
        Assert.Equal(1, syncAttempts);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Cancellation_produces_no_diagnostic_even_after_a_successful_restore()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        var diagnostics = new List<TeleportFailureDiagnostic>();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };
        var service = CreateDiagnosticService(context, diagnostics);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Empty(diagnostics);
        Assert.Single(context.Mover.RestoredSnapshots);
    }

    [Fact]
    public async Task Unsafe_post_move_result_rolls_back_without_an_internal_diagnostic_or_sync()
    {
        var context = new TeleportTestContext();
        var diagnostics = new List<TeleportFailureDiagnostic>();
        var syncCount = 0;
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = _ =>
        {
            context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Lava);
            return Task.CompletedTask;
        };
        var service = CreateDiagnosticService(context, diagnostics, () => syncCount++);

        var result = await service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.RolledBack, result);
        Assert.Empty(diagnostics);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
        Assert.Equal(0, syncCount);
    }

    [Fact]
    public async Task Successful_surface_and_waypoint_transactions_each_sync_exactly_once()
    {
        foreach (var useSurface in new[] { true, false })
        {
            var context = new TeleportTestContext();
            var diagnostics = new List<TeleportFailureDiagnostic>();
            var syncCount = 0;
            context.Terrain.SetSafeFeet(0, 65, 0);
            var service = CreateDiagnosticService(context, diagnostics, () => syncCount++);

            var result = useSurface
                ? await service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken)
                : await service.TeleportToWaypointAsync(
                    new Vector3(0f, 65f, 0f),
                    TestContext.Current.CancellationToken);

            Assert.Equal(TeleportResult.Success, result);
            Assert.Equal(1, syncCount);
            Assert.Empty(diagnostics);
        }
    }

    [Fact]
    public async Task Diagnostic_callback_failure_never_replaces_the_original_teleport_exception()
    {
        var context = new TeleportTestContext();
        var teleportFailure = new InvalidOperationException("position write failed");
        var reporterFailure = new IOException("reporter failed");
        var reportAttempts = 0;
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Mover.MoveException = teleportFailure;
        var service = new SafeTeleportService(
            context.Terrain,
            context.Chunks,
            context.Mover,
            context.Collisions,
            context.Clock,
            static () => { },
            _ =>
            {
                reportAttempts++;
                throw reporterFailure;
            });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(teleportFailure, thrown);
        Assert.Equal(1, reportAttempts);
    }

    [Fact]
    public async Task Missing_callbacks_are_treated_as_no_ops()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        var service = new SafeTeleportService(
            context.Terrain,
            context.Chunks,
            context.Mover,
            context.Collisions,
            context.Clock,
            null!,
            null!);

        var result = await service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
    }

    [Fact]
    public async Task Equal_distance_candidates_prefer_the_one_with_safer_surroundings()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(-1, 65, 0);
        context.Terrain.SetSafeFeet(1, 65, 0);
        foreach (var (x, z) in new[] { (1, -1), (1, 1), (2, -1), (2, 0), (2, 1) })
        {
            context.Terrain.SetSafeFeet(x, 65, z);
        }

        var result = await context.Service.TeleportToSurfaceAsync(
            0,
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
        Assert.Equal(new Vector3(1.5f, 65f, 0.5f), Assert.Single(context.Mover.Movements).Position);
    }

    [Theory]
    [InlineData(TeleportBlockKind.Lava)]
    [InlineData(TeleportBlockKind.Fire)]
    [InlineData(TeleportBlockKind.Cactus)]
    [InlineData(TeleportBlockKind.Spikes)]
    [InlineData(TeleportBlockKind.Damaging)]
    public async Task Hazardous_ground_is_rejected(TeleportBlockKind ground)
    {
        var context = new TeleportTestContext();
        context.Terrain.SetBlock(0, 64, 0, ground);
        context.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Air);
        context.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Air);

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.NoSafePosition, result);
        Assert.Empty(context.Mover.Movements);
    }

    [Theory]
    [InlineData(TeleportBlockKind.SafeSolid)]
    [InlineData(TeleportBlockKind.Lava)]
    [InlineData(TeleportBlockKind.Fire)]
    [InlineData(TeleportBlockKind.Cactus)]
    [InlineData(TeleportBlockKind.Spikes)]
    [InlineData(TeleportBlockKind.Leaves)]
    [InlineData(TeleportBlockKind.Falling)]
    [InlineData(TeleportBlockKind.Damaging)]
    public async Task Collidable_or_harmful_player_cells_are_rejected(TeleportBlockKind obstruction)
    {
        foreach (var obstructedY in new[] { 65, 66 })
        {
            var context = new TeleportTestContext();
            context.Terrain.SetSafeFeet(0, 65, 0);
            context.Terrain.SetBlock(0, obstructedY, 0, obstruction);

            var result = await context.Service.TeleportToSurfaceAsync(
                0,
                0,
                TestContext.Current.CancellationToken);

            Assert.Equal(TeleportResult.NoSafePosition, result);
            Assert.Empty(context.Mover.Movements);
        }
    }

    [Theory]
    [InlineData(TeleportBlockKind.SafeSolid)]
    [InlineData(TeleportBlockKind.Leaves)]
    [InlineData(TeleportBlockKind.Falling)]
    public async Task Ordinary_collidable_surfaces_accept_waypoint_teleport(TeleportBlockKind ground)
    {
        var context = new TeleportTestContext();
        context.Terrain.SetBlock(0, 64, 0, ground);
        context.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Air);
        context.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Air);

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
        Assert.Equal(new Vector3(0.5f, 65f, 0.5f), Assert.Single(context.Mover.Movements).Position);
    }

    [Fact]
    public async Task Surface_search_steps_through_harmless_plants_to_real_ground()
    {
        var context = new TeleportTestContext();
        context.Terrain.DefaultSurfaceHeight = 66;
        context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.SafeSolid);
        context.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Passable);
        context.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Passable);
        context.Terrain.SetBlock(0, 67, 0, TeleportBlockKind.Air);

        var result = await context.Service.TeleportToSurfaceAsync(
            0,
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
        Assert.Equal(65f, Assert.Single(context.Mover.Movements).Position.Y);
    }

    [Fact]
    public async Task Water_surface_is_allowed_only_with_breathable_head_space()
    {
        var safe = new TeleportTestContext();
        safe.Terrain.DefaultSurfaceHeight = 64;
        safe.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Water);
        safe.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Air);
        safe.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Air);
        Assert.Equal(
            TeleportResult.Success,
            await safe.Service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken));

        var submergedHead = new TeleportTestContext();
        submergedHead.Terrain.MaxY = 66;
        submergedHead.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Water);
        submergedHead.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Water);
        submergedHead.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Water);
        Assert.Equal(
            TeleportResult.NoSafePosition,
            await submergedHead.Service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cells_outside_height_bounds_are_rejected_without_terrain_reads_outside_the_world()
    {
        var context = new TeleportTestContext();
        context.Terrain.MinY = 0;
        context.Terrain.MaxY = 65;
        context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.SafeSolid);
        context.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Air);

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.NoSafePosition, result);
        Assert.DoesNotContain(context.Terrain.BlockReads, cell => cell.Y is < 0 or > 65);
        Assert.Empty(context.Mover.Movements);
    }

    [Fact]
    public async Task Entity_collision_rejects_an_otherwise_safe_candidate()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Collisions.OtherBlockingEntityPositions.Add(new Vector3(0.5f, 65f, 0.5f));

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.NoSafePosition, result);
        Assert.Empty(context.Mover.Movements);
    }

    [Fact]
    public async Task Collision_query_excludes_the_teleporting_players_own_body()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Collisions.PlayerSelfOverlapPositions.Add(new Vector3(0.5f, 65f, 0.5f));

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.Success, result);
        Assert.Single(context.Mover.Movements);
        Assert.Equal(2, context.Collisions.QueryCount);
        Assert.Equal(2, context.Collisions.ExcludedPlayerSelfOverlapCount);
    }

    [Fact]
    public async Task Chunk_loading_has_a_hard_ten_second_timeout_and_never_moves_after_timeout()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Chunks.Load = _ => new TaskCompletionSource<IChunkLoadLease>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        context.Clock.Delay = (_, _) => Task.CompletedTask;

        var result = await context.Service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.ChunkTimeout, result);
        Assert.Equal([TimeSpan.FromSeconds(10)], context.Clock.RequestedDelays);
        Assert.True(context.Chunks.LastToken.IsCancellationRequested);
        Assert.Empty(context.Mover.Movements);
        Assert.Equal(0, context.Chunks.Lease.DisposeCount);
    }

    [Fact]
    public async Task Timed_out_noncooperative_loader_disposes_a_lease_that_completes_late()
    {
        var context = new TeleportTestContext();
        var completion = new TaskCompletionSource<IChunkLoadLease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateLease = new FakeChunkLoadLease();
        context.Chunks.Load = _ => completion.Task;
        context.Clock.Delay = (_, _) => Task.CompletedTask;

        var result = await context.Service.TeleportToSurfaceAsync(
            0,
            0,
            TestContext.Current.CancellationToken);
        completion.SetResult(lateLease);
        await lateLease.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.ChunkTimeout, result);
        Assert.Equal(1, lateLease.DisposeCount);
    }

    [Fact]
    public async Task Timeout_clock_failure_still_cancels_an_unbounded_chunk_load()
    {
        var context = new TeleportTestContext();
        context.Chunks.Load = async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        context.Clock.Delay = (_, _) => Task.FromException(new InvalidOperationException("clock failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken));

        Assert.True(context.Chunks.LastToken.IsCancellationRequested);
        Assert.Empty(context.Mover.Movements);
    }

    [Fact]
    public async Task Synchronous_timeout_clock_failure_disposes_a_chunk_lease_that_completes_late()
    {
        var context = new TeleportTestContext();
        var completion = new TaskCompletionSource<IChunkLoadLease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateLease = new FakeChunkLoadLease();
        context.Chunks.Load = _ => completion.Task;
        context.Clock.Delay = (_, _) => throw new InvalidOperationException("clock creation failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken));
        completion.SetResult(lateLease);
        await lateLease.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(context.Chunks.LastToken.IsCancellationRequested);
        Assert.Equal(1, lateLease.DisposeCount);
    }

    [Fact]
    public async Task No_safe_position_and_out_of_world_never_capture_or_move_the_player()
    {
        var noSafe = new TeleportTestContext();
        var noSafeResult = await noSafe.Service.TeleportToSurfaceAsync(
            0,
            0,
            TestContext.Current.CancellationToken);
        var outside = new TeleportTestContext();
        outside.Terrain.IsColumnInWorldResult = false;
        var outsideResult = await outside.Service.TeleportToSurfaceAsync(
            int.MaxValue,
            int.MinValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.NoSafePosition, noSafeResult);
        Assert.Equal(TeleportResult.OutOfWorld, outsideResult);
        Assert.Equal(0, noSafe.Mover.CaptureCount);
        Assert.Equal(0, outside.Mover.CaptureCount);
        Assert.Empty(noSafe.Mover.Movements);
        Assert.Empty(outside.Mover.Movements);
        Assert.Empty(outside.Chunks.Requests);
        Assert.Equal(1, noSafe.Chunks.Lease.DisposeCount);
        Assert.Equal(0, outside.Chunks.Lease.DisposeCount);
    }

    [Fact]
    public async Task Waypoint_search_does_not_fall_back_to_a_remote_surface_height()
    {
        var context = new TeleportTestContext();
        context.Terrain.DefaultSurfaceHeight = 200;
        context.Terrain.SetSafeFeet(0, 201, 0);

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.NoSafePosition, result);
        Assert.Equal(0, context.Terrain.SurfaceHeightReadCount);
        Assert.Empty(context.Mover.Movements);
    }

    [Fact]
    public async Task Failed_post_move_validation_restores_a_safe_snapshot_once()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = cancellationToken =>
        {
            context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Lava);
            return Task.CompletedTask;
        };

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.RolledBack, result);
        Assert.Single(context.Mover.Movements);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
        Assert.Equal(0, context.Mover.ExactRestoreAttempts);
        Assert.Equal(1, context.Chunks.Lease.DisposeCount);
    }

    [Fact]
    public async Task Rolled_back_post_validation_never_notifies_an_authoritative_position_commit()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = cancellationToken =>
        {
            context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Lava);
            return Task.CompletedTask;
        };
        var commitCount = 0;
        var service = CreateObservedService(context, () => commitCount++);

        var result = await service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.RolledBack, result);
        Assert.Equal(0, commitCount);
    }

    [Fact]
    public async Task No_safe_position_never_notifies_an_authoritative_position_commit()
    {
        var context = new TeleportTestContext();
        var commitCount = 0;
        var service = CreateObservedService(context, () => commitCount++);

        var result = await service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.NoSafePosition, result);
        Assert.Equal(0, commitCount);
    }

    [Fact]
    public async Task Chunk_timeout_never_notifies_an_authoritative_position_commit()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Chunks.Load = _ => new TaskCompletionSource<IChunkLoadLease>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        context.Clock.Delay = (_, _) => Task.CompletedTask;
        var commitCount = 0;
        var service = CreateObservedService(context, () => commitCount++);

        var result = await service.TeleportToSurfaceAsync(
            0,
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.ChunkTimeout, result);
        Assert.Equal(0, commitCount);
    }

    [Fact]
    public async Task Failed_post_move_entity_validation_rolls_back()
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Collisions.CollisionForCall = call => call >= 2;

        var result = await context.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeleportResult.RolledBack, result);
        Assert.Single(context.Mover.Movements);
        Assert.Single(context.Mover.RestoredSnapshots);
    }

    [Fact]
    public async Task Caller_cancellation_while_loading_propagates_promptly_without_movement()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Chunks.Load = _ => new TaskCompletionSource<IChunkLoadLease>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        context.Clock.Delay = (_, _) => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

        var teleport = context.Service.TeleportToSurfaceAsync(0, 0, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teleport);
        Assert.Empty(context.Mover.Movements);
    }

    [Fact]
    public async Task Cancellation_after_prevalidation_but_before_move_propagates_without_movement()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Collisions.OnQuery = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Empty(context.Mover.Movements);
        Assert.Empty(context.Mover.RestoredSnapshots);
        Assert.Equal(1, context.Chunks.Lease.DisposeCount);
    }

    [Fact]
    public async Task Cancellation_during_initial_world_validation_takes_precedence_over_out_of_world()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.IsColumnInWorldResult = false;
        context.Terrain.OnColumnQuery = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToSurfaceAsync(0, 0, cancellation.Token));

        Assert.Empty(context.Chunks.Requests);
        Assert.Empty(context.Mover.Movements);
    }

    [Fact]
    public async Task Cancellation_during_move_restores_then_propagates()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Mover.OnMove = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Single(context.Mover.Movements);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_update_restores_then_propagates()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Single(context.Mover.Movements);
        Assert.Single(context.Mover.RestoredSnapshots);
    }

    [Fact]
    public async Task Cancellation_during_post_validation_restores_then_propagates()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Collisions.OnQueryNumber = call =>
        {
            if (call == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Single(context.Mover.Movements);
        Assert.Single(context.Mover.RestoredSnapshots);
    }

    [Fact]
    public async Task Cancellation_that_also_makes_post_validation_unsafe_restores_then_propagates()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = _ =>
        {
            context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Lava);
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Single(context.Mover.Movements);
        Assert.Single(context.Mover.RestoredSnapshots);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dependency_rollback_exception_from_move_or_tick_still_restores_once(bool failDuringMove)
    {
        var context = new TeleportTestContext();
        context.Terrain.SetSafeFeet(0, 65, 0);
        var dependencyFailure = new TeleportRollbackException(
            new InvalidOperationException("dependency original"),
            new IOException("dependency restore"));
        if (failDuringMove)
        {
            context.Mover.OnMove = () => throw dependencyFailure;
        }
        else
        {
            context.Clock.WaitForUpdate = _ => throw dependencyFailure;
        }

        var thrown = await Assert.ThrowsAsync<TeleportRollbackException>(() =>
            context.Service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                TestContext.Current.CancellationToken));

        Assert.Same(dependencyFailure, thrown);
        Assert.Single(context.Mover.Movements);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Cancellation_and_fault_from_chunk_loader_propagates_cancellation_without_movement()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Chunks.Load = _ =>
        {
            cancellation.Cancel();
            return Task.FromException<IChunkLoadLease>(new InvalidOperationException("load failed"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToSurfaceAsync(0, 0, cancellation.Token));

        Assert.Empty(context.Mover.Movements);
        Assert.Equal(0, context.Mover.RestoreAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_pre_move_terrain_or_collision_fault_propagates_cancellation(bool failTerrain)
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        if (failTerrain)
        {
            context.Terrain.OnBlockReadNumber = _ =>
            {
                cancellation.Cancel();
                throw new InvalidOperationException("terrain failed");
            };
        }
        else
        {
            context.Collisions.OnQuery = () =>
            {
                cancellation.Cancel();
                throw new InvalidOperationException("collision failed");
            };
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Empty(context.Mover.Movements);
        Assert.Equal(0, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Cancellation_and_post_move_collision_fault_restores_once_then_propagates_cancellation()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Collisions.OnQueryNumber = call =>
        {
            if (call == 2)
            {
                cancellation.Cancel();
                throw new InvalidOperationException("collision failed");
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Single(context.Mover.Movements);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Cancellation_during_successful_restore_wins_over_rolled_back_result()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = _ =>
        {
            context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Lava);
            return Task.CompletedTask;
        };
        context.Mover.OnRestore = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.TeleportToWaypointAsync(new Vector3(0f, 65f, 0f), cancellation.Token));

        Assert.Single(context.Mover.Movements);
        Assert.Equal(ExpectedSafeRollback(context.Mover), Assert.Single(context.Mover.RestoredSnapshots));
        Assert.Equal(1, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Clock_or_validation_exceptions_after_move_restore_before_propagating()
    {
        foreach (var failClock in new[] { true, false })
        {
            var context = new TeleportTestContext();
            context.Terrain.SetSafeFeet(0, 65, 0);
            if (failClock)
            {
                context.Clock.WaitForUpdate = _ => throw new InvalidOperationException("clock failed");
            }
            else
            {
                context.Clock.WaitForUpdate = _ =>
                {
                    context.Terrain.ThrowOnReadNumber = context.Terrain.BlockReads.Count + 1;
                    return Task.CompletedTask;
                };
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Service.TeleportToWaypointAsync(
                    new Vector3(0f, 65f, 0f),
                    TestContext.Current.CancellationToken));

            Assert.Single(context.Mover.Movements);
            Assert.Single(context.Mover.RestoredSnapshots);
            Assert.Equal(1, context.Chunks.Lease.DisposeCount);
        }
    }

    [Fact]
    public async Task Restore_failure_is_explicit_and_never_reported_as_success()
    {
        var context = new TeleportTestContext();
        using var cancellation = new CancellationTokenSource();
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Clock.WaitForUpdate = _ => throw new InvalidOperationException("clock failed");
        context.Mover.OnRestore = cancellation.Cancel;
        context.Mover.RestoreException = new IOException("restore failed");

        var exception = await Assert.ThrowsAsync<TeleportRollbackException>(() =>
            context.Service.TeleportToWaypointAsync(
                new Vector3(0f, 65f, 0f),
                cancellation.Token));

        Assert.IsType<InvalidOperationException>(exception.OriginalFailure);
        Assert.IsType<IOException>(exception.RestoreFailure);
        Assert.Single(context.Mover.Movements);
        Assert.Equal(1, context.Mover.RestoreAttempts);
    }

    [Fact]
    public async Task Non_finite_waypoints_are_out_of_world_and_integer_extremes_do_not_overflow()
    {
        foreach (var position in new[]
                 {
                     new Vector3(float.NaN, 0f, 0f),
                     new Vector3(0f, float.PositiveInfinity, 0f),
                     new Vector3(0f, 0f, float.NegativeInfinity),
                 })
        {
            var context = new TeleportTestContext();
            Assert.Equal(
                TeleportResult.OutOfWorld,
                await context.Service.TeleportToWaypointAsync(position, TestContext.Current.CancellationToken));
            Assert.Empty(context.Chunks.Requests);
        }

        var surface = TeleportCandidate.GenerateSurface(int.MaxValue, int.MinValue).ToArray();
        var waypoint = TeleportCandidate.GenerateWaypoint(int.MinValue, int.MaxValue, int.MaxValue).ToArray();
        Assert.NotEmpty(surface);
        Assert.NotEmpty(waypoint);
        Assert.All(surface, candidate =>
        {
            Assert.InRange(candidate.X, int.MaxValue - 8, int.MaxValue);
            Assert.InRange(candidate.Z, int.MinValue, int.MinValue + 8);
        });
        Assert.All(waypoint, candidate => Assert.InRange(candidate.Y!.Value, int.MaxValue - 8, int.MaxValue));
    }

    private static PlayerMovementSnapshot ExpectedSafeRollback(FakePlayerMover mover) => mover.Snapshot with
    {
        LinearVelocity = Vector3.Zero,
        AngularVelocity = Vector3.Zero,
        FallDistance = 0f,
        IsFalling = false,
        HasPendingFallDamage = false,
    };

    private static SafeTeleportService CreateObservedService(
        TeleportTestContext context,
        Action onPositionCommitted) =>
        new(
            context.Terrain,
            context.Chunks,
            context.Mover,
            context.Collisions,
            context.Clock,
            onPositionCommitted);

    private static SafeTeleportService CreateDiagnosticService(
        TeleportTestContext context,
        List<TeleportFailureDiagnostic> diagnostics,
        Action? onPositionCommitted = null) =>
        new(
            context.Terrain,
            context.Chunks,
            context.Mover,
            context.Collisions,
            context.Clock,
            onPositionCommitted ?? (static () => { }),
            diagnostics.Add);

    private static void AssertDiagnostic(
        List<TeleportFailureDiagnostic> diagnostics,
        TeleportExecutionStage expectedStage,
        Exception expectedException)
    {
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(expectedStage, diagnostic.Stage);
        Assert.Same(expectedException, diagnostic.Exception);
    }
}

internal sealed class TeleportTestContext
{
    internal FakeTeleportTerrain Terrain { get; } = new();

    internal FakeChunkLoader Chunks { get; } = new();

    internal FakePlayerMover Mover { get; } = new();

    internal FakeEntityCollisionQuery Collisions { get; } = new();

    internal FakeTeleportClock Clock { get; } = new();

    internal SafeTeleportService Service => new(Terrain, Chunks, Mover, Collisions, Clock);
}

internal sealed class FakeTeleportTerrain : ITerrainAccess
{
    private readonly Dictionary<(int X, int Y, int Z), TeleportBlockKind> _blocks = [];
    private int _blockReadCount;

    internal int MinY { get; set; } = -256;

    internal int MaxY { get; set; } = 512;

    internal bool IsColumnInWorldResult { get; set; } = true;

    internal Action? OnColumnQuery { get; set; }

    internal int DefaultSurfaceHeight { get; set; } = 64;

    internal int SurfaceHeightReadCount { get; private set; }

    internal int? ThrowOnReadNumber { get; set; }

    internal Exception? BlockReadException { get; set; }

    internal Action<int>? OnBlockReadNumber { get; set; }

    internal List<(int X, int Y, int Z)> BlockReads { get; } = [];

    public bool IsColumnInWorld(int x, int z)
    {
        OnColumnQuery?.Invoke();
        return IsColumnInWorldResult;
    }

    public bool IsCellInWorld(int x, int y, int z) => IsColumnInWorld(x, z) && y >= MinY && y <= MaxY;

    public int GetSurfaceHeight(int x, int z)
    {
        SurfaceHeightReadCount++;
        return DefaultSurfaceHeight;
    }

    public TeleportBlockKind GetBlockKind(int x, int y, int z)
    {
        BlockReads.Add((x, y, z));
        _blockReadCount++;
        OnBlockReadNumber?.Invoke(_blockReadCount);
        if (_blockReadCount == ThrowOnReadNumber)
        {
            throw BlockReadException ?? new InvalidOperationException("terrain failed");
        }

        return _blocks.GetValueOrDefault((x, y, z), TeleportBlockKind.Air);
    }

    internal void SetSafeFeet(int x, int feetY, int z)
    {
        SetBlock(x, feetY - 1, z, TeleportBlockKind.SafeSolid);
        SetBlock(x, feetY, z, TeleportBlockKind.Air);
        SetBlock(x, feetY + 1, z, TeleportBlockKind.Air);
    }

    internal void SetBlock(int x, int y, int z, TeleportBlockKind kind) => _blocks[(x, y, z)] = kind;
}

internal sealed class FakeChunkLoader : IChunkLoader
{
    internal FakeChunkLoadLease Lease { get; } = new();

    internal Func<CancellationToken, Task<IChunkLoadLease>>? CustomLoad { get; set; }

    internal Func<CancellationToken, Task<IChunkLoadLease>> Load
    {
        get => CustomLoad ?? (_ => Task.FromResult<IChunkLoadLease>(Lease));
        set => CustomLoad = value;
    }

    internal List<(int X, int Z, int Radius)> Requests { get; } = [];

    internal CancellationToken LastToken { get; private set; }

    public Task<IChunkLoadLease> LoadAreaAsync(
        int centerX,
        int centerZ,
        int radius,
        CancellationToken cancellationToken)
    {
        Requests.Add((centerX, centerZ, radius));
        LastToken = cancellationToken;
        return Load(cancellationToken);
    }
}

internal sealed class FakeChunkLoadLease : IChunkLoadLease
{
    private int _disposed;

    internal int DisposeCount { get; private set; }

    internal TaskCompletionSource Disposed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeCount++;
        Disposed.TrySetResult();
    }
}

internal sealed class FakePlayerMover : IPlayerMover
{
    internal PlayerMovementSnapshot Snapshot { get; } = new(
        new Vector3(100f, 70f, -30f),
        Quaternion.CreateFromYawPitchRoll(1f, 0.25f, -0.5f),
        new Vector3(1f, 2f, 3f),
        new Vector3(4f, 5f, 6f),
        12.5f,
        true,
        true);

    internal int CaptureCount { get; private set; }

    internal List<PlayerMovementSnapshot> Movements { get; } = [];

    internal List<PlayerMovementSnapshot> RestoredSnapshots { get; } = [];

    internal int RestoreAttempts { get; private set; }

    internal int ExactRestoreAttempts { get; private set; }

    internal Action? OnMove { get; set; }

    internal Exception? CaptureException { get; set; }

    internal Exception? MoveException { get; set; }

    internal Action? OnRestore { get; set; }

    internal Exception? RestoreException { get; set; }

    public PlayerMovementSnapshot CaptureSnapshot()
    {
        CaptureCount++;
        if (CaptureException is not null)
        {
            throw CaptureException;
        }

        return Snapshot;
    }

    public void Move(PlayerMovementSnapshot movement)
    {
        Movements.Add(movement);
        if (MoveException is not null)
        {
            throw MoveException;
        }

        OnMove?.Invoke();
    }

    public void Restore(PlayerMovementSnapshot snapshot)
    {
        ExactRestoreAttempts++;
    }

    public void RestoreSafely(PlayerMovementSnapshot snapshot)
    {
        RestoreAttempts++;
        OnRestore?.Invoke();
        if (RestoreException is not null)
        {
            throw RestoreException;
        }

        RestoredSnapshots.Add(snapshot);
    }
}

internal sealed class FakeEntityCollisionQuery : IEntityCollisionQuery
{
    private int _queryCount;

    internal HashSet<Vector3> PlayerSelfOverlapPositions { get; } = [];

    internal HashSet<Vector3> OtherBlockingEntityPositions { get; } = [];

    internal int QueryCount => _queryCount;

    internal int ExcludedPlayerSelfOverlapCount { get; private set; }

    internal Func<int, bool>? CollisionForCall { get; set; }

    internal Action? OnQuery { get; set; }

    internal Action<int>? OnQueryNumber { get; set; }

    public bool HasBlockingCollisionExcludingPlayer(Vector3 feetPosition)
    {
        _queryCount++;
        OnQuery?.Invoke();
        OnQueryNumber?.Invoke(_queryCount);
        if (PlayerSelfOverlapPositions.Contains(feetPosition))
        {
            ExcludedPlayerSelfOverlapCount++;
        }

        return OtherBlockingEntityPositions.Contains(feetPosition)
            || CollisionForCall?.Invoke(_queryCount) == true;
    }
}

internal sealed class FakeTeleportClock : ITeleportClock
{
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } =
        (duration, cancellationToken) => Task.Delay(duration, cancellationToken);

    internal Func<CancellationToken, Task> WaitForUpdate { get; set; } = _ => Task.CompletedTask;

    internal List<TimeSpan> RequestedDelays { get; } = [];

    internal int UpdateWaitCount { get; private set; }

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        RequestedDelays.Add(duration);
        return Delay(duration, cancellationToken);
    }

    public Task WaitForNextUpdateAsync(CancellationToken cancellationToken)
    {
        UpdateWaitCount++;
        return WaitForUpdate(cancellationToken);
    }
}
