# Chunk-Driven Exploration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reveal exactly the `16×16` terrain chunk personally entered by the player, as one atomic map update on the first frame when its surface data is readable.

**Architecture:** Convert world positions to explicit terrain-chunk identities, queue only entered chunks, retry pending chunks every update after terrain processing, sample all 256 colors into a temporary buffer, and commit the buffer to one existing `64×64` map tile under one lock/version increment. Keep the `.sctm` format, tile cache, renderer, and flush cadence unchanged.

**Tech Stack:** C#/.NET 10, Survivalcraft `TerrainChunkState`, xUnit v3, existing `ExplorationTileStore` persistence.

## Global Constraints

- A terrain chunk is exactly `16×16` world columns.
- Entering a chunk reveals only that chunk; never reveal neighboring chunks merely because they are loaded or visible.
- Correctly floor negative world coordinates: `-1` and `-16` are chunk `-1`; `-17` is chunk `-2`.
- Do not expose a partially sampled chunk. Commit all 256 opaque colors together or commit nothing.
- Treat `TerrainChunkState.InvalidPropagatedLight` and later states as surface-readable; do not wait for mesh state `Valid`.
- Retry a not-ready current/previously-entered chunk every frame. Do not restore the old 0.5-second stationary throttle.
- Re-entering a completed chunk resamples all 256 columns so changed terrain and old partial/transparent cache data are repaired.
- `ExplorationRecordResult.Pressure`, `NotReady`, or an exception leaves the chunk pending.
- Keep `MapTile.Size == 64`, tile encoding version, filenames, checksums, flush interval, and renderer sampling unchanged.
- Before Task 1, complete the shared dirty-worktree baseline task in `2026-07-14-teleport-runtime-repair.md`. This plan assumes that baseline commit exists.
- Work test-first and commit each task separately. Do not include unrelated dirty-worktree files in a task commit.

---

### Task 1: Add terrain-chunk coordinates and a retry scheduler

**Files:**

- Create: `src/SurvivalcraftTravelMap/Map/TerrainChunkCoordinate.cs`
- Create: `src/SurvivalcraftTravelMap/Map/TerrainChunkExplorationScheduler.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TerrainChunkCoordinateTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TerrainChunkExplorationSchedulerTests.cs`

- [ ] **Step 1: Write failing coordinate tests**

Use this contract:

```csharp
public readonly record struct TerrainChunkCoordinate(int X, int Z)
{
    public const int Size = 16;
    public const int PixelCount = Size * Size;
    public int OriginX => checked(X * Size);
    public int OriginZ => checked(Z * Size);
    public static TerrainChunkCoordinate FromWorld(int worldX, int worldZ);
}
```

Test boundary values `0, 15, 16, 31, -1, -16, -17, int.MinValue, int.MaxValue` independently on X and Z. Assert checked overflow only when accessing an origin that cannot be represented.

- [ ] **Step 2: Write failing scheduler tests**

Use this contract:

```csharp
public sealed class TerrainChunkExplorationScheduler
{
    public int PendingCount { get; }
    public bool ObservePlayerPosition(int worldX, int worldZ);
    public IReadOnlyList<TerrainChunkCoordinate> GetPendingAttempts(int maximumCount);
    public void MarkCompleted(TerrainChunkCoordinate chunk);
    public void Clear();
}
```

Test:

- repeated positions in one chunk enqueue once;
- crossing X or Z boundary enqueues only the entered chunk;
- a newly entered current chunk is returned before older pending chunks;
- if the newly entered chunk is already pending, it is moved back to the front;
- with more than four pending chunks and four attempts per frame, older chunks are returned round-robin and cannot starve behind four not-ready entries;
- `MarkCompleted` removes it;
- `A → B → A` enqueues A again after its earlier completion;
- an uncompleted chunk stays pending across repeated calls;
- `maximumCount <= 0` throws;
- `Clear` resets both current identity and pending state.

