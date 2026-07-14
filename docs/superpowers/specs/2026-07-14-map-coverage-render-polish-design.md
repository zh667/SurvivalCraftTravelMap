# Survivalcraft Travel Map — Map Coverage and Render Polish Design

Date: 2026-07-14  
Status: user-approved design, pending implementation plan  
Branch: `feature/travel-map`

## 1. Purpose

This iteration fixes four problems found during the latest isolated in-world test:

1. Already-loaded terrain near the player can remain as flat blank/gray squares, including after teleporting or walking into one of those squares.
2. Dragging or zooming the large map can make parts of previously recorded terrain disappear into flat gray.
3. The large map draws a cyan survey crosshair over the player instead of using only the minimap-style direction arrow.
4. A new settings profile defaults to a 192-pixel minimap, while the accepted default is 160 pixels.

The approved visual distinction is:

> Real gray terrain such as exposed rock and mountain surfaces keeps its terrain color and detail. Only flat gray caused by missing exploration data or render loss is removed.

## 2. Confirmed Evidence and Root Causes

### 2.1 Persistent coverage holes

The current isolated world cache was decoded at the screenshot location around `X=71, Z=245`. The player's 16×16 terrain chunk was recorded, while several adjacent chunks were absent in a checkerboard pattern. These are genuinely missing explored pixels, not rock-colored terrain.

The game visibility range in the isolated profile is 64 blocks. A 256-pixel minimap at 0.5 blocks per pixel requests a 128×128-block square. Its corners are farther than 64 blocks from the player, so the square footprint can include chunks outside the game's effectively circular loaded area. Reducing the minimap to 160 pixels makes the requested footprint smaller, but does not by itself guarantee that delayed or previously missed loaded chunks are eventually recorded.

The exploration scheduler currently relies primarily on in-memory completion state. It can therefore stop retrying a chunk whose persistent 16×16 coverage is still incomplete. Teleportation and delayed terrain readiness make this failure more visible.

### 2.2 Zoom and pan aggregation drop known terrain

At coarse rendering levels, multiple stored pixels are aggregated into one display sample. The current aggregate is rejected unless every source pixel in the region is explored. One unknown source pixel can therefore hide all known pixels in the same aggregate, producing flat gray cells during zoom-out.

Dragging can expose the same defect even without changing scale: moving the viewport changes which world pixels fall into a visible aggregate and where partially explored boundaries are clipped. A known region can therefore be re-evaluated as an aggregate containing one unknown pixel and disappear. Separately, dragging onto a genuinely unrecorded cache hole correctly exposes blank background; those holes are repaired by the persistent-coverage reconciliation in Section 3.

### 2.3 Duplicate large-map player indicators

`MapSurfaceWidget` enables the cyan survey crosshair by default. The minimap disables it, but the large-map dialog does not. The large map already draws the directional player marker, so the crosshair is an unintended second indicator.

### 2.4 Settings default

The runtime settings model and new settings document currently initialize `MiniMapSize` to 192. Persisted settings must remain user-owned; only a new profile or a missing value should receive the new 160-pixel default.

## 3. Exploration Coverage Model

### 3.1 Source of truth

Persistent explored-pixel coverage is the source of truth for whether a terrain chunk is complete. A terrain chunk is complete only when all pixels in its 16×16 world-block area are recorded as explored in the map tile store.

The scheduler's in-memory queues remain performance aids. They must not permanently override or contradict persistent coverage.

### 3.2 Reconciliation triggers

The component reconciles the current minimap footprint with persistent coverage in these cases:

- immediately after entering or loading a world;
- when the player crosses a terrain-chunk boundary;
- when a teleport changes the player's covered area;
- when minimap size or blocks-per-pixel changes;
- periodically at a bounded cadence while the player remains in the same chunk.

During reconciliation, every footprint chunk with incomplete persistent coverage is ensured in the pending queue. Complete chunks are not rewritten merely because they are inside the footprint.

The player's current chunk receives highest priority. Other incomplete chunks are processed nearest-first. A chunk that is not yet surface-readable remains eligible for later retry. Walking into an old hole must promote that chunk to current-chunk priority and repair it as soon as its surface is readable.

### 3.3 Bounded work and loaded-terrain boundary

The existing bounded per-frame sampling policy remains. Reconciliation itself must also be bounded or use a small footprint cursor so a large configuration cannot cause a single-frame scan spike.

The mod records only terrain already readable from the running game. It must not:

- force-load or generate remote chunks;
- fabricate terrain colors for unavailable data;
- mark an unavailable pixel explored;
- explore terrain merely because the user pans the large map over it.

Therefore a completely unavailable region remains blank. When the game later loads that terrain and it is inside the player's minimap footprint, reconciliation records and persists it.

### 3.4 Atomicity and persistence

Chunk sampling remains atomic at 16×16 pixels. A failed or incomplete terrain read must not commit a partially painted terrain chunk. Successful writes continue through the existing tile-store save queue and `.sctm` format; no disk-format migration is required.

## 4. Large-Map Level-of-Detail Rendering

### 4.1 Partial aggregate rule

At display strides greater than one pixel:

- if an aggregate contains zero explored source pixels, it remains absent and the normal unexplored background is shown;
- if it contains one or more explored source pixels, it is rendered;
- its color and alpha are averaged using only the explored source pixels, not the total aggregate area;
- a fully explored aggregate retains the existing result.

