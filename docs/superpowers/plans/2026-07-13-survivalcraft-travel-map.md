# Survivalcraft Travel Map Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `SurvivalcraftTravelMap.netmod`, a maintainable NetMod that preserves the 34GPS minimap and invitation teleport workflow, removes Mod-count reporting, and adds persistent exploration, scalable map UI, waypoints, day/night tint, and server-validated safe coordinate teleportation.

**Architecture:** Keep engine-independent map math, persistence, waypoint, and safe-teleport logic in focused C# classes with interfaces around Survivalcraft APIs. Bind those classes through a thin NetMod loader, player component, UI widgets, terrain adapter, player mover, and network packages. Preserve legacy invitation packet ID `41`; use a separate coordinate teleport packet ID `61`.

**Tech Stack:** C# 14/15, .NET 10.0, Survivalcraft 2.4.40.6 assemblies, NetMod API 1.44, Newtonsoft.Json, xUnit, PowerShell packaging, Git.

## Global Constraints

- Target exactly `net10.0`; the installed game runtime and original DLL both target .NET 10.0.
- Resolve game references from MSBuild property `SurvivalcraftDir`, falling back to the repository parent directory.
- Never commit proprietary game DLLs or the original `34GPSFix.netmod`.
- Keep `E:/game/SurvivalcraftNet2.4/NetMods/34GPSFix.netmod` byte-for-byte unchanged; expected SHA-256 is `00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B`.
- Package identity is `Survivalcraft Travel Map` / `SurvivalcraftTravelMap` / `SurvivalcraftTravelMap.dll`.
- The new package must refuse to initialize when `34GPSFix` is also installed.
- Delete all Mod counting, verification code `181215270`, package ID `60`, and reporting threads.
- Preserve legacy invitation packet ID `41` and its six existing message encodings.
- Use packet ID `61` only for coordinate teleport capability/request/response.
- Coordinate teleport is always visible in local and multiplayer UI; multiplayer execution requires SCTM server validation.
- New production behavior follows Red-Green-Refactor. Each task ends with a fresh specification reviewer, then a separate code-quality reviewer.
- Do not push a task until its tests, package checks, and both review gates pass.

## Repository Layout

```text
SurvivalCraftTravelMap.sln
Directory.Build.props
README.md
src/SurvivalcraftTravelMap/
  SurvivalcraftTravelMap.csproj
  Mod/TravelMapModLoader.cs
  Mod/TravelMapComponent.cs
  Map/MapTransform.cs
  Map/MapTile.cs
  Map/TileCoordinate.cs
  Map/TerrainMapSampler.cs
  Map/ExplorationRecorder.cs
  Map/DayNightBrightness.cs
  Persistence/TileCodec.cs
  Persistence/ExplorationTileStore.cs
  Persistence/AtomicFile.cs
  Persistence/WorldKey.cs
  Settings/TravelMapSettings.cs
  Settings/TravelMapSettingsStore.cs
  Waypoints/Waypoint.cs
  Waypoints/WaypointRepository.cs
  Teleport/SafeTeleportService.cs
  Teleport/TeleportCandidate.cs
  Teleport/TeleportContracts.cs
  Teleport/SurvivalcraftTerrainAccess.cs
  Teleport/SurvivalcraftChunkLoader.cs
  Teleport/SurvivalcraftPlayerMover.cs
  Network/LegacyGpsPackage.cs
  Network/CoordinateTeleportPackage.cs
  Network/InvitationManager.cs
  UI/MiniMapRenderer.cs
  UI/TravelMapDialog.cs
  UI/TravelMapSettingsWidget.cs
  UI/TeleportPanelWidget.cs
  Assets/BlockPixelColor.json
  Assets/Point.png
  Assets/TeleportButton.png
  Assets/TeleportButton_Pressed.png
  Assets/TeleportTo.png
  modinfo.json
  mod.netxdb
tests/SurvivalcraftTravelMap.Tests/
  SurvivalcraftTravelMap.Tests.csproj
  MapTransformTests.cs
  TileCoordinateTests.cs
  TileCodecTests.cs
  ExplorationTileStoreTests.cs
  DayNightBrightnessTests.cs
  TravelMapSettingsTests.cs
  WaypointRepositoryTests.cs
  SafeTeleportServiceTests.cs
  CoordinateTeleportPackageTests.cs
  LegacyGpsPackageTests.cs
  PackageStructureTests.cs
tools/Build-NetMod.ps1
tools/Verify-Package.ps1
docs/user-guide.md
```

