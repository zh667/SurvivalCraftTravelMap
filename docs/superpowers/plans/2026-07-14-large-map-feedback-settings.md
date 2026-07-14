# Large-Map Feedback and Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show every map/teleport result above the open large map, remove duplicate generic failures, and let the user close settings without closing the map.

**Architecture:** Introduce a small notice value/controller for replacement and expiry, render one top-layer toast inside `TravelMapDialog`, and route component messages to it whenever that dialog is registered with `DialogsManager`. Add a `完成` callback to the settings widget and make Cancel close settings before it closes the large map.

**Tech Stack:** C#/.NET 10, Survivalcraft widget/dialog APIs, existing dispatcher and coalescing save queue, xUnit v3.

## Global Constraints

- An open large map must visibly show teleport success/failure, unavailable/busy actions, unexplored-area messages, and persistence failures.
- The toast renders above the map, context card, and settings panel.
- Async completions reach UI only through `GameUpdateDispatcher`.
- Repeated messages replace and refresh one toast; no unbounded queue.
- Toast duration is 2.5 seconds.
- Information, success, and failure use distinct existing palette colors.
- No specific teleport result may be overwritten by a second generic `旅行操作未能完成` message.
- `完成` closes settings only and requests persistence.
- Cancel/Esc closes settings first; a second Cancel/Esc closes the large map.
- Settings continue to apply live and the large map remains open after `完成`.
- Toast placement does not cover top-right controls or bottom-left coordinates.
- Work test-first and commit each task separately.

---

### Task 1: Model one replacing, expiring large-map notice

**Files:**

- Create: `src/SurvivalcraftTravelMap/UI/TravelMapNotice.cs`
- Create: `tests/SurvivalcraftTravelMap.Tests/TravelMapNoticeTests.cs`

**Interfaces:**

- Produces: `TravelMapNoticeKind`, `TravelMapNotice`, and `TravelMapNoticeController`.
- Consumed later by `TravelMapDialog` and `TravelMapComponent`.

- [ ] **Step 1: Write failing notice-controller tests**

```csharp
[Fact]
public void Show_replaces_the_current_notice_and_refreshes_expiry()
{
    var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));
    controller.Show(new TravelMapNotice("first", TravelMapNoticeKind.Information), 10d);
    controller.Show(new TravelMapNotice("second", TravelMapNoticeKind.Failure), 11d);

    Assert.Equal("second", controller.Current?.Text);
    Assert.Equal(TravelMapNoticeKind.Failure, controller.Current?.Kind);
    Assert.True(controller.Update(13.49d));
    Assert.False(controller.Update(13.5d));
    Assert.Null(controller.Current);
}

[Theory]
[InlineData("")]
[InlineData("   ")]
public void Empty_notices_are_rejected(string text)
{
    var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));
    Assert.Throws<ArgumentException>(() =>
        controller.Show(new TravelMapNotice(text, TravelMapNoticeKind.Information), 0d));
}
```

Also assert non-finite timestamps and non-positive durations throw, and `Clear()` immediately removes the notice.

- [ ] **Step 2: Run the notice tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~TravelMapNoticeTests'
```

Expected: compilation fails because the notice types do not exist.

- [ ] **Step 3: Implement the pure notice types**

```csharp
namespace SurvivalcraftTravelMap.UI;

public enum TravelMapNoticeKind
{
    Information,
    Success,
    Failure,
}

public readonly record struct TravelMapNotice(string Text, TravelMapNoticeKind Kind);

public sealed class TravelMapNoticeController
{
    private readonly double _durationSeconds;
    private double _expiresAt;

    public TravelMapNoticeController(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        _durationSeconds = duration.TotalSeconds;
    }

    public TravelMapNotice? Current { get; private set; }

    public void Show(TravelMapNotice notice, double now)
    {
        if (string.IsNullOrWhiteSpace(notice.Text))
            throw new ArgumentException("Notice text is required.", nameof(notice));
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        Current = notice;
        _expiresAt = now + _durationSeconds;
    }

    public bool Update(double now)
    {
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (Current.HasValue && now >= _expiresAt)
            Current = null;
        return Current.HasValue;
    }

    public void Clear() => Current = null;
}
```

- [ ] **Step 4: Run tests and verify GREEN**

Run the command from Step 2.

Expected: all notice tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/SurvivalcraftTravelMap/UI/TravelMapNotice.cs tests/SurvivalcraftTravelMap.Tests/TravelMapNoticeTests.cs
git diff --cached --check
git commit -m "feat: model large-map notices"
```

---

### Task 2: Render and route feedback inside the modal map

**Files:**

- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs`
- Modify: `src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`

**Interfaces:**

- Consumes: `TravelMapNoticeController` from Task 1.
- Produces: `TravelMapDialog.ShowNotice(TravelMapNotice notice)`.
- Produces: `TravelMapActionStatus.FailedWithFeedback`.
- Preserves: global `Gui.DisplaySmallMessage` when the large map is not open.

- [ ] **Step 1: Write failing routing and action-status tests**

Add pure mapping coverage:

```csharp
[Theory]
[InlineData(CoordinateTeleportResultCode.Success, TravelMapNoticeKind.Success)]
[InlineData(CoordinateTeleportResultCode.NoSafePosition, TravelMapNoticeKind.Failure)]
[InlineData(CoordinateTeleportResultCode.OutOfWorld, TravelMapNoticeKind.Failure)]
[InlineData(CoordinateTeleportResultCode.Rejected, TravelMapNoticeKind.Failure)]
public void Coordinate_results_map_to_visible_notice_kinds(
    CoordinateTeleportResultCode result,
    TravelMapNoticeKind expected)
{
    Assert.Equal(expected, TravelMapNoticeFactory.For(result).Kind);
}
```

Add package/source assertions that `ShowMessage` checks `DialogsManager.Dialogs.Contains(_largeMapDialog)`, calls `_largeMapDialog.ShowNotice(...)`, and uses `Gui.DisplaySmallMessage` only in the fallback branch. Assert the notice widgets are added after `_settingsWidget` and `_contextCard`.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~TravelMapUiStateTests'
```

Expected: FAIL because there is no dialog notice layer or typed teleport notice mapping.

- [ ] **Step 3: Add notice mapping and top-layer widgets**

Add `TravelMapNoticeFactory` to `TravelMapNotice.cs`:

```csharp
using SurvivalcraftTravelMap.Network;
using SurvivalcraftTravelMap.Teleport;

public static class TravelMapNoticeFactory
{
    public static TravelMapNotice For(CoordinateTeleportResultCode result) => new(
        CoordinateTeleportResultText.For(result),
        result == CoordinateTeleportResultCode.Success
            ? TravelMapNoticeKind.Success
            : TravelMapNoticeKind.Failure);

    public static TravelMapNotice For(TeleportResult result) => For(result switch
    {
        TeleportResult.Success => CoordinateTeleportResultCode.Success,
        TeleportResult.ChunkTimeout => CoordinateTeleportResultCode.TimedOut,
        TeleportResult.NoSafePosition => CoordinateTeleportResultCode.NoSafePosition,
        TeleportResult.OutOfWorld => CoordinateTeleportResultCode.OutOfWorld,
        TeleportResult.RolledBack => CoordinateTeleportResultCode.RolledBack,
        TeleportResult.Busy => CoordinateTeleportResultCode.Rejected,
        _ => CoordinateTeleportResultCode.InternalError,
    });
}
```

In `TravelMapDialog`, add a 560×48 maximum toast canvas, basalt translucent background, 2 px outline, and centered label after both settings and context widgets. Add:

```csharp
public void ShowNotice(TravelMapNotice notice)
{
    _noticeController.Show(notice, Time.FrameStartTime);
    _noticeLabel.Text = notice.Text;
    var color = notice.Kind switch
    {
        TravelMapNoticeKind.Success => SurveyCyan,
        TravelMapNoticeKind.Failure => HazardAmber,
        _ => SnowText,
    };
    _noticeLabel.Color = color;
    _noticeBackground.OutlineColor = color;
    _noticeHost.IsVisible = true;
}
```

In `ArrangeOverride`, center the toast horizontally at Y=58 and clamp its width to `MathF.Min(560f, ActualSize.X - 32f)`. In `Update`, hide it when `_noticeController.Update(Time.FrameStartTime)` returns false. `ResetToPlayer` clears stale notices.

- [ ] **Step 4: Route component messages on the update thread**

Change the component helper to accept a kind:

```csharp
private void ShowMessage(TravelMapNotice notice) =>
    ShowMessage(notice.Text, notice.Kind);

private void ShowMessage(
    string message,
    TravelMapNoticeKind kind = TravelMapNoticeKind.Information)
{
    try
    {
        _dispatcher?.Invoke(() =>
        {
            if (_largeMapDialog is not null
                && DialogsManager.Dialogs.Contains(_largeMapDialog))
            {
                _largeMapDialog.ShowNotice(new TravelMapNotice(message, kind));
                return;
            }

            Gui?.DisplaySmallMessage(
                message,
                Engine.Color.White,
                blinking: false,
                playNotificationSound: false);
        });
    }
    catch (ObjectDisposedException)
    {
    }
}
```

Every local/host/remote coordinate result calls `ShowMessage(TravelMapNoticeFactory.For(result))` before returning its dispatch status. Add `FailedWithFeedback` to `TravelMapActionStatus`; map `LocalFailed` to it. `TravelMapDialog.ExecuteActionAsync` shows a generic failure only for plain `Failed`, and does nothing for `FailedWithFeedback`.

