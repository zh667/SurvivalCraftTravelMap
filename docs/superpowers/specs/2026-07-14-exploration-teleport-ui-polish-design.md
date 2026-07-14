# Survivalcraft Travel Map — Exploration, Teleport, and UI Polish Design

Date: 2026-07-14
Status: user-approved design, pending implementation plan
Branch: `feature/travel-map`

## 1. Purpose

This change fixes four issues found during the final in-world test:

1. Surface teleport succeeds at some map positions but returns `NoSafePosition` at many harmless locations.
2. Teleport and unexplored-area messages are drawn behind the modal large map.
3. The persistent map records only the single terrain chunk entered by the player, even though the minimap already covers a much larger loaded area. Extreme zoom therefore exposes a disconnected patchwork of recorded chunks.
4. The settings panel has no local close action, so the user must close the entire large map.

The approved product rule is:

> Every already-loaded terrain chunk intersecting the configured minimap footprint is explored and permanently recorded in the large map. Looking or panning around the large map does not explore remote terrain.

## 2. Confirmed Runtime Evidence

The latest isolated session contains successful surface requests interspersed with `NoSafePosition`; there are no current-session `InternalError` failures. The safe-position classifier currently requires:

- the exact terrain top to classify as `SafeSolid`;
- the feet cell to classify as literal `Air`;
- the head cell to classify as literal `Air`.

Harmless non-collidable blocks fall through the adapter to `Damaging`. Leaves, fluids, sand, and gravel receive dedicated non-safe classifications. This explains false failures in vegetation, forest, shore, and sandy terrain.

The large-map dialog sends feedback through `Gui.DisplaySmallMessage`. That game HUD message is below the modal dialog, which explains why it becomes visible only after closing the large map.

The test settings were:

- minimap size: 256 px;
- minimap scale: 0.5 blocks/px;
- large-map scale: approximately 0.35 blocks/px.

At 256 px and 0.5 blocks/px, the minimap footprint is approximately 128×128 world blocks, or roughly 8×8 terrain chunks. The current recorder captures only the center chunk. At 0.35 blocks/px, each 16×16 recorded terrain chunk is displayed at roughly 45×45 screen pixels, making the missing surrounding chunks conspicuous.

## 3. Exploration Semantics

### 3.1 Minimap footprint

The exploration footprint is derived from the configured minimap, whether or not the HUD widget is currently visible:

```text
footprint width in blocks  = MiniMapSize × MiniMapBlocksPerPixel
footprint height in blocks = MiniMapSize × MiniMapBlocksPerPixel
```

The footprint remains centered on the local player. All terrain chunks intersecting this square are candidates for recording.

Examples:

- 256 px at 0.5 blocks/px: about 128×128 blocks, approximately 8×8 chunks;
- 256 px at 1.0 blocks/px: about 256×256 blocks, approximately 16×16 chunks.

Changing minimap size or scale changes the future exploration footprint. Hiding the minimap does not disable exploration recording.

### 3.2 Loaded-terrain boundary

The mod records only a candidate whose terrain surface is already readable from the running game. It must not request, generate, or retain remote terrain solely to fill the map.

Consequences:

- terrain visible and loaded around the player becomes persistent large-map data;
- if the configured minimap footprint extends beyond the game's currently loaded terrain, the unavailable outer portion remains unexplored;
- unavailable chunks are retried when the game later loads them;
- large-map pan and zoom never reveal or generate terrain;
- old historical gaps are filled when the player returns close enough for those chunks to enter the minimap footprint. They are not fabricated retroactively.

### 3.3 Scheduling and responsiveness

Whenever the player crosses a terrain-chunk boundary, or the minimap size/scale changes, the scheduler recomputes the footprint. It also periodically re-observes the footprint while stationary so chunks that finish loading are not missed.

Work remains bounded per frame:

- the player's current chunk is highest priority;
- remaining candidates are ordered nearest-first around the player;
- pending candidates are deduplicated;
- a small fixed number of ready chunks are sampled per frame;
- not-ready and transiently failed chunks remain pending without starving later work;
- each 16×16 chunk is committed atomically, never as a partially drawn rectangle.

The current chunk must still appear on the first frame its surface becomes readable. A normal minimap footprint should become continuous within a short bounded sequence of frames without a noticeable frame spike.

### 3.4 Persistence and rendering

The existing `.sctm` format, 64×64 map tiles, checksums, atomic tile commit, save queue, and renderer budget remain unchanged.

Minimap and large map continue to read the same persistent tile store. Therefore every newly recorded surrounding chunk appears on both surfaces and remains present after leaving and reopening the world.

Unexplored terrain remains genuinely blank. The implementation must not interpolate colors across absent chunks or mark absent pixels as explored. The disconnected screenshot is addressed by recording the legitimate loaded minimap footprint, not by visually painting over missing data.

Pan and zoom must preserve the map center/anchor and must never discard known tile descriptors. Regression tests cover repeated drag, zoom-in, zoom-out, and very wide known-tile catalogs.

## 4. Safe Teleport Semantics

### 4.1 Core rule

A block is rejected because of actual player harm or invalid body placement, not merely because it belongs to a broad terrain category.

The following are allowed when body placement is otherwise valid:

- grass, flowers, and other harmless non-collidable decoration in the feet space;
- ordinary leaves as a supporting surface when collidable;
- sand and gravel as ordinary collidable ground;
- non-damaging water as a destination surface, provided the player's head remains breathable;
- any other non-damaging, non-blocking content.