- [ ] **Step 3: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TerrainChunkCoordinateTests|FullyQualifiedName~TerrainChunkExplorationSchedulerTests'
```

Expected: compilation fails because both types are absent.

- [ ] **Step 4: Implement floor division and ordered deduplicated pending work**

Use mathematical floor division, not C# truncation:

```csharp
private static int FloorDivideBySize(int value)
{
    var quotient = Math.DivRem(value, Size, out var remainder);
    return remainder < 0 ? quotient - 1 : quotient;
}
```

Back the scheduler with a `LinkedList<TerrainChunkCoordinate>` plus a dictionary from coordinate to linked-list node. Track the current chunk separately. On a chunk transition, add the new chunk to the front or move its existing pending node to the front. `GetPendingAttempts` always returns the pending current chunk first, then round-robins the remaining slots over older entries by moving attempted non-current nodes to the tail. It does not mark anything complete; only `MarkCompleted` removes an entry. With the component's limit of four, the current chunk is retried every frame while older pending chunks still make bounded progress.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run Step 3 again. Expected: all selected tests pass.

- [ ] **Step 6: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Map/TerrainChunkCoordinate.cs src/SurvivalcraftTravelMap/Map/TerrainChunkExplorationScheduler.cs tests/SurvivalcraftTravelMap.Tests/TerrainChunkCoordinateTests.cs tests/SurvivalcraftTravelMap.Tests/TerrainChunkExplorationSchedulerTests.cs
git diff --cached --check
git commit -m "feat: schedule personally entered terrain chunks"
```

---

### Task 2: Add one-lock map-region commits

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Map/MapTile.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/MapTileRegionTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs`

- [ ] **Step 1: Write failing region tests**

Add:

```csharp
public void SetRegion(
    int x,
    int z,
    int width,
    int height,
    ReadOnlySpan<Rgba32> colors);
```

Test a `16×16` write at `(0,0)`, `(16,32)`, and `(48,48)`. Assert all pixels and explored bits are updated, pixels outside the region remain unchanged, and `Version` increases exactly once per region call. Test that transparent input clears the explored bit consistently with `SetPixel`.

For invalid origin, size, overflow, bounds, or `colors.Length != width*height`, assert an exception and byte-for-byte unchanged tile snapshot/version. Add a reader/writer concurrency test proving snapshots see either the complete old region or the complete new region, never a mixed one.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~MapTileRegionTests|FullyQualifiedName~TravelMapRenderBudgetTests'
```

Expected: compilation fails because `SetRegion` does not exist.

- [ ] **Step 3: Implement validation-before-lock and one atomic mutation**

Validate every argument and the checked `width*height` before entering `_sync`. Inside one lock, iterate z-major through the source span, update explored bits and RGBA bytes using the same rules as `SetPixel`, then increment `_version` once. Do not implement this by calling `SetPixel` in a loop.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all selected tests pass.

- [ ] **Step 5: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Map/MapTile.cs tests/SurvivalcraftTravelMap.Tests/MapTileRegionTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs
git diff --cached --check
git commit -m "feat: commit explored map regions atomically"
```

---

### Task 3: Sample one complete surface-readable terrain chunk

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Map/TerrainMapSampler.cs`
- Modify: `src/SurvivalcraftTravelMap/Map/SurvivalcraftTerrainMapSource.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TerrainMapSamplerTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs`

- [ ] **Step 1: Add failing sampler/readiness tests**

Retain the existing per-column API for source/test compatibility and add an explicit, strongly typed chunk-readiness API:

```csharp
public interface ITerrainMapSource
{
    bool IsColumnReady(int x, int z);
    bool IsChunkSurfaceReady(TerrainChunkCoordinate chunk) =>
        IsColumnReady(chunk.OriginX, chunk.OriginZ);
    // existing height/content/temperature/humidity members remain
}
```

Add:

```csharp
public bool TrySampleChunk(
    TerrainChunkCoordinate chunk,
    Span<Rgba32> destination);
```

