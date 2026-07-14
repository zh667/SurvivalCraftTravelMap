# Map Render, Marker, and Default Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep all known terrain visible during large-map zoom and drag, make the large-map player marker match the minimap, and use 160 pixels as the new-profile minimap default.

**Architecture:** Change immutable tile-snapshot LOD aggregation to average only explored source pixels, then prove viewport movement cannot discard the resulting known cells. Configure the large-map `MapSurfaceWidget` with the same red outlined arrow rule as the minimap, and change only new/missing settings defaults to 160 while preserving explicit persisted values and historical schema-one migration.

**Tech Stack:** C#/.NET 10, existing retained-mode Survivalcraft widgets, immutable tile snapshots and integral sums, xUnit v3, PowerShell packaging scripts.

## Global Constraints

- A coarse aggregate renders when it contains at least one explored pixel.
- Average RGBA using only explored pixels; a zero-explored aggregate remains absent.
- Do not recolor or suppress genuine gray rock, mountain, ore, shadow, or water terrain.
- Fixed-scale drag away and back must reproduce the same persisted known pixels and colors.
- Preserve the renderer's `MaximumTerrainSamplesPerFrame` and LOD materialization budgets.
- The large map shows no cyan survey crosshair.
- The large-map player indicator uses the minimap's red fill, dark outline, heading, triangle geometry, and size rule.
- New or missing settings use `MiniMapSize == 160`; valid explicitly persisted choices remain unchanged.
- Keep schema-one explicit `384 -> 192` migration behavior; schema-two explicit 384 remains 384.
- Do not change teleport, invitation, waypoint, toast, `.sctm`, or network behavior.
- Work test-first and commit each task separately. Do not mix unrelated files into a task commit.

---

### Task 1: Preserve partially explored LOD aggregates during zoom and drag

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Map/MapTile.cs:266-318`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs:658-710`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs` render-budget regression section

**Interfaces:**

- Preserves: `MapTileSnapshot.TryGetExploredRegion(int x, int z, int width, int height, out Rgba32 color) : bool`.
- Changes semantics: returns `false` only when the region has zero explored pixels.
- Preserves: `TravelMapRenderModel.RenderTerrain` budgets and `TileStoreMapPixelSource` LOD cache interfaces.

- [ ] **Step 1: Replace the obsolete all-or-nothing region test with failing partial-average tests**

Replace `Explored_region_is_unknown_when_even_a_non_origin_cell_is_unknown` with:

```csharp
[Fact]
public void Partially_explored_region_averages_only_known_rgba_values()
{
    var tile = new MapTile(0, 0);
    tile.SetPixel(0, 0, new Rgba32(10, 20, 30, 40));
    tile.SetPixel(1, 1, new Rgba32(30, 40, 50, 80));

    var found = tile.CreateVersionedSnapshot().Snapshot.TryGetExploredRegion(
        0,
        0,
        2,
        2,
        out var average);

    Assert.True(found);
    Assert.Equal(new Rgba32(20, 30, 40, 60), average);
}

[Fact]
public void Completely_unknown_region_remains_absent()
{
    var snapshot = new MapTile(0, 0).CreateVersionedSnapshot().Snapshot;

    Assert.False(snapshot.TryGetExploredRegion(0, 0, 2, 2, out _));
}
```

Retain `Explored_region_returns_the_rounded_integer_average_of_all_rgba_channels` to prove fully explored behavior is unchanged.

- [ ] **Step 2: Write the failing fixed-scale pan-retention regression**

Add this helper and test to `TravelMapRenderBudgetTests` before changing production aggregation:

```csharp
private static MapTile CreateQuarterExploredTile(Rgba32 color)
{
    var tile = new MapTile(0, 0);
    for (var z = 0; z < MapTile.Size; z += 2)
    {
        for (var x = 0; x < MapTile.Size; x += 2)
            tile.SetPixel(x, z, color);
    }

    return tile;
}

