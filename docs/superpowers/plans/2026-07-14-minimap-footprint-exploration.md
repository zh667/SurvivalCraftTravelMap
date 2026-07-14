# Minimap Footprint Exploration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permanently record every already-readable terrain chunk intersecting the configured minimap footprint, including while the minimap HUD is hidden.

**Architecture:** Add a pure footprint value object that converts player position plus minimap size/scale into a nearest-first chunk catalog. Extend the existing bounded retry scheduler to track the current footprint instead of one entered chunk, then feed it from `TravelMapComponent` every update while preserving atomic 16×16 recording and the four-attempt frame budget.

**Tech Stack:** C#/.NET 10, Survivalcraft terrain chunks, existing `ExplorationRecorder`/`ExplorationTileStore`, xUnit v3.

## Global Constraints

- Footprint width and height are `MiniMapSize * MiniMapBlocksPerPixel` world blocks.
- Record the configured footprint even when `IsMiniMapVisible == false`.
- Only `TerrainChunkState.InvalidPropagatedLight` or later can be sampled.
- Never request, generate, or retain remote terrain for exploration.
- The current player chunk is attempted first; remaining chunks are nearest-first and deterministic.
- Process at most `MaximumChunkAttemptsPerFrame == 4` attempts per update.
- Remove pending chunks that leave the footprint; enqueue them again if they later re-enter.
- Retry not-ready chunks while they remain in the footprint.
- Commit each terrain chunk as one atomic 16×16 update; never expose partial pixels.
- Preserve the 64×64 `.sctm` tile format, checksums, flush cadence, and renderer budgets.
- Work test-first and commit each task separately. Do not mix unrelated files into a task commit.

---

### Task 1: Model the minimap footprint and schedule visible chunks

**Files:**

- Create: `src/SurvivalcraftTravelMap/Map/MinimapExplorationFootprint.cs`
- Modify: `src/SurvivalcraftTravelMap/Map/TerrainChunkExplorationScheduler.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/MinimapExplorationFootprintTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TerrainChunkExplorationSchedulerTests.cs`

**Interfaces:**

- Consumes: `TerrainChunkCoordinate.FromWorld(int, int)` and `TerrainChunkCoordinate.Size == 16`.
- Produces: `MinimapExplorationFootprint.Create(float playerX, float playerZ, int sizePixels, float blocksPerPixel)`.
- Produces: `MinimapExplorationFootprint.ChunksNearestFirst` and `CenterChunk`.
- Produces: `TerrainChunkExplorationScheduler.ObserveFootprint(MinimapExplorationFootprint footprint)`.

- [ ] **Step 1: Write failing footprint tests**

Add tests with these exact assertions:

```csharp
[Theory]
[InlineData(256, 0.5f, 8, 8)]
[InlineData(256, 1f, 16, 16)]
public void Footprint_covers_every_chunk_intersecting_the_minimap_square(
    int size,
    float blocksPerPixel,
    int minimumChunksWide,
    int minimumChunksHigh)
{
    var footprint = MinimapExplorationFootprint.Create(8f, 8f, size, blocksPerPixel);

    Assert.True(footprint.MaximumChunk.X - footprint.MinimumChunk.X + 1 >= minimumChunksWide);
    Assert.True(footprint.MaximumChunk.Z - footprint.MinimumChunk.Z + 1 >= minimumChunksHigh);
    Assert.Equal(footprint.CenterChunk, footprint.ChunksNearestFirst[0]);
    Assert.Equal(footprint.ChunksNearestFirst.Count, footprint.ChunksNearestFirst.Distinct().Count());
}

[Theory]
[InlineData(-0.25f, -1)]
[InlineData(-16.25f, -2)]
[InlineData(15.75f, 0)]
[InlineData(16.25f, 1)]
public void Center_chunk_uses_floor_coordinates_at_negative_boundaries(float coordinate, int expected)
{
    var footprint = MinimapExplorationFootprint.Create(coordinate, coordinate, 160, 0.5f);

    Assert.Equal(new TerrainChunkCoordinate(expected, expected), footprint.CenterChunk);
}

[Theory]
[InlineData(float.NaN)]
[InlineData(float.PositiveInfinity)]
[InlineData(float.NegativeInfinity)]
public void Nonfinite_player_or_scale_values_are_rejected(float value)
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        MinimapExplorationFootprint.Create(value, 0f, 192, 1f));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        MinimapExplorationFootprint.Create(0f, 0f, 192, value));
}
```

Also assert `sizePixels <= 0` and `blocksPerPixel <= 0` throw, and assert nearest-first ordering is by squared chunk distance, then X, then Z.

- [ ] **Step 2: Run the footprint tests and verify RED**

