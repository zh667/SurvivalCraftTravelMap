# Adaptive Minimap HUD Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the oversized plugin-like HUD with a Survivalcraft-style adaptive minimap, modal-aware visibility, click-to-open interaction, and a contextual player-teleport icon.

**Architecture:** Keep settings migration, HUD policy, layout, and interaction decisions in engine-independent helpers. `TravelMapComponent` is the only owner of runtime widget visibility and placement. `MapSurfaceWidget` exposes visual properties so the minimap can use the new style without changing the large-map appearance.

**Tech Stack:** C#/.NET 10, Survivalcraft 2.4.40.6 widget APIs, xUnit v3, PNG assets, PowerShell packaging.

## Global Constraints

- Use logical GUI coordinates from `Player.GuiWidget.ActualSize`; never use camera viewport pixels for HUD placement.
- Default minimap size is `192×192` logical units (about `256×256` screen pixels in the user's current UI scale).
- Preserve all five size choices: `160 / 192 / 256 / 320 / 384`.
- Migrate only an explicit schema-1 `MiniMapSize` value of `384` to `192`; preserve every other valid schema-1 size and every schema-2 value.
- A temporary modal hide must never mutate or save `IsMiniMapVisible`.
- Hide both HUD widgets for inventory/character/crafting/sleep/modal dialogs and the large map; restore them automatically after the surface closes.
- Ordinary chat may remain visible. Active text entry disables map/button interaction without changing the saved visibility setting.
- Show the player-teleport icon only in multiplayer when at least one other player exists.
- Keep the large map's existing cyan survey crosshair and normal marker styling; minimap-only styling must not leak into it.
- Do not reintroduce a text button labelled `玩家传送` or any Mod-count/reporting behavior.
- Create project-original icon artwork; use the old package only as visual reference and do not copy its image bytes.
- Before Task 1, complete the shared dirty-worktree baseline task in `2026-07-14-teleport-runtime-repair.md`. This plan assumes that baseline commit exists.
- Work test-first and commit each task separately. Do not include unrelated dirty-worktree files in a task commit.

---

### Task 1: Migrate settings schema and change the default size

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Settings/TravelMapSettings.cs`
- Modify: `src/SurvivalcraftTravelMap/Settings/TravelMapSettingsStore.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapSettingsTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`

- [ ] **Step 1: Add failing default and schema-migration tests**

Add assertions for all of the following:

```csharp
Assert.Equal(192, new TravelMapSettings().MiniMapSize);
Assert.Equal(2, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
```

Test this exact migration matrix:

| Input | Loaded size | Outcome | Saved schema |
|---|---:|---|---:|
| no file | 192 | `Created` | 2 |
| schema 1, size 384 | 192 | `MigratedPreviousSchema` | 2 |
| schema 1, size 160/192/256/320 | unchanged | `MigratedPreviousSchema` | 2 |
| schema 2, size 384 | 384 | `Loaded` | 2 |
| schema 3 | read-only defaults | `UnsupportedFutureSchemaReadOnly` | original bytes unchanged |
| corrupt file | 192 | `CorruptIsolated` | 2 |

Also retain tests proving unversioned JSON migration, extension-data preservation, and future-schema byte preservation.

- [ ] **Step 2: Run the focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapSettingsTests|FullyQualifiedName~TravelMapSettingsStoreTests'
```

Expected: failures report the current default/schema as `256`/`1`, and schema 1 is still classified as current.

- [ ] **Step 3: Implement schema 2 and the one-time migration**

Use explicit constants and classification:

```csharp
private const int CurrentSchemaVersion = 2;

private enum SchemaKind
{
    Invalid,
    Previous,
    Current,
    Future,
}
```

Classify `1` as `Previous`, `2` as `Current`, and numeric values greater than `2` as `Future`. Add `MigratedPreviousSchema` to `TravelMapSettingsLoadOutcome`. After deserializing an explicit schema-1 document, apply only:

```csharp
if (schema == SchemaKind.Previous && settings.MiniMapSize == 384)
{
    settings.MiniMapSize = 192;
}
```

Then normalize and save with `SettingsDocument.SchemaVersion = CurrentSchemaVersion`. Change both settings defaults from `256` to `192`. Do not migrate unversioned or schema-2 `384` values.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run the command from Step 2. Expected: all selected tests pass.

- [ ] **Step 5: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Settings/TravelMapSettings.cs src/SurvivalcraftTravelMap/Settings/TravelMapSettingsStore.cs tests/SurvivalcraftTravelMap.Tests/TravelMapSettingsTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs
git diff --cached --check
git commit -m "fix: migrate minimap default to adaptive size"
```

---

### Task 2: Add pure HUD visibility policy and unified adaptive layout

**Files:**

- Create: `src/SurvivalcraftTravelMap/UI/TravelMapHudPolicy.cs`
- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapOverlayLayout.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TravelMapHudPolicyTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`

- [ ] **Step 1: Write the failing policy matrix**

Define tests against these contracts:

```csharp
internal readonly record struct TravelMapHudSignals(
    bool HasUi,
    bool IsMainPlayer,
    bool IsRuntimeActive,
    bool MiniMapSettingEnabled,
    bool HasModalSurface,
    bool IsLargeMapOpen,
    bool HasOtherPlayers,
    bool InvitationFeatureAvailable,
    bool HasTextEntryFocus);

internal readonly record struct TravelMapHudState(
    bool ShowMiniMap,
    bool ShowTeleportButton,
    bool AllowMiniMapInput);
```

Cover normal single-player, multiplayer with/without another player, saved setting off, non-main player, runtime inactive, modal surface, large map, and active text input. Evaluate once with a modal and once after removing it, asserting that the original settings object was not changed.

- [ ] **Step 2: Write failing layout tests**

Add:

```csharp
internal readonly record struct TravelMapHudPositions(
    Vector2 MiniMap,
    Vector2 TeleportButton);

internal static TravelMapHudPositions PlaceHud(
    Vector2 guiLogicalSize,
    float miniMapSize);
```

For a `1062.5×597.65625` logical GUI and size `192`, assert:

```text
MiniMap       = (794.5, 24)
TeleportButton = (938.5, 220)
```

Also test tiny and zero-size GUIs: the `192×192` map and `48×46` button positions must be clamped without negative coordinates.

- [ ] **Step 3: Run the focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapHudPolicyTests|FullyQualifiedName~TravelMapUiStateTests'
```

Expected: compilation fails because the new policy/layout APIs do not exist.

- [ ] **Step 4: Implement the pure policy and layout**

Use one base gate for both widgets:

```csharp
var showHud = signals.HasUi
    && signals.IsMainPlayer
    && signals.IsRuntimeActive
    && signals.MiniMapSettingEnabled
    && !signals.HasModalSurface
    && !signals.IsLargeMapOpen;

return new TravelMapHudState(
    ShowMiniMap: showHud,
    ShowTeleportButton: showHud
        && signals.InvitationFeatureAvailable
        && signals.HasOtherPlayers,
    AllowMiniMapInput: showHud && !signals.HasTextEntryFocus);
```

`PlaceHud` uses constants `RightMargin=76f`, `TopMargin=24f`, `TeleportGap=4f`, and `TeleportButtonSize=(48f,46f)`. Place the map with `PlaceTopRight`; align the icon's right edge with the map and put it four logical units below. Clamp each result to the GUI bounds.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run Step 3 again. Expected: all selected tests pass.

- [ ] **Step 6: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/UI/TravelMapHudPolicy.cs src/SurvivalcraftTravelMap/UI/TravelMapOverlayLayout.cs tests/SurvivalcraftTravelMap.Tests/TravelMapHudPolicyTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs
git diff --cached --check
git commit -m "feat: add adaptive travel map hud policy"
```

---

### Task 3: Implement minimap-specific styling and click-to-open behavior

**Files:**

- Modify: `src/SurvivalcraftTravelMap/UI/MiniMapRenderer.cs`
- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapRenderModel.cs`
- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapUiController.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/MiniMapTextRendererTests.cs`

- [ ] **Step 1: Add failing pure rendering and activation tests**

Add these contracts and assertions:

```csharp
Assert.Equal(18f, TravelMapRenderModel.MiniMapPlayerArrowSize(192));
Assert.Equal("X:488 Y:63 Z:-60",
    TravelMapRenderModel.FormatCompactCoordinates(new Vector3(488.9f, 63.2f, -60.1f)));
Assert.Equal(0.65f, TravelMapTypography.MiniMapCoordinateScale);
```

Add `TravelMapUiController.HandleMiniMapActivation(bool isPressed, bool isHovered, bool inputBlocked)`. It returns `OpenLargeMap` only when pressed, hovered, and not blocked. Test every false branch.

Extend the render-sink test so minimap overlays receive a red marker at size `18`, while the existing large-map test still receives its previous marker size/color.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapUiStateTests|FullyQualifiedName~MiniMapTextRendererTests'
```

Expected: compilation failures for the new helpers, followed by marker/style assertion failures.

- [ ] **Step 3: Add configurable `MapSurfaceWidget` visuals**

Add minimap-overridable properties while preserving current defaults for `TravelMapDialog`:

```csharp
public bool ShowSurveyCrosshair { get; set; } = true;
public bool ShowFrameShadow { get; set; }
public bool ShowCoordinateBackdrop { get; set; }
public bool UseCompactCoordinates { get; set; }
public bool DrawPlayerOutline { get; set; }
public float CoordinateTextScale { get; set; } = TravelMapTypography.SecondaryLabelScale;
public float FrameThickness { get; set; } = 1f;
public Rgba32 PlayerMarkerColor { get; set; } = TravelMapPalette.SurveyCyan;
public Rgba32 BackgroundColor { get; set; } = TravelMapPalette.Basalt;
public Rgba32 FrameColor { get; set; } = TravelMapPalette.Moss;
public Rgba32 FrameShadowColor { get; set; } = new(0x12, 0x12, 0x12, 0x80);
```

Add minimap palette values: neutral dark gray background, warm gray-white frame, red marker, and dark marker outline. Draw the outline triangle before the red triangle, turn off the cyan crosshair only on `MiniMapRenderer`, and draw a translucent lower-left strip no taller than `18` logical units before queuing compact coordinates at scale `0.65`. Day/night brightness continues to affect terrain cells only.

Treat the declared `192×192` as the complete widget footprint and reserve its outermost one-pixel gutter for the shadow, so the shadow is outside the warm frame but remains inside the widget's clip bounds. Draw a one-pixel, 50%-alpha dark rectangle on that outer half-pixel inset, then draw warm frame rectangles at the next two one-pixel insets. Add tested `MiniMapVisualStyle` constants for shadow thickness `1f`, frame thickness `2f`, shadow alpha `0x80`, and coordinate-strip height `18f`; a source/render contract must prove `MapSurfaceWidget.Draw` consumes them in shadow-then-frame order only when `ShowFrameShadow` is true.

Extend the positional overlay state without forcing the large map to change:

```csharp
public readonly record struct MapOverlayState(
    Vector3 PlayerPosition,
    float PlayerHeading,
    float PlayerArrowSize,
    IReadOnlyList<Waypoint> Waypoints,
    bool ShowCoordinates,
    Rgba32? PlayerMarkerColor = null);
```

`RenderOverlays` uses `PlayerMarkerColor ?? TravelMapPalette.SurveyCyan` and permits arrow sizes down to `14f`; existing large-map calls retain cyan and their current size. `MapSurfaceWidget` passes its configured marker color for the minimap.

Set `MiniMapRenderer` to:

```csharp
PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(size);
ShowSurveyCrosshair = false;
ShowFrameShadow = true;
ShowWaypointLabels = false;
ShowCoordinateBackdrop = true;
UseCompactCoordinates = true;
DrawPlayerOutline = true;
FrameThickness = 2f;
```

`ShowFrameShadow` defaults to false, and the large-map test must assert that no shadow primitive is emitted. This prevents minimap-only framing from leaking into `TravelMapDialog`.

Do not set `IsVisible` in `MiniMapRenderer.MeasureOverride`; visibility belongs to `TravelMapComponent`.

- [ ] **Step 4: Wire click activation inside `MiniMapRenderer`**

Add `Action requestOpenLargeMap` to a new constructor overload after `Func<bool> inputBlocked`. Keep the existing constructor signature and delegate it to the new overload with a no-op callback so Task 3 remains source-compatible with the `TravelMapComponent` call site until Task 4 wires the real callback. During `Update`, after computing hover, pass `Input.IsMouseButtonDownOnce(MouseButton.Left)`, hover, and `_inputBlocked()` to `HandleMiniMapActivation`. Invoke the callback and clear input on `OpenLargeMap`. Preserve wheel zoom behavior.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all selected tests pass.

- [ ] **Step 6: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/UI/MiniMapRenderer.cs src/SurvivalcraftTravelMap/UI/TravelMapRenderModel.cs src/SurvivalcraftTravelMap/UI/TravelMapUiController.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs tests/SurvivalcraftTravelMap.Tests/MiniMapTextRendererTests.cs
git diff --cached --check
git commit -m "feat: restyle and activate adaptive minimap"
```

---

### Task 4: Wire component visibility, placement, and the contextual teleport icon

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Modify: `tools/Generate-Assets.ps1`
- Modify: `src/SurvivalcraftTravelMap/Assets/TeleportButton.png`
- Modify: `src/SurvivalcraftTravelMap/Assets/TeleportButton_Pressed.png`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `README.md`
- Modify: `docs/user-guide.md`
- Modify: `docs/smoke-test-2026-07-13.md`

- [ ] **Step 1: Add failing source-wiring and package tests**

Require all of the following:

- `TravelMapComponent` calls `TravelMapHudPolicy.Evaluate` every frame.
- Both widgets consume one `TravelMapOverlayLayout.PlaceHud` result.
- `MiniMapRenderer` receives an open-large-map callback.
- The source no longer contains `Text = "玩家传送"` or `BevelledButtonWidget` for the teleport entry.
- The entry is a `BitmapButtonWidget` using both packaged teleport button textures.
- `MiniMapRenderer.MeasureOverride` does not assign `IsVisible`.
- Both PNGs are valid `64×64` images and their byte hashes differ.
- Replaying `tools/Generate-Assets.ps1` into a temporary directory reproduces all checked-in PNG bytes; the generator no longer contains the current large single-arrow routine.
- `GetHudSignals` maps `Gui?.ModalPanelWidget is not null`, non-large-map dialogs, and the exact large-map membership check into policy signals; tests cover created-but-not-shown and currently-shown large-map states.
- The HUD policy is evaluated and applied before invitation click handling and before the `_miniMap is null` early return.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~TravelMapHudPolicyTests'
```

Expected: source-wiring tests fail against the current text button and independent placement methods.

- [ ] **Step 3: Implement component-owned HUD state**

Replace the button field with `BitmapButtonWidget`. Add helpers:

```csharp
private TravelMapHudSignals GetHudSignals();
private void ApplyHudState(TravelMapHudState state);
private int CountOtherPlayers();
private void OpenLargeMap();
```

`HasModalSurface` combines `Gui?.ModalPanelWidget is not null` and dialogs other than the large-map dialog. The exact open check is:

```csharp
var isLargeMapOpen = _largeMapDialog is not null
    && DialogsManager.Dialogs.Contains(_largeMapDialog);
```

Merely constructing `_largeMapDialog` must not hide the HUD. `ApplyHudState` sets `IsVisible` and `IsEnabled` for both widgets without changing settings. In `Update`, compute/apply state before `UpdateInvitationUi` and before any `_miniMap is null` return, so an invitation-only UI cannot bypass modal hiding. Keep `HandleLargeMapHotkey` running while the HUD is hidden so `M` closes the large map.

Add a source-contract test for the real signal mapping, plus named policy theory cases for inventory, character, crafting, sleep, generic dialog, and large map. Each case evaluates hidden then closed/restored and asserts the same `TravelMapSettings` object remains unchanged.

Have both the `M` path and minimap-click callback call `OpenLargeMap()`, which rechecks input focus, resets the dialog to the player, shows it, and immediately hides the HUD for the current frame.

- [ ] **Step 4: Replace the text entry with a native-style bitmap icon**

Update `tools/Generate-Assets.ps1` as the source of truth, then generate original `64×64` normal/pressed PNGs: warm gray stone/bevel frame, dark neutral center, simple two-person/transfer glyph, restrained moss accent, transparent outside corners. The pressed state moves/darkens the glyph by one pixel. Remove/replace `Draw-TeleportArrow`; do not copy the old package bitmap or retain the current large green-arrow treatment. Generate into a temporary directory first, compare `Point.png` and `TeleportTo.png` with the checked-in versions, then regenerate the checked-in assets and run the reproducibility test.

Load both textures through the mod asset stream, construct `Subtexture` values, and create a `48×46` logical `BitmapButtonWidget` with no text. Dispose the widget and directly loaded textures in `CleanupUi`. Ignore clicks when hidden, disabled, a modal is active, or text input owns focus.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all selected tests pass.

- [ ] **Step 6: Update documentation and the manual UI matrix**

Document default size `192`, click-to-open, `M`, automatic modal hiding/restoration, and the multiplayer-only teleport icon. Add manual checks at UI scales 0.75/1.0/1.25 for inventory, character, crafting, sleep, large map, chat, solo, and a second player.

- [ ] **Step 7: Run the complete automated gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: all tests pass and the build reports zero warnings/errors.

- [ ] **Step 8: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs tools/Generate-Assets.ps1 src/SurvivalcraftTravelMap/Assets/TeleportButton.png src/SurvivalcraftTravelMap/Assets/TeleportButton_Pressed.png tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs README.md docs/user-guide.md docs/smoke-test-2026-07-13.md
git diff --cached --check
git commit -m "feat: integrate adaptive minimap hud"
```

The cross-plan package and in-world verification gate is defined at the end of `2026-07-14-teleport-runtime-repair.md`; execute it only after this plan and the chunk-exploration plan are green.
