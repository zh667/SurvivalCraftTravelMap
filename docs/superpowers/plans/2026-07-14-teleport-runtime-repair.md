# Teleport Runtime Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the proven first-teleport reflection failure, preserve safe movement/rollback semantics, and make every unexpected teleport failure diagnosable without logging target coordinates.

**Architecture:** Keep Survivalcraft reflection inside `SurvivalcraftPlayerFacade`, transaction stages inside `SafeTeleportService`, and request context/log formatting in a small diagnostic layer. Network sessions still map unexpected exceptions to `InternalError`, while the service reports the precise failed stage before rethrowing. Existing host/remote deadlines and successful `PositionSet` synchronization remain unchanged.

**Tech Stack:** C#/.NET 10, Survivalcraft 2.4.40.6 runtime assemblies, reflection, async-local diagnostic context, xUnit v3, PowerShell packaging.

## Global Constraints

- The confirmed root cause is exact: `ComponentLocomotion.m_falling` is a private instance `bool`, while `ComponentHealth.m_wasStanding` is a public instance `bool`; the current `NonPublic`-only lookup fails on the latter.
- Resolve both public and non-public instance fields, then validate non-static `bool` shape before using them.
- Do not weaken safe Y search, collision checks, chunk loading, next-frame validation, rollback, or velocity/fall-state clearing.
- Pre-move failures leave the player untouched. Post-move failures restore safely. Only a fully successful transaction broadcasts `PositionSet`.
- Keep the integrated-host chunk budget at 10 seconds and the remote ID-61 protocol deadline at its current 4/5-second design.
- Preserve packet IDs `41` and `61` and all invitation behavior.
- Cancellation and expected results (`NoSafePosition`, `OutOfWorld`, `ChunkTimeout`, `Busy`, successful rollback) are not internal diagnostics.
- Detailed logs contain route, request ID when available, request kind, failed stage, exception full type, a number-redacted message, and a number-redacted stack trace. The logger never appends request target X/Y/Z as structured fields.
- User-facing `InternalError` text is `传送失败，详细原因已写入日志`.
- A failed static type initializer remains poisoned for the lifetime of the process. The final live test must fully exit the game before loading the fixed DLL.
- Work test-first and commit each task separately. Do not include unrelated dirty-worktree files in a task commit.

---

### Task 0: Preserve and commit the existing runtime-repair baseline

**Run this task exactly once before any of the three repair plans.** The worktree already contains reviewed-but-uncommitted integrated-host activation, persistence identity, transparent-cache, timeout, and synchronization changes from the preceding live-debug round. They must not be mixed into later task commits or discarded.

**Files:**

- Existing modified files reported by `git status --short` under `README.md`, `docs/`, `src/`, and `tests/`
- Existing untracked runtime files: `src/SurvivalcraftTravelMap/UI/TravelMapOverlayLayout.cs` and `tests/SurvivalcraftTravelMap.Tests/DatabaseInjectionTests.cs`
- Exclude the three already committed/tracked implementation-plan documents from this baseline

- [ ] **Step 1: Audit the dirty set before staging**

```powershell
git status --short
git diff --check
git diff --name-only
```

Expected: only the known runtime-repair/docs/test files are dirty. Stop and investigate any unrelated user file rather than staging it.

- [ ] **Step 2: Re-run the existing baseline gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: the full suite passes and the build has zero warnings/errors. Record the actual count; `439/439` is only the last known baseline.

- [ ] **Step 3: Commit the known baseline separately**

```powershell
git add README.md docs src tests
git diff --cached --check
git diff --cached --stat
git commit -m "fix: stabilize integrated-host travel map runtime"
```

Confirm `git status --short` is clean before beginning Task 1. Do not claim this intermediate baseline fixes the newly diagnosed minimap, chunk, or reflection defects.

---

### Task 1: Fix and validate Survivalcraft movement-field binding

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Teleport/SurvivalcraftPlayerMover.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs`

- [ ] **Step 1: Add failing reflection tests against real and fixture types**

Expose this internal helper for the friend test assembly:

```csharp
internal static FieldInfo GetRequiredBooleanInstanceField(Type type, string name);
```

Test nested fixture fields for public/private instance `bool` success and missing/static/non-`bool` rejection. Then bind the actual game types and assert:

```csharp
var falling = SurvivalcraftPlayerFacade.GetRequiredBooleanInstanceField(
    typeof(ComponentLocomotion), "m_falling");