Run:

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~MinimapExplorationFootprintTests'
```

Expected: compilation fails because `MinimapExplorationFootprint` does not exist.

- [ ] **Step 3: Implement the footprint value object**

Create this public shape and use a half-open world rectangle so exact boundaries do not add an extra chunk:

```csharp
namespace SurvivalcraftTravelMap.Map;

public sealed class MinimapExplorationFootprint
{
    private MinimapExplorationFootprint(
        TerrainChunkCoordinate centerChunk,
        TerrainChunkCoordinate minimumChunk,
        TerrainChunkCoordinate maximumChunk,
        IReadOnlyList<TerrainChunkCoordinate> chunksNearestFirst)
    {
        CenterChunk = centerChunk;
        MinimumChunk = minimumChunk;
        MaximumChunk = maximumChunk;
        ChunksNearestFirst = chunksNearestFirst;
    }

    public TerrainChunkCoordinate CenterChunk { get; }
    public TerrainChunkCoordinate MinimumChunk { get; }
    public TerrainChunkCoordinate MaximumChunk { get; }
    public IReadOnlyList<TerrainChunkCoordinate> ChunksNearestFirst { get; }

    public static MinimapExplorationFootprint Create(
        float playerX,
        float playerZ,
        int sizePixels,
        float blocksPerPixel)
    {
        if (!float.IsFinite(playerX) || !float.IsFinite(playerZ))
            throw new ArgumentOutOfRangeException(nameof(playerX));
        if (sizePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizePixels));
        if (!float.IsFinite(blocksPerPixel) || blocksPerPixel <= 0f)
            throw new ArgumentOutOfRangeException(nameof(blocksPerPixel));

        var halfExtent = (double)sizePixels * blocksPerPixel / 2d;
        var minimumWorldX = CheckedFloor(playerX - halfExtent);
        var minimumWorldZ = CheckedFloor(playerZ - halfExtent);
        var maximumWorldX = CheckedCeilingMinusOne(playerX + halfExtent);
        var maximumWorldZ = CheckedCeilingMinusOne(playerZ + halfExtent);
        var center = TerrainChunkCoordinate.FromWorld(CheckedFloor(playerX), CheckedFloor(playerZ));
        var minimum = TerrainChunkCoordinate.FromWorld(minimumWorldX, minimumWorldZ);
        var maximum = TerrainChunkCoordinate.FromWorld(maximumWorldX, maximumWorldZ);
        var chunks = Enumerate(minimum, maximum, center);
        return new MinimapExplorationFootprint(center, minimum, maximum, chunks);
    }

    private static int CheckedFloor(double value) => checked((int)Math.Floor(value));
    private static int CheckedCeilingMinusOne(double value) => checked((int)Math.Ceiling(value) - 1);

    private static IReadOnlyList<TerrainChunkCoordinate> Enumerate(
        TerrainChunkCoordinate minimum,
        TerrainChunkCoordinate maximum,
        TerrainChunkCoordinate center)
    {
        var chunks = new List<TerrainChunkCoordinate>();
        for (var z = (long)minimum.Z; z <= maximum.Z; z++)
        for (var x = (long)minimum.X; x <= maximum.X; x++)
            chunks.Add(new TerrainChunkCoordinate(checked((int)x), checked((int)z)));

        return chunks
            .OrderBy(chunk => DistanceSquared(chunk, center))
            .ThenBy(chunk => chunk.X)
            .ThenBy(chunk => chunk.Z)
            .ToArray();
    }

    private static long DistanceSquared(TerrainChunkCoordinate left, TerrainChunkCoordinate right)
    {
        var dx = (long)left.X - right.X;
        var dz = (long)left.Z - right.Z;
        return dx * dx + dz * dz;
    }
}
```

Guard loop overflow at `int.MaxValue` by iterating with `long` and checked conversion, while keeping the public chunk coordinates as `int`.

- [ ] **Step 4: Write failing scheduler tests**

Replace single-chunk assumptions with footprint behavior:

```csharp
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
```

Retain tests for maximum-count validation, round-robin retry fairness, `MarkCompleted`, and `Clear`.

- [ ] **Step 5: Run scheduler tests and verify RED**

Run:

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TerrainChunkExplorationSchedulerTests|FullyQualifiedName~MinimapExplorationFootprintTests'
```

Expected: scheduler tests fail because `ObserveFootprint` is absent and the old scheduler tracks only `_current`.

- [ ] **Step 6: Implement footprint-aware pending work**

Use these fields and method contract:

