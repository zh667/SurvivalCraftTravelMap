# Harm-Based Safe Teleport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow teleport onto harmless vegetation, leaves, sand/gravel, and safe water while still preventing damage, suffocation, entity overlap, harmful falls, and invalid Y placement.

**Architecture:** Separate block metadata classification from candidate-placement policy. Represent harmless non-collidable content explicitly as passable, resolve surface support below decoration, assess each candidate with a typed rejection reason, and aggregate expected `NoSafePosition` diagnostics without logging coordinates.

**Tech Stack:** C#/.NET 10, Survivalcraft block metadata and collision APIs, transactional `SafeTeleportService`, xUnit v3.

## Global Constraints

- Reject actual damage/lethal blocks: magma/lava, fire, cactus, spikes, `ShouldAvoid`, `KillsWhenStuck`, and positive `DefaultHeat`.
- Grass, flowers, and harmless non-collidable decoration are passable body space.
- Ordinary leaves, sand, and gravel are valid collidable support.
- Non-damaging water is valid when the destination head remains breathable.
- A collidable feet/head cell, fluid-filled head, blocking entity, out-of-world cell, or harmful fall remains unsafe.
- Surface teleport keeps the existing radius-eight horizontal search and nearest-distance preference.
- Waypoint teleport keeps its ±8 Y preference and must not fall back to a remote surface height.
- Preserve chunk-load timeout, cancellation, movement snapshot, velocity/fall reset, next-frame validation, rollback, and authoritative `PositionSet` rules.
- Expected `NoSafePosition` diagnostics contain rejection categories/counts and no requested/candidate coordinates.
- Work test-first and commit each task separately.

---

### Task 1: Classify harmless passable blocks and centralize safety policy

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Teleport/TeleportContracts.cs`
- Modify: `src/SurvivalcraftTravelMap/Teleport/SurvivalcraftTerrainAccess.cs`
- Create: `src/SurvivalcraftTravelMap/Teleport/TeleportBlockSafetyPolicy.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TeleportBlockSafetyPolicyTests.cs`

**Interfaces:**

- Produces: `TeleportBlockKind.Passable`.
- Produces: `TeleportBlockSafetyPolicy.IsHarmful`, `IsStableSupport`, `IsWater`, `IsFeetPassable`, and `IsHeadBreathable`.
- Consumes: existing explicit hazard kinds and metadata flags.

- [ ] **Step 1: Write failing metadata and policy tests**

Replace the adapter theory with cases that prove classification precedence:

```csharp
public static TheoryData<SurvivalcraftBlockMetadata, TeleportBlockKind> BlockKinds => new()
{
    { new(IsAir: true), TeleportBlockKind.Air },
    { new(), TeleportBlockKind.Passable },
    { new(IsCollidable: true), TeleportBlockKind.SafeSolid },
    { new(IsFluid: true), TeleportBlockKind.Fluid },
    { new(IsLeaves: true, IsCollidable: true), TeleportBlockKind.Leaves },
    { new(IsFalling: true, IsCollidable: true), TeleportBlockKind.Falling },
    { new(IsDamaging: true, IsCollidable: true), TeleportBlockKind.Damaging },
    { new(Hazard: SurvivalcraftBlockHazard.Lava), TeleportBlockKind.Lava },
    { new(Hazard: SurvivalcraftBlockHazard.Fire), TeleportBlockKind.Fire },
    { new(Hazard: SurvivalcraftBlockHazard.Cactus), TeleportBlockKind.Cactus },
    { new(Hazard: SurvivalcraftBlockHazard.Spikes), TeleportBlockKind.Spikes },
};
```

Add policy assertions:

```csharp
[Theory]
[InlineData(TeleportBlockKind.SafeSolid)]
[InlineData(TeleportBlockKind.Leaves)]
[InlineData(TeleportBlockKind.Falling)]
public void Ordinary_collidable_ground_is_stable_support(TeleportBlockKind kind) =>
    Assert.True(TeleportBlockSafetyPolicy.IsStableSupport(kind));

[Theory]
[InlineData(TeleportBlockKind.Air)]
[InlineData(TeleportBlockKind.Passable)]
[InlineData(TeleportBlockKind.Water)]
[InlineData(TeleportBlockKind.Fluid)]
public void Harmless_noncollidable_content_is_feet_passable(TeleportBlockKind kind) =>
    Assert.True(TeleportBlockSafetyPolicy.IsFeetPassable(kind));