private static (int X, int Z, Vector2 Minimum, Vector2 Maximum, Rgba32 Color) CellIdentity(
    MapTerrainCell cell) =>
    (cell.WorldX, cell.WorldZ, cell.ScreenMinimum, cell.ScreenMaximum, cell.Color);

[Fact]
public void Fixed_scale_pan_round_trip_retains_partially_explored_lod_cells()
{
    const int tileCount = 129;
    var color = new Rgba32(70, 80, 90, 255);
    var source = new TileStoreMapPixelSource(
        new KnownTileProvider(CreateQuarterExploredTile(color), tileCount));
    var viewport = new Vector2((tileCount * MapTile.Size) - 2, MapTile.Size - 2);
    var original = new MapTransform(
        new Vector2(((tileCount * MapTile.Size) - 1) / 2f, (MapTile.Size - 1) / 2f),
        1f,
        viewport);
    var shifted = original with { Center = original.Center + new Vector2(1f, 1f) };

    var first = new CapturingRenderSink();
    var middle = new CapturingRenderSink();
    var returned = new CapturingRenderSink();
    var firstStats = TravelMapRenderModel.RenderTerrain(source, original, 1f, first);
    TravelMapRenderModel.RenderTerrain(source, shifted, 1f, middle);
    var returnedStats = TravelMapRenderModel.RenderTerrain(source, original, 1f, returned);

    Assert.True(firstStats.WorldStride > 1);
    Assert.NotEmpty(first.Terrain);
    Assert.NotEmpty(middle.Terrain);
    Assert.Equal(firstStats.WorldStride, returnedStats.WorldStride);
    Assert.Equal(
        first.Terrain.Select(CellIdentity).ToArray(),
        returned.Terrain.Select(CellIdentity).ToArray());
    Assert.All(returned.Terrain, cell => Assert.Equal(color, cell.Color));
}
```

The test deliberately uses 129 known tiles so the production render budget selects a stride above one. The quarter-explored pattern makes every coarse cell partial, which the old all-or-nothing aggregate incorrectly hides.

- [ ] **Step 3: Run snapshot and pan tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~MapTileSnapshotTests|FullyQualifiedName~Fixed_scale_pan_round_trip_retains_partially_explored_lod_cells'
```

Expected: the snapshot test fails and the pan test fails at `Assert.NotEmpty(first.Terrain)` because the old method rejects every partial aggregate.

- [ ] **Step 4: Average by explored count instead of total area**

Replace the existing `pixelCount` completeness block with:

```csharp
var sums = _regionSums.Value;
var exploredCount = RegionSum(sums.Explored, x, z, endX, endZ);
if (exploredCount == 0)
{
    color = default;
    return false;
}

color = new Rgba32(
    Average(RegionSum(sums.Red, x, z, endX, endZ), exploredCount),
    Average(RegionSum(sums.Green, x, z, endX, endZ), exploredCount),
    Average(RegionSum(sums.Blue, x, z, endX, endZ), exploredCount),
    Average(RegionSum(sums.Alpha, x, z, endX, endZ), exploredCount));
return true;
```

Do not set exploration bits for unknown sub-pixels and do not persist the averaged color; this is display-only immutable snapshot aggregation.

- [ ] **Step 5: Run snapshot and pan tests and verify GREEN**

Run the command from Step 3.

Expected: all selected tests pass, the first and returned pan frames have identical known-cell catalogs, and genuine color `(70,80,90,255)` is retained.