---

### Task 1: Reproducible repository, build, and minimal NetMod

**Files:**
- Create: `SurvivalCraftTravelMap.sln`
- Create: `Directory.Build.props`
- Create: `src/SurvivalcraftTravelMap/SurvivalcraftTravelMap.csproj`
- Create: `src/SurvivalcraftTravelMap/Mod/TravelMapModLoader.cs`
- Create: `src/SurvivalcraftTravelMap/modinfo.json`
- Create: `src/SurvivalcraftTravelMap/mod.netxdb`
- Create: `tests/SurvivalcraftTravelMap.Tests/SurvivalcraftTravelMap.Tests.csproj`
- Create: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Create: `tools/Build-NetMod.ps1`
- Create: `tools/Verify-Package.ps1`

**Interfaces:**
- Consumes: game assemblies from `$(SurvivalcraftDir)`.
- Produces: `TravelMapModLoader : ModLoader`, a compiling solution, and `artifacts/SurvivalcraftTravelMap.netmod`.

- [ ] **Step 1: Write the failing package identity test**

```csharp
[Fact]
public void Manifest_has_new_identity_and_no_dependencies()
{
    using var json = JsonDocument.Parse(File.ReadAllText(TestPaths.Manifest));
    var root = json.RootElement;
    Assert.Equal("Survivalcraft Travel Map", root.GetProperty("Name").GetString());
    Assert.Equal("SurvivalcraftTravelMap", root.GetProperty("PackageName").GetString());
    Assert.Equal("1.44", root.GetProperty("ApiVersion").GetString());
    Assert.Equal(0, root.GetProperty("Dependencies").GetArrayLength());
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/SurvivalcraftTravelMap.Tests -c Release --filter Manifest_has_new_identity_and_no_dependencies`  
Expected: FAIL because the project/manifest does not exist.

- [ ] **Step 3: Add the solution, project references, manifest, XDB, and minimal loader**

`Directory.Build.props` must define:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <SurvivalcraftDir Condition="'$(SurvivalcraftDir)' == ''">$(MSBuildThisFileDirectory)..\</SurvivalcraftDir>
  </PropertyGroup>
</Project>
```

The Mod project must reference `Survivalcraft.dll`, `Engine.dll`, `EntitySystem.dll`, `Newtonsoft.Json.dll`, and `LiteNetLib.dll` with `Private=false`. `TravelMapModLoader.__ModInitialize()` initially registers no packages and performs only the old-package conflict check.

- [ ] **Step 4: Implement deterministic packaging and structural verification**

`Build-NetMod.ps1` must build Release, stage only the Mod DLL, manifest, XDB, and Assets, create a ZIP with stable relative paths, and rename it `.netmod`. `Verify-Package.ps1` must reject duplicate entries, game DLLs, package ID `60` strings, `AntiCheatReportPackage`, and files outside the allowlist.

- [ ] **Step 5: Verify GREEN and package**

Run:

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release
powershell -ExecutionPolicy Bypass -File tools/Build-NetMod.ps1
powershell -ExecutionPolicy Bypass -File tools/Verify-Package.ps1 artifacts/SurvivalcraftTravelMap.netmod
```

Expected: all tests PASS; package verification prints `PACKAGE_OK`.

- [ ] **Step 6: Review and commit**

Specification reviewer checks identity, target framework, conflict behavior, absence of bundled game DLLs, and original hash. Code-quality reviewer checks deterministic paths and PowerShell failure handling.

```bash
git add .
git commit -m "build: bootstrap reproducible travel map netmod"
```

---

### Task 2: Map coordinates, settings normalization, and day/night brightness

**Files:**
- Create: `src/SurvivalcraftTravelMap/Map/TileCoordinate.cs`
- Create: `src/SurvivalcraftTravelMap/Map/MapTransform.cs`
- Create: `src/SurvivalcraftTravelMap/Map/DayNightBrightness.cs`
- Create: `src/SurvivalcraftTravelMap/Settings/TravelMapSettings.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TileCoordinateTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/MapTransformTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/DayNightBrightnessTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TravelMapSettingsTests.cs`