[Theory]
[InlineData(TeleportBlockKind.Air, true)]
[InlineData(TeleportBlockKind.Passable, true)]
[InlineData(TeleportBlockKind.Water, false)]
[InlineData(TeleportBlockKind.Fluid, false)]
public void Head_must_remain_breathable(TeleportBlockKind kind, bool expected) =>
    Assert.Equal(expected, TeleportBlockSafetyPolicy.IsHeadBreathable(kind));
```

Assert only lava, fire, cactus, spikes, and damaging return true from `IsHarmful`.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~AdapterContractTests|FullyQualifiedName~TeleportBlockSafetyPolicyTests'
```

Expected: compilation fails because `Passable` and `TeleportBlockSafetyPolicy` do not exist.

- [ ] **Step 3: Add the semantic block kind and policy**

Add `Passable` after `Air` in `TeleportBlockKind` and create:

```csharp
namespace SurvivalcraftTravelMap.Teleport;

public static class TeleportBlockSafetyPolicy
{
    public static bool IsHarmful(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Lava or
        TeleportBlockKind.Fire or
        TeleportBlockKind.Cactus or
        TeleportBlockKind.Spikes or
        TeleportBlockKind.Damaging;

    public static bool IsStableSupport(TeleportBlockKind kind) => kind is
        TeleportBlockKind.SafeSolid or
        TeleportBlockKind.Leaves or
        TeleportBlockKind.Falling;

    public static bool IsWater(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Water or
        TeleportBlockKind.Fluid;

    public static bool IsFeetPassable(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Air or
        TeleportBlockKind.Passable or
        TeleportBlockKind.Water or
        TeleportBlockKind.Fluid;

    public static bool IsHeadBreathable(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Air or
        TeleportBlockKind.Passable;
}
```

Change adapter precedence to:

```csharp
private static TeleportBlockKind Classify(SurvivalcraftBlockMetadata metadata) => metadata.Hazard switch
{
    SurvivalcraftBlockHazard.Lava => TeleportBlockKind.Lava,
    SurvivalcraftBlockHazard.Fire => TeleportBlockKind.Fire,
    SurvivalcraftBlockHazard.Cactus => TeleportBlockKind.Cactus,
    SurvivalcraftBlockHazard.Spikes => TeleportBlockKind.Spikes,
    _ when metadata.IsDamaging => TeleportBlockKind.Damaging,
    _ when metadata.IsAir => TeleportBlockKind.Air,
    _ when metadata.IsFluid => TeleportBlockKind.Fluid,
    _ when metadata.IsLeaves => TeleportBlockKind.Leaves,
    _ when metadata.IsFalling => TeleportBlockKind.Falling,
    _ when metadata.IsCollidable => TeleportBlockKind.SafeSolid,
    _ => TeleportBlockKind.Passable,
};
```

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: all adapter and policy tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/SurvivalcraftTravelMap/Teleport/TeleportContracts.cs src/SurvivalcraftTravelMap/Teleport/SurvivalcraftTerrainAccess.cs src/SurvivalcraftTravelMap/Teleport/TeleportBlockSafetyPolicy.cs tests/SurvivalcraftTravelMap.Tests/AdapterContractTests.cs tests/SurvivalcraftTravelMap.Tests/TeleportBlockSafetyPolicyTests.cs
git diff --cached --check
git commit -m "fix: classify teleport terrain by actual harm"
```

---

### Task 2: Resolve safe surfaces through vegetation, leaves, sand, and water

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Teleport/TeleportContracts.cs`
- Modify: `src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs`

**Interfaces:**

- Consumes: `TeleportBlockSafetyPolicy` from Task 1.
- Produces: public `TeleportCandidateRejectionReason` shared with Task 3 diagnostics.
- Produces: internal typed candidate assessment reused by surface search, waypoint search, surrounding score, and post-move validation.

- [ ] **Step 1: Replace broad rejection tests with approved behavior tests**

Add these cases:

```csharp
[Theory]
[InlineData(TeleportBlockKind.SafeSolid)]
[InlineData(TeleportBlockKind.Leaves)]
[InlineData(TeleportBlockKind.Falling)]
public async Task Ordinary_collidable_surfaces_accept_waypoint_teleport(TeleportBlockKind ground)
{
    var context = new TeleportTestContext();
    context.Terrain.SetBlock(0, 64, 0, ground);
    context.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Air);
    context.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Air);

    var result = await context.Service.TeleportToWaypointAsync(
        new Vector3(0f, 65f, 0f),
        TestContext.Current.CancellationToken);

    Assert.Equal(TeleportResult.Success, result);
    Assert.Equal(new Vector3(0.5f, 65f, 0.5f), Assert.Single(context.Mover.Movements).Position);
}

[Fact]
public async Task Surface_search_steps_through_harmless_plants_to_real_ground()
{
    var context = new TeleportTestContext();
    context.Terrain.DefaultSurfaceHeight = 66;
    context.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.SafeSolid);
    context.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Passable);
    context.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Passable);
    context.Terrain.SetBlock(0, 67, 0, TeleportBlockKind.Air);

    var result = await context.Service.TeleportToSurfaceAsync(
        0,
        0,
        TestContext.Current.CancellationToken);

    Assert.Equal(TeleportResult.Success, result);
    Assert.Equal(65f, Assert.Single(context.Mover.Movements).Position.Y);
}

[Fact]
public async Task Water_surface_is_allowed_only_with_breathable_head_space()
{
    var safe = new TeleportTestContext();
    safe.Terrain.DefaultSurfaceHeight = 64;
    safe.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Water);
    safe.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Air);
    safe.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Air);
    Assert.Equal(
        TeleportResult.Success,
        await safe.Service.TeleportToSurfaceAsync(0, 0, TestContext.Current.CancellationToken));

    var submergedHead = new TeleportTestContext();
    submergedHead.Terrain.MaxY = 66;
    submergedHead.Terrain.SetBlock(0, 64, 0, TeleportBlockKind.Water);
    submergedHead.Terrain.SetBlock(0, 65, 0, TeleportBlockKind.Water);
    submergedHead.Terrain.SetBlock(0, 66, 0, TeleportBlockKind.Water);
    Assert.Equal(
        TeleportResult.NoSafePosition,
        await submergedHead.Service.TeleportToWaypointAsync(
            new Vector3(0f, 65f, 0f),
            TestContext.Current.CancellationToken));
}
```

Keep explicit rejection coverage for lava, fire, cactus, spikes, damaging, collidable body/head cells, entity collision, and out-of-world bounds.

- [ ] **Step 2: Run safety tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~SafeTeleportServiceTests'
```

Expected: leaves/falling/water/passable cases fail under the existing exact `SafeSolid/Air/Air` rule.

- [ ] **Step 3: Add typed assessment and surface resolution**

Add this enum to `TeleportContracts.cs`:

```csharp
public enum TeleportCandidateRejectionReason
{
    OutOfWorld,
    HarmfulContent,
    NoSupport,
    BlockedBody,
    NonBreathableHead,
    EntityCollision,
}
```

Inside `SafeTeleportService`, add:

```csharp
private const int MaximumDecorationScanDepth = 8;

private readonly record struct CandidateAssessment(
    bool IsSafe,
    TeleportCandidateRejectionReason? Rejection)
{
    public static CandidateAssessment Safe { get; } = new(true, null);
    public static CandidateAssessment Reject(TeleportCandidateRejectionReason reason) =>
        new(false, reason);
}
```

Replace `IsSafe` with `AssessCandidate` using these rules:

```csharp
var groundKind = GetBlockKind(x, feetY - 1, z, cancellationToken);
var feetKind = GetBlockKind(x, feetY, z, cancellationToken);
var headKind = GetBlockKind(x, feetY + 1, z, cancellationToken);

if (TeleportBlockSafetyPolicy.IsHarmful(groundKind)
    || TeleportBlockSafetyPolicy.IsHarmful(feetKind)
    || TeleportBlockSafetyPolicy.IsHarmful(headKind))
    return CandidateAssessment.Reject(TeleportCandidateRejectionReason.HarmfulContent);
if (!TeleportBlockSafetyPolicy.IsStableSupport(groundKind)
    && !TeleportBlockSafetyPolicy.IsWater(groundKind))
    return CandidateAssessment.Reject(TeleportCandidateRejectionReason.NoSupport);
if (!TeleportBlockSafetyPolicy.IsFeetPassable(feetKind))
    return CandidateAssessment.Reject(TeleportCandidateRejectionReason.BlockedBody);
if (!TeleportBlockSafetyPolicy.IsHeadBreathable(headKind))
    return CandidateAssessment.Reject(TeleportCandidateRejectionReason.NonBreathableHead);
if (HasBlockingCollisionExcludingPlayer(GetPosition(x, feetY, z), cancellationToken))
    return CandidateAssessment.Reject(TeleportCandidateRejectionReason.EntityCollision);