- [ ] **Step 6: Run all renderer budget tests**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapRenderBudgetTests|FullyQualifiedName~MapTileSnapshotTests'
```

Expected: all selected tests pass and existing maximum-query/materialization assertions remain unchanged.

- [ ] **Step 7: Commit Task 1**

```powershell
git add src/SurvivalcraftTravelMap/Map/MapTile.cs tests/SurvivalcraftTravelMap.Tests/TravelMapRenderBudgetTests.cs
git diff --cached --check
git commit -m "fix: retain known terrain at coarse lod"
```

---

### Task 2: Use the minimap player arrow on the large map

**Files:**

- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs:107-116,268-330`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs:937-970`

**Interfaces:**

- Consumes: `TravelMapPalette.MiniMapPlayer`, `MapSurfaceWidget.DrawPlayerOutline`, and `TravelMapRenderModel.MiniMapPlayerArrowSize(int)`.
- Preserves: `MapSurfaceWidget` heading and triangle rendering shared by both surfaces.

- [ ] **Step 1: Write failing large-map marker contract tests**

Add source-contract assertions for the `TravelMapDialog` surface initializer and update method:

```csharp
AssertCodeContains(dialogConstructor, "ShowSurveyCrosshair = false");
AssertCodeContains(dialogConstructor, "PlayerMarkerColor = TravelMapPalette.MiniMapPlayer");
AssertCodeContains(dialogConstructor, "DrawPlayerOutline = true");
AssertCodeContains(
    dialogUpdate,
    "_surface.PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(_settings.MiniMapSize);");
AssertCodeDoesNotContain(dialogConstructor, "PlayerArrowSize = 32f");
```

Keep `Overlay_marker_style_can_be_overridden_for_the_minimap_without_changing_large_map_defaults`; it verifies the render model remains generic. Add this sizing-rule test because both surface constructors will now consume the same function:

```csharp
[Theory]
[InlineData(160, 15f)]
[InlineData(192, 18f)]
[InlineData(384, 24f)]
public void Shared_minimap_arrow_sizing_rule_is_stable(int mapSize, float expected)
{
    Assert.Equal(expected, TravelMapRenderModel.MiniMapPlayerArrowSize(mapSize));
}
```

Retain `MiniMapTextRendererTests` assertions that `DrawPlayerOutline` emits the dark outline triangle followed by the red fill triangle; both surfaces use that same widget implementation.

- [ ] **Step 2: Run marker tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~TravelMapUiStateTests|FullyQualifiedName~MiniMapTextRendererTests'
```

Expected: source contracts fail because the large map still enables the default crosshair and fixed 32-pixel cyan marker.

- [ ] **Step 3: Configure the large-map surface with the shared marker style**

Replace `PlayerArrowSize = 32f` in the surface initializer with:

```csharp
ShowSurveyCrosshair = false,
PlayerMarkerColor = TravelMapPalette.MiniMapPlayer,
DrawPlayerOutline = true,
PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(settings.MiniMapSize),
```

At the start of `TravelMapDialog.Update`, after notice expiry handling and before input actions, add:

```csharp
_surface.PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(
    _settings.MiniMapSize);
```

Do not add a second marker implementation. `MapSurfaceWidget.Player(...)` already supplies the shared heading-aware triangle geometry and optional outline.

- [ ] **Step 4: Run marker tests and verify GREEN**

Run the command from Step 2.

Expected: all selected tests pass; source contracts prove no crosshair and setting-driven shared style.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs
git diff --cached --check
git commit -m "fix: unify large map player marker"
```

---

### Task 3: Default new settings profiles to a 160-pixel minimap

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Settings/TravelMapSettings.cs:17`
- Modify: `src/SurvivalcraftTravelMap/Settings/TravelMapSettingsStore.cs:507-518`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapSettingsTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs:466-658`

**Interfaces:**

- Preserves: `TravelMapSettings.SupportedMiniMapSizes == [160, 192, 256, 320, 384]`.
- Changes: default `TravelMapSettings.MiniMapSize` and `SettingsDocument.MiniMapSize` from 192 to 160.
- Preserves: current-schema explicit values and schema-one explicit 384 migration.

- [ ] **Step 1: Change tests to the accepted default and verify RED**

Change `Defaults_match_the_design`:

```csharp
Assert.Equal(160, settings.MiniMapSize);
```

Change the following assertions from 192 to 160 because each path constructs a new or safe fallback value rather than loading an explicit user size:

```csharp
// Missing_settings_create_schema_two_defaults
Assert.Equal(160, result.Settings.MiniMapSize);
Assert.Equal(160, document.RootElement.GetProperty("MiniMapSize").GetInt32());