**Interfaces:**
- Produces: `TileCoordinate.FromWorld(int x, int z)`, `MapTransform.WorldToScreen`, `MapTransform.ScreenToWorld`, `MapTransform.ZoomAt`, `DayNightBrightness.Calculate`, and `TravelMapSettings.Normalize`.

- [ ] **Step 1: Write failing negative-coordinate tests**

```csharp
[Theory]
[InlineData(-1, -1, 63)]
[InlineData(-64, -1, 0)]
[InlineData(-65, -2, 63)]
public void Negative_world_coordinates_use_floor_division(int world, int tile, int local)
{
    var result = TileCoordinate.FromWorld(world, world);
    Assert.Equal(tile, result.TileX);
    Assert.Equal(local, result.LocalX);
}
```

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TileCoordinateTests"`  
Expected: FAIL because `TileCoordinate` is missing.

- [ ] **Step 3: Implement floor division and immutable coordinate records**

Use `Math.DivRem` plus a negative-remainder correction; tile size is the constant `64`.

- [ ] **Step 4: Write failing round-trip and cursor-anchored zoom tests**

```csharp
[Fact]
public void ZoomAt_keeps_world_coordinate_under_cursor()
{
    var map = new MapTransform(new Vector2(100, -50), 2.0f, new Vector2(800, 600));
    var cursor = new Vector2(610, 210);
    var before = map.ScreenToWorld(cursor);
    var after = map.ZoomAt(cursor, 0.5f);
    AssertVectorNear(before, after.ScreenToWorld(cursor), 0.001f);
}
```

- [ ] **Step 5: Implement transform, settings, and brightness**

`TravelMapSettings.Normalize()` must clamp sizes to `160/192/256/320/384`, minimap scale to `0.5..8`, large-map scale to `0.25..32`, and night brightness to `0.4..1`. `DayNightBrightness.Calculate(float timeOfDay, float minimum)` must return `1` at noon, `minimum` at midnight, and use smoothstep across dawn/dusk.

- [ ] **Step 6: Run all focused tests, review, and commit**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TileCoordinateTests|FullyQualifiedName~MapTransformTests|FullyQualifiedName~DayNightBrightnessTests|FullyQualifiedName~TravelMapSettingsTests"`  
Expected: PASS.

Specification reviewer checks every numeric range against the design. Code-quality reviewer checks float tolerances, immutability, and absence of engine dependencies.

```bash
git add src tests
git commit -m "feat: add map math and normalized settings"
```

---

### Task 3: Versioned exploration tiles, compression, atomic persistence, and LRU

**Files:**
- Create: `src/SurvivalcraftTravelMap/Map/MapTile.cs`
- Create: `src/SurvivalcraftTravelMap/Persistence/TileCodec.cs`
- Create: `src/SurvivalcraftTravelMap/Persistence/AtomicFile.cs`
- Create: `src/SurvivalcraftTravelMap/Persistence/ExplorationTileStore.cs`
- Create: `src/SurvivalcraftTravelMap/Persistence/WorldKey.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TileCodecTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/ExplorationTileStoreTests.cs`

**Interfaces:**
- Produces: `MapTile.SetPixel`, `MapTile.TryGetPixel`, `TileCodec.Write`, `TileCodec.Read`, `ExplorationTileStore.GetOrLoad`, `MarkDirty`, `FlushAsync`, and `WorldKey.ForLocal/ForServer`.

- [ ] **Step 1: Write failing codec round-trip and corruption tests**

```csharp
[Fact]
public void Codec_round_trips_exploration_and_rgba()
{
    var tile = new MapTile(-2, 3);
    tile.SetPixel(63, 0, new Rgba32(1, 2, 3, 255));
    using var stream = new MemoryStream();
    TileCodec.Write(stream, tile);
    stream.Position = 0;
    var loaded = TileCodec.Read(stream);
    Assert.True(loaded.TryGetPixel(63, 0, out var color));
    Assert.Equal(new Rgba32(1, 2, 3, 255), color);
}
```

Add a second test that flips one compressed byte and expects `InvalidDataException`.

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TileCodecTests"`  
Expected: FAIL because codec types are missing.

- [ ] **Step 3: Implement format version 1**

Write magic `SCTM`, version byte `1`, tile coordinates, a 512-byte explored bitmap, 16,384 RGBA bytes, and SHA-256 checksum inside a Deflate payload. Read must validate all lengths, coordinates, version, and checksum before returning a tile.