```csharp
private readonly HashSet<TerrainChunkCoordinate> _visible = [];
private TerrainChunkCoordinate? _center;

public bool ObserveFootprint(MinimapExplorationFootprint footprint)
{
    ArgumentNullException.ThrowIfNull(footprint);
    var nextVisible = footprint.ChunksNearestFirst.ToHashSet();
    var changed = !_visible.SetEquals(nextVisible);

    foreach (var leaving in _visible.Where(chunk => !nextVisible.Contains(chunk)).ToArray())
        RemovePending(leaving);

    foreach (var entering in footprint.ChunksNearestFirst.Where(chunk => !_visible.Contains(chunk)))
        EnqueueLast(entering);

    _visible.Clear();
    _visible.UnionWith(nextVisible);
    _center = footprint.CenterChunk;
    MovePendingToFront(footprint.CenterChunk);
    return changed;
}
```

Keep one node per chunk in `_nodes`. `GetPendingAttempts` returns the pending center first, then rotates other pending nodes round-robin. `Clear` empties `_pending`, `_nodes`, `_visible`, and `_center`.

- [ ] **Step 7: Run focused tests and verify GREEN**

Run the command from Step 5.

Expected: all footprint and scheduler tests pass.

- [ ] **Step 8: Commit Task 1**

```powershell
git add src/SurvivalcraftTravelMap/Map/MinimapExplorationFootprint.cs src/SurvivalcraftTravelMap/Map/TerrainChunkExplorationScheduler.cs tests/SurvivalcraftTravelMap.Tests/MinimapExplorationFootprintTests.cs tests/SurvivalcraftTravelMap.Tests/TerrainChunkExplorationSchedulerTests.cs
git diff --cached --check
git commit -m "feat: explore the configured minimap footprint"
```

---

### Task 2: Feed the footprint from the live player and settings

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs`

**Interfaces:**

- Consumes: `MinimapExplorationFootprint.Create(float, float, int, float)` from Task 1.
- Consumes: `TerrainChunkExplorationScheduler.ObserveFootprint(...)` from Task 1.
- Preserves: `ExplorationRecorder.RecordChunk(TerrainChunkCoordinate)` and four attempts per frame.

- [ ] **Step 1: Write failing component contract tests**

Update the extracted `UpdateExploration` assertions to require:

```csharp
AssertCodeContains(exploration, "MinimapExplorationFootprint.Create(");
AssertCodeContains(exploration, "_settings.MiniMapSize");
AssertCodeContains(exploration, "_settings.MiniMapBlocksPerPixel");
AssertCodeContains(exploration, "_explorationScheduler.ObserveFootprint(footprint);");
AssertCodeDoesNotContain(exploration, "ObservePlayerPosition");
AssertCodeDoesNotContain(exploration, "_settings.IsMiniMapVisible");
AssertCodeContains(exploration, "GetPendingAttempts(MaximumChunkAttemptsPerFrame)");
```

Retain assertions that `UpdateExploration()` runs before the `_miniMap` visibility/null early return.

- [ ] **Step 2: Run the component contract test and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests'
```

Expected: FAIL because `UpdateExploration` still observes only the player's current chunk.

- [ ] **Step 3: Integrate the footprint without consulting HUD visibility**

Use the live floating-point position and normalized settings:

```csharp
private void UpdateExploration()
{
    if (_explorationRecorder is null || _settings is null)
        return;

    var position = Player.ComponentBody.Position;
    var footprint = MinimapExplorationFootprint.Create(
        position.X,
        position.Z,
        _settings.MiniMapSize,
        _settings.MiniMapBlocksPerPixel);
    _explorationScheduler.ObserveFootprint(footprint);

    foreach (var chunk in _explorationScheduler.GetPendingAttempts(MaximumChunkAttemptsPerFrame))
    {
        try
        {
            var result = _explorationRecorder.RecordChunk(chunk);
            if (result == ExplorationRecordResult.Recorded)
            {
                _explorationScheduler.MarkCompleted(chunk);
            }
            else if (result == ExplorationRecordResult.Pressure
                     && !_explorationPressureWarningShown)
            {
                _explorationPressureWarningShown = true;
                ShowMessage("地图存储持续失败；已暂停记录新区块，现有探索仍会保留并重试保存");
            }
        }
        catch (Exception exception)
        {
            var errorSignature = $"{exception.GetType().FullName}: {exception.Message}";
            if (_explorationFailureWarnings.Add((chunk, errorSignature)))
            {
                Engine.Log.Warning(
                    $"[TravelMap] Terrain chunk ({chunk.X}, {chunk.Z}) exploration failed: {errorSignature}");
            }
        }
    }
}
```

Do not add a visibility check and do not call `SubsystemTerrain.TerrainUpdater` or any chunk-request API.