// Explicit_schemas_use_the_document_default_when_minimap_size_is_missing
Assert.Equal(160, result.Settings.MiniMapSize);
Assert.Equal(160, document.RootElement.GetProperty("MiniMapSize").GetInt32());

// Any_schema_number_greater_than_two_is_future_and_preserved_byte_for_byte
Assert.Equal(160, result.Settings.MiniMapSize);

// Numeric_schema_values_other_than_exact_one_two_or_greater_than_two_are_invalid
Assert.Equal(160, result.Settings.MiniMapSize);

// Future_schema_is_preserved_byte_for_byte_and_all_saves_are_read_only
Assert.Equal(160, result.Settings.MiniMapSize);

// Corrupt_new_settings_are_isolated_and_replaced_with_safe_defaults
Assert.Equal(160, result.Settings.MiniMapSize);
```

Do not change `Previous_schema_preserves_unknown_fields_when_migrating` (`203` normalizes to `192`) or `First_load_migrates_only_the_two_legacy_flags_and_preserves_the_old_file` (the legacy explicit `384` becomes `192`).

Add explicit persistence coverage:

```csharp
[Theory]
[InlineData(160)]
[InlineData(192)]
[InlineData(256)]
[InlineData(320)]
[InlineData(384)]
public async Task Current_schema_preserves_every_explicit_supported_size(int size)
{
    using var directory = new UiTemporaryDirectory();
    var store = new TravelMapSettingsStore(directory.Path);
    await File.WriteAllTextAsync(
        store.SettingsPath,
        $"{{\"schemaVersion\":2,\"MiniMapSize\":{size}}}",
        TestContext.Current.CancellationToken);

    var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);

    Assert.Equal(TravelMapSettingsLoadOutcome.Loaded, result.Outcome);
    Assert.Equal(size, result.Settings.MiniMapSize);
}
```

Keep the existing schema-one theory row `[InlineData(384, 192)]` unchanged.

- [ ] **Step 2: Run settings tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapSettingsTests|FullyQualifiedName~TravelMapSettingsStoreTests'
```

Expected: default assertions fail because production defaults are still 192.

- [ ] **Step 3: Change only new/missing defaults**

In `TravelMapSettings`:

```csharp
public int MiniMapSize { get; set; } = 160;
```

In nested `TravelMapSettingsStore.SettingsDocument`:

```csharp
public int MiniMapSize { get; set; } = 160;
```

Keep this historical migration exactly as it is:

```csharp
if (schema == SchemaKind.Previous && settings.MiniMapSize == 384)
{
    settings.MiniMapSize = 192;
}
```

Do not increment the schema version: changing a default for an absent property requires no persisted document transformation and explicit current values remain authoritative.

- [ ] **Step 4: Run settings tests and verify GREEN**

Run the command from Step 2.

Expected: all settings tests pass, including explicit current-schema preservation and historical migration.

- [ ] **Step 5: Commit Task 3**

```powershell
git add src/SurvivalcraftTravelMap/Settings/TravelMapSettings.cs src/SurvivalcraftTravelMap/Settings/TravelMapSettingsStore.cs tests/SurvivalcraftTravelMap.Tests/TravelMapSettingsTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs
git diff --cached --check
git commit -m "fix: default minimap size to 160"
```

---

### Task 4: Combined regression, deterministic package, and isolated install

**Files:**

- Modify after actual results: `.superpowers/sdd/polish-feedback-task-4-report.md`
- Modify after actual results: `.superpowers/sdd/progress.md`
- Modify only after user acceptance: `docs/smoke-test-2026-07-13.md`

**Interfaces:**

- Consumes: all commits from this plan and `2026-07-14-map-coverage-reconciliation.md`.
- Produces: an exact verified `.netmod` in `.superpowers/smoke-game/NetMods` and a new 11-row manual acceptance gate.

- [ ] **Step 1: Run repository and protected-original integrity checks**

