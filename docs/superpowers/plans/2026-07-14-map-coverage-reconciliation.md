# Map Coverage Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair persistent checkerboard holes by reconciling the loaded minimap footprint against actual 16×16 explored-pixel coverage, with current-chunk priority and bounded retry work.

**Architecture:** Add a lock-safe region completeness query to `MapTile`, route it through the tile store's known-tile catalog and `ExplorationRecorder`, and teach `TerrainChunkExplorationScheduler` to compare its visible catalog with that predicate a few chunks per update. `TravelMapComponent` invokes reconciliation before sampling, so teleportation, entering an old hole, and delayed terrain readiness all converge on the existing atomic recorder without force-loading terrain or materializing empty map tiles merely to check them.

**Tech Stack:** C#/.NET 10, existing 64×64 `.sctm` tiles, 16×16 terrain chunks, xUnit v3.

## Global Constraints

- Persistent explored-pixel coverage is the source of truth; scheduler queues are only performance state.
- A terrain chunk is complete only when all 256 pixels in its 16×16 area are explored and opaque.
- Only already-readable game terrain may be recorded; never request or generate remote chunks.
- The current player chunk is reconciled and attempted first.
- Coverage checks and recording attempts remain bounded per update.
- Not-ready chunks stay pending and retry later without log spam.
- Successful recording remains one atomic 16×16 mutation.
- Keep `MaximumChunkAttemptsPerFrame == 4` and add `MaximumCoverageChecksPerFrame == 4`.
- Preserve the `.sctm` format, checksum behavior, save queue, invitation logic, and teleport behavior.
- Work test-first and commit each task separately. Do not mix unrelated files into a task commit.

---

### Task 1: Query complete 16×16 persistent coverage

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Map/MapTile.cs:123-168`
- Modify: `src/SurvivalcraftTravelMap/Persistence/ExplorationTileStore.cs:130-178`
- Modify: `src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs:12-47`
- Modify: `tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs`

**Interfaces:**

- Consumes: `MapTile.TryGetPixelCore(byte[], byte[], int, out Rgba32)`.
- Produces: `MapTile.IsRegionFullyExplored(int x, int z, int width, int height) : bool`.
- Produces: `ExplorationTileStore.IsRegionFullyExplored(int tileX, int tileZ, int x, int z, int width, int height) : bool`.
- Produces: `ExplorationRecorder.IsChunkFullyExplored(TerrainChunkCoordinate chunk) : bool`.

- [ ] **Step 1: Write failing coverage-query tests**

Add these tests to `ExplorationRecorderTests`:

```csharp
[Fact]
public void Chunk_coverage_requires_all_256_opaque_pixels_and_repairs_after_recording()
{
    using var directory = new TemporaryDirectory();
    var store = new ExplorationTileStore(directory.Path);
    var chunk = new TerrainChunkCoordinate(2, -3);
    var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
    var tile = store.GetOrLoad(coordinate.TileX, coordinate.TileZ);
    var recorder = CreateRecorder(
        new FakeTerrainMapSource(defaultContent: 1),
        store,
        new Rgba32(10, 20, 30, 255));

    var diagnosticsBeforeUnknownQuery = store.Diagnostics;
    Assert.False(recorder.IsChunkFullyExplored(chunk));
    Assert.Equal(diagnosticsBeforeUnknownQuery, store.Diagnostics);
    tile.SetRegion(
        coordinate.LocalX,
        coordinate.LocalZ,
        TerrainChunkCoordinate.Size,
        TerrainChunkCoordinate.Size,
        Enumerable.Repeat(
            new Rgba32(1, 2, 3, 255),
            TerrainChunkCoordinate.PixelCount).ToArray());
    tile.SetPixel(coordinate.LocalX + 15, coordinate.LocalZ + 15, default);
    Assert.False(recorder.IsChunkFullyExplored(chunk));

    Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));
    Assert.True(recorder.IsChunkFullyExplored(chunk));
}