This preserves recorded terrain when zooming out. At coarse levels, the rendered cell may visually cover an unknown sub-part within that cell, but the underlying exploration mask and persisted data remain unchanged. Returning to stride 1 restores exact pixel boundaries.

### 4.2 Terrain colors

The renderer must not special-case gray as an error color. Detailed gray, blue-gray, or purple-gray pixels produced by actual rock, mountain, ore, shadow, or other terrain remain visible. The fix targets only absent aggregates and coverage holes.

### 4.3 Pan, zoom, and budget behavior

Pan and zoom continue to preserve the selected map center and cursor anchor. The existing render budget remains enforced; partial aggregates count as ordinary rendered samples.

At a fixed zoom level, dragging away and back to the same world rectangle must reproduce the same known pixels and colors. Viewport clipping, aggregate alignment, tile-descriptor selection, and render-budget culling must never convert persisted explored pixels into unexplored background. Repeated zoom-in, zoom-out, and drag operations must not mutate or discard known map data.

## 5. Player Marker Consistency

The large map disables the cyan survey crosshair and displays only the same red, outlined, heading-aware triangular arrow used by the minimap.

Consistency includes:

- the same fill and outline colors;
- the same heading convention and rotation;
- the same triangle geometry;
- size derived from the same minimap marker sizing rule, updated when the minimap-size setting changes.

The player's position remains anchored to world coordinates on both map surfaces.

## 6. Minimap Default Size

The default minimap size for a new settings profile is 160 pixels.

- A missing settings file creates a 160-pixel value.
- A newly constructed runtime settings object uses 160.
- A valid persisted user selection such as 192, 256, 320, or 384 is preserved.
- Existing schema migration behavior is retained unless it specifically substitutes the old default for an absent value; migration must not overwrite an explicit user choice.
- The settings panel continues to offer the existing size choices with 160 selected for a new profile.

## 7. Error Handling and Diagnostics

An unreadable terrain chunk is an expected transient state, not an exception. It stays incomplete and is retried later without flooding logs.

Unexpected failures in coverage lookup, sampling, or committing follow the existing diagnostic and recovery paths. They must not mark the chunk complete. Repeated reconciliation must deduplicate pending work and must not create an unbounded retry queue.

The renderer treats corrupt or unavailable tile data as absent using the existing tile validation behavior; it must not manufacture a gray replacement tile.

## 8. Non-Goals

This iteration does not:

- automatically reveal terrain that the game has never loaded around the player;
- generate a continuous world map from the world seed;
- alter safe-teleport rules, invitation teleport, waypoints, or large-map feedback behavior from the preceding accepted design;
- change the `.sctm` format;
- remove genuine gray terrain colors;
- publish, tag, merge, or open an additional pull request before full in-world acceptance.

## 9. Automated Verification

Tests must cover at least:

1. A footprint chunk marked complete in memory but incomplete in persistent coverage is re-enqueued.
2. A teleported player arrives before neighboring terrain is ready; delayed readiness is eventually recorded without another teleport.
3. Walking into a historical blank chunk prioritizes and repairs the current chunk.
4. Periodic reconciliation repairs delayed chunks while stationary and remains bounded/deduplicated.
5. Hiding the minimap does not disable footprint exploration.
6. A partially explored 2×2 or larger LOD aggregate renders the average of only its explored pixels.
7. A zero-explored aggregate stays absent, and a fully explored aggregate produces the previous color.
8. At a fixed zoom level, dragging across partial exploration boundaries and returning to the original rectangle reproduces the same known pixels and colors.
9. Repeated pan and zoom transformations retain all known terrain under the render budget and do not lose known tile descriptors through viewport culling.
10. The large map has no survey crosshair and uses the minimap-style player arrow, including setting-driven size updates.
11. New settings default to 160, while valid persisted choices and deliberate migrations are preserved.

The full Release test suite and warning-as-error build must pass. Packaging must remain deterministic, contain the expected entries, install only into the isolated smoke-game copy, and leave the protected original `34GPSFix.netmod` unchanged.

## 10. In-World Acceptance

Using a fresh isolated install of the exact final package:

1. Start a new profile and confirm the minimap initially uses the 160-pixel size.
2. Stand still after world load and confirm all surface-readable chunks in the minimap footprint fill progressively without checkerboard holes.
3. Teleport to a distant explored destination, wait for terrain loading, and confirm the new current chunk and nearby loaded footprint fill without requiring a second teleport.
4. Walk into a previously blank nearby square and confirm it appears as soon as its terrain surface is readable.
5. Hide the minimap, move through loaded terrain, reopen the large map, and confirm the exploration was still persisted.
6. Reopen the world and confirm repaired coverage remains present.
7. At the same zoom level, repeatedly drag across the problematic area and back; persisted terrain must reproduce identically instead of turning flat gray.
8. Repeatedly zoom in and out through the scale that previously produced flat gray cells; all previously known terrain remains visible.
9. Confirm real exposed rock and mountain regions retain their natural detailed gray colors.
10. Confirm the large map displays the same red outlined direction arrow as the minimap and no cyan crosshair.
11. Confirm a pre-existing profile keeps its explicitly saved minimap size.

Implementation is not accepted until these rows are recorded with PASS/FAIL evidence against the exact packaged DLL.