- [ ] **Step 4: Write failing atomic-save, corrupt-isolation, and dirty-LRU tests**

Tests use a temporary directory. A corrupt tile must be renamed with `.corrupt`; a dirty tile must not be evicted from a capacity-2 cache until `FlushAsync` succeeds.

- [ ] **Step 5: Implement store and world keys**

`AtomicFile.ReplaceAsync` writes `path.tmp`, flushes, then uses `File.Move(temp, path, true)`. `WorldKey` normalizes input to lowercase invariant, trims trailing separators, and returns the first 24 uppercase hex characters of SHA-256.

- [ ] **Step 6: Run, review, and commit**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TileCodecTests|FullyQualifiedName~ExplorationTileStoreTests"`  
Expected: PASS with no files left in temporary test directories.

Specification reviewer checks paths, tile size, schema version, corruption behavior, five-second flush API, and 128-tile default. Code-quality reviewer checks disposal, cancellation, locks, and atomic replacement.

```bash
git add src tests
git commit -m "feat: persist explored map tiles safely"
```

---

### Task 4: Terrain sampling and exploration recording

**Files:**
- Create: `src/SurvivalcraftTravelMap/Map/TerrainMapSampler.cs`
- Create: `src/SurvivalcraftTravelMap/Map/ExplorationRecorder.cs`
- Create: `src/SurvivalcraftTravelMap/Map/BlockPixelData.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TerrainMapSamplerTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/ExplorationRecorderTests.cs`
- Create: `src/SurvivalcraftTravelMap/Assets/BlockPixelColor.json`

**Interfaces:**
- Consumes: `ITerrainMapSource.GetTopHeight/GetContent/GetSeasonalTemperature/GetSeasonalHumidity`.
- Produces: `TerrainMapSampler.Sample(int x, int z)` and `ExplorationRecorder.RecordVisibleArea`.

- [ ] **Step 1: Write failing sampler characterization tests**

Cover transparent-top IDs `28,99,19,174,25`, water normalization IDs `226,229,232,233`, and environmental tint IDs `8,12,13,14,18,225,256`. Use an in-memory fake source with recorded calls.

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TerrainMapSamplerTests"`  
Expected: FAIL because sampler interfaces and class are missing.

- [ ] **Step 3: Implement sampler without UI or persistence dependencies**

Load the 257-entry color dictionary once, validate keys `0..256`, and throw a descriptive initialization exception for missing entries. Return base daytime `Rgba32`; do not apply day/night brightness here.

- [ ] **Step 4: Write failing recorder tests**

Assert that recording a radius crossing `(63,63)` updates four tiles, marks only touched tiles dirty, and never samples a coordinate outside the requested visible square.

- [ ] **Step 5: Implement recorder and run GREEN**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TerrainMapSamplerTests|FullyQualifiedName~ExplorationRecorderTests"`  
Expected: PASS.

- [ ] **Step 6: Review and commit**

Specification reviewer compares sampled behavior with the original minimap and confirms unexplored terrain is never sampled by rendering. Code-quality reviewer checks dictionary validation and avoids allocating an image per frame.

```bash
git add src tests
git commit -m "feat: sample terrain into explored tiles"
```

---

### Task 5: Waypoint model and durable repository

**Files:**
- Create: `src/SurvivalcraftTravelMap/Waypoints/Waypoint.cs`
- Create: `src/SurvivalcraftTravelMap/Waypoints/WaypointRepository.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/WaypointRepositoryTests.cs`

**Interfaces:**
- Produces: `Add(string name, Vector3 position)`, `Rename(Guid id, string name)`, `Remove(Guid id)`, `GetAll()`, `LoadAsync`, and `SaveAsync`.

- [ ] **Step 1: Write failing CRUD and reload tests**

```csharp
[Fact]
public async Task Repository_preserves_exact_xyz_and_allows_duplicate_names()
{
    var repo = CreateRepository();
    repo.Add("家", new Vector3(10.5f, 42.25f, -3.5f));
    repo.Add("家", new Vector3(20.5f, 70f, 9.5f));
    await repo.SaveAsync();
    var loaded = CreateRepository();
    await loaded.LoadAsync();
    Assert.Equal(2, loaded.GetAll().Count);
    Assert.Equal(42.25f, loaded.GetAll()[0].Position.Y);
}
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WaypointRepositoryTests"`  
Expected: FAIL because waypoint types are missing.