The following remain rejected:

- magma/lava, fire, cactus, spikes, or a block the runtime marks as damaging or lethal;
- a collidable block intersecting the player's feet, body, or head volume;
- a fluid-filled/non-breathable head position;
- another blocking entity occupying the destination body volume;
- an out-of-world coordinate;
- a candidate that would leave the player without safe ground or safe water below and cause a harmful fall.

### 4.2 Surface landing resolution

For each candidate X/Z column, nearest to the requested point first:

1. Read the terrain top after the required chunk area is ready.
2. If the top is harmless non-collidable decoration, scan through it to identify the real support while treating the decoration as passable body space.
3. If the top is non-damaging collidable terrain, including ordinary leaves, sand, or gravel, place the feet immediately above it.
4. If the top is non-damaging water, place the player at the water surface with breathable head space; entering the water is allowed.
5. Validate the full player bounding box and excluding-player entity collision.
6. Move transactionally, clear fall state/velocity as already designed, wait for the next update, and validate again.
7. Roll back safely if post-move validation fails.

The horizontal search remains close to the selected map coordinate. A genuinely hazardous area may still return `NoSafePosition`; the fix must not silently teleport the player far away merely to force success.

Waypoint teleport keeps its intended Y preference but applies the same passable-body, breathable-head, harmful-block, collision, next-frame validation, and rollback rules.

### 4.3 Failure diagnostics

An ordinary `NoSafePosition` result gains aggregate rejection diagnostics without logging the requested or candidate coordinates. The diagnostic records counts/categories such as:

- damaging support or body content;
- no stable support or safe water surface;
- blocked feet/body/head volume;
- non-breathable head;
- blocking entity collision;
- out-of-world candidate.

This makes future live failures distinguishable while preserving the existing coordinate-redaction rule.

## 5. Large-Map Feedback

The large map gains its own toast/status layer above the map, context menu, and settings panel.

Requirements:

- messages are visible without closing the large map;
- asynchronous teleport completion can safely enqueue a message for the UI thread;
- teleport success, expected failure, unavailable action, busy action, unexplored area, and persistence failure all use the in-dialog toast while the dialog is visible;
- the normal game HUD message remains the fallback when the large map is not visible;
- repeated identical messages replace/refresh the current toast instead of stacking an unbounded queue;
- the toast dismisses automatically after a short readable duration;
- success, informational, and failure states are visually distinguishable using the existing survey palette;
- messages must not cover the top-right Settings/Close controls or the bottom-left coordinate label.

The component must not emit a second generic failure message when a more specific teleport result has already been displayed.

## 6. Settings Panel Interaction

The settings panel gains a clearly labelled `完成` button.

- Pressing `完成` hides only the settings panel and returns to the large map.
- Pressing Esc/Cancel while settings are open performs the same settings-only close.
- Pressing Esc/Cancel while settings are closed closes the large map as before.
- Checkbox, slider, and size changes continue to apply live.
- Closing settings requests/flushes any pending coalesced save but does not close the map.
- The top-level `设置` button may still toggle the panel.

## 7. Non-Goals

This iteration does not:

- reveal arbitrary remote areas by panning the large map;
- force world generation or keep extra game chunks loaded;
- change invitation teleport behavior or packet IDs;
- restore Mod-count reporting or telemetry;
- change the `.sctm` disk format;
- add a new public release, tag, or pull request before in-world acceptance.

## 8. Verification

### 8.1 Automated

- Exploration-footprint tests cover scale/size calculations, chunk-boundary rounding including negative coordinates, deduplication, nearest-first priority, bounded work, stationary retries, and hidden-minimap behavior.
- Recorder tests prove full 16×16 atomic commits and no writes for unreadable chunks.
- Teleport adapter tests cover harmless passable decoration, leaves, sand, gravel, water surface, real damage blocks, blocked head/body, entity collision, and aggregate rejection diagnostics.
- Transaction tests retain next-frame validation, rollback, position synchronization, cancellation, and busy-request guarantees.
- Dialog tests cover toast routing/lifetime/replacement and settings `完成`/Esc behavior.
- Render tests repeat drag and zoom transforms across sparse and large tile catalogs without losing known terrain.
- Full Release tests and warning-as-error build pass.
- Package structure and protected original-package hash checks pass.

### 8.2 In-world acceptance

Using a fresh isolated copy of the exact final package:

1. Stand still after world load and confirm the large map records the full loaded minimap footprint, not only the center chunk.
2. Walk across a chunk boundary and confirm the new current chunk appears immediately while nearby loaded chunks fill without a visible stall.
3. Hide the minimap, walk, reopen the large map, and confirm exploration recording continued.
4. Reopen the world and confirm the surrounding footprint persists.
5. Drag and zoom the large map through the previously problematic scale and confirm known terrain is not lost.
6. Right-click unexplored terrain and confirm the message appears above the open large map.
7. Teleport onto ordinary vegetation, leaves, sand/gravel, and a water surface; confirm success without suffocation or fall damage.
8. Attempt magma/fire/cactus/spikes, blocked body space, and non-breathable underwater head positions; confirm safe refusal with an immediately visible message.
9. Open settings, change each relevant control, press `完成`, and confirm the map remains open and the setting is applied/persisted.
10. Open settings again, press Esc, and confirm only settings close; press Esc again and confirm the large map closes.

Implementation is not complete until these in-world rows are recorded with PASS/FAIL evidence.