Test destination length must be exactly `256`; not-ready returns false and performs no height/content reads; ready samples exactly 256 columns in z-major order; an alpha-zero sample returns false; and negative chunk origins map to the correct world columns. The caller's eventual tile remains untouched on every false/throwing path. Update the fake used by `TerrainMapSamplerTests` to override the chunk method; the default interface implementation keeps older fakes compiling until Task 4 replaces their area-recording usage.

Add adapter tests for:

```csharp
internal static bool IsSurfaceReadable(TerrainChunkState state);
```

All states before `InvalidPropagatedLight` must be false. `InvalidPropagatedLight`, `InvalidVertices1`, `InvalidVertices2`, and `Valid` must be true.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TerrainMapSamplerTests|FullyQualifiedName~AdapterContractTests'
```

Expected: compilation or assertions fail because readiness is still per-column and restricted to `Valid`.

- [ ] **Step 3: Implement one readiness check and 256 samples**

`SurvivalcraftTerrainMapSource.IsChunkSurfaceReady(TerrainChunkCoordinate chunk)` passes `chunk.OriginX`/`chunk.OriginZ` to `GetChunkAtCell` and applies `state >= TerrainChunkState.InvalidPropagatedLight`. Keep that threshold in the tested `IsSurfaceReadable` helper. Do not pass chunk indices directly to a cell-coordinate API. Retain `IsColumnReady` and `TerrainMapSampler.TrySample` as compatibility helpers; new chunk recording must not call them.

`TrySampleChunk` validates the span, checks readiness once, then fills `destination[(localZ * 16) + localX]` through the existing `Sample` method. If any returned color has `A == 0`, return false. Do not mutate persistence in this method.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all selected tests pass.

- [ ] **Step 5: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Map/TerrainMapSampler.cs src/SurvivalcraftTravelMap/Map/SurvivalcraftTerrainMapSource.cs tests/SurvivalcraftTravelMap.Tests/TerrainMapSamplerTests.cs tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs
git diff --cached --check
git commit -m "fix: sample terrain when chunk surfaces are readable"
```

---

### Task 4: Record a chunk as one all-or-nothing tile mutation

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/ExplorationTileStoreTests.cs`

- [ ] **Step 1: Replace area tests with failing chunk tests**

Add the new result/API alongside the existing `RecordVisibleArea` compatibility method in Task 4 so `TravelMapComponent` still compiles until Task 5 switches the call site:

```csharp
public enum ExplorationRecordResult
{
    Recorded,
    NotReady,
    Pressure,
}

public ExplorationRecordResult RecordChunk(TerrainChunkCoordinate chunk);
```

Test `RecordChunk` while retaining the existing area tests for this intermediate commit. Task 5 removes the compatibility method and its area-only tests after the component is migrated. Test:

- a ready chunk writes all 256 cells and advances the tile version once;
- a not-ready or alpha-zero chunk returns `NotReady`, acquires no mutation lease, and leaves old data unchanged;
- storage pressure returns `Pressure` and leaves old data unchanged;
- a sampler exception leaves old data unchanged and propagates;
- re-recording overwrites all 256 colors, repairing a deliberately partial/transparent legacy region;
- chunks at every `64×64` tile quadrant map to local offsets `(0|16|32|48)` and never span two tiles;
- persisted/reloaded `.sctm` data contains all 256 cells with the existing codec version.
- the existing concurrent flush, failed-flush retry, trim/eviction, and mutation-lease pinning tests remain present and green after converting them from `RecordVisibleArea` to `RecordChunk`;
- every success and exception path disposes its admitted mutation lease, so no tile remains pinned.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~ExplorationRecorderTests|FullyQualifiedName~ExplorationTileStoreTests'
```

Expected: compilation fails because `RecordChunk` and `NotReady` do not exist.

- [ ] **Step 3: Implement sample-first, lease-second recording**

Use a temporary buffer before touching the store:

```csharp
Span<Rgba32> colors = stackalloc Rgba32[TerrainChunkCoordinate.PixelCount];
if (!_sampler.TrySampleChunk(chunk, colors))
{
    return ExplorationRecordResult.NotReady;
}
```