- [ ] **Step 3: Implement schema version 1 and validation**

Trim names, reject empty names, preserve duplicate names, store stable GUID, XYZ, and UTC creation time. Unknown schema versions open read-only. Invalid JSON is renamed `.corrupt` once, then an empty repository is created.

- [ ] **Step 4: Run GREEN, review, and commit**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WaypointRepositoryTests"`  
Expected: PASS.

Specification reviewer checks full XYZ and all context-menu operations. Code-quality reviewer checks immutable snapshots and atomic saves.

```bash
git add src tests
git commit -m "feat: add durable named waypoints"
```

---

### Task 6: Engine-independent safe teleport search and transaction

**Files:**
- Create: `src/SurvivalcraftTravelMap/Teleport/TeleportContracts.cs`
- Create: `src/SurvivalcraftTravelMap/Teleport/TeleportCandidate.cs`
- Create: `src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs`

**Interfaces:**
- Produces: `TeleportToSurfaceAsync(int x, int z, CancellationToken)`, `TeleportToWaypointAsync(Vector3 xyz, CancellationToken)`, and result enum `Success/ChunkTimeout/NoSafePosition/OutOfWorld/RolledBack`.
- Consumes: `ITerrainAccess`, `IChunkLoader`, `IPlayerMover`, `IEntityCollisionQuery`, and `ITeleportClock`.

- [ ] **Step 1: Write failing candidate-order tests**

Assert surface search uses Chebyshev radius 8 ordered by horizontal squared distance. Assert waypoint search tries vertical offsets `0,+1,-1,...,+8,-8` before increasing horizontal distance.

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SafeTeleportServiceTests"`  
Expected: FAIL because teleport contracts are missing.

- [ ] **Step 3: Implement candidate generation only**

Keep generation deterministic with coordinate tie-breakers `X` then `Z`; do not call game APIs in iterators.

- [ ] **Step 4: Add failing safety and timeout tests**

Cover solid ground plus two clear cells, lava/fire/cactus/spikes rejection, water/leaves/falling block rejection, height bounds, entity collision, ten-second chunk timeout, exact `X+0.5/Z+0.5`, zero velocity, and no movement on failure.

- [ ] **Step 5: Implement transactional move and rollback**

Store a `PlayerMovementSnapshot`, move only after a candidate passes, reset velocity/fall state, wait one update tick through `ITeleportClock`, validate again, and call `Restore(snapshot)` on failure.

- [ ] **Step 6: Run GREEN, review, and commit**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SafeTeleportServiceTests"`  
Expected: every safety case PASS.

Specification reviewer audits every accepted/rejected surface and failure status. Code-quality reviewer focuses on cancellation, rollback, determinism, and integer overflow.

```bash
git add src tests
git commit -m "feat: add transactional safe teleport search"
```

---

### Task 7: Survivalcraft adapters and local teleport integration

**Files:**
- Create: `src/SurvivalcraftTravelMap/Teleport/SurvivalcraftTerrainAccess.cs`
- Create: `src/SurvivalcraftTravelMap/Teleport/SurvivalcraftChunkLoader.cs`
- Create: `src/SurvivalcraftTravelMap/Teleport/SurvivalcraftPlayerMover.cs`
- Create: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs`

**Interfaces:**
- Consumes: `SubsystemTerrain`, `ComponentPlayer`, `ComponentBody`, game chunk states, and Task/CancellationToken.
- Produces: concrete implementations of Task 6 contracts and local-world teleport entry points.

- [ ] **Step 1: Write failing adapter contract tests around thin fake facades**

Tests assert block collision metadata maps to `Safe/Fluid/Leaves/Falling/Damaging`, chunk polling stops at cancellation, movement snapshot contains position and both velocities, and restore is lossless.

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~AdapterContractTests"`  
Expected: FAIL because adapters are missing.

- [ ] **Step 3: Implement the thinnest game bindings**

Do not duplicate candidate logic. `TravelMapComponent.Load` resolves player, terrain, time-of-day, and GUI dependencies; server work type creates services but never UI textures. Local mode calls `SafeTeleportService` directly.

- [ ] **Step 4: Build against actual game assemblies and inspect warnings**