[Fact]
public void Chunk_coverage_uses_floor_tiles_for_negative_chunk_coordinates()
{
    using var directory = new TemporaryDirectory();
    var store = new ExplorationTileStore(directory.Path);
    var recorder = CreateRecorder(
        new FakeTerrainMapSource(defaultContent: 1),
        store,
        new Rgba32(10, 20, 30, 255));
    var chunk = new TerrainChunkCoordinate(-1, -4);

    Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));

    Assert.True(recorder.IsChunkFullyExplored(chunk));
    Assert.True(store.ContainsKnownTile(-1, -1));
}
```

Also add the exact bounds theory:

```csharp
[Theory]
[InlineData(-1, 0, 1, 1)]
[InlineData(0, -1, 1, 1)]
[InlineData(0, 0, 0, 1)]
[InlineData(0, 0, 1, 0)]
[InlineData(63, 0, 2, 1)]
[InlineData(0, 63, 1, 2)]
public void Region_coverage_rejects_invalid_bounds(int x, int z, int width, int height)
{
    var tile = new MapTile(0, 0);

    Assert.ThrowsAny<ArgumentOutOfRangeException>(
        () => tile.IsRegionFullyExplored(x, z, width, height));
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~ExplorationRecorderTests'
```

Expected: compilation fails because `IsChunkFullyExplored` and `IsRegionFullyExplored` do not exist.

- [ ] **Step 3: Implement the lock-safe map region query**

Add this public method to `MapTile` immediately after `TryGetPixel`:

```csharp
public bool IsRegionFullyExplored(int x, int z, int width, int height)
{
    if ((uint)x >= Size)
        throw new ArgumentOutOfRangeException(nameof(x));
    if ((uint)z >= Size)
        throw new ArgumentOutOfRangeException(nameof(z));
    if (width <= 0)
        throw new ArgumentOutOfRangeException(nameof(width));
    if (height <= 0)
        throw new ArgumentOutOfRangeException(nameof(height));
    if (checked(x + width) > Size)
        throw new ArgumentOutOfRangeException(nameof(width));
    if (checked(z + height) > Size)
        throw new ArgumentOutOfRangeException(nameof(height));

    lock (_sync)
    {
        for (var localZ = 0; localZ < height; localZ++)
        {
            var rowStart = ((z + localZ) * Size) + x;
            for (var localX = 0; localX < width; localX++)
            {
                if (!TryGetPixelCore(_explored, _colors, rowStart + localX, out _))
                    return false;
            }
        }

        return true;
    }
}
```

The query checks both the explored bit and nonzero alpha through `TryGetPixelCore`; do not inspect only the bit mask because legacy transparent pixels are intentionally treated as unexplored.

Add this catalog-aware method to `ExplorationTileStore` immediately before `GetOrLoad`:

```csharp
public bool IsRegionFullyExplored(
    int tileX,
    int tileZ,
    int x,
    int z,
    int width,
    int height)
{
    if (!ContainsKnownTile(tileX, tileZ))
        return false;

    return GetOrLoad(tileX, tileZ).IsRegionFullyExplored(x, z, width, height);
}
```

The known-tile catalog never removes entries during a store lifetime, so the two calls cannot turn a known tile back into an unknown tile. This fast path must leave `TileMaterializations`, `FileProbeCount`, and `DiskReadAttempts` unchanged for a never-recorded tile.

- [ ] **Step 4: Expose terrain-chunk coverage through the recorder**

Add this method before `RecordChunk`:

```csharp
public bool IsChunkFullyExplored(TerrainChunkCoordinate chunk)
{
    var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
    return _tileStore.IsRegionFullyExplored(
        coordinate.TileX,
        coordinate.TileZ,
        coordinate.LocalX,
        coordinate.LocalZ,
        TerrainChunkCoordinate.Size,
        TerrainChunkCoordinate.Size);
}
```

Terrain chunk origins are multiples of 16 and map tiles are 64, so the queried 16×16 region always stays inside one tile, including negative coordinates.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the command from Step 2.

Expected: every `ExplorationRecorderTests` test passes.

- [ ] **Step 6: Commit Task 1**

```powershell
git add src/SurvivalcraftTravelMap/Map/MapTile.cs src/SurvivalcraftTravelMap/Persistence/ExplorationTileStore.cs src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs
git diff --cached --check
git commit -m "feat: query persistent chunk coverage"
```

---

### Task 2: Reconcile visible chunks with persistent coverage

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Map/TerrainChunkExplorationScheduler.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TerrainChunkExplorationSchedulerTests.cs`

**Interfaces:**

- Consumes: `MinimapExplorationFootprint.ChunksNearestFirst` and `.CenterChunk`.
- Produces: `TerrainChunkExplorationScheduler.ReconcileCoverage(Func<TerrainChunkCoordinate, bool> isFullyExplored, int maximumChecks) : int`.
- Preserves: `ObserveFootprint`, `GetPendingAttempts`, `MarkCompleted`, and pending deduplication.

- [ ] **Step 1: Write failing reconciliation tests**

Add these tests:

```csharp
[Fact]
public void Reconciliation_reenqueues_a_persistently_incomplete_chunk_marked_completed_in_memory()
{
    var scheduler = new TerrainChunkExplorationScheduler();
    var footprint = MinimapExplorationFootprint.Create(8f, 8f, 32, 1f);
    scheduler.ObserveFootprint(footprint);
    scheduler.MarkCompleted(footprint.CenterChunk);

    var checks = scheduler.ReconcileCoverage(
        chunk => chunk != footprint.CenterChunk,
        maximumChecks: 4);

    Assert.Equal(4, checks);
    Assert.Equal(footprint.CenterChunk, scheduler.GetPendingAttempts(1)[0]);
}

[Fact]
public void Reconciliation_removes_complete_chunks_and_deduplicates_incomplete_chunks()
{
    var scheduler = new TerrainChunkExplorationScheduler();
    var footprint = MinimapExplorationFootprint.Create(16f, 16f, 16, 1f);
    scheduler.ObserveFootprint(footprint);
    var incomplete = footprint.ChunksNearestFirst[1];

    scheduler.ReconcileCoverage(chunk => chunk != incomplete, maximumChecks: 4);
    scheduler.ReconcileCoverage(chunk => chunk != incomplete, maximumChecks: 4);

    Assert.Equal(1, scheduler.PendingCount);
    Assert.Equal(incomplete, scheduler.GetPendingAttempts(1)[0]);
}

[Fact]
public void Reconciliation_checks_the_current_chunk_first_then_advances_a_bounded_cursor()
{
    var scheduler = new TerrainChunkExplorationScheduler();
    var footprint = MinimapExplorationFootprint.Create(8f, 8f, 64, 1f);
    scheduler.ObserveFootprint(footprint);
    var first = new List<TerrainChunkCoordinate>();
    var second = new List<TerrainChunkCoordinate>();

    Assert.Equal(4, scheduler.ReconcileCoverage(chunk => { first.Add(chunk); return true; }, 4));
    Assert.Equal(4, scheduler.ReconcileCoverage(chunk => { second.Add(chunk); return true; }, 4));

    Assert.Equal(footprint.CenterChunk, first[0]);
    Assert.Equal(footprint.CenterChunk, second[0]);
    Assert.Equal(4, first.Distinct().Count());
    Assert.Equal(4, second.Distinct().Count());
    Assert.False(first.Skip(1).SequenceEqual(second.Skip(1)));
}
```

Add null-predicate and nonpositive-limit tests. Extend `Clear_resets_visible_identity_and_pending_state` to prove reconciliation returns zero after `Clear` without invoking the predicate.

- [ ] **Step 2: Run scheduler tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TerrainChunkExplorationSchedulerTests'
```

Expected: compilation fails because `ReconcileCoverage` is absent.

- [ ] **Step 3: Retain nearest-first visible order**

Add fields:

```csharp
private IReadOnlyList<TerrainChunkCoordinate> _visibleNearestFirst = Array.Empty<TerrainChunkCoordinate>();
private int _coverageCursor;
```

At the end of `ObserveFootprint`, update the ordered catalog and reset only when the visible set changes:

```csharp
if (changed)
{
    _visibleNearestFirst = footprint.ChunksNearestFirst.ToArray();
    _coverageCursor = 0;
}

_center = footprint.CenterChunk;
MovePendingToFront(footprint.CenterChunk);
return changed;
```

If the set is unchanged but the center changes, retain the cursor and still update `_center`; this gives the newly entered current chunk first priority without restarting the whole scan.

Keep the public compatibility method `ObservePlayerPosition` internally consistent by adding this after `_visible.Add(chunk)` and before assigning `_center`:

```csharp
if (changed)
{
    _visibleNearestFirst = [chunk];
    _coverageCursor = 0;
}
```

- [ ] **Step 4: Implement bounded coverage reconciliation**

Add:

```csharp
public int ReconcileCoverage(
    Func<TerrainChunkCoordinate, bool> isFullyExplored,
    int maximumChecks)
{
    ArgumentNullException.ThrowIfNull(isFullyExplored);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChecks);
    if (_visibleNearestFirst.Count == 0)
        return 0;

    var candidates = new List<TerrainChunkCoordinate>(
        Math.Min(maximumChecks, _visibleNearestFirst.Count));
    if (_center is TerrainChunkCoordinate center)
        candidates.Add(center);

    var examined = 0;
    while (candidates.Count < maximumChecks && examined < _visibleNearestFirst.Count)
    {
        if (_coverageCursor >= _visibleNearestFirst.Count)
            _coverageCursor = 0;

        var candidate = _visibleNearestFirst[_coverageCursor++];
        examined++;
        if (!candidates.Contains(candidate))
            candidates.Add(candidate);
    }

    foreach (var candidate in candidates)
    {
        if (isFullyExplored(candidate))
        {
            MarkCompleted(candidate);
        }
        else if (!_nodes.ContainsKey(candidate))
        {
            EnqueueLast(candidate);
        }
    }

    if (_center is TerrainChunkCoordinate current)
        MovePendingToFront(current);
    return candidates.Count;
}
```

Update `Clear`:

```csharp
_visibleNearestFirst = Array.Empty<TerrainChunkCoordinate>();
_coverageCursor = 0;
```

Do not remove an incomplete chunk merely because an attempt returned `NotReady`; only leaving the footprint or a positive persistent-coverage result removes it.

- [ ] **Step 5: Run scheduler tests and verify GREEN**

Run the command from Step 2.

Expected: all scheduler tests pass, including bounded cursor progression and deduplication.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src/SurvivalcraftTravelMap/Map/TerrainChunkExplorationScheduler.cs tests/SurvivalcraftTravelMap.Tests/TerrainChunkExplorationSchedulerTests.cs
git diff --cached --check
git commit -m "feat: reconcile exploration coverage"
```