Use `Success` for successful teleport, waypoint save, rename, and delete confirmations; use `Failure` for expected teleport refusals and persistence errors; use `Information` for unexplored and unavailable/busy messages.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: all selected tests pass; source contracts prove modal routing and no duplicate generic result.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src/SurvivalcraftTravelMap/UI/TravelMapNotice.cs src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs src/SurvivalcraftTravelMap/Mod/TravelMapComponent.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs
git diff --cached --check
git commit -m "fix: show travel feedback above the large map"
```

---

### Task 3: Close settings without closing the large map

**Files:**

- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapSettingsWidget.cs`
- Modify: `src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs`
- Modify: `tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs`

**Interfaces:**

- Produces: `TravelMapSettingsWidget(..., Action requestClose)` constructor dependency.
- Produces: `TravelMapSettingsWidget.RequestPersist()` for settings-only close.
- Produces: internal `TravelMapDialogCancelPolicy.Resolve(bool settingsVisible)` pure decision.

- [ ] **Step 1: Write failing cancel-policy and structure tests**

Add:

```csharp
public enum TravelMapDialogCancelAction
{
    CloseSettings,
    CloseDialog,
}

[Theory]
[InlineData(true, TravelMapDialogCancelAction.CloseSettings)]
[InlineData(false, TravelMapDialogCancelAction.CloseDialog)]
public void Cancel_closes_the_innermost_surface_first(
    bool settingsVisible,
    TravelMapDialogCancelAction expected) =>
    Assert.Equal(expected, TravelMapDialogCancelPolicy.Resolve(settingsVisible));
```

Assert `TravelMapSettingsWidget.cs` contains a `BevelledButtonWidget` with text `完成`, invokes `_requestClose()`, and requests save first. Assert `TravelMapDialog.Update` handles settings-visible Cancel before `DialogsManager.HideDialog(this)`.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --filter 'FullyQualifiedName~PackageStructureTests|FullyQualifiedName~TravelMapUiStateTests'
```

Expected: FAIL because settings has no completion button or nested Cancel policy.

- [ ] **Step 3: Implement the pure cancel policy**

Add to `TravelMapDialog.cs` outside the widget class:

```csharp
public enum TravelMapDialogCancelAction
{
    CloseSettings,
    CloseDialog,
}

public static class TravelMapDialogCancelPolicy
{
    public static TravelMapDialogCancelAction Resolve(bool settingsVisible) =>
        settingsVisible
            ? TravelMapDialogCancelAction.CloseSettings
            : TravelMapDialogCancelAction.CloseDialog;
}
```

- [ ] **Step 4: Add the `完成` button and persistence request**

Extend the settings constructor with `Action requestClose`, store it after a null check, set `Size = new Vector2(420f, 470f)`, and add one 120×40 bottom-centered button:

```csharp
private readonly Action _requestClose;
private readonly BevelledButtonWidget _doneButton;

public TravelMapSettingsWidget(
    TravelMapSettings settings,
    TravelMapSettingsStore store,
    Action<string> notify,
    Action requestClose)
```

Immediately after the current `_notify` constructor assignment, add:

```csharp
_requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));
Size = new Vector2(420f, 470f);
```

Replace the old `Size = new Vector2(420f, 430f);` assignment rather than retaining both. After the existing minimap-size button loop, add:

```csharp
_doneButton = new BevelledButtonWidget
{
    Text = "完成",
    Size = new Vector2(120f, 40f),
    Color = SnowText,
    CenterColor = Moss,
};
Children.Add(_doneButton);
SetWidgetPosition(_doneButton, new Vector2(150f, 418f));
```

At the end of `Update`:

```csharp
if (_doneButton.IsClicked)
{
    RequestPersist();
    _requestClose();
}
```

Expose:

```csharp
public void RequestPersist() => _saveQueue.RequestSave();
```

Keep `Persist()` as a private forwarding method if existing call sites remain.

- [ ] **Step 5: Make Cancel close settings first**

Construct the widget with `CloseSettings`. Add:

```csharp
private void CloseSettings()
{
    _settingsWidget.RequestPersist();
    _settingsWidget.IsVisible = false;
    _lastDragPosition = null;
}
```

At the beginning of dialog `Update`, after due scale persistence:

```csharp
if (Input.Cancel)
{
    if (TravelMapDialogCancelPolicy.Resolve(_settingsWidget.IsVisible)
        == TravelMapDialogCancelAction.CloseSettings)
    {
        CloseSettings();
        return;
    }

    PersistScale();
    DialogsManager.HideDialog(this);
    return;
}

