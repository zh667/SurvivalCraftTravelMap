# Minimap Navigation and Customization Roadmap

**Goal:** Add creature markers, heading-aware orientation, compass directions, movable and shaped minimaps, time display, large-map recentering, touch gestures, safe settings reset, and seven-language UI support to `SurvivalcraftTravelMap.netmod`.

**Reference boundary:** `NetMods/[API1.9.2.1]自定义小地图v1.0.0.scmod` is a behavioral and mathematical reference only. Do not copy its binary, package layout, API calls, settings file, or decompiled source into the `.netmod`. Reimplement each behavior against Survivalcraft 2.4.40.6 / NetMod API 1.44 and this repository's existing render, persistence, networking, and test architecture.

**Delivery rule:** Implement one user-visible feature at a time. Every phase ends with focused tests, the full Release suite, warnings-as-errors build, deterministic `.netmod` packaging, package verification, isolated-game installation, and user smoke testing. Do not begin the next feature until the current isolated build is accepted.

## Global constraints

- Preserve explored-terrain persistence, LOD coverage, waypoints, safe teleport, invitations, and server-authoritative networking.
- Never reveal terrain pixels that have not been explored. Overlay markers may only use live entities already present in the local simulation.
- Keep the large map north-up; the new north-up/heading-up choice applies to the minimap. This matches the reference behavior and preserves predictable large-map drag/teleport coordinates.
- Transform terrain, exploration boundaries, waypoints, creature markers, compass labels, and hit testing through one shared map transform. Do not build independent rotation math for each overlay.
- Keep mouse input unchanged while adding touch. A touch gesture must not synthesize a right-click teleport/context action.
- A presentation reset must not delete explored tiles, waypoints, server settings, invitation state, or world data.
- All new UI text uses `LanguageControl.Get("TravelMap", key)` from the first feature onward. Chinese is the fallback during staged development; phase 10 supplies all seven complete catalogs.
- Package language files as `Assets/Lang/<language>.json`; NetMod API 1.44 `ModEntity.LoadLauguage` merges the active file into `LanguageControl`.
- Preserve unknown JSON properties and future-schema read-only behavior.
- Use immutable/plain render DTOs at the UI boundary; renderer tests must not need live game entities.

## Settings and compatibility foundation

The first feature introduces settings schema 3. Refactor schema classification so:

- schema 1 remains readable and keeps its historical explicit `MiniMapSize=384 -> 192` migration;
- schema 2 migrates to schema 3 without changing any existing value;
- schema 3 is current;
- schema greater than 3 stays read-only and byte-preserving;
- unversioned and corrupt-file behavior remains unchanged;
- later phases may add optional schema-3 properties whose missing values use documented defaults.

New presentation defaults:

| Setting | Default | Normalization |
|---|---:|---|
| `ShowCreatureMarkers` | `true` | Boolean |
| `CreatureMarkerSize` | `5` px | `3..16` |
| `MiniMapOrientation` | `NorthUp` | Known enum only |
| `ShowCompassNorth` | `true` | Boolean |
| `ShowCompassOtherDirections` | `true` | Boolean |
| `CompassFontScale` | `1.0` | `0.5..2.0` |
| `MiniMapAnchorX/Y` | top-right default | finite normalized values clamped to GUI |
| `MiniMapShape` | `RoundedSquare` | Circle/Square/Hexagon/RoundedSquare |
| `ShowGameTime` | `true` | Boolean |

Settings reset will use a single `TravelMapSettings.CreateDefaults()`/`ResetPresentationToDefaults()` source rather than duplicating literal values in the widget.

---

## Phase 1 — Creature markers

**Status:** Accepted in the isolated game on 2026-07-15.

**Behavior**

- Add an on/off setting and marker-size slider.
- Show living, non-local-player creatures on minimap and large map.
- Use reference-compatible category colors: land/water predators red, birds yellow, other creatures green, with a dark outline.
- Snapshot live engine creatures into `CreatureMapMarker(Position, Kind)` DTOs before rendering; never retain `ComponentCreature` in the renderer.
- Clip markers to the map viewport and batch them with the existing primitive budget.

**Primary files**