---

### Task 3: Run reconciliation from the live component

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs:85-95,857-910`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs`

**Interfaces:**

- Consumes: `ExplorationRecorder.IsChunkFullyExplored` from Task 1.
- Consumes: `TerrainChunkExplorationScheduler.ReconcileCoverage` from Task 2.
- Preserves: four `RecordChunk` attempts per update and footprint identity recomputation.

- [ ] **Step 1: Write failing component contract assertions**

In the exploration contract test, require these exact source properties:

```csharp
AssertCodeContains(source, "private const int MaximumCoverageChecksPerFrame = 4;");
AssertCodeContains(exploration, "_explorationScheduler.ReconcileCoverage(");
AssertCodeContains(exploration, "_explorationRecorder.IsChunkFullyExplored");
AssertCodeContains(exploration, "MaximumCoverageChecksPerFrame");
Assert.True(
    exploration.IndexOf("ReconcileCoverage", StringComparison.Ordinal)
    < exploration.IndexOf("GetPendingAttempts", StringComparison.Ordinal));
AssertCodeDoesNotContain(exploration, "_settings.IsMiniMapVisible");
```

Retain the existing assertions for `MaximumChunkAttemptsPerFrame == 4`, footprint identity, and no terrain force-load API.

Add this delayed-readiness regression to `ExplorationRecorderTests`:

```csharp
[Fact]
public void Reconciled_teleport_footprint_survives_not_ready_then_records_when_ready()
{
    using var directory = new TemporaryDirectory();
    var store = new ExplorationTileStore(directory.Path);
    var scheduler = new TerrainChunkExplorationScheduler();
    var footprint = MinimapExplorationFootprint.Create(1000f, -1000f, 16, 1f);
    var chunk = footprint.CenterChunk;
    scheduler.ObserveFootprint(footprint);
    scheduler.MarkCompleted(chunk);
    var notReadyRecorder = CreateRecorder(
        new FakeTerrainMapSource(defaultContent: 1, isReady: false),
        store,
        new Rgba32(10, 20, 30, 255));
    scheduler.ReconcileCoverage(notReadyRecorder.IsChunkFullyExplored, maximumChecks: 4);
    Assert.Equal(ExplorationRecordResult.NotReady, notReadyRecorder.RecordChunk(chunk));
    Assert.Equal(chunk, scheduler.GetPendingAttempts(1)[0]);

    var readyRecorder = CreateRecorder(
        new FakeTerrainMapSource(defaultContent: 1, isReady: true),
        store,
        new Rgba32(10, 20, 30, 255));
    Assert.Equal(ExplorationRecordResult.Recorded, readyRecorder.RecordChunk(chunk));
    scheduler.MarkCompleted(chunk);

    Assert.True(readyRecorder.IsChunkFullyExplored(chunk));
    Assert.Equal(0, scheduler.PendingCount);
}
```