- [ ] **Step 4: Add recorder coverage for a multi-chunk footprint sequence**

Add this test, using the existing `FakeTerrainMapSource`, `CreateRecorder`, and `AssertRegion` helpers:

```csharp
[Fact]
public void Footprint_sequence_records_four_atomic_chunks_and_preserves_an_unreadable_target()
{
    using var directory = new TemporaryDirectory();
    var expected = new Rgba32(10, 20, 30, 255);
    var store = new ExplorationTileStore(directory.Path);
    var ready = new FakeTerrainMapSource(defaultContent: 1, isReady: true);
    var recorder = CreateRecorder(ready, store, expected);
    var scheduler = new TerrainChunkExplorationScheduler();
    var footprint = MinimapExplorationFootprint.Create(16f, 16f, 32, 1f);
    scheduler.ObserveFootprint(footprint);

    foreach (var chunk in scheduler.GetPendingAttempts(4))
    {
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));
        scheduler.MarkCompleted(chunk);
    }

    Assert.Equal(0, scheduler.PendingCount);
    foreach (var chunk in footprint.ChunksNearestFirst)
    {
        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        AssertRegion(
            store.GetOrLoad(coordinate.TileX, coordinate.TileZ),
            coordinate.LocalX,
            coordinate.LocalZ,
            expected);
    }

    var unreadable = new FakeTerrainMapSource(defaultContent: 1, isReady: false);
    var unreadableRecorder = CreateRecorder(unreadable, store, expected);
    var next = MinimapExplorationFootprint.Create(40f, 8f, 16, 1f);
    scheduler.ObserveFootprint(next);
    var pending = scheduler.GetPendingAttempts(1)[0];
    Assert.Equal(ExplorationRecordResult.NotReady, unreadableRecorder.RecordChunk(pending));
    Assert.Equal(1, scheduler.PendingCount);
    var pendingCoordinate = TileCoordinate.FromWorld(pending.OriginX, pending.OriginZ);
    Assert.False(store.GetOrLoad(pendingCoordinate.TileX, pendingCoordinate.TileZ).TryGetPixel(
        pendingCoordinate.LocalX,
        pendingCoordinate.LocalZ,
        out _));
}
```

- [ ] **Step 5: Run focused exploration tests and verify GREEN**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~ExplorationRecorderTests|FullyQualifiedName~TerrainChunkExplorationSchedulerTests|FullyQualifiedName~MinimapExplorationFootprintTests'
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs
git diff --cached --check
git commit -m "feat: persist loaded terrain across the minimap view"
```

---

### Task 3: Prove zoom/pan cannot hide recorded footprint tiles

**Files:**

- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`

**Interfaces:**

- Consumes: existing `TravelMapRenderModel.RenderTerrain`, `MapTransform`, `TravelMapUiController.HandleWheel`, and `HandlePan`.
- Produces: regression evidence only; production renderer changes are allowed only if a new test exposes a real transform or tile-catalog defect.

- [ ] **Step 1: Add the sparse-footprint transform regression**

Create known 64×64 tiles around negative and positive coordinates, then run this scale sequence around a fixed pointer:

```csharp
var scales = new[] { 2f, 0.5f, 0.35355335f, 0.25f, 1f, 8f, 2f };
foreach (var scale in scales)
{
    transform = transform with { BlocksPerPixel = scale };
    var sink = new RecordingMapSink();
    var stats = TravelMapRenderModel.RenderTerrain(source, transform, 1f, sink);
    Assert.InRange(stats.PixelQueries, 0, TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
    Assert.All(sink.Cells, cell => Assert.True(float.IsFinite(cell.ScreenMinimum.X)));
}
```

Add repeated `HandlePan` calls between scale changes and assert `ScreenToWorld(WorldToScreen(point))` returns the original point within `0.001f`. When a known pixel is inside the viewport, assert the sink contains its world coordinate after every scale/pan round-trip.

- [ ] **Step 2: Run the render tests**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapRenderBudgetTests|FullyQualifiedName~TravelMapUiStateTests'
```

Expected: PASS. If a new assertion fails, make the smallest renderer/transform correction that preserves the existing global tile-catalog and 262,144-sample guarantees, then rerun until green.

- [ ] **Step 3: Run the complete automated gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: every test passes; build reports zero warnings and zero errors.

- [ ] **Step 4: Commit Task 3**

```powershell
git add tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs src/SurvivalcraftTravelMap/UI/TravelMapRenderModel.cs src/SurvivalcraftTravelMap/UI/TravelMapUiController.cs
git diff --cached --check
git commit -m "test: cover footprint rendering through pan and zoom"
```

Do not stage production files if the regression passes without production changes.