var wasStanding = SurvivalcraftPlayerFacade.GetRequiredBooleanInstanceField(
    typeof(ComponentHealth), "m_wasStanding");

Assert.False(falling.IsPublic);
Assert.True(wasStanding.IsPublic);
Assert.False(falling.IsStatic);
Assert.False(wasStanding.IsStatic);
Assert.Equal(typeof(bool), falling.FieldType);
Assert.Equal(typeof(bool), wasStanding.FieldType);
```

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~AdapterContractTests'
```

Expected: the real public `m_wasStanding` field cannot be resolved by the current helper.

- [ ] **Step 3: Implement the exact field contract**

Use the approved instance lookup first, then a static-only fallback solely so an incompatible static field receives a shape error rather than looking missing:

```csharp
const BindingFlags flags = BindingFlags.Instance
    | BindingFlags.Public
    | BindingFlags.NonPublic;
var field = type.GetField(name, flags)
    ?? type.GetField(
        name,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(type.FullName, name);
if (field.IsStatic || field.FieldType != typeof(bool))
{
    throw new InvalidOperationException(
        $"Field {type.FullName}.{name} must be an instance Boolean field; " +
        $"actual type={field.FieldType.FullName}, static={field.IsStatic}.");
}

return field;
```

Have both static `FieldInfo` fields call this helper. Do not lazy-ignore a missing field; an incompatible game build must fail with a descriptive error.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all adapter tests pass against the actual `Survivalcraft.dll` in the game directory.

- [ ] **Step 5: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Teleport/SurvivalcraftPlayerMover.cs tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs
git diff --cached --check
git commit -m "fix: bind public and private movement fields"
```

---

### Task 2: Add exact failure-stage diagnostics without changing the transaction

**Files:**

- Create: `src/SurvivalcraftTravelMap/Teleport/TeleportDiagnostics.cs`
- Modify: `src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs`

- [ ] **Step 1: Add failing diagnostic-stage tests**

Define:

```csharp
public enum TeleportExecutionStage
{
    ProtocolDispatch,
    ChunkLoad,
    CandidateSearch,
    MovementSnapshot,
    PositionWrite,
    PostMoveValidation,
    Rollback,
    PositionSync,
}

public sealed record TeleportFailureDiagnostic(
    TeleportExecutionStage Stage,
    Exception Exception);
```

Extend test fakes with injectable exceptions. Induce failures at chunk load, terrain/candidate search, snapshot capture, movement write, next-frame/post-move validation, rollback itself, and position synchronization. Assert exactly one diagnostic with the precise stage and original exception.

Also assert:

- cancellation produces zero diagnostics;
- snapshot failure produces zero move/restore/sync calls;
- a move/post-validation failure performs exactly one safe restore and zero sync calls;
- an unsafe post-validation result returns `RolledBack` without an internal diagnostic;
- a position-sync exception rolls back and never returns success;
- both successful surface and waypoint transactions sync exactly once;
- an exception thrown by the diagnostic callback never replaces the original teleport exception.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~SafeTeleportServiceTests'
```

Expected: compilation fails because diagnostic types/constructor do not exist.

- [ ] **Step 3: Add a compatible reporter constructor and mutable execution trace**

Keep the current five- and six-argument constructors. Add the full constructor:

```csharp
public SafeTeleportService(
    ITerrainAccess terrain,
    IChunkLoader chunkLoader,
    IPlayerMover playerMover,
    IEntityCollisionQuery collisionQuery,
    ITeleportClock clock,
    Action onPositionCommitted,
    Action<TeleportFailureDiagnostic> reportFailure);
```

Default missing callbacks to no-ops. Each public transaction owns a small mutable trace. Set its stage immediately before chunk loading, candidate/terrain reads, snapshot capture, position write, next-frame validation, and position synchronization. At the outer boundary, catch non-cancellation exceptions, invoke the reporter once inside a protective try/catch, then rethrow the original exception unchanged.

- [ ] **Step 4: Preserve the original stage across a successful rollback**