The test uses a new distant footprint to model teleport arrival, deliberately starts with stale in-memory completion, then proves a transient unreadable surface does not lose the retry.

- [ ] **Step 2: Run the package structure tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests'
```

Expected: the new coverage-reconciliation assertions fail.

- [ ] **Step 3: Integrate bounded reconciliation before recording attempts**

Add beside the existing attempt constant:

```csharp
private const int MaximumCoverageChecksPerFrame = 4;
```

In `UpdateExploration`, use this exact insertion point after footprint observation and before `GetPendingAttempts`:

```csharp
if (_explorationFootprintIdentity != footprintIdentity)
{
    _explorationFootprintIdentity = footprintIdentity;
    var footprint = MinimapExplorationFootprint.Create(footprintIdentity);
    _explorationScheduler.ObserveFootprint(footprint);
}

_explorationScheduler.ReconcileCoverage(
    _explorationRecorder.IsChunkFullyExplored,
    MaximumCoverageChecksPerFrame);
```

The existing `foreach (var chunk in _explorationScheduler.GetPendingAttempts(MaximumChunkAttemptsPerFrame))` block follows immediately after this insertion without changing its `Recorded`, `NotReady`, `Pressure`, or exception branches. Do not add timers, teleport callbacks, minimap-visibility checks, or chunk-load requests. Every update is already bounded; a teleport or chunk-boundary crossing changes footprint identity, and the current chunk is the first reconciled candidate.

- [ ] **Step 4: Run focused coverage tests and verify GREEN**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~ExplorationRecorderTests|FullyQualifiedName~TerrainChunkExplorationSchedulerTests|FullyQualifiedName~MinimapExplorationFootprintTests'
```

Expected: all selected tests pass.

- [ ] **Step 5: Run the full suite and warning-as-error build**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: all tests pass and the build reports zero warnings and zero errors.

- [ ] **Step 6: Commit Task 3**

```powershell
git add src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs
git diff --cached --check
git commit -m "fix: retry incomplete map coverage"
```

After this commit, hand the task to an independent reviewer. Critical or Important findings must be fixed and re-reviewed before starting the rendering plan.
