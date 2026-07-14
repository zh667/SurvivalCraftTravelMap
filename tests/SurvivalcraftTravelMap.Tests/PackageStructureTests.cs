using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Game.NetWork;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Network;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class PackageStructureTests
{
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

    [Fact]
    public void Shared_build_configuration_targets_net10_and_detects_game_directories()
    {
        var document = XDocument.Load(TestPaths.BuildProps);
        var properties = document.Descendants("PropertyGroup").Elements().ToList();

        Assert.Equal("net10.0", properties.Single(e => e.Name == "TargetFramework").Value);
        Assert.Equal("latest", properties.Single(e => e.Name == "LangVersion").Value);
        Assert.Equal("enable", properties.Single(e => e.Name == "Nullable").Value);
        var gameDirectories = properties.Where(e => e.Name == "SurvivalcraftDir").ToList();
        Assert.Equal(2, gameDirectories.Count);
        Assert.All(
            gameDirectories,
            element => Assert.StartsWith(
                "'$(SurvivalcraftDir)' == '' and Exists(",
                element.Attribute("Condition")?.Value,
                StringComparison.Ordinal));
        Assert.Contains(
            gameDirectories,
            element => element.Value == "$(MSBuildThisFileDirectory)..\\");
        Assert.Contains(
            gameDirectories,
            element => element.Value == "$(MSBuildThisFileDirectory)..\\..\\..\\");
    }

    [Fact]
    public void Shared_build_configuration_resolves_an_existing_game_directory()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = TestPaths.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(TestPaths.ModProject);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-getProperty:SurvivalcraftDir");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        var gameDirectory = standardOutput.Trim();
        Assert.True(
            File.Exists(Path.Combine(gameDirectory, "Survivalcraft.dll")),
            $"Resolved game directory '{gameDirectory}' does not contain Survivalcraft.dll. {standardError}");
    }

    [Fact]
    public void Mod_project_references_game_assemblies_without_copying_them()
    {
        var document = XDocument.Load(TestPaths.ModProject);
        var properties = document.Descendants("PropertyGroup").Elements().ToList();
        var references = document.Descendants("Reference").ToDictionary(
            element => element.Attribute("Include")?.Value ?? string.Empty);

        Assert.Equal("1.0.0", properties.Single(e => e.Name == "Version").Value);
        Assert.Equal("1.0.0.0", properties.Single(e => e.Name == "AssemblyVersion").Value);
        Assert.Equal("1.0.0.0", properties.Single(e => e.Name == "FileVersion").Value);
        Assert.Equal("1.0.0", properties.Single(e => e.Name == "InformationalVersion").Value);
        Assert.Equal(
            "false",
            properties.Single(e => e.Name == "IncludeSourceRevisionInInformationalVersion").Value);
        Assert.Equal("false", properties.Single(e => e.Name == "EnableSourceLink").Value);

        foreach (var assembly in new[]
                 {
                     "Survivalcraft",
                     "Engine",
                     "EntitySystem",
                     "Newtonsoft.Json",
                     "LiteNetLib",
                 })
        {
            Assert.True(references.TryGetValue(assembly, out var reference));
            Assert.Equal("false", reference.Element("Private")?.Value);
        }
    }

    [Fact]
    public void Mod_assembly_has_stable_informational_version_without_a_source_revision()
    {
        var assembly = typeof(TravelMapModLoader).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal("1.0.0", informationalVersion);
        Assert.DoesNotMatch("(?i)[0-9a-f]{40}", informationalVersion ?? string.Empty);
    }

    [Fact]
    public void Loader_checks_conflict_and_registers_only_travel_map_protocols()
    {
        var source = File.ReadAllText(TestPaths.Loader);

        Assert.Contains("ModsManager.GetModEntity", source, StringComparison.Ordinal);
        Assert.Contains("LegacyPackageName = \"34GPSFix\"", source, StringComparison.Ordinal);
        Assert.Contains("DialogsManager.Alert", source, StringComparison.Ordinal);
        Assert.Contains("TravelMapPackageRegistration.TryRegister", source, StringComparison.Ordinal);
        Assert.Contains("PackageManager.RegisterPackage", source, StringComparison.Ordinal);
        Assert.Contains("PackageManager.UnRegisterPackage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageId = 60", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AntiCheat", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Component_stops_before_player_runtime_initialization_when_legacy_mod_is_present()
    {
        var source = File.ReadAllText(TestPaths.Component);
        var conflictGate = source.IndexOf("TravelMapStartup.EnsureInitialized", StringComparison.Ordinal);
        var playerInitialization = source.IndexOf("Entity.FindComponent<ComponentPlayer>", StringComparison.Ordinal);

        Assert.True(conflictGate >= 0);
        Assert.True(playerInitialization > conflictGate);
    }

    [Fact]
    public void Teleport_runtime_sources_scope_routes_report_fallbacks_and_never_log_target_coordinates()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var runtime = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "Network",
            "TravelMapNetworkRuntime.cs"));
        var package = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "Network",
            "CoordinateTeleportPackage.cs"));
        var reportStart = component.IndexOf("private static void ReportCoordinateTeleportResult", StringComparison.Ordinal);
        var reportEnd = component.IndexOf("private void InitializeUiSettings", reportStart, StringComparison.Ordinal);
        var reportMethod = component[reportStart..reportEnd];

        Assert.Contains("TeleportDiagnosticContext.Ensure", runtime, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", runtime, StringComparison.Ordinal);
        Assert.Contains("TeleportExecutionStage.ProtocolDispatch", runtime, StringComparison.Ordinal);
        Assert.Contains("!TeleportDiagnosticContext.HasReportedFailure", runtime, StringComparison.Ordinal);
        Assert.Contains("CoordinateTeleportResponseBoundary.ExecuteAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("TeleportDiagnosticContext.Ensure", package, StringComparison.Ordinal);
        Assert.Contains("TeleportDiagnosticReporter.Report", component, StringComparison.Ordinal);
        Assert.Contains("route={route}, request={request.RequestId}, kind={request.Kind}, result=", reportMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("request.X", reportMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("request.Z", reportMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("request.Target", reportMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("target=(", reportMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Internal_error_user_message_is_the_safe_diagnostic_notice()
    {
        Assert.Equal(
            "传送失败，详细原因已写入日志",
            CoordinateTeleportResultText.For(CoordinateTeleportResultCode.InternalError));
    }

    [Fact]
    public void Production_component_wires_transactional_activation_current_position_waypoints_and_minimap_wheel()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var miniMap = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "UI",
            "MiniMapRenderer.cs"));

        Assert.Contains("TravelMapLoadTransaction.TryRun", component, StringComparison.Ordinal);
        Assert.Contains("_runtimeCleanup.Run", component, StringComparison.Ordinal);
        Assert.Contains("CurrentPositionWaypointHandler", component, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTopHeight", component, StringComparison.Ordinal);
        Assert.Contains("IsMapInputBlocked", component, StringComparison.Ordinal);
        Assert.Contains("public override void Update()", miniMap, StringComparison.Ordinal);
        Assert.Contains("Input.MouseWheelMovement", miniMap, StringComparison.Ordinal);
        Assert.Contains("_wheelInteraction.HandleWheel", miniMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Component_drives_minimap_footprint_exploration_after_terrain_views_every_update()
    {
        var source = File.ReadAllText(TestPaths.Component);
        var update = ExtractBraceBlock(source, "public void Update(float dt)");
        var exploration = ExtractBraceBlock(source, "private void UpdateExploration()");
        var cleanup = ExtractBraceBlock(source, "private void CleanupRuntimeResources()");

        AssertCodeContains(source, "public UpdateOrder UpdateOrder => UpdateOrder.Views;");
        AssertCodeContains(source, "private const int MaximumChunkAttemptsPerFrame = 4;");
        AssertCodeContains(source, "private const int MaximumCoverageChecksPerFrame = 4;");
        AssertCodeContains(
            source,
            "private readonly TerrainChunkExplorationScheduler _explorationScheduler = new();",
            "scheduler field");
        AssertCodeContains(
            source,
            "private MinimapExplorationFootprintIdentity? _explorationFootprintIdentity;",
            "footprint identity cache");
        AssertCodeContains(update, "UpdateExploration();");
        AssertCodeDoesNotContain(update, "UpdateExploration(dt)");
        Assert.True(
            IndexOfCode(update, "UpdateExploration();")
            < IndexOfCode(update, "if (_miniMap is null || _settings is null)"));

        AssertCodeContains(exploration, "MinimapExplorationFootprintIdentity.Create(");
        AssertCodeContains(exploration, "_settings.MiniMapSize");
        AssertCodeContains(exploration, "_settings.MiniMapBlocksPerPixel");
        var identityChange = ExtractBraceBlock(
            exploration,
            "if (_explorationFootprintIdentity != footprintIdentity)");
        AssertCodeContains(identityChange, "_explorationFootprintIdentity = footprintIdentity;");
        AssertCodeContains(identityChange, "MinimapExplorationFootprint.Create(footprintIdentity);");
        AssertCodeContains(identityChange, "_explorationScheduler.ObserveFootprint(footprint);");
        Assert.Equal(1, CountOccurrences(exploration, "MinimapExplorationFootprint.Create("));
        Assert.Equal(1, CountOccurrences(exploration, "ObserveFootprint("));
        AssertCodeDoesNotContain(exploration, "ObservePlayerPosition");
        AssertCodeDoesNotContain(exploration, "_settings.IsMiniMapVisible");
        AssertCodeContains(exploration, "_explorationScheduler.ReconcileCoverage(");
        AssertCodeContains(exploration, "_explorationRecorder.IsChunkFullyExplored");
        AssertCodeContains(exploration, "MaximumCoverageChecksPerFrame");
        Assert.True(
            exploration.IndexOf("ReconcileCoverage", StringComparison.Ordinal)
            < exploration.IndexOf("GetPendingAttempts", StringComparison.Ordinal));
        AssertCodeContains(
            exploration,
            "_explorationScheduler.GetPendingAttempts(MaximumChunkAttemptsPerFrame)",
            "bounded pending snapshot");
        AssertCodeDoesNotContain(identityChange, "GetPendingAttempts");
        Assert.True(
            IndexOfCode(exploration, "if (_explorationFootprintIdentity != footprintIdentity)")
            < IndexOfCode(
                exploration,
                "foreach (var chunk in _explorationScheduler.GetPendingAttempts(MaximumChunkAttemptsPerFrame))"));
        AssertCodeContains(cleanup, "_explorationScheduler.Clear();");
        AssertCodeContains(cleanup, "_explorationFootprintIdentity = null;");
        AssertCodeContains(cleanup, "_explorationFailureWarnings.Clear();");

        foreach (var legacyReference in new[]
                 {
                     "_lastRecordedX",
                     "_lastRecordedZ",
                     "_stationaryRecordElapsed",
                     "SettingsManager.VisibilityRange",
                     "RecordVisibleArea",
                 })
        {
            AssertCodeDoesNotContain(source, legacyReference);
        }
    }

    [Fact]
    public void Component_attempts_each_pending_chunk_independently_and_completes_only_recorded_chunks()
    {
        var source = File.ReadAllText(TestPaths.Component);
        var exploration = ExtractBraceBlock(source, "private void UpdateExploration()");
        var attemptLoop = ExtractBraceBlock(
            exploration,
            "foreach (var chunk in _explorationScheduler.GetPendingAttempts(MaximumChunkAttemptsPerFrame))");
        var recordedBranch = ExtractBraceBlock(
            attemptLoop,
            "if (result == ExplorationRecordResult.Recorded)");
        var exceptionHandler = ExtractBraceBlock(attemptLoop, "catch (Exception exception)");

        AssertCodeContains(attemptLoop, "try");
        AssertCodeContains(attemptLoop, "_explorationRecorder.RecordChunk(chunk)");
        AssertCodeContains(recordedBranch, "_explorationScheduler.MarkCompleted(chunk);");
        Assert.Equal(1, CountOccurrences(exploration, "MarkCompleted("));
        AssertCodeContains(attemptLoop, "result == ExplorationRecordResult.Pressure");
        AssertCodeContains(attemptLoop, "!_explorationPressureWarningShown");

        AssertCodeContains(exceptionHandler, "exception.GetType().FullName");
        AssertCodeContains(exceptionHandler, "exception.Message");
        AssertCodeContains(exceptionHandler, "_explorationFailureWarnings.Add((chunk, errorSignature))");
        AssertCodeContains(exceptionHandler, "Engine.Log.Warning");
        AssertCodeDoesNotContain(exceptionHandler, "MarkCompleted");
        foreach (var forbiddenExit in new[] { "return", "break", "continue", "goto", "throw" })
        {
            Assert.Equal(0, CountOccurrences(exceptionHandler, forbiddenExit));
        }
    }

    [Fact]
    public void Hud_overlays_are_positioned_in_gui_logical_coordinates()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var positioning = ExtractBraceBlock(component, "private void UpdateHudPositions()");

        AssertCodeContains(positioning, "var guiSize = Player.GuiWidget.ActualSize;");
        AssertCodeContains(
            positioning,
            "var positions = TravelMapOverlayLayout.PlaceHud(new Vector2(guiSize.X, guiSize.Y), _settings.MiniMapSize);");
        Assert.Equal(1, CountOccurrences(positioning, "TravelMapOverlayLayout.PlaceHud("));
        AssertCodeContains(positioning, "positions.MiniMap");
        AssertCodeContains(positioning, "positions.TeleportButton");
        AssertCodeDoesNotContain(positioning, "ActiveCamera.ViewportSize");
        AssertCodeDoesNotContain(component, "TravelMapOverlayLayout.PlaceTopRight(");
    }

    [Fact]
    public void Component_evaluates_and_applies_real_hud_signals_before_clicks_and_early_returns()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var update = ExtractBraceBlock(component, "public void Update(float dt)");
        var signals = ExtractBraceBlock(component, "private TravelMapHudSignals GetHudSignals()");
        var apply = ExtractBraceBlock(component, "private void ApplyHudState(TravelMapHudState state)");

        AssertCodeContains(update, "var hudState = TravelMapHudPolicy.Evaluate(GetHudSignals());");
        AssertCodeContains(update, "ApplyHudState(hudState);");
        AssertCodeContains(update, "UpdateInvitationUi(hudState);");
        Assert.True(IndexOfCode(update, "TravelMapHudPolicy.Evaluate(GetHudSignals())")
            < IndexOfCode(update, "ApplyHudState(hudState)"));
        Assert.True(IndexOfCode(update, "ApplyHudState(hudState)")
            < IndexOfCode(update, "UpdateInvitationUi(hudState)"));
        Assert.True(IndexOfCode(update, "ApplyHudState(hudState)")
            < IndexOfCode(update, "if (_miniMap is null || _settings is null)"));
        Assert.True(IndexOfCode(update, "HandleLargeMapHotkey()")
            < IndexOfCode(update, "if (_miniMap is null || _settings is null)"));

        AssertCodeContains(
            signals,
            "var isLargeMapOpen = _largeMapDialog is not null && DialogsManager.Dialogs.Contains(_largeMapDialog);");
        AssertCodeContains(
            signals,
            "var hasModalSurface = Gui?.ModalPanelWidget is not null || DialogsManager.Dialogs.Any(dialog => !ReferenceEquals(dialog, _largeMapDialog));");
        AssertCodeContains(signals, "HasModalSurface: hasModalSurface");
        AssertCodeContains(signals, "IsLargeMapOpen: isLargeMapOpen");
        AssertCodeContains(signals, "HasOtherPlayers: CountOtherPlayers() > 0");
        AssertCodeContains(
            signals,
            "InvitationFeatureAvailable: TravelMapRuntimePolicy.CreatesInvitationUi(RuntimeContext)");
        AssertCodeContains(signals, "HasTextEntryFocus: !GetMapInputFocus().AllowsMapHotkey");

        AssertCodeContains(apply, "_miniMap.IsVisible = state.ShowMiniMap;");
        AssertCodeContains(apply, "_miniMap.IsEnabled = state.AllowMiniMapInput;");
        AssertCodeContains(apply, "_teleportPanelButton.IsVisible = state.ShowTeleportButton;");
        AssertCodeContains(
            apply,
            "_teleportPanelButton.IsEnabled = state.ShowTeleportButton && state.AllowMiniMapInput;");
        AssertCodeDoesNotContain(apply, "_settings.");
    }

    [Fact]
    public void Component_wires_clickable_minimap_and_contextual_bitmap_invitation_icon()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var miniMap = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "UI",
            "MiniMapRenderer.cs"));
        var attach = ExtractBraceBlock(component, "private void AttachUiWidgets(UiInitializationState state)");
        var invitation = ExtractBraceBlock(component, "private void InitializeInvitationUi()");
        var updateInvitation = ExtractBraceBlock(
            component,
            "private void UpdateInvitationUi(TravelMapHudState hudState)");
        var openLargeMap = ExtractBraceBlock(component, "private void OpenLargeMap()");
        var cleanup = ExtractBraceBlock(component, "private void CleanupUi()");
        var measure = ExtractBraceBlock(miniMap, "public override void MeasureOverride(Engine.Vector2 parentAvailableSize)", startOccurrence: 2);

        AssertCodeContains(component, "private BitmapButtonWidget? _teleportPanelButton;");
        AssertCodeContains(component, "private Texture2D? _teleportButtonTexture;");
        AssertCodeContains(component, "private Texture2D? _teleportButtonPressedTexture;");
        AssertCodeContains(attach, "OpenLargeMap");
        AssertCodeContains(invitation, "new BitmapButtonWidget");
        AssertCodeContains(invitation, "Size = new Engine.Vector2(48f, 46f)");
        AssertCodeContains(invitation, "NormalSubtexture = new Subtexture(_teleportButtonTexture");
        AssertCodeContains(invitation, "ClickedSubtexture = new Subtexture(_teleportButtonPressedTexture");
        Assert.Contains("TeleportButton.png", invitation, StringComparison.Ordinal);
        Assert.Contains("TeleportButton_Pressed.png", invitation, StringComparison.Ordinal);
        Assert.DoesNotContain("Text = \"玩家传送\"", component, StringComparison.Ordinal);
        AssertCodeDoesNotContain(component, "BevelledButtonWidget");
        AssertCodeDoesNotContain(measure, "IsVisible");

        AssertCodeContains(updateInvitation, "if (!hudState.ShowTeleportButton");
        AssertCodeContains(updateInvitation, "|| !_teleportPanelButton.IsEnabled");
        AssertCodeContains(updateInvitation, "|| !GetMapInputFocus().AllowsMapHotkey");
        AssertCodeContains(openLargeMap, "if (_largeMapDialog is null || !GetMapInputFocus().AllowsMapHotkey)");
        AssertCodeContains(openLargeMap, "_largeMapDialog.ResetToPlayer();");
        AssertCodeContains(openLargeMap, "DialogsManager.ShowDialog(Player.GuiWidget, _largeMapDialog);");
        AssertCodeContains(openLargeMap, "ApplyHudState(TravelMapHudPolicy.Evaluate(GetHudSignals()));");
        Assert.Equal(1, CountOccurrences(component, "_largeMapDialog.ResetToPlayer()"));

        AssertCodeContains(cleanup, "teleportPanelButton.Dispose");
        AssertCodeContains(cleanup, "teleportButtonTexture.Dispose");
        AssertCodeContains(cleanup, "teleportButtonPressedTexture.Dispose");
    }

    [Fact]
    public void Large_map_notice_layer_is_above_settings_and_context_and_uses_one_timed_adaptive_toast()
    {
        var dialog = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "UI",
            "TravelMapDialog.cs"));
        var constructor = ExtractBraceBlock(dialog, "public TravelMapDialog(");
        var arrange = ExtractBraceBlock(dialog, "public override void ArrangeOverride()");
        var update = ExtractBraceBlock(dialog, "public override void Update()");
        var reset = ExtractBraceBlock(dialog, "public void ResetToPlayer()");
        var showNotice = ExtractBraceBlock(dialog, "public void ShowNotice(TravelMapNotice notice)");

        var settingsIndex = constructor.IndexOf("Children.Add(_settingsWidget);", StringComparison.Ordinal);
        var contextIndex = constructor.IndexOf("Children.Add(_contextCard);", StringComparison.Ordinal);
        var noticeIndex = constructor.IndexOf("Children.Add(_noticeHost);", StringComparison.Ordinal);
        Assert.True(settingsIndex >= 0);
        Assert.True(contextIndex > settingsIndex);
        Assert.True(noticeIndex > contextIndex);

        AssertCodeContains(dialog, "new TravelMapNoticeController(TimeSpan.FromSeconds(2.5))");
        AssertCodeContains(showNotice, "_noticeController.Show(notice, Time.FrameStartTime);");
        AssertCodeContains(showNotice, "TravelMapNoticeKind.Success => SurveyCyan");
        AssertCodeContains(showNotice, "TravelMapNoticeKind.Failure => HazardAmber");
        AssertCodeContains(showNotice, "_noticeHost.IsVisible = true;");
        AssertCodeContains(arrange, "MathF.Min(560f, ActualSize.X - 32f)");
        AssertCodeContains(arrange, "new Vector2((ActualSize.X - noticeWidth) / 2f, 58f)");
        AssertCodeContains(update, "_noticeController.Update(Time.FrameStartTime)");
        AssertCodeContains(reset, "_noticeController.Clear();");
    }

    [Fact]
    public void Settings_close_independently_after_requesting_persistence()
    {
        var settings = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "UI",
            "TravelMapSettingsWidget.cs"));
        var dialog = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "UI",
            "TravelMapDialog.cs"));
        var settingsConstructor = ExtractBraceBlock(settings, "public TravelMapSettingsWidget(");
        var settingsUpdate = ExtractBraceBlock(settings, "public override void Update()");
        var dialogUpdate = ExtractBraceBlock(dialog, "public override void Update()");
        var closeSettings = ExtractBraceBlock(dialog, "private void CloseSettings()");

        AssertCodeContains(settings, "private readonly Action _requestClose;");
        AssertCodeContains(settingsConstructor, "Size = new Vector2(420f, 470f);");
        AssertCodeContains(settingsConstructor, "Text = \"完成\"");
        AssertCodeContains(settingsConstructor, "Size = new Vector2(120f, 40f)");
        AssertCodeContains(settingsConstructor, "SetWidgetPosition(_doneButton, new Vector2(150f, 418f));");
        AssertCodeContains(settings, "public void RequestPersist() => _saveQueue.RequestSave();");
        AssertCodeContains(settingsUpdate, "RequestPersist();");
        AssertCodeContains(settingsUpdate, "_requestClose();");
        Assert.True(
            settingsUpdate.IndexOf("RequestPersist();", StringComparison.Ordinal)
            < settingsUpdate.IndexOf("_requestClose();", StringComparison.Ordinal));

        AssertCodeContains(closeSettings, "_settingsWidget.RequestPersist();");
        AssertCodeContains(closeSettings, "_settingsWidget.IsVisible = false;");
        AssertCodeContains(closeSettings, "_lastDragPosition = null;");
        AssertCodeContains(dialogUpdate, "TravelMapDialogCancelPolicy.Resolve(_settingsWidget.IsVisible)");
        AssertCodeContains(dialogUpdate, "CloseSettings();");
        Assert.True(
            dialogUpdate.IndexOf("CloseSettings();", StringComparison.Ordinal)
            < dialogUpdate.IndexOf("DialogsManager.HideDialog(this);", StringComparison.Ordinal));
        Assert.True(
            dialogUpdate.IndexOf("if (_closeButton.IsClicked)", StringComparison.Ordinal)
            < dialogUpdate.LastIndexOf("DialogsManager.HideDialog(this);", StringComparison.Ordinal));
    }

    [Fact]
    public void Component_routes_notices_into_open_map_and_uses_hud_only_as_fallback()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var typed = ExtractBraceBlock(component, "private void ShowMessage(TravelMapNotice notice)");
        var routed = ExtractBraceBlock(component, "private void ShowMessage(\n        string message,");

        AssertCodeContains(typed, "ShowMessage(notice.Text, notice.Kind);");
        AssertCodeContains(routed, "_dispatcher?.Invoke");
        AssertCodeContains(routed, "DialogsManager.Dialogs.Contains(_largeMapDialog)");
        AssertCodeContains(routed, "_largeMapDialog.ShowNotice(new TravelMapNotice(message, kind));");
        AssertCodeContains(routed, "return;");
        AssertCodeContains(routed, "Gui?.DisplaySmallMessage(");
        Assert.True(
            routed.IndexOf("_largeMapDialog.ShowNotice", StringComparison.Ordinal)
            < routed.IndexOf("Gui?.DisplaySmallMessage", StringComparison.Ordinal));
    }

    [Fact]
    public void Specific_teleport_feedback_is_not_overwritten_by_a_generic_action_failure()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var dialog = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "UI",
            "TravelMapDialog.cs"));
        var statusMap = ExtractBraceBlock(
            component,
            "private static TravelMapActionStatus ToActionStatus(TravelMapTeleportDispatchResult result)");
        var execute = ExtractBraceBlock(dialog, "private async Task ExecuteActionAsync(");

        AssertCodeContains(dialog, "FailedWithFeedback");
        AssertCodeContains(
            statusMap,
            "TravelMapTeleportDispatchResult.LocalFailed => TravelMapActionStatus.FailedWithFeedback");
        AssertCodeContains(execute, "result == TravelMapActionStatus.Failed");
        AssertCodeDoesNotContain(execute, "result == TravelMapActionStatus.FailedWithFeedback");
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("character")]
    [InlineData("crafting")]
    [InlineData("sleep")]
    [InlineData("generic dialog")]
    public void Modal_surface_hides_then_restores_hud_without_changing_settings(string surface)
    {
        _ = surface;
        var settings = CreateNonDefaultHudSettings();
        var sameSettings = settings;
        var before = JsonSerializer.SerializeToUtf8Bytes(settings);
        var signals = VisibleHudSignals(settings) with { HasModalSurface = true };

        Assert.Equal(new TravelMapHudState(false, false, false), TravelMapHudPolicy.Evaluate(signals));
        Assert.Equal(
            new TravelMapHudState(true, true, true),
            TravelMapHudPolicy.Evaluate(signals with { HasModalSurface = false }));
        Assert.Same(sameSettings, settings);
        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(settings));
    }

    [Fact]
    public void Large_map_hides_then_restores_hud_without_changing_settings()
    {
        var settings = CreateNonDefaultHudSettings();
        var sameSettings = settings;
        var before = JsonSerializer.SerializeToUtf8Bytes(settings);
        var signals = VisibleHudSignals(settings) with { IsLargeMapOpen = true };

        Assert.Equal(new TravelMapHudState(false, false, false), TravelMapHudPolicy.Evaluate(signals));
        Assert.Equal(
            new TravelMapHudState(true, true, true),
            TravelMapHudPolicy.Evaluate(signals with { IsLargeMapOpen = false }));
        Assert.Same(sameSettings, settings);
        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(settings));
    }

    [Fact]
    public void Constructed_but_not_shown_large_map_does_not_hide_hud()
    {
        var settings = CreateNonDefaultHudSettings();
        var state = TravelMapHudPolicy.Evaluate(VisibleHudSignals(settings) with
        {
            IsLargeMapOpen = false,
        });

        Assert.Equal(new TravelMapHudState(true, true, true), state);
    }

    [Fact]
    public void Assembly_exposes_exactly_network_package_ids_41_and_61()
    {
        var packageTypes = typeof(TravelMapModLoader).Assembly.GetTypes()
            .Where(type => typeof(IPackage).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            .ToArray();
        var packageIds = packageTypes
            .Select(type => Assert.IsAssignableFrom<IPackage>(Activator.CreateInstance(type)).ID)
            .Order()
            .ToArray();

        Assert.Equal(new byte[] { 41, 61 }, packageIds);
        Assert.DoesNotContain((byte)60, packageIds);
    }

    [Fact]
    public void Product_sources_contain_no_mod_count_reporting_or_obsolete_verification_markers()
    {
        var source = string.Join(
            "\n",
            Directory.GetFiles(
                    Path.Combine(TestPaths.RepositoryRoot, "src", "SurvivalcraftTravelMap"),
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".cs" or ".json" or ".netxdb")
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        foreach (var marker in new[]
                 {
                     "AntiCheatReportPackage",
                     "ReadOnlyModList",
                     "ReadOnlyModListAll",
                     "CheckDataBaseValid",
                     "181215270",
                     "Setting.png",
                 })
        {
            Assert.DoesNotContain(marker, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Final_xdb_injects_exactly_one_travel_map_component_with_new_guids()
    {
        var document = XDocument.Load(TestPaths.Xdb);
        var root = Assert.IsType<XElement>(document.Root);
        var player = Assert.Single(root.Elements("EntityTemplate"));
        var member = Assert.Single(player.Elements("MemberComponentTemplate"));
        var gameplay = Assert.Single(root.Elements("Folder"));
        var component = Assert.Single(gameplay.Elements("ComponentTemplate"));
        var classParameter = Assert.Single(component.Elements("Parameter"));

        Assert.Equal("SurvivalCraftMap", root.Name.LocalName);
        Assert.Equal("Player", player.Attribute("Name")?.Value);
        Assert.Equal("4be6c1c5-d65d-4537-8a8b-a391969e6dc2", player.Attribute("Guid")?.Value);
        Assert.Equal("TravelMap", member.Attribute("Name")?.Value);
        Assert.Equal("Gameplay", gameplay.Attribute("Name")?.Value);
        Assert.Equal("d3d4b692-acc9-4128-9b99-a5acf1de1fbb", gameplay.Attribute("Guid")?.Value);
        Assert.Equal("TravelMap", component.Attribute("Name")?.Value);
        Assert.Equal("b05700ed-7e4e-4679-98f5-b597f421496b", component.Attribute("InheritanceParent")?.Value);
        Assert.Equal("Class", classParameter.Attribute("Name")?.Value);
        Assert.Equal("SurvivalcraftTravelMap.Mod.TravelMapComponent", classParameter.Attribute("Value")?.Value);
        Assert.Equal("string", classParameter.Attribute("Type")?.Value);

        var newGuids = new[]
        {
            member.Attribute("Guid")?.Value,
            component.Attribute("Guid")?.Value,
            classParameter.Attribute("Guid")?.Value,
        };
        Assert.Equal(component.Attribute("Guid")?.Value, member.Attribute("InheritanceParent")?.Value);
        Assert.All(newGuids, value => Assert.True(Guid.TryParse(value, out _), $"Invalid GUID '{value}'."));
        Assert.Equal(newGuids.Length, newGuids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(
            newGuids,
            value => string.Equals(value, "736FC2A9-9B0A-2E00-F7C8-95A4A6811FEE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "387007A5-9269-1362-A0E7-DFEA4AC68E02", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "B13D2D65-46A7-D038-8111-DE8FCBA58FBC", StringComparison.OrdinalIgnoreCase));
    }

    private static TravelMapSettings CreateNonDefaultHudSettings() => new()
    {
        IsMiniMapVisible = true,
        ShowCoordinates = false,
        UseDayNightTint = false,
        AcceptTeleportInvitations = false,
        MiniMapSize = 320,
        MiniMapBlocksPerPixel = 3.5f,
        LargeMapBlocksPerPixel = 7f,
        LargeMapHotkey = "K",
        NightMinimumBrightness = 0.75f,
    };

    private static TravelMapHudSignals VisibleHudSignals(TravelMapSettings settings) => new(
        HasUi: true,
        IsMainPlayer: true,
        IsRuntimeActive: true,
        MiniMapSettingEnabled: settings.IsMiniMapVisible,
        HasModalSurface: false,
        IsLargeMapOpen: false,
        HasOtherPlayers: true,
        InvitationFeatureAvailable: true,
        HasTextEntryFocus: false);

    private static string ExtractBraceBlock(string source, string anchor, int startOccurrence = 1)
    {
        var tokens = TokenizeCSharp(source);
        var anchorTokens = TokenizeCSharp(anchor);
        var anchorIndex = -1;
        var searchStart = 0;
        for (var occurrence = 0; occurrence < startOccurrence; occurrence++)
        {
            anchorIndex = FindTokenSequence(tokens, anchorTokens, searchStart);
            if (anchorIndex < 0)
            {
                break;
            }

            searchStart = anchorIndex + anchorTokens.Count;
        }

        Assert.True(anchorIndex >= 0, $"Could not find code anchor '{anchor}'.");
        var openingBraceIndex = anchorIndex + anchorTokens.Count;
        while (openingBraceIndex < tokens.Count && tokens[openingBraceIndex].Text != "{")
        {
            openingBraceIndex++;
        }

        Assert.True(openingBraceIndex < tokens.Count, $"Could not find opening brace after code anchor '{anchor}'.");
        var depth = 0;
        for (var index = openingBraceIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "{")
            {
                depth++;
            }
            else if (tokens[index].Text == "}" && --depth == 0)
            {
                return source[tokens[openingBraceIndex].Start..tokens[index].End];
            }
        }

        throw new InvalidDataException($"Could not find closing brace after '{anchor}'.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var tokens = TokenizeCSharp(source);
        var valueTokens = TokenizeCSharp(value);
        Assert.NotEmpty(valueTokens);
        var count = 0;
        for (var index = 0; index <= tokens.Count - valueTokens.Count;)
        {
            if (TokensMatchAt(tokens, valueTokens, index))
            {
                count++;
                index += valueTokens.Count;
            }
            else
            {
                index++;
            }
        }

        return count;
    }

    private static void AssertCodeContains(
        string source,
        string expectedCode,
        string? description = null) =>
        Assert.True(
            IndexOfCode(source, expectedCode) >= 0,
            $"Could not find expected code{(description is null ? string.Empty : $" ({description})")}: {expectedCode}");

    private static void AssertCodeDoesNotContain(string source, string unexpectedCode) =>
        Assert.True(
            IndexOfCode(source, unexpectedCode) < 0,
            $"Found forbidden code: {unexpectedCode}");

    private static int IndexOfCode(string source, string expectedCode)
    {
        var tokens = TokenizeCSharp(source);
        var expectedTokens = TokenizeCSharp(expectedCode);
        var tokenIndex = FindTokenSequence(tokens, expectedTokens);
        return tokenIndex < 0 ? -1 : tokens[tokenIndex].Start;
    }

    private static int FindTokenSequence(
        IReadOnlyList<SourceToken> tokens,
        IReadOnlyList<SourceToken> expected,
        int startIndex = 0)
    {
        if (expected.Count == 0)
        {
            return -1;
        }

        for (var index = startIndex; index <= tokens.Count - expected.Count; index++)
        {
            if (TokensMatchAt(tokens, expected, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TokensMatchAt(
        IReadOnlyList<SourceToken> tokens,
        IReadOnlyList<SourceToken> expected,
        int index)
    {
        for (var offset = 0; offset < expected.Count; offset++)
        {
            if (!string.Equals(tokens[index + offset].Text, expected[offset].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<SourceToken> TokenizeCSharp(string source, int baseOffset = 0)
    {
        var tokens = new List<SourceToken>();
        for (var index = 0; index < source.Length;)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (TrySkipComment(source, ref index)
                || TrySkipStringLiteral(source, ref index, tokens, baseOffset)
                || TrySkipCharacterLiteral(source, ref index))
            {
                continue;
            }

            var start = index;
            if (IsIdentifierStart(source[index]))
            {
                index++;
                while (index < source.Length && IsIdentifierPart(source[index]))
                {
                    index++;
                }
            }
            else if (char.IsDigit(source[index]))
            {
                index++;
                while (index < source.Length
                       && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '.'))
                {
                    index++;
                }
            }
            else
            {
                index++;
            }

            tokens.Add(new SourceToken(
                source[start..index],
                baseOffset + start,
                baseOffset + index));
        }

        return tokens;
    }

    private static bool TrySkipComment(string source, ref int index)
    {
        if (index + 1 >= source.Length || source[index] != '/')
        {
            return false;
        }

        if (source[index + 1] == '/')
        {
            index += 2;
            while (index < source.Length && source[index] is not '\r' and not '\n')
            {
                index++;
            }

            return true;
        }

        if (source[index + 1] != '*')
        {
            return false;
        }

        index += 2;
        while (index + 1 < source.Length && (source[index] != '*' || source[index + 1] != '/'))
        {
            index++;
        }

        index = Math.Min(source.Length, index + 2);
        return true;
    }

    private static bool TrySkipCharacterLiteral(string source, ref int index)
    {
        if (source[index] != '\'')
        {
            return false;
        }

        index++;
        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index = Math.Min(source.Length, index + 2);
            }
            else if (source[index++] == '\'')
            {
                break;
            }
        }

        return true;
    }

    private static bool TrySkipStringLiteral(
        string source,
        ref int index,
        List<SourceToken>? interpolatedCodeTokens = null,
        int baseOffset = 0)
    {
        var cursor = index;
        var dollarCount = 0;
        var verbatim = false;

        if (source[cursor] == '@')
        {
            verbatim = true;
            cursor++;
            if (cursor < source.Length && source[cursor] == '$')
            {
                dollarCount = 1;
                cursor++;
            }
        }
        else
        {
            while (cursor < source.Length && source[cursor] == '$')
            {
                dollarCount++;
                cursor++;
            }

            if (cursor < source.Length && source[cursor] == '@')
            {
                verbatim = true;
                cursor++;
            }
        }

        if (cursor >= source.Length || source[cursor] != '"')
        {
            return false;
        }

        var quoteCount = CountCharacterRun(source, cursor, '"');
        if (quoteCount >= 3)
        {
            index = dollarCount == 0
                ? SkipRawString(source, cursor + quoteCount, quoteCount)
                : SkipRawInterpolatedString(
                    source,
                    cursor + quoteCount,
                    quoteCount,
                    dollarCount,
                    interpolatedCodeTokens,
                    baseOffset);
            return true;
        }

        if (quoteCount != 1 || dollarCount > 1)
        {
            return false;
        }

        index = dollarCount == 1
            ? SkipInterpolatedString(
                source,
                cursor + 1,
                verbatim,
                interpolatedCodeTokens,
                baseOffset)
            : SkipOrdinaryString(source, cursor + 1, verbatim);
        return true;
    }

    private static int SkipOrdinaryString(string source, int index, bool verbatim)
    {
        while (index < source.Length)
        {
            if (verbatim && source[index] == '"' && index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
            }
            else if (!verbatim && source[index] == '\\')
            {
                index = Math.Min(source.Length, index + 2);
            }
            else if (source[index] == '"')
            {
                return index + 1;
            }
            else
            {
                index++;
            }
        }

        return source.Length;
    }

    private static int SkipInterpolatedString(
        string source,
        int index,
        bool verbatim,
        List<SourceToken>? interpolatedCodeTokens,
        int baseOffset)
    {
        while (index < source.Length)
        {
            if (verbatim && source[index] == '"' && index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
            }
            else if (!verbatim && source[index] == '\\')
            {
                index = Math.Min(source.Length, index + 2);
            }
            else if (source[index] == '"')
            {
                return index + 1;
            }
            else if (source[index] == '{' && index + 1 < source.Length && source[index + 1] == '{')
            {
                index += 2;
            }
            else if (source[index] == '{')
            {
                var expressionStart = index + 1;
                var afterExpression = SkipInterpolationHole(source, expressionStart);
                var expressionEnd = Math.Max(expressionStart, afterExpression - 1);
                if (interpolatedCodeTokens is not null && expressionEnd > expressionStart)
                {
                    interpolatedCodeTokens.AddRange(TokenizeCSharp(
                        source[expressionStart..expressionEnd],
                        baseOffset + expressionStart));
                }

                index = afterExpression;
            }
            else if (source[index] == '}' && index + 1 < source.Length && source[index + 1] == '}')
            {
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return source.Length;
    }

    private static int SkipInterpolationHole(string source, int index)
    {
        var depth = 1;
        while (index < source.Length)
        {
            if (TrySkipComment(source, ref index)
                || TrySkipStringLiteral(source, ref index)
                || TrySkipCharacterLiteral(source, ref index))
            {
                continue;
            }

            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return index + 1;
            }

            index++;
        }

        return source.Length;
    }

    private static int SkipRawString(string source, int index, int quoteCount)
    {
        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            var runLength = CountCharacterRun(source, index, '"');
            if (runLength >= quoteCount)
            {
                return index + runLength;
            }

            index += runLength;
        }

        return source.Length;
    }

    private static int SkipRawInterpolatedString(
        string source,
        int index,
        int quoteCount,
        int dollarCount,
        List<SourceToken>? interpolatedCodeTokens,
        int baseOffset)
    {
        while (index < source.Length)
        {
            if (source[index] == '"')
            {
                var quoteRun = CountCharacterRun(source, index, '"');
                if (quoteRun >= quoteCount)
                {
                    return index + quoteRun;
                }

                index += quoteRun;
                continue;
            }

            if (source[index] != '{')
            {
                index++;
                continue;
            }

            var openingRun = CountCharacterRun(source, index, '{');
            if (!OpensRawInterpolationHole(openingRun, dollarCount))
            {
                index += openingRun;
                continue;
            }

            var expressionStart = index + openingRun;
            var afterExpression = SkipRawInterpolationHole(
                source,
                expressionStart,
                dollarCount,
                out var expressionEnd);
            if (interpolatedCodeTokens is not null && expressionEnd > expressionStart)
            {
                interpolatedCodeTokens.AddRange(TokenizeCSharp(
                    source[expressionStart..expressionEnd],
                    baseOffset + expressionStart));
            }

            index = afterExpression;
        }

        return source.Length;
    }

    private static int SkipRawInterpolationHole(
        string source,
        int index,
        int dollarCount,
        out int expressionEnd)
    {
        var nestedBraceDepth = 0;
        while (index < source.Length)
        {
            if (TrySkipComment(source, ref index)
                || TrySkipStringLiteral(source, ref index)
                || TrySkipCharacterLiteral(source, ref index))
            {
                continue;
            }

            if (source[index] == '{')
            {
                var nestedOpeningRun = CountCharacterRun(source, index, '{');
                nestedBraceDepth += nestedOpeningRun;
                index += nestedOpeningRun;
                continue;
            }

            if (source[index] != '}')
            {
                index++;
                continue;
            }

            var closingRunStart = index;
            var closingRun = CountCharacterRun(source, index, '}');
            var nestedClosures = Math.Min(nestedBraceDepth, closingRun);
            nestedBraceDepth -= nestedClosures;
            var delimiterCandidateLength = closingRun - nestedClosures;
            if (nestedBraceDepth == 0
                && OpensRawInterpolationHole(delimiterCandidateLength, dollarCount))
            {
                expressionEnd = closingRunStart + nestedClosures;
                return closingRunStart + closingRun;
            }

            index += closingRun;
        }

        expressionEnd = source.Length;
        return source.Length;
    }

    private static bool OpensRawInterpolationHole(int braceRunLength, int dollarCount) =>
        braceRunLength >= dollarCount
        && braceRunLength - dollarCount < dollarCount;

    private static int CountCharacterRun(string source, int index, char value)
    {
        var start = index;
        while (index < source.Length && source[index] == value)
        {
            index++;
        }

        return index - start;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

    private readonly record struct SourceToken(string Text, int Start, int End);

    [Fact]
    public void Source_block_extraction_ignores_method_anchors_in_comments_and_string_literals()
    {
        var source = """"
            internal sealed class Probe
            {
                // private void Target() { DecoyFromLineComment(); }
                /* private void Target() { DecoyFromBlockComment(); } */
                private const string Normal = "private void Target() { DecoyFromNormal(); }";
                private const string Verbatim = @"private void Target() { DecoyFromVerbatim(); }";
                private const string Interpolated = $"prefix {new { Text = "private void Target() { DecoyFromInterpolated(); }" }} suffix";
                private const string InterpolatedVerbatim = $@"private void Target() {{ DecoyFromInterpolatedVerbatim(); }}";
                private const string Raw = """private void Target() { DecoyFromRaw(); }""";

                private void Target()
                {
                    RealBody();
                }
            }
            """";

        var block = ExtractBraceBlock(source, "private void Target()");

        Assert.Contains("RealBody();", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Decoy", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_token_count_ignores_invocations_in_comments_and_string_literals()
    {
        var source = """"
            {
                // _scheduler.MarkCompleted(chunk);
                /* _scheduler.MarkCompleted(chunk); */
                var normal = "_scheduler.MarkCompleted(chunk);";
                var verbatim = @"_scheduler.MarkCompleted(chunk);";
                var interpolated = $"{{_scheduler.MarkCompleted(chunk)}}";
                var raw = """_scheduler.MarkCompleted(chunk);""";
                _scheduler.MarkCompleted(chunk);
            }
            """";

        Assert.Equal(1, CountOccurrences(source, "MarkCompleted("));
    }

    [Fact]
    public void Source_token_count_ignores_fake_exits_but_detects_throw_expressions()
    {
        var source = """"
            {
                // return; break; continue; goto Exit; throw;
                /* return; break; continue; goto Exit; throw exception; */
                var normal = "return; break; continue; goto Exit; throw exception;";
                var verbatim = @"return; break; continue; goto Exit; throw exception;";
                var interpolated = $"{{return; break; continue; goto Exit; throw exception;}}";
                var raw = """return; break; continue; goto Exit; throw exception;""";
                throw exception;
            }
            """";

        Assert.Equal(0, CountOccurrences(source, "return"));
        Assert.Equal(0, CountOccurrences(source, "break"));
        Assert.Equal(0, CountOccurrences(source, "continue"));
        Assert.Equal(0, CountOccurrences(source, "goto"));
        Assert.Equal(1, CountOccurrences(source, "throw"));
    }

    [Fact]
    public void Single_dollar_raw_interpolation_skips_literal_text_and_preserves_each_code_hole()
    {
        var source = """"""
            {
                var raw = $""""
                    literal throw; and a shorter quote run """
                    {{ literal braces contain throw exception; }}
                    { GenuineSingle(); throw exception; }
                    literal after first hole throw;
                    { NextSingle(); }
                    """";
                AfterRaw();
            }
            """""";

        Assert.Equal(1, CountOccurrences(source, "GenuineSingle("));
        Assert.Equal(1, CountOccurrences(source, "NextSingle("));
        Assert.Equal(1, CountOccurrences(source, "AfterRaw("));
        Assert.Equal(1, CountOccurrences(source, "throw"));
    }

    [Fact]
    public void Multi_dollar_raw_interpolation_honors_brace_delimiters_nested_literals_and_later_holes()
    {
        var source = """"""
            {
                var raw = $$""""
                    one brace is literal: { FakeLiteral(); throw exception; }
                    {{
                        if (ready) { NestedBlock(); }
                        var normal = "throw exception; }";
                        var verbatim = @"throw exception; }}";
                        var nestedRaw = """throw exception; }}""";
                        var interpolated = $"{{throw exception;}} {new { Text = "throw" }}";
                        GenuineDouble();
                        throw exception;
                    }}
                    three opening braces keep surplus literal text and open a real hole:
                    {{{ GreaterBraceRun(); }}}
                    four opening braces are literal: {{{{ FakeEvenBraceRun(); throw exception; }}}}
                    literal after holes: { FakeAfter(); throw exception; }
                    {{ NextDouble(); }}
                    """";
                AfterDoubleRaw();
            }
            """""";

        Assert.Equal(0, CountOccurrences(source, "FakeLiteral("));
        Assert.Equal(0, CountOccurrences(source, "FakeEvenBraceRun("));
        Assert.Equal(0, CountOccurrences(source, "FakeAfter("));
        Assert.Equal(1, CountOccurrences(source, "NestedBlock("));
        Assert.Equal(1, CountOccurrences(source, "GenuineDouble("));
        Assert.Equal(1, CountOccurrences(source, "GreaterBraceRun("));
        Assert.Equal(1, CountOccurrences(source, "NextDouble("));
        Assert.Equal(1, CountOccurrences(source, "AfterDoubleRaw("));
        Assert.Equal(1, CountOccurrences(source, "throw"));
    }

    [Fact]
    public void Required_assets_exist_and_are_structurally_loadable()
    {
        var assets = Path.Combine(TestPaths.RepositoryRoot, "src", "SurvivalcraftTravelMap", "Assets");
        var required = new[]
        {
            "BlockPixelColor.json",
            "Point.png",
            "TeleportButton.png",
            "TeleportButton_Pressed.png",
            "TeleportTo.png",
        };

        Assert.Equal(required, Directory.GetFiles(assets).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using var colors = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, required[0])));
        Assert.Equal(257, colors.RootElement.EnumerateObject().Count());
        foreach (var name in required.Skip(1))
        {
            var bytes = File.ReadAllBytes(Path.Combine(assets, name));
            Assert.True(bytes.Length >= 24, $"{name} is truncated.");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
            Assert.Equal("IHDR", Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal(
                64u,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
            Assert.Equal(
                64u,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
        }

        Assert.NotEqual(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, "TeleportButton.png"))),
            SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, "TeleportButton_Pressed.png"))));
    }

    [Fact]
    public void Asset_generator_reproduces_every_checked_in_png_and_uses_the_compact_transfer_glyph()
    {
        var assets = Path.Combine(TestPaths.RepositoryRoot, "src", "SurvivalcraftTravelMap", "Assets");
        var generator = Path.Combine(TestPaths.RepositoryRoot, "tools", "Generate-Assets.ps1");
        var generatorSource = File.ReadAllText(generator);
        using var temporaryDirectory = new TemporaryDirectory();

        var result = PowerShellRunner.Run(generator, "-OutputDirectory", temporaryDirectory.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Draw-TeleportArrow", generatorSource, StringComparison.Ordinal);
        Assert.Contains("Draw-TeleportButton", generatorSource, StringComparison.Ordinal);
        Assert.Contains("Draw-TeleportPerson", generatorSource, StringComparison.Ordinal);
        foreach (var name in new[]
                 {
                     "Point.png",
                     "TeleportButton.png",
                     "TeleportButton_Pressed.png",
                     "TeleportTo.png",
                 })
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(assets, name)),
                File.ReadAllBytes(Path.Combine(temporaryDirectory.Path, name)));
        }
    }
}

public sealed class PackageVerifierBehaviorTests
{
    [Fact]
    public void Verifier_accepts_a_valid_package_and_prints_success_marker()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreateValidPackage(temporaryDirectory.Path);

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_rejects_a_package_with_the_wrong_filename()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreateValidPackage(temporaryDirectory.Path);
        var renamed = Path.Combine(temporaryDirectory.Path, "TravelMap.netmod");
        File.Move(package, renamed);

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, renamed);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SurvivalcraftTravelMap.netmod", result.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_rejects_a_missing_required_asset()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreateValidPackage(temporaryDirectory.Path);
        PackageFixtures.RemoveEntry(package, "Assets/Point.png");

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Assets/Point.png", result.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_rejects_setting_png_even_under_assets()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreatePackage(
            temporaryDirectory.Path,
            new PackageEntry("Assets/Setting.png", MinimalPng.Bytes));

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Setting.png", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verifier_rejects_duplicate_entries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreatePackage(
            temporaryDirectory.Path,
            new PackageEntry("modinfo.json", "{}"));

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("duplicate entry", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_rejects_a_game_dll_nested_under_assets()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreatePackage(
            temporaryDirectory.Path,
            new PackageEntry("Assets/nested/Engine.dll", "game assembly"));

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("forbidden game DLL", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AntiCheatReportPackage", "AntiCheatReportPackage")]
    [InlineData("PackageId = 60", "package ID 60")]
    [InlineData("1.0.0+0123456789abcdef0123456789abcdef01234567", "source revision")]
    public void Verifier_rejects_forbidden_content(string content, string expectedMessage)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreateValidPackage(temporaryDirectory.Path);
        PackageFixtures.ReplaceEntry(package, "Assets/BlockPixelColor.json", Encoding.UTF8.GetBytes(content));

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Assets/../escape.txt", "stable relative path")]
    [InlineData("notes.txt", "outside the package allowlist")]
    public void Verifier_rejects_disallowed_paths(string entryName, string expectedMessage)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreatePackage(
            temporaryDirectory.Path,
            new PackageEntry(entryName, "disallowed"));

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_rejects_an_allowlisted_entry_over_the_uncompressed_size_limit()
    {
        const int maximumEntryBytes = 8 * 1024 * 1024;
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreateValidPackage(temporaryDirectory.Path);
        PackageFixtures.ReplaceEntry(
            package,
            "Assets/BlockPixelColor.json",
            new byte[maximumEntryBytes + 1]);

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("uncompressed size limit", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_rejects_allowlisted_entries_over_the_aggregate_uncompressed_size_limit()
    {
        const int entryBytes = 6 * 1024 * 1024;
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreateValidPackage(temporaryDirectory.Path);
        var compressiblePayload = new byte[entryBytes];
        PackageFixtures.ReplaceEntry(package, "SurvivalcraftTravelMap.dll", compressiblePayload);
        PackageFixtures.ReplaceEntry(package, "modinfo.json", compressiblePayload);
        PackageFixtures.ReplaceEntry(package, "mod.netxdb", compressiblePayload);

        var result = PowerShellRunner.Run(TestPaths.VerifyScript, package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "aggregate uncompressed size limit",
            result.AllOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PACKAGE_OK", result.StandardOutput, StringComparison.Ordinal);
    }
}

public sealed class DeterministicBuildBehaviorTests
{
    [Fact]
    public void Consecutive_real_builds_have_identical_hash_entries_and_timestamps()
    {
        var firstResult = PowerShellRunner.Run(TestPaths.BuildScript);
        Assert.Equal(0, firstResult.ExitCode);
        Assert.Contains("NETMOD_BUILT", firstResult.StandardOutput, StringComparison.Ordinal);
        var first = PackageSnapshot.Read(TestPaths.BuiltPackage);

        var secondResult = PowerShellRunner.Run(TestPaths.BuildScript);
        Assert.Equal(0, secondResult.ExitCode);
        Assert.Contains("NETMOD_BUILT", secondResult.StandardOutput, StringComparison.Ordinal);
        var second = PackageSnapshot.Read(TestPaths.BuiltPackage);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.EntryNames, second.EntryNames);
        Assert.Equal(first.Timestamps, second.Timestamps);
        Assert.Equal(first.EntryNames.Order(StringComparer.Ordinal), first.EntryNames);
        Assert.Single(first.Timestamps.Distinct());
    }
}

internal sealed record PackageEntry(string Name, byte[] Content)
{
    internal PackageEntry(string name, string content)
        : this(name, Encoding.UTF8.GetBytes(content))
    {
    }
}

internal sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError)
{
    internal string AllOutput => StandardOutput + Environment.NewLine + StandardError;
}

internal sealed record PackageSnapshot(string Sha256, string[] EntryNames, DateTimeOffset[] Timestamps)
{
    internal static PackageSnapshot Read(string packagePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        using var archive = ZipFile.OpenRead(packagePath);
        return new PackageSnapshot(
            hash,
            archive.Entries.Select(entry => entry.FullName).ToArray(),
            archive.Entries.Select(entry => entry.LastWriteTime).ToArray());
    }
}

internal static class PackageFixtures
{
    internal static string CreateValidPackage(string directory) => CreatePackage(directory);

    internal static string CreatePackage(string directory, params PackageEntry[] additionalEntries)
    {
        var packagePath = Path.Combine(directory, "SurvivalcraftTravelMap.netmod");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        AddEntry(
            archive,
            new PackageEntry(
                "SurvivalcraftTravelMap.dll",
                File.ReadAllBytes(typeof(TravelMapModLoader).Assembly.Location)));
        AddEntry(
            archive,
            new PackageEntry(
                "modinfo.json",
                "{\"Name\":\"Survivalcraft Travel Map\",\"Author\":\"SCTM\",\"Version\":\"1.0.0\",\"ApiVersion\":\"1.44\",\"ScVersion\":\"2.4.40.6\",\"PackageName\":\"SurvivalcraftTravelMap\",\"Dependencies\":[]}"));
        AddEntry(archive, new PackageEntry("mod.netxdb", FinalXdb));
        AddEntry(archive, new PackageEntry("Assets/BlockPixelColor.json", CreateColorJson()));
        AddEntry(archive, new PackageEntry("Assets/Point.png", MinimalPng.Bytes));
        AddEntry(archive, new PackageEntry("Assets/TeleportButton.png", MinimalPng.Bytes));
        AddEntry(archive, new PackageEntry("Assets/TeleportButton_Pressed.png", MinimalPng.Bytes));
        AddEntry(archive, new PackageEntry("Assets/TeleportTo.png", MinimalPng.Bytes));

        foreach (var entry in additionalEntries)
        {
            AddEntry(archive, entry);
        }

        return packagePath;
    }

    internal static void RemoveEntry(string packagePath, string entryName)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
    }

    internal static void ReplaceEntry(string packagePath, string entryName, byte[] content)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        AddEntry(archive, new PackageEntry(entryName, content));
    }

    private static void AddEntry(ZipArchive archive, PackageEntry fixture)
    {
        var entry = archive.CreateEntry(fixture.Name);
        using var stream = entry.Open();
        stream.Write(fixture.Content);
    }

    private static string CreateColorJson() =>
        "{" + string.Join(",", Enumerable.Range(0, 257).Select(index => $"\"{index}\":\"#000000\"")) + "}";

    private const string FinalXdb = """
        <SurvivalCraftMap>
          <EntityTemplate Name="Player" Guid="4be6c1c5-d65d-4537-8a8b-a391969e6dc2">
            <MemberComponentTemplate Name="TravelMap" Guid="32be124c-0f5b-4ca0-ae58-df7fa2b707d3" InheritanceParent="4b67335f-9888-4824-9f0e-cc5f72204b8e" />
          </EntityTemplate>
          <Folder Name="Gameplay" Guid="d3d4b692-acc9-4128-9b99-a5acf1de1fbb">
            <ComponentTemplate Name="TravelMap" Guid="4b67335f-9888-4824-9f0e-cc5f72204b8e" InheritanceParent="b05700ed-7e4e-4679-98f5-b597f421496b">
              <Parameter Name="Class" Guid="e14340ef-ab75-4dbe-aad2-9b08f7b7b61a" Value="SurvivalcraftTravelMap.Mod.TravelMapComponent" Type="string" />
            </ComponentTemplate>
          </Folder>
        </SurvivalCraftMap>
        """;
}

internal static class MinimalPng
{
    internal static byte[] Bytes { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}

internal static class PowerShellRunner
{
    internal static PowerShellResult Run(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = TestPaths.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);

        return new PowerShellResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SurvivalcraftTravelMap.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class TestPaths
{
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    internal static string Manifest => Path.Combine(
        RepositoryRoot,
        "src",
        "SurvivalcraftTravelMap",
        "modinfo.json");

    internal static string BuildProps => Path.Combine(RepositoryRoot, "Directory.Build.props");

    internal static string ModProject => Path.Combine(
        RepositoryRoot,
        "src",
        "SurvivalcraftTravelMap",
        "SurvivalcraftTravelMap.csproj");

    internal static string Loader => Path.Combine(
        RepositoryRoot,
        "src",
        "SurvivalcraftTravelMap",
        "Mod",
        "TravelMapModLoader.cs");

    internal static string Component => Path.Combine(
        RepositoryRoot,
        "src",
        "SurvivalcraftTravelMap",
        "Mod",
        "TravelMapComponent.cs");

    internal static string Xdb => Path.Combine(
        RepositoryRoot,
        "src",
        "SurvivalcraftTravelMap",
        "mod.netxdb");

    internal static string BuildScript => Path.Combine(
        RepositoryRoot,
        "tools",
        "Build-NetMod.ps1");

    internal static string VerifyScript => Path.Combine(
        RepositoryRoot,
        "tools",
        "Verify-Package.ps1");

    internal static string BuiltPackage => Path.Combine(
        RepositoryRoot,
        "artifacts",
        "SurvivalcraftTravelMap.netmod");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SurvivalCraftTravelMap.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