- `Settings/TravelMapSettings.cs`
- `Settings/TravelMapSettingsStore.cs`
- `UI/CreatureMapMarker.cs` (new)
- `UI/MiniMapRenderer.cs`
- `UI/TravelMapDialog.cs`
- `UI/TravelMapSettingsWidget.cs`
- `Mod/TravelMapComponent.cs`
- settings/render/component contract tests

**Acceptance**

- Toggle affects both maps immediately and persists after restart.
- Red/yellow/green category mapping is deterministic.
- Dead creatures and the local player are absent.
- Marker-size changes do not resize the player arrow.
- Empty creature lists add no primitives and no allocations proportional to the map area.

## Phase 2 — Minimap orientation

**Status:** Accepted in the isolated game on 2026-07-15.

**Behavior**

- Add `NorthUp` and `HeadingUp` modes.
- In north-up mode, terrain remains fixed and the player arrow rotates.
- In heading-up mode, the player's forward direction stays at the top; terrain, boundaries, waypoints and creature markers rotate around the player while the arrow stays upright.
- Large map remains north-up.

**Architecture**

- Extend `MapTransform` (or a dedicated immutable minimap transform) with rotation-aware `WorldToScreen` and `ScreenToWorld` functions.
- Keep rotation at zero for the large map and teleport hit testing.
- Add round-trip, cardinal-axis, waypoint, marker and clipping tests at headings `0`, `90`, `180`, and `270` degrees.

**Acceptance**

- No terrain/marker drift while turning in place.
- Switching orientation does not change zoom, explored data, or minimap position.
- Large-map right-click still resolves the original world coordinate.

## Phase 3 — Compass directions

**Status:** Accepted in the isolated game on 2026-07-15.

**Behavior**

- Add N/E/S/W edge labels, independent north/other-direction choices, and font scale.
- North is visually distinct; use localized labels.
- Labels follow the selected map shape and orientation.

**Architecture**

- Use the shared rotation transform from phase 2.
- Add a pure `CompassLayout` that intersects world direction rays with circle, square, hexagon, or rounded-square boundaries.
- Reserve coordinate/time overlay space so labels do not overlap them.

**Acceptance**

- Direction positions are correct in both orientation modes.
- North-only produces exactly one direction label.
- Labels remain inside all four map shapes and at every supported map size.

## Phase 4 — Movable minimap

**Status:** Accepted in the isolated game on 2026-07-15.

**Behavior**

- Add a settings action that enters placement mode.
- Drag the minimap with mouse or one finger; confirm/cancel and persist the position.
- Store normalized anchor coordinates so placement survives window size and UI-scale changes.
- Clamp the complete minimap to the logical GUI safe area.

**Architecture**

- Extend `TravelMapOverlayLayout` with `PlaceCustom` and normalization/clamping helpers.
- Keep the invitation button attached beneath/near the chosen minimap and clamped independently.
- Placement mode suppresses minimap-open and wheel-zoom actions.

**Acceptance**

- Position survives restart, window resize, and UI scale `0.75/1.0/1.25`.
- Neither minimap nor invitation button can be dragged permanently off-screen.
- Cancel restores the previous persisted position.

## Phase 5 — Map shapes

**Behavior**

- Add Circle, Square, Hexagon, and Rounded Square.
- Clip terrain and every overlay consistently; border follows the selected shape.
- Apply the selected shape to the minimap and the large-map viewport, matching the reference feature.

**Architecture**

- Create shared `MapClipGeometry`/`MapShapeGeometry`; do not scatter shape tests across draw methods.
- Prefer geometry/scissor masking supported by the current engine; allocate no per-frame render targets unless profiling proves necessary.
- Make hit testing use the same geometry so invisible corners cannot open/teleport on the map.

**Acceptance**

- No terrain, boundary line, waypoint, creature, player marker, compass, or input leaks beyond the shape.
- Shape changes preserve center and zoom.
- Large-map drag and right-click remain accurate at visible edges.

## Phase 6 — Game-time display

**Behavior**

- Add a toggle and display `HH:mm` from `SubsystemTimeOfDay`.
- Place time with the coordinate strip without covering the player marker or compass.
- Time text stays readable under night tint.

**Architecture**

- Pass a pure `Func<float>`/formatted-time DTO from the component; do not make the widget locate subsystems.
- Cache the formatted string and refresh only when the displayed minute changes.