Resolve the map tile/local offset from `chunk.OriginX/OriginZ`, acquire exactly one mutation lease, and return `Pressure` without mutation if admission fails. Wrap the admitted lease in `using` (or an equivalent `try/finally`) and call `lease.Tile.SetRegion(localX, localZ, 16, 16, colors)` once:

```csharp
if (_tileStore.TryAcquireMutation(tileX, tileZ, out var lease)
    == TileMutationAdmission.Pressure)
{
    return ExplorationRecordResult.Pressure;
}

var admittedLease = lease
    ?? throw new InvalidOperationException("Mutation admission returned no lease.");
using (admittedLease)
{
    admittedLease.Tile.SetRegion(localX, localZ, 16, 16, colors);
}

return ExplorationRecordResult.Recorded;
```

Disposal is part of the persistence contract: it advances the dirty generation and releases the cache pin. Preserve the current flush/trim race tests so a concurrent flush cannot clear a newer chunk mutation or evict an in-flight tile.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all selected tests pass.

- [ ] **Step 5: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs tests/SurvivalcraftTravelMap.Tests/ExplorationTileStoreTests.cs
git diff --cached --check
git commit -m "fix: reveal explored chunks atomically"
```

---

### Task 5: Drive exploration after terrain update and document the behavior

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Modify: `src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs`
- Modify: `README.md`
- Modify: `docs/user-guide.md`
- Modify: `docs/smoke-test-2026-07-13.md`

- [ ] **Step 1: Add failing component wiring tests**

Require `TravelMapComponent.UpdateOrder == UpdateOrder.Views`, a `TerrainChunkExplorationScheduler` field, and no `_lastRecordedX`, `_lastRecordedZ`, `_stationaryRecordElapsed`, `SettingsManager.VisibilityRange`, or `RecordVisibleArea` references.

Add a component-level source contract proving that each update:

1. observes the player's current world X/Z;
2. requests a small bounded snapshot of pending chunks (use `MaximumChunkAttemptsPerFrame = 4`);
3. calls `RecordChunk` for each;
4. marks completed only on `Recorded`;
5. keeps `NotReady`, `Pressure`, and exceptions pending;
6. clears the scheduler during cleanup.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~TerrainChunkExplorationSchedulerTests'
```

Expected: source-wiring tests find the old radius scan, default update order, and 0.5-second retry.

- [ ] **Step 3: Wire the scheduler into `TravelMapComponent`**

Set `UpdateOrder.Views` so terrain update order `100` has run before exploration. Replace `UpdateExploration(float dt)` with `UpdateExploration()`. Observe the current floored X/Z every frame, then process up to four pending attempts. Wrap each attempt in its own `try/catch`: mark only `Recorded` complete, keep every other result pending, log/throttle an exception by chunk/error signature, and continue to the remaining attempts in the same frame. This prevents a permanently failing current chunk from starving the scheduler's round-robin backlog. Retain the one-time pressure warning and clear the scheduler in `CleanupRuntimeResources`.

After the component is migrated and source-contract tests prove there are no callers, remove the temporary `RecordVisibleArea` compatibility method and its area-only tests in this same task.

- [ ] **Step 4: Update docs and manual checks**

Replace radius/0.5-second/`Valid` descriptions with the exact chunk semantics. Add manual checks for `15→16`, `-1→-17`, no adjacent reveal, atomic 256-cell appearance, re-entry refresh, and keeping the existing World2 partial cache.

- [ ] **Step 5: Run the complete automated gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: all tests pass with zero warnings/errors. Existing tile codec and render-budget tests remain green.

- [ ] **Step 6: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs README.md docs/user-guide.md docs/smoke-test-2026-07-13.md
git diff --cached --check
git commit -m "fix: reveal entered terrain chunks immediately"
```

The cross-plan package and in-world verification gate is defined at the end of `2026-07-14-teleport-runtime-repair.md`; execute it only after this plan and the adaptive-HUD plan are green.