if (_closeButton.IsClicked)
{
    PersistScale();
    DialogsManager.HideDialog(this);
    return;
}
```

The top-right `关闭` button continues to close the entire map even when settings are visible; `完成` and Cancel close settings only.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: all selected tests pass.

- [ ] **Step 7: Run full tests and warning-as-error build**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
```

Expected: every test passes and build reports zero warnings/errors.

- [ ] **Step 8: Commit Task 3**

```powershell
git add src/SurvivalcraftTravelMap/UI/TravelMapSettingsWidget.cs src/SurvivalcraftTravelMap/UI/TravelMapDialog.cs tests/SurvivalcraftTravelMap.Tests/PackageStructureTests.cs tests/SurvivalcraftTravelMap.Tests/TravelMapUiStateTests.cs
git diff --cached --check
git commit -m "fix: close map settings independently"
```

---

### Task 4: Build, package, install, and collect final in-world evidence

**Files:**

- Modify: `.superpowers/sdd/progress.md`
- Modify: `docs/smoke-test-2026-07-13.md`
- Modify: `README.md`

**Interfaces:**

- Consumes: all commits from the exploration, teleport, and UI plans.
- Produces: one deterministic `.netmod` installed into the isolated smoke-game copy plus recorded PASS/FAIL evidence.

- [ ] **Step 1: Run repository integrity checks**

```powershell
git status --short
git diff --check
git log --oneline --decorate -12
Get-FileHash 'E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod' -Algorithm SHA256
```

Expected: tracked worktree is clean; original package hash remains `00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B`.

- [ ] **Step 2: Run the full automated release gate**

```powershell
dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
dotnet build SurvivalCraftTravelMap.sln -c Release -warnaserror -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -Configuration Release -SurvivalcraftDir 'E:\game\SurvivalcraftNet2.4\'
powershell -ExecutionPolicy Bypass -File scripts/verify-package.ps1 -PackagePath artifacts/SurvivalcraftTravelMap.netmod
```

Expected: tests all pass; build has zero warnings/errors; verifier prints `PACKAGE_OK`.

- [ ] **Step 3: Prove deterministic packaging**

Hash the package, rebuild it, verify it again, and compare hashes:

```powershell
$first = (Get-FileHash artifacts/SurvivalcraftTravelMap.netmod -Algorithm SHA256).Hash
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -Configuration Release -SurvivalcraftDir 'E:\game\SurvivalcraftNet2.4\'
powershell -ExecutionPolicy Bypass -File scripts/verify-package.ps1 -PackagePath artifacts/SurvivalcraftTravelMap.netmod
$second = (Get-FileHash artifacts/SurvivalcraftTravelMap.netmod -Algorithm SHA256).Hash
if ($first -ne $second) { throw "Package hashes differ: $first vs $second" }
```

Expected: identical hashes and `PACKAGE_OK` both times.

- [ ] **Step 4: Install the exact artifact into the isolated copy**

Copy only `artifacts/SurvivalcraftTravelMap.netmod` to `.superpowers/smoke-game/NetMods/SurvivalcraftTravelMap.netmod`, hash source and destination, and assert equality:

```powershell
$source = (Resolve-Path 'artifacts/SurvivalcraftTravelMap.netmod').Path
$destination = (Resolve-Path '.superpowers/smoke-game/NetMods').Path + '\SurvivalcraftTravelMap.netmod'
Copy-Item -LiteralPath $source -Destination $destination -Force
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) { throw "Installed package hash mismatch" }
```

Do not alter the user's main `NetMods` package during this test.

- [ ] **Step 5: Execute the approved in-world acceptance matrix**

Record PASS/FAIL for all ten rows in the approved design document:

- stationary load records the full minimap footprint;
- crossing a chunk boundary updates current and nearby loaded chunks;
- hidden minimap still records;
- restart preserves the footprint;
- drag/zoom retains known terrain;
- unexplored message appears above the open map;
- vegetation/leaves/sand/gravel/water teleports are safe;
- damaging/blocked/non-breathable destinations refuse visibly;
- `完成` applies settings and leaves the map open;
- first Esc closes settings and second Esc closes the map.

For any failure, capture the last 200 relevant `[TravelMap]` log lines and return to the owning implementation task. Do not mark the release complete with a failed or untested row.

- [ ] **Step 6: Update evidence documents and commit**

Write actual test totals, build output, package/DLL hashes and sizes, isolated destination hash, log timestamps, world name, and the ten acceptance rows. Then:

```powershell
git add .superpowers/sdd/progress.md docs/smoke-test-2026-07-13.md README.md
git diff --cached --check
git commit -m "docs: record final travel map acceptance evidence"
```

Do not push, open a pull request, tag, or merge until the user reviews the final evidence.