```powershell
git status --short
git diff --check
$protected = (Get-FileHash 'E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod' -Algorithm SHA256).Hash
if ($protected -ne '00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B') {
    throw "Protected original package changed: $protected"
}
```

Expected: no unrelated working-tree changes, no whitespace errors, and the protected hash matches exactly.

- [ ] **Step 2: Run the complete automated gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -Configuration Release -SurvivalcraftDir 'E:\game\SurvivalcraftNet2.4\'
powershell -ExecutionPolicy Bypass -File scripts/verify-package.ps1 -PackagePath artifacts/SurvivalcraftTravelMap.netmod
```

Expected: every test passes, build reports zero warnings/errors, and verifier prints `PACKAGE_OK`.

- [ ] **Step 3: Prove deterministic packaging**

```powershell
$first = (Get-FileHash artifacts/SurvivalcraftTravelMap.netmod -Algorithm SHA256).Hash
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -Configuration Release -SurvivalcraftDir 'E:\game\SurvivalcraftNet2.4\'
powershell -ExecutionPolicy Bypass -File scripts/verify-package.ps1 -PackagePath artifacts/SurvivalcraftTravelMap.netmod
$second = (Get-FileHash artifacts/SurvivalcraftTravelMap.netmod -Algorithm SHA256).Hash
if ($first -ne $second) { throw "Package hashes differ: $first vs $second" }
```

Expected: both package hashes are identical and the second verifier also prints `PACKAGE_OK`.

- [ ] **Step 4: Install only the exact artifact into the isolated smoke game**

```powershell
$source = (Resolve-Path 'artifacts/SurvivalcraftTravelMap.netmod').Path
$destinationDirectory = (Resolve-Path '.superpowers/smoke-game/NetMods').Path
$destination = Join-Path $destinationDirectory 'SurvivalcraftTravelMap.netmod'
Copy-Item -LiteralPath $source -Destination $destination -Force
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) { throw "Installed package hash mismatch" }
```

Do not copy to `E:\game\SurvivalcraftNet2.4\NetMods`.

- [ ] **Step 5: Record automated evidence without claiming manual acceptance**

In `.superpowers/sdd/polish-feedback-task-4-report.md`, replace previous package/test values with the actual new totals, build counts, package hash/size/entry count, DLL hash/size, protected-original hash, and isolated destination hash. Set every manual row below to `PENDING` until observed in the game:

```markdown
| # | Acceptance row | Status |
|---|---|---|
| 1 | New profile starts with minimap size 160 | PENDING |
| 2 | Stationary loaded footprint fills without checkerboard holes | PENDING |
| 3 | Teleport destination fills after delayed terrain readiness | PENDING |
| 4 | Entering an old blank repairs the current chunk | PENDING |
| 5 | Hidden minimap still records movement | PENDING |
| 6 | Reopen world preserves repaired coverage | PENDING |
| 7 | Fixed-scale drag away/back never turns known terrain flat gray | PENDING |
| 8 | Zoom out/in never turns known terrain flat gray | PENDING |
| 9 | Genuine rock/mountain gray remains detailed | PENDING |
| 10 | Large map uses red outlined arrow and has no cyan crosshair | PENDING |
| 11 | Existing profile preserves its explicit minimap size | PENDING |
```

Do not mark the release complete or commit final smoke-test acceptance while any row is pending.

- [ ] **Step 6: Commit automated evidence and request in-world verification**

```powershell
git add .superpowers/sdd/polish-feedback-task-4-report.md .superpowers/sdd/progress.md
git diff --cached --check
git commit -m "test: record map polish release evidence"
```

Launch/reopen only `.superpowers/smoke-game` for the user. After the user tests, record each row as PASS or FAIL with timestamp/log/screenshot evidence. A failed row returns to its owning task; all PASS rows are required before updating `docs/smoke-test-2026-07-13.md`, pushing, opening the single final PR, tagging, or merging.

After each implementation task, use a fresh implementer followed by an independent reviewer. Fix and re-review every Critical or Important finding before moving to the next task.