Before rollback, save `originalStage` and set `Rollback`. If safe restore succeeds, restore `originalStage` before returning `RolledBack` or rethrowing the original exception. If restore fails, leave the stage at `Rollback` and throw the existing `TeleportRollbackException`. This ensures `Rollback` means the rollback operation failed, not merely that a rollback occurred.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run Step 2 again. Expected: all safe-teleport tests pass, including existing safety and deadline cases.

- [ ] **Step 6: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Teleport/TeleportDiagnostics.cs src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs
git diff --cached --check
git commit -m "feat: report safe teleport failure stages"
```

---

### Task 3: Add request context, redact logs, and retain protocol behavior

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Teleport/TeleportDiagnostics.cs`
- Modify: `src/SurvivalcraftTravelMap/Network/CoordinateTeleportPackage.cs`
- Modify: `src/SurvivalcraftTravelMap/Network/TravelMapNetworkPeerIdentity.cs`
- Modify: `src/SurvivalcraftTravelMap/Network/TravelMapNetworkRuntime.cs`
- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TeleportDiagnosticReporterTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/CoordinateTeleportPackageTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/LegacyGpsPackageTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`

- [ ] **Step 1: Add failing formatter/context tests**

Add an async-flow-safe context that contains no coordinates and tracks whether a nested layer already emitted the failure:

```csharp
internal readonly record struct TeleportRequestDiagnosticContext(
    string Route,
    uint? RequestId,
    string Kind);

internal static class TeleportDiagnosticContext
{
    internal static TeleportRequestDiagnosticContext? Current { get; }
    internal static bool HasReportedFailure { get; }
    internal static IDisposable Ensure(TeleportRequestDiagnosticContext context);
    internal static void MarkFailureReported();
}
```

Add a pure formatter:

```csharp
internal static string FormatFailure(
    TeleportRequestDiagnosticContext? context,
    TeleportFailureDiagnostic diagnostic);
```

`FormatFailure` must not use raw `Exception.ToString()`. Emit the full exception type, but run both `Exception.Message` and `Exception.StackTrace` through a deterministic invariant-number redactor that replaces signed integer/decimal/scientific literals with `<number>`. Recurse through inner exceptions with the same rule. This preserves method/type structure while preventing an engine message from echoing an exact coordinate.

Assert output contains route, request ID (or `none`), kind, stage, exception full type, redacted message, and redacted stack. Throw an exception whose message contains distinctive positive/negative/decimal target values and assert none survives. Test nested `Ensure` calls for the same request share the reported flag, unrelated nested scopes restore the previous context, and parallel async tasks do not leak context.

- [ ] **Step 2: Add failing protocol mapping tests**

Make the remote session executor and integrated-host executor throw a sentinel exception. Assert both return `CoordinateTeleportResultCode.InternalError`; host result reporting still contains the original request ID/kind/result; no success synchronization occurs. Add a pure/extracted ID-41 execution-boundary test whose invitation executor throws: it must report `ProtocolDispatch` when no deeper diagnostic exists and return the exact Chinese failure response instead of faulting an observed task. Retain existing deadline tests to prove remote and host budgets did not change.

- [ ] **Step 3: Run focused tests and confirm RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~CoordinateTeleportPackageTests|FullyQualifiedName~TeleportDiagnosticReporterTests|FullyQualifiedName~LegacyGpsPackageTests|FullyQualifiedName~PackageStructureTests'
```

Expected: missing context/formatter failures and current source-log redaction failures.

- [ ] **Step 4: Scope ID-61 executions and wire the service reporter**

Use `TeleportDiagnosticContext.Ensure` with route `remote` around the complete `TravelMapNetworkRuntime.HandleCoordinateOnServerAsync` boundary and its `CoordinateTeleportServerSession` executor call; use route `host` around `AuthoritativeHostTeleportSession`. `Ensure` reuses an already matching scope so nested layers share `HasReportedFailure`. Use request ID and enum name only. Dispose scopes in all success, error, timeout, cancellation, binding, and response-send paths.

Implement one `TeleportDiagnosticReporter.Report` path that formats the current context plus stage/exception, writes to `Engine.Log.Warning`, then marks the scope reported. Pass it into the seven-argument `SafeTeleportService` constructor. In each ID-61 and outer network `catch (Exception exception)`, if `HasReportedFailure` is false, report `ProtocolDispatch` before mapping to `InternalError`; this closes binding/dispatch failures outside the safe service without duplicating a deeper stage. Add a source-contract test for the outer `TravelMapNetworkRuntime` catch, not only the session catch.