return CandidateAssessment.Safe;
```

For surface search, scan from `GetSurfaceHeight` downward no more than eight cells while the block is `Air` or `Passable`. Stop on stable support, water, harmful content, out-of-world, or the depth limit. The candidate feet Y is support Y + 1. Do not scan through leaves, falling ground, solid blocks, or water.

Update surrounding-score checks and post-move validation to use `AssessCandidate(candidate).IsSafe`. Post-move rejection still returns `RolledBack` through the existing transaction.

- [ ] **Step 4: Run the full safety suite and verify GREEN**

Run the command from Step 2.

Expected: all `SafeTeleportServiceTests` pass, including existing rollback/cancellation/position-sync coverage.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/SurvivalcraftTravelMap/Teleport/TeleportContracts.cs src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs
git diff --cached --check
git commit -m "fix: accept harmless teleport surfaces safely"
```

---

### Task 3: Log aggregate reasons for expected no-safe results

**Files:**

- Modify: `src/SurvivalcraftTravelMap/Teleport/TeleportDiagnostics.cs`
- Modify: `src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs`
- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TeleportDiagnosticReporterTests.cs`

**Interfaces:**

- Consumes: public `TeleportCandidateRejectionReason` from Task 2.
- Produces: public `TeleportSearchDiagnostic`.
- Produces: `TeleportDiagnosticReporter.ReportSearch(TeleportSearchDiagnostic)` and `FormatSearch(...)`.
- Preserves: exception `TeleportFailureDiagnostic` and its number-redacted formatting.

- [ ] **Step 1: Write failing aggregate and redaction tests**

Define test expectations with no coordinate-shaped fields:

```csharp
var diagnostic = new TeleportSearchDiagnostic(new Dictionary<TeleportCandidateRejectionReason, int>
{
    [TeleportCandidateRejectionReason.HarmfulContent] = 12,
    [TeleportCandidateRejectionReason.NonBreathableHead] = 3,
});
var text = TeleportDiagnosticReporter.FormatSearch(
    new TeleportRequestDiagnosticContext("host", 78, "SurfaceRequest"),
    diagnostic);

Assert.Contains("route=host", text, StringComparison.Ordinal);
Assert.Contains("request=78", text, StringComparison.Ordinal);
Assert.Contains("HarmfulContent=<number>", text, StringComparison.Ordinal);
Assert.Contains("NonBreathableHead=<number>", text, StringComparison.Ordinal);
Assert.DoesNotContain("targetX", text, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("targetZ", text, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("candidate", text, StringComparison.OrdinalIgnoreCase);
```

In service tests, capture search diagnostics, create only harmful candidates, assert one diagnostic is reported when returning `NoSafePosition`, and assert no search diagnostic for Success, OutOfWorld, ChunkTimeout, Busy, or RolledBack.

- [ ] **Step 2: Run diagnostics tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TeleportDiagnosticReporterTests|FullyQualifiedName~SafeTeleportServiceTests'
```

Expected: compilation fails because search diagnostics do not exist.

- [ ] **Step 3: Add the diagnostic contract and reporter**

Add:

```csharp
public sealed record TeleportSearchDiagnostic(
    IReadOnlyDictionary<TeleportCandidateRejectionReason, int> RejectionCounts);
```

`FormatSearch` must write route/request/kind plus every nonzero enum count in enum order, run count values through the existing `RedactNumbers`, and never accept coordinates as parameters.

Extend the deepest `SafeTeleportService` constructor with `Action<TeleportSearchDiagnostic> reportSearch`; all convenience constructors pass a no-op. Accumulate one count for every rejected assessment and call `_reportSearch(...)` exactly once immediately before each `NoSafePosition` return.

Wire the runtime constructor in `TravelMapComponent.InitializeCoreRuntime` to `TeleportDiagnosticReporter.ReportSearch` alongside the existing exception reporter.

- [ ] **Step 4: Run diagnostics and safety tests and verify GREEN**

Run the command from Step 2.

Expected: all selected tests pass and only `NoSafePosition` produces aggregate search diagnostics.

- [ ] **Step 5: Run the complete automated gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: every test passes; build has zero warnings and zero errors.

- [ ] **Step 6: Commit Task 3**

```powershell
git add src/SurvivalcraftTravelMap/Teleport/TeleportDiagnostics.cs src/SurvivalcraftTravelMap/Teleport/SafeTeleportService.cs src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs tests/SurvivalcraftTravelMap.Tests/SafeTeleportServiceTests.cs tests/SurvivalcraftTravelMap.Tests/TeleportDiagnosticReporterTests.cs
git diff --cached --check
git commit -m "feat: diagnose rejected teleport candidates"
```
