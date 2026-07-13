# Task 9 report: legacy invitations and server-authoritative travel protocol

## Outcome

Implemented the complete Task 9 networking boundary without changing `mod.netxdb`. The legacy 34GPSFix invitation wire format remains byte-compatible on package ID 41, while new coordinate travel uses an independent, strictly validated package ID 61 protocol. Local worlds continue to use the local `SafeTeleportService`; multiplayer clients only send requests and never move a player directly.

The original `E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod` was inspected read-only and remains byte-for-byte unchanged. Its final SHA-256 is `00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B`.

## Legacy package ID 41

- Preserved the six original Int32 message discriminators and payload shapes: `Request` (0), `Response` (1), `Teleport` (2), `MTeleport` (3), `TResponse` (4), and `TAllow` (5).
- Preserved BinaryWriter little-endian integers, booleans, 7-bit-length UTF-8 strings, and the Newtonsoft JSON player-list fields `ServerNumber` and `PlayerName`. Golden byte tests cover every message kind.
- Added strict bounded decoding: 64 KiB maximum payload, 16 KiB maximum string, 1,024 maximum players, strict UTF-8, exact payload consumption, and rejection of unknown, malformed, truncated, oversized, or trailing data.
- Added a thread-safe 30-second invitation manager with self/offline checks, one pending invitation participation per player, administrator immediate travel, accept/reject/expiry paths, and source-player binding.
- `AcceptTeleportInvitations=false` now automatically rejects only invitation dialogs (`TResponse` message type 1). Ordinary result messages (`TResponse` message type 0) are still displayed.
- Added an actual four-players-per-page `TeleportPanelWidget`, a client-side player-list entry point, invitation dialogs, and ordinary result notifications.
- Accepted and administrator-immediate player travel is executed by the inviter's server-side `SafeTeleportService` near the target player. The client never writes position.
- IL inspection showed the original `MulitServerUtils` methods (`MTPPlayer`, `MAddITPPlayers`, `AddResponseAction`, `SendAllowStage`, and related helpers) are one-byte `ret` no-ops. The `MTeleport` wire shape is still accepted; this implementation returns an ordinary, explicit “cross-server travel unsupported” result instead of silently doing nothing.

## Coordinate package ID 61

- Added independent `CapabilityRequest`, `CapabilityResponse`, `SurfaceRequest`, `WaypointRequest`, and `Result` messages with nonzero UInt32 request IDs.
- Surface requests carry only Int32 X/Z. They never accept or transmit a client-selected Y. Waypoint requests carry finite bounded XYZ floats; the server still performs safe-placement validation.
- Added stable result codes: success, rejected, unsupported, disabled, timed out, no safe position, out of world, rolled back, malformed, duplicate, disconnected, and internal error.
- Added strict bounded decoding: 512-byte maximum payload, 256-byte maximum result text, strict UTF-8, exact modes/kinds/results, finite and protocol-bounded coordinates, nonzero request IDs, and exact payload consumption.
- Added server capability settings, independently controlling surface and waypoint travel, persisted at `app:/SurvivalcraftTravelMap/server-settings.json`. Missing or corrupt configuration falls back safely to enabled defaults; corrupt files are isolated.
- Added peer-bound server sessions, in-flight and completed replay protection, serial request ordering with UInt32 wrap support, wrong-peer rejection, concurrent duplicate rejection, and disconnect cancellation/cleanup.
- Added a client session with capability discovery, five-second response deadlines, late/wrong-peer response rejection, request-ID wrap handling, and one unsupported/timeout notification per session.
- Wired both packages through the game's real `IPackage`, `PackageManager.RegisterPackage`, `NetNode.QueuePackage`, `ProjectNet`, `Client`, and player-component boundaries. Package conflicts are detected before registration.
- Server responses are emitted only after the server-side `SafeTeleportService` completes. No client movement dependency exists in the ID 61 client session.

## Removed behavior and activation boundary

- No mod-count reporting, package ID 60, anti-cheat package, or related registration was added. Source tests guard this boundary.
- No XDB activation was performed. `src/SurvivalcraftTravelMap/mod.netxdb` is unchanged from base `008fd54`; activation and in-game smoke work remain Task 10.
- The map and player invitation-travel features remain; unrelated original telemetry behavior does not.

## TDD evidence

Task 9 was developed in red/green slices:

1. ID 41 golden/malformed tests initially failed because the legacy codec and package did not exist, then passed after byte-compatible serialization and bounded parsing were implemented.
2. Invitation lifecycle tests initially failed on missing invitation types, then covered the 30-second boundary, self/offline/admin behavior, pending conflicts, accept/reject, ordinary-message visibility, and disabled-dialog auto-rejection.
3. ID 61 codec tests initially failed on missing protocol types, then covered all message forms plus unknown kind/result, mismatched mode, zero IDs, NaN/infinity/out-of-range values, truncated/oversized strings, and trailing bytes.
4. Client/server session tests initially failed on missing runtime behavior, then covered capability negotiation, independent settings, five-second timeout, single notification, wrong peers, late responses, replay, concurrent duplicates, request-ID wrap, and disconnect cancellation.
5. A bounded-string red test exposed `BinaryReader.ReadString` accepting allocation before size validation; decoding was changed to validate the 7-bit byte length first. A replay red test exposed old IDs succeeding after the replay cache aged out; monotonic serial ordering fixed it.
6. Production boundary tests initially failed before real package registration/routing, server settings persistence, map-mode routing, and invitation UI integration were connected.

Final suite: 330 passing tests, zero failures.

## Verification

```text
dotnet test tests/SurvivalcraftTravelMap.Tests/SurvivalcraftTravelMap.Tests.csproj -c Release --no-restore
Passed: 330, Failed: 0, Skipped: 0

dotnet build src/SurvivalcraftTravelMap/SurvivalcraftTravelMap.csproj -c Release --no-restore -p:TreatWarningsAsErrors=true
0 warnings, 0 errors

powershell -ExecutionPolicy Bypass -File tools/Build-NetMod.ps1
NETMOD_BUILT ...\artifacts\SurvivalcraftTravelMap.netmod

powershell -ExecutionPolicy Bypass -File tools/Verify-Package.ps1 -PackagePath artifacts\SurvivalcraftTravelMap.netmod
PACKAGE_OK

git diff --check
clean (line-ending notices only)

git diff --exit-code 008fd54 -- src/SurvivalcraftTravelMap/mod.netxdb
no differences
```

## Runtime/API evidence

- Decompiled `IPackage` exposes byte `ID`, `To`/`Except`/`From`, `MinNeedState`, `ReadData`, `WriteData`, and `Handle(ProjectNet, NetNode, bool)`; both new package implementations use those exact members.
- `PackageStreamReader`/`PackageStreamWriter` derive from `BinaryReader`/`BinaryWriter`, matching the golden wire tests.
- `PackageManager.RegisterPackage` rejects duplicate byte IDs, so the loader checks conflicts before registering IDs 41 and 61.
- `Client` supplies `ID`, `PlayerGuid`, `TokenId`, `Peer`, `PlayerData`, and `IsConnected`; the ID 61 server session identity binds the source connection fields to the owning player component.
- `SubsystemPlayers` supplies `ComponentPlayers` and `MainPlayer`, used for the server player list and client UI dispatch respectively.