**Acceptance**

- Handles `00:00`, noon, and day wrap correctly.
- Toggle persists and updates immediately.
- Coordinates and time fit every supported minimap size and language.

## Phase 7 — Large-map locate/follow

**Behavior**

- Add a localized Locate button.
- Clicking it recenters on the current player and enables follow mode.
- Manual drag or pointer-anchored zoom disables follow; clicking Locate re-enables it.
- Preserve the user's current zoom when locating.

**Acceptance**

- Locate works after arbitrary drag/zoom without closing the dialog.
- Follow state has no effect on minimap centering or persisted zoom.
- Button layout remains valid at narrow windows and all languages.

## Phase 8 — Touch operation

**Behavior**

- Large map: one-finger drag and two-finger midpoint-anchored pinch zoom.
- Minimap: tap opens, placement mode supports one-finger drag.
- End-of-pinch transition must not jump into a drag.

**Architecture**

- Add a pure gesture state machine (`Idle`, `Dragging`, `Pinching`) fed by normalized touch samples.
- Reuse the existing `TravelMapUiController` pan/zoom commands.
- Retain mouse wheel, left drag, right-click context menu, keyboard M, Cancel and Back behavior.

**Acceptance**

- Pinch keeps the world point beneath the midpoint stable.
- Lifting one finger after pinch does not pan-jump or open a context menu.
- Mouse regression tests remain green.

## Phase 9 — Settings reset

**Behavior**

- Add a confirmation dialog and reset all presentation settings introduced by this mod.
- Apply changes live and persist atomically.
- Do not reset `AcceptTeleportInvitations` unless explicitly presented as part of the confirmation; never reset server policy or world data.

**Architecture**

- Reset through the settings model's single defaults source.
- Preserve read-only future-schema behavior: show an explanatory notice and do not overwrite the file.
- Rebuild/refresh controls after reset instead of reconstructing the whole player component.

**Acceptance**

- Reset restores size, zoom, tint, coordinates, creature settings, orientation, compass, position, shape and time.
- Explored tiles and waypoints are byte-for-byte untouched.
- Cancel changes nothing.

## Phase 10 — Seven-language completion

**Languages**

- `zh-CN`, `en-US`, `es-419`, `pt-BR`, `ro-RO`, `ru-RU`, `vi-VN`.

**Behavior and packaging**

- Move every Travel Map title, setting, notice, context action, confirmation, direction label and button to the `TravelMap` language namespace.
- Package one complete JSON file per language under `Assets/Lang`.
- If the active game language has no catalog, `LanguageControl.Get` falls back visibly to the Chinese value supplied by the mod rather than returning a raw key.

**Acceptance**

- A key-parity test proves all seven catalogs contain the same key set and non-empty values.
- Package tests prove all seven exact asset paths exist in the `.netmod`.
- UI smoke tests check long strings do not overlap at supported window/UI scales.
- No user-facing hardcoded Chinese remains in production C# except fallback/error bootstrapping that cannot use the language system.

---

## Per-phase verification gate

1. Write failing unit/source-contract tests before production changes.
2. Run the smallest filtered tests and verify the intended failure.
3. Implement only the current feature.
4. Run the focused tests and all `SurvivalcraftTravelMap.Tests` in Release.
5. Build with warnings as errors.
6. Run `tools/Build-NetMod.ps1` and `tools/Verify-Package.ps1`.
7. Build twice when package inputs change and verify deterministic SHA-256.
8. Install only into the existing isolated smoke-game directory; never overwrite the main game's current mod during development.
9. Launch the isolated game and hand the phase-specific smoke checklist to the user.
10. Commit the accepted feature separately, then begin the next phase.

## Final integrated smoke matrix

- UI scale: `0.75`, `1.0`, `1.25`.
- Minimap size: `160`, `192`, `256`, `320`, `384`.
- Shape: all four.
- Orientation: north-up and heading-up.
- Input: mouse, keyboard, one-touch drag, two-touch pinch.
- Runtime: single-player/integrated host and remote client.
- Data: existing schema-2 settings, existing explored tiles, existing waypoints, corrupt settings, future schema read-only.
- Languages: all seven catalogs.
- Map movement: turn in place, walk across chunk boundaries, large-map drag round trip, zoom extremes, locate/follow.