Wrap direct local and legacy ID-41 calls in `local`/`invitation` contexts with no fabricated request ID. Extract the invitation execution/result mapping into a testable async helper (or equivalent seam). It must catch an unexpected exception, emit a fallback `ProtocolDispatch` diagnostic only if none was already reported, and always send/return `传送失败，详细原因已写入日志` to the inviter. Do not rely on the existing `_ =>` `TeleportResult` switch arm, because an exception never reaches that switch.

- [ ] **Step 5: Redact result logs and update the user message**

Reduce `ReportCoordinateTeleportResult` to:

```text
[TravelMap] Coordinate teleport route={route}, request={id}, kind={kind}, result={result}.
```

Remove every use of `request.X`, `request.Z`, `request.Target`, and `target=(` from that method. Update both ID-61 `InternalError` text and the ID-41 generic internal-failure fallback to `传送失败，详细原因已写入日志`.

- [ ] **Step 6: Run focused tests and confirm GREEN**

Run Step 3 again. Expected: all selected tests pass; protocol errors still map to `InternalError`, and no target coordinate appears in diagnostic/result output.

- [ ] **Step 7: Commit only this task**

```powershell
git add src/SurvivalcraftTravelMap/Teleport/TeleportDiagnostics.cs src/SurvivalcraftTravelMap/Network/CoordinateTeleportPackage.cs src/SurvivalcraftTravelMap/Network/TravelMapNetworkPeerIdentity.cs src/SurvivalcraftTravelMap/Network/TravelMapNetworkRuntime.cs src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs tests/SurvivalcraftTravelMap.Tests/TeleportDiagnosticReporterTests.cs tests/SurvivalcraftTravelMap.Tests/CoordinateTeleportPackageTests.cs tests/SurvivalcraftTravelMap.Tests/LegacyGpsPackageTests.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs
git diff --cached --check
git commit -m "fix: diagnose and redact teleport failures"
```

---

### Task 4: Run the combined release gate and a fresh-process in-world verification

**Prerequisite:** Complete every task in this plan plus `2026-07-14-adaptive-minimap-hud.md` and `2026-07-14-chunk-driven-exploration.md`.

**Files:**

- Modify: `README.md`
- Modify: `docs/user-guide.md`
- Modify: `docs/smoke-test-2026-07-13.md`
- Modify: `.superpowers/sdd/progress.md` (ignored tracking file; do not force-add)

- [ ] **Step 1: Run all automated tests and a warnings-as-errors build**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: every test passes; build has zero warnings and zero errors. Record the actual counts rather than copying the historical `439/439` value.

- [ ] **Step 2: Build and verify the package while protecting the original file**

```powershell
$original='E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod'
$before=(Get-FileHash -Algorithm SHA256 -LiteralPath $original).Hash
& powershell -ExecutionPolicy Bypass -File tools/Build-NetMod.ps1 -SurvivalcraftDir 'E:\game\SurvivalcraftNet2.4'
& powershell -ExecutionPolicy Bypass -File tools/Verify-Package.ps1 artifacts/SurvivalcraftTravelMap.netmod
$after=(Get-FileHash -Algorithm SHA256 -LiteralPath $original).Hash
if ($before -ne $after -or $after -ne '00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B') { throw 'Original package changed.' }
Get-FileHash -Algorithm SHA256 artifacts/SurvivalcraftTravelMap.netmod
```

Expected: `PACKAGE_OK`, original hash unchanged, and a new SCTM package hash recorded in the smoke-test document.

- [ ] **Step 3: Install the exact package into isolated `NetMods` and invalidate stale cache**

First verify every resolved source/destination stays under the worktree's `.superpowers/smoke-game` directory. With the isolated game stopped:

1. create `.superpowers/smoke-game/DisabledNetMods` if absent;
2. move the isolated copies of `NetMods/34GPSFix.netmod` and `NetMods/CompassMenu.netmod` into that disabled directory (never touch the main game's files);
3. copy the exact artifact to `.superpowers/smoke-game/NetMods/SurvivalcraftTravelMap.netmod`;
4. remove only the three exact stale cache entries `ModsCache/34GPSFix.netmod`, `ModsCache/CompassMenu.netmod`, and `ModsCache/SurvivalcraftTravelMap.netmod`, allowing the isolated game to rebuild its cache;
5. assert isolated `NetMods` contains SCTM and contains neither conflicting Mod.

Use literal, verified paths rather than a recursive cleanup:

```powershell
$smoke=[IO.Path]::GetFullPath((Join-Path $PWD '.superpowers\smoke-game')).TrimEnd('\')
$prefix=$smoke+'\'
function Assert-SmokePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Outside smoke game: $full" }
    $full
}
$netMods=Assert-SmokePath (Join-Path $smoke 'NetMods')
$disabled=Assert-SmokePath (Join-Path $smoke 'DisabledNetMods')
$cache=Assert-SmokePath (Join-Path $smoke 'ModsCache')
New-Item -ItemType Directory -Path $disabled -Force | Out-Null
foreach($name in @('34GPSFix.netmod','CompassMenu.netmod')) {
    $source=Assert-SmokePath (Join-Path $netMods $name)
    $destination=Assert-SmokePath (Join-Path $disabled $name)
    if(Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination $destination -Force }
}
$artifact=[IO.Path]::GetFullPath((Join-Path $PWD 'artifacts\SurvivalcraftTravelMap.netmod'))
$installed=Assert-SmokePath (Join-Path $netMods 'SurvivalcraftTravelMap.netmod')
Copy-Item -LiteralPath $artifact -Destination $installed -Force
foreach($name in @('34GPSFix.netmod','CompassMenu.netmod','SurvivalcraftTravelMap.netmod')) {
    $cached=Assert-SmokePath (Join-Path $cache $name)
    if(Test-Path -LiteralPath $cached) { Remove-Item -LiteralPath $cached -Force }
}
if((Get-FileHash -Algorithm SHA256 -LiteralPath $artifact).Hash -ne
   (Get-FileHash -Algorithm SHA256 -LiteralPath $installed).Hash) { throw 'Isolated package hash mismatch.' }
```

Compare artifact and isolated-`NetMods` SHA-256. Recheck the main `E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod` hash afterward. Do not copy SCTM into the user's main `NetMods`, do not replace the main `34GPSFix.netmod`, and do not touch primary world files.

- [ ] **Step 4: Fully stop the old process before testing**

Confirm no Survivalcraft process from the isolated copy is running. A DLL hot swap is insufficient because the previous `SurvivalcraftPlayerFacade` type-initialization failure is cached for that process. Start a completely new process using the isolated World2 copy and retained old travel-map cache.

- [ ] **Step 5: Execute the approved in-world matrix**

Verify and capture evidence for:

1. default minimap is visually about `256×256` physical pixels, top/right adaptive margins are correct, and no text teleport button appears;
2. inventory, character, crafting, sleep, modal dialogs, and the large map hide both HUD items; closing them restores the prior setting;
3. clicking the minimap and pressing `M` open the large map;
4. crossing one `16×16` boundary reveals exactly that complete chunk immediately and no adjacent chunk;
5. re-entering a formerly partial/transparent World2 chunk repairs all 256 pixels;
6. right-click surface teleport succeeds at a safe Y, does not fall/suffocate, and writes a success result;
7. waypoint teleport succeeds safely;
8. a deliberately unsafe/no-position target leaves the player unmoved or rolls back cleanly;
9. single-player hides the invitation icon; adding another player shows the icon below the map and invitation teleport still works;
10. normal result logs contain no exact map/waypoint target coordinates. The forced internal-failure format/stage/redaction path is verified by automated tests, not by fault injection into the release DLL during this live run.

- [ ] **Step 6: Update truthful documentation and commit the verification record**

Replace historical hashes/counts only where they are explicitly labelled current. Mark a row PASS only when evidence exists; otherwise leave it pending. Do not claim a shareable/public release or create a release/tag while distribution authorization is unresolved.

```powershell
git add README.md docs/user-guide.md docs/smoke-test-2026-07-13.md
git diff --cached --check
git commit -m "docs: record repaired runtime verification"
```

- [ ] **Step 7: Request final specification and code-quality reviews**

Run a fresh specification review against the approved design, then a separate code-quality review over all commits from the three plans. Resolve every finding, rerun the affected focused tests and the complete release gate, and create only one final PR after both reviews are clean.