Run: `dotnet build SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir=E:\game\SurvivalcraftNet2.4\`  
Expected: 0 errors. Any nullable warning at an engine boundary must be handled explicitly rather than suppressed globally.

- [ ] **Step 5: Review and commit**

Specification reviewer confirms local vs server/client work-type behavior and 10-second loading. Code-quality reviewer checks engine calls remain on the correct thread and resources are disposed.

```bash
git add src tests
git commit -m "feat: bind safe teleport to survivalcraft"
```

---

### Task 8: Minimap, large map, settings UI, and waypoint menus

**Files:**
- Create: `src/SurvivalcraftTravelMap/UI/MiniMapRenderer.cs`
- Create: `src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs`
- Create: `src/SurvivalcraftTravelMap/UI/TravelMapSettingsWidget.cs`
- Create: `src/SurvivalcraftTravelMap/Settings/TravelMapSettingsStore.cs`
- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`

**Interfaces:**
- Consumes: map transform, tile store, day/night brightness, waypoint repository, safe teleport service.
- Produces: corner minimap, `M` dialog, scroll zoom, drag pan, right-click context actions, and persistent settings.

- [ ] **Step 1: Write failing UI-state tests**

Test that `M` opens only with no text/chat/modal focus, wheel changes zoom only when hovered, right-click unexplored returns `Unexplored`, right-click waypoint returns waypoint actions, and accepted map sizes are exactly `160/192/256/320/384`.

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TravelMapUiStateTests"`  
Expected: FAIL because UI state controller is missing.

- [ ] **Step 3: Implement a testable `TravelMapUiController` before widgets**

Controller emits commands `OpenLargeMap`, `Pan`, `Zoom`, `ShowGroundMenu`, `ShowWaypointMenu`, `ShowUnexploredMessage`; widgets translate game input to these commands.

- [ ] **Step 4: Implement renderers and settings migration**

Render only tile pixels already marked explored. Apply night tint to terrain quads only. Draw player arrow, waypoint icons, labels, and `X/Y/Z` without tint. Migrate only `isDisplayMap` and `isAllowTelePortRequest` from `GPSSetting.xml`; never delete the old file.

- [ ] **Step 5: Run tests and build package**

Run:

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release
powershell -ExecutionPolicy Bypass -File tools/Build-NetMod.ps1
```

Expected: PASS and package builds.

- [ ] **Step 6: Review and commit**

Specification reviewer checks every UI action, range, tint exclusion, and unexplored rule. Code-quality reviewer checks draw batching, texture lifetime, input focus, and per-frame allocations.

```bash
git add src tests
git commit -m "feat: add minimap and interactive travel map"
```

---

### Task 9: Legacy invitations and server-validated coordinate teleport protocol

**Files:**
- Create: `src/SurvivalcraftTravelMap/Network/LegacyGpsPackage.cs`
- Create: `src/SurvivalcraftTravelMap/Network/CoordinateTeleportPackage.cs`
- Create: `src/SurvivalcraftTravelMap/Network/InvitationManager.cs`
- Create: `src/SurvivalcraftTravelMap/UI/TeleportPanelWidget.cs`
- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapModLoader.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/LegacyGpsPackageTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/CoordinateTeleportPackageTests.cs`

**Interfaces:**
- Produces: package ID `41` byte-compatible legacy serializer, package ID `61` capability/request/response serializer, server handlers, and client five-second timeout tracking.

- [ ] **Step 1: Write failing golden-byte tests for packet ID 41**

Create fixed expected byte arrays for Request, Response, Teleport, MTeleport, TResponse, and TAllow based on the original field order. Round-trip each payload and assert ID `41`.

- [ ] **Step 2: Verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~LegacyGpsPackageTests"`  
Expected: FAIL because serializer is missing.

- [ ] **Step 3: Implement only the legacy wire format and invitation behavior**

Preserve 30-second timeout, self/offline checks, admin immediate teleport, and four players per page. Change invitation preference handling so `AcceptTeleportInvitations=false` rejects invitation dialogs but still shows ordinary result messages.

- [ ] **Step 4: Write failing ID 61 capability and authority tests**

Tests assert request carries request ID and target only, server never trusts client-computed safe Y, unsupported server returns/causes a single session notification, and client position does not change before success response.

- [ ] **Step 5: Implement coordinate protocol and server handler**

Message kinds are `CapabilityRequest`, `CapabilityResponse`, `SurfaceRequest`, `WaypointRequest`, and `Result`. Server configuration defaults both teleport modes enabled, calls `SafeTeleportService`, and maps every service result to a stable response code.

- [ ] **Step 6: Run, review, and commit**

Run: `dotnet test -c Release --filter "FullyQualifiedName~LegacyGpsPackageTests|FullyQualifiedName~CoordinateTeleportPackageTests"`  
Expected: PASS.

Specification reviewer checks IDs, byte compatibility, server authority, timeouts, and invitation behavior. Code-quality reviewer checks malformed payload bounds, replayed request IDs, client disconnects, and no global state leak between sessions.

```bash
git add src tests
git commit -m "feat: preserve invitations and validate network teleports"
```

---

### Task 10: Final integration, package audit, documentation, and game smoke test

**Files:**
- Modify: `src/SurvivalcraftTravelMap/mod.netxdb`
- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapModLoader.cs`
- Modify: `tools/Verify-Package.ps1`
- Create: `README.md`
- Create: `docs/user-guide.md`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`

**Interfaces:**
- Consumes: all previous tasks.
- Produces: release candidate `.netmod`, installation guide, server requirements, known limitations, and audit evidence.

- [ ] **Step 1: Write failing final package tests**

Assert one new `TravelMapComponent` XDB injection, new GUIDs, required five assets, no `Setting.png` dependency, no `AntiCheatReportPackage`, no ID `60`, no verification code, and no game DLL entries.

- [ ] **Step 2: Verify RED against the pre-integration package**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PackageStructureTests"`  
Expected: FAIL until final XDB and package allowlist are complete.

- [ ] **Step 3: Complete integration and user documentation**

README and user guide must document installation, removal of `34GPSFix`, `M`, wheel zoom, left-drag, right-click actions, settings path, map data path, local safety rules, server requirement for multiplayer coordinate teleport, invitation compatibility, backup/uninstall, and no Mod-count reporting.

- [ ] **Step 4: Run the full automated verification**

Run:

```powershell
$before=(Get-FileHash -Algorithm SHA256 'E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod').Hash
dotnet test SurvivalCraftTravelMap.sln -c Release
powershell -ExecutionPolicy Bypass -File tools/Build-NetMod.ps1
powershell -ExecutionPolicy Bypass -File tools/Verify-Package.ps1 artifacts/SurvivalcraftTravelMap.netmod
$after=(Get-FileHash -Algorithm SHA256 'E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod').Hash
if($before -ne $after -or $after -ne '00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B'){throw 'Original mod changed'}
```

Expected: all tests PASS, `PACKAGE_OK`, original hash unchanged.

- [ ] **Step 5: Perform the game smoke-test matrix**

Use a copied test world. Verify flat ground, forest, mountain, cave waypoint, water/lava rejection, negative coordinates, day/noon/night tint, restart persistence, local teleport rollback, host server, client/server SCTM, and unsupported server timeout. Record results in `docs/smoke-test-2026-07-13.md` with pass/fail and exact reproduction notes.

- [ ] **Step 6: Final independent reviews**

One reviewer performs full design-to-code traceability. A different reviewer performs security/safety review of file paths, packet parsing, server authority, teleport rollback, and package contents. Fix every blocking finding and rerun Step 4.

- [ ] **Step 7: Commit and push release candidate**

```bash
git add .
git commit -m "release: build Survivalcraft Travel Map"
git push origin main
```

Do not create a public release tag until public-distribution authorization has been confirmed by the repository owner.

## Execution Order and Review Gates

Tasks execute strictly in order. For each task:

1. Implementation agent follows the RED/GREEN steps and commits locally.
2. Specification reviewer compares the diff to the approved design and this task only.
3. Implementation agent fixes specification findings and reruns focused plus full tests.
4. Code-quality reviewer checks safety, maintainability, error paths, and tests.
5. Implementation agent fixes quality findings and reruns tests.
6. Primary agent verifies evidence, then permits the next task.

Tasks 6, 7, 9, and 10 are safety-critical. Their reviewers must explicitly reject any client-authoritative multiplayer position change, unbounded chunk wait, partial teleport, missing rollback, path traversal, corrupt-file overwrite, or packet length trust.
