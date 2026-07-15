using System.Numerics;
using System.Text.Json;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Network;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.Teleport;
using SurvivalcraftTravelMap.UI;
using SurvivalcraftTravelMap.Waypoints;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapUiStateTests
{
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
        Assert.Equal(CoordinateTeleportResultText.For(result), TravelMapNoticeFactory.For(result).Text);
    }

    [Theory]
    [InlineData(TeleportResult.Success, CoordinateTeleportResultCode.Success, TravelMapNoticeKind.Success)]
    [InlineData(TeleportResult.ChunkTimeout, CoordinateTeleportResultCode.TimedOut, TravelMapNoticeKind.Failure)]
    [InlineData(TeleportResult.NoSafePosition, CoordinateTeleportResultCode.NoSafePosition, TravelMapNoticeKind.Failure)]
    [InlineData(TeleportResult.OutOfWorld, CoordinateTeleportResultCode.OutOfWorld, TravelMapNoticeKind.Failure)]
    [InlineData(TeleportResult.RolledBack, CoordinateTeleportResultCode.RolledBack, TravelMapNoticeKind.Failure)]
    [InlineData(TeleportResult.Busy, CoordinateTeleportResultCode.Rejected, TravelMapNoticeKind.Failure)]
    public void Local_teleport_results_map_to_the_same_visible_notices_as_coordinate_results(
        TeleportResult result,
        CoordinateTeleportResultCode equivalent,
        TravelMapNoticeKind expectedKind)
    {
        var notice = TravelMapNoticeFactory.For(result);

        Assert.Equal(expectedKind, notice.Kind);
        Assert.Equal(CoordinateTeleportResultText.For(equivalent), notice.Text);
    }

    [Fact]
    public void Top_right_overlay_position_uses_the_gui_logical_size()
    {
        var position = TravelMapOverlayLayout.PlaceTopRight(
            new Vector2(1062.5f, 597.65625f),
            new Vector2(384f),
            rightMargin: 100f,
            topMargin: 32f);

        Assert.Equal(new Vector2(578.5f, 32f), position);
    }

    [Fact]
    public void Top_right_overlay_position_pins_to_origin_when_overlay_is_larger_than_gui()
    {
        var position = TravelMapOverlayLayout.PlaceTopRight(
            new Vector2(320f, 240f),
            new Vector2(384f),
            rightMargin: 100f,
            topMargin: 32f);

        Assert.Equal(Vector2.Zero, position);
    }

    [Fact]
    public void Hud_positions_use_the_gui_logical_size_and_shared_right_edge()
    {
        var positions = TravelMapOverlayLayout.PlaceHud(
            new Vector2(1062.5f, 597.65625f),
            miniMapSize: 192f);

        Assert.Equal(new Vector2(794.5f, 24f), positions.MiniMap);
        Assert.Equal(new Vector2(938.5f, 220f), positions.TeleportButton);
    }

    [Fact]
    public void Hud_widgets_are_clamped_independently_to_a_small_gui()
    {
        var positions = TravelMapOverlayLayout.PlaceHud(
            new Vector2(220f, 210f),
            miniMapSize: 192f);

        Assert.Equal(new Vector2(0f, 18f), positions.MiniMap);
        Assert.Equal(new Vector2(144f, 164f), positions.TeleportButton);
    }

    [Theory]
    [InlineData(40f, 40f)]
    [InlineData(0f, 0f)]
    public void Hud_widgets_pin_to_nonnegative_coordinates_when_the_gui_is_smaller_than_them(
        float guiWidth,
        float guiHeight)
    {
        var positions = TravelMapOverlayLayout.PlaceHud(
            new Vector2(guiWidth, guiHeight),
            miniMapSize: 192f);

        Assert.Equal(Vector2.Zero, positions.MiniMap);
        Assert.Equal(Vector2.Zero, positions.TeleportButton);
    }

    [Theory]
    [InlineData(-100f, -80f, -192f)]
    [InlineData(float.NaN, float.PositiveInfinity, float.NegativeInfinity)]
    [InlineData(float.PositiveInfinity, float.NaN, float.NaN)]
    public void Hud_layout_does_not_propagate_negative_or_non_finite_inputs(
        float guiWidth,
        float guiHeight,
        float miniMapSize)
    {
        var positions = TravelMapOverlayLayout.PlaceHud(
            new Vector2(guiWidth, guiHeight),
            miniMapSize);

        AssertFiniteAndNonnegative(positions.MiniMap);
        AssertFiniteAndNonnegative(positions.TeleportButton);
    }

    [Fact]
    public void Existing_top_right_layout_does_not_propagate_non_finite_inputs()
    {
        var position = TravelMapOverlayLayout.PlaceTopRight(
            new Vector2(float.NaN, float.PositiveInfinity),
            new Vector2(float.NegativeInfinity, float.NaN),
            rightMargin: float.NaN,
            topMargin: float.PositiveInfinity);

        AssertFiniteAndNonnegative(position);
    }

    [Fact]
    public async Task Minimap_wheel_requires_hover_and_unblocked_input_then_persists_sqrt2_zoom()
    {
        var settings = new TravelMapSettings { MiniMapBlocksPerPixel = 2f };
        var saves = 0;
        using var interaction = new MiniMapWheelInteraction(
            settings,
            _ =>
            {
                saves++;
                return Task.CompletedTask;
            },
            _ => { },
            TimeSpan.Zero);
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(256f));

        var outside = interaction.HandleWheel(transform, new Vector2(5f), 1f, isHovered: false, inputBlocked: false);
        var blocked = interaction.HandleWheel(transform, new Vector2(5f), 1f, isHovered: true, inputBlocked: true);
        var zoomed = interaction.HandleWheel(transform, new Vector2(5f), 1f, isHovered: true, inputBlocked: false);
        await interaction.WhenSaveIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(transform, outside);
        Assert.Equal(transform, blocked);
        Assert.Equal(2f / MathF.Sqrt(2f), zoomed.BlocksPerPixel, 5);
        Assert.Equal(zoomed.BlocksPerPixel, settings.MiniMapBlocksPerPixel);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Secondary_label_scale_meets_the_readability_floor()
    {
        Assert.True(TravelMapTypography.SecondaryLabelScale >= 0.8f);
    }

    [Fact]
    public void Compact_minimap_contract_uses_the_192_pixel_design_values()
    {
        Assert.Equal(18f, TravelMapRenderModel.MiniMapPlayerArrowSize(192));
        Assert.Equal(
            "X:488 Y:63 Z:-60",
            TravelMapRenderModel.FormatCompactCoordinates(new Vector3(488.9f, 63.2f, -60.1f)));
        Assert.Equal(0.65f, TravelMapTypography.MiniMapCoordinateScale);
    }

    [Theory]
    [InlineData(true, false, false, false, true, false, false)]
    [InlineData(false, true, true, false, false, true, false)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(false, false, false, true, false, false, true)]
    public void Focus_evaluator_maps_real_input_signals(
        bool textBoxFocused,
        bool chatVisible,
        bool chatTextFocused,
        bool modalCapture,
        bool expectedText,
        bool expectedChat,
        bool expectedModal)
    {
        var focus = TravelMapInputFocusEvaluator.Evaluate(new TravelMapInputFocusSignals(
            textBoxFocused,
            chatVisible,
            chatTextFocused,
            modalCapture));

        Assert.Equal(expectedText, focus.HasTextFocus);
        Assert.Equal(expectedChat, focus.HasChatFocus);
        Assert.Equal(expectedModal, focus.HasModalFocus);
    }

    private readonly TravelMapUiController _controller = new();

    [Theory]
    [InlineData(true, TravelMapDialogCancelAction.CloseSettings)]
    [InlineData(false, TravelMapDialogCancelAction.CloseDialog)]
    public void Cancel_closes_the_innermost_surface_first(
        bool settingsVisible,
        TravelMapDialogCancelAction expected) =>
        Assert.Equal(expected, TravelMapDialogCancelPolicy.Resolve(settingsVisible));

    [Theory]
    [InlineData(true, true, false, TravelMapUiCommandKind.OpenLargeMap)]
    [InlineData(false, true, false, TravelMapUiCommandKind.None)]
    [InlineData(true, false, false, TravelMapUiCommandKind.None)]
    [InlineData(true, true, true, TravelMapUiCommandKind.None)]
    public void Minimap_activation_requires_a_pressed_hovered_unblocked_click(
        bool isPressed,
        bool isHovered,
        bool inputBlocked,
        TravelMapUiCommandKind expected)
    {
        var command = _controller.HandleMiniMapActivation(isPressed, isHovered, inputBlocked);

        Assert.Equal(expected, command.Kind);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void M_opens_only_without_text_chat_or_modal_focus(
        bool textFocus,
        bool chatFocus,
        bool modalFocus,
        bool shouldOpen)
    {
        var focus = new TravelMapFocusState(textFocus, chatFocus, modalFocus);

        var command = _controller.HandleOpenHotkey(true, focus);

        Assert.Equal(
            shouldOpen ? TravelMapUiCommandKind.OpenLargeMap : TravelMapUiCommandKind.None,
            command.Kind);
    }

    [Fact]
    public void M_toggles_an_open_map_but_chat_focus_still_blocks_it()
    {
        Assert.Equal(
            TravelMapUiCommandKind.CloseLargeMap,
            _controller.HandleToggleHotkey(true, isOpen: true, TravelMapFocusState.Clear).Kind);
        Assert.Equal(
            TravelMapUiCommandKind.None,
            _controller.HandleToggleHotkey(
                true,
                isOpen: true,
                new TravelMapFocusState(false, true, false)).Kind);
    }

    [Fact]
    public void Unpressed_hotkey_does_not_open_the_map()
    {
        var command = _controller.HandleOpenHotkey(false, TravelMapFocusState.Clear);

        Assert.Equal(TravelMapUiCommandKind.None, command.Kind);
    }

    [Fact]
    public void Opening_transform_is_recentered_on_the_current_player_position()
    {
        var transform = _controller.CenterLargeMap(
            new Vector2(123f, -456f),
            new Vector2(800f, 600f),
            blocksPerPixel: 2f);

        Assert.Equal(new Vector2(123f, -456f), transform.Center);
        Assert.Equal(new Vector2(800f, 600f), transform.ViewportSize);
        Assert.Equal(2f, transform.BlocksPerPixel);
    }

    [Fact]
    public void Wheel_changes_zoom_only_while_the_map_is_hovered()
    {
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(800f, 600f));

        var outside = _controller.HandleWheel(transform, new Vector2(100f), 1f, isHovered: false, 0.25f, 32f);
        var inside = _controller.HandleWheel(transform, new Vector2(100f), 1f, isHovered: true, 0.25f, 32f);

        Assert.Equal(TravelMapUiCommandKind.None, outside.Kind);
        Assert.Equal(TravelMapUiCommandKind.Zoom, inside.Kind);
    }

    [Fact]
    public void Wheel_uses_sqrt2_steps_and_keeps_world_under_mouse_anchored()
    {
        var pointer = new Vector2(137f, 211f);
        var transform = new MapTransform(new Vector2(1000f, -400f), 2f, new Vector2(800f, 600f));
        var worldBefore = transform.ScreenToWorld(pointer);

        var command = _controller.HandleWheel(transform, pointer, 1f, isHovered: true, 0.25f, 32f);

        var zoomed = Assert.IsType<MapTransform>(command.Transform);
        Assert.Equal(2f / MathF.Sqrt(2f), zoomed.BlocksPerPixel, 5);
        Assert.Equal(worldBefore.X, zoomed.ScreenToWorld(pointer).X, 4);
        Assert.Equal(worldBefore.Y, zoomed.ScreenToWorld(pointer).Y, 4);
    }

    [Fact]
    public void Wheel_zoom_is_clamped_to_the_view_range()
    {
        var transform = new MapTransform(Vector2.Zero, 0.25f, new Vector2(800f, 600f));

        var command = _controller.HandleWheel(transform, Vector2.Zero, 10f, true, 0.25f, 32f);

        Assert.Equal(0.25f, Assert.IsType<MapTransform>(command.Transform).BlocksPerPixel);
    }

    [Fact]
    public void Left_drag_pans_in_the_opposite_world_direction()
    {
        var transform = new MapTransform(new Vector2(10f, 20f), 2f, new Vector2(800f, 600f));

        var command = _controller.HandlePan(transform, new Vector2(12f, -3f), isDragging: true);

        Assert.Equal(TravelMapUiCommandKind.Pan, command.Kind);
        Assert.Equal(new Vector2(-14f, 26f), Assert.IsType<MapTransform>(command.Transform).Center);
    }

    [Fact]
    public void Repeated_minimap_zoom_and_pan_preserve_pointer_anchor_and_world_round_trips()
    {
        var pointer = new Vector2(37f, 151f);
        var point = new Vector2(-95.25f, 110.75f);
        var transform = new MapTransform(new Vector2(-32f, 48f), 2f, new Vector2(192f));
        var scales = new[] { 2f, 0.5f, 0.35355335f, 0.25f, 1f, 8f, 2f };
        var wheelSteps = new[] { 0f, 4f, 1f, 1f, -4f, -6f, 4f };
        var panDeltas = new[]
        {
            new Vector2(13f, -7f),
            new Vector2(-5f, 11f),
        };

        for (var index = 0; index < scales.Length; index++)
        {
            if (wheelSteps[index] != 0f)
            {
                var worldUnderPointer = transform.ScreenToWorld(pointer);
                var zoom = _controller.HandleWheel(
                    transform,
                    pointer,
                    wheelSteps[index],
                    isHovered: true,
                    minimumBlocksPerPixel: 0.25f,
                    maximumBlocksPerPixel: 8f);
                transform = Assert.IsType<MapTransform>(zoom.Transform);

                Assert.Equal(worldUnderPointer.X, transform.ScreenToWorld(pointer).X, 3);
                Assert.Equal(worldUnderPointer.Y, transform.ScreenToWorld(pointer).Y, 3);
            }

            Assert.Equal(scales[index], transform.BlocksPerPixel, 5);
            foreach (var panDelta in panDeltas)
            {
                var pan = _controller.HandlePan(transform, panDelta, isDragging: true);
                transform = Assert.IsType<MapTransform>(pan.Transform);
                var roundTrip = transform.ScreenToWorld(transform.WorldToScreen(point));

                Assert.InRange(roundTrip.X, point.X - 0.001f, point.X + 0.001f);
                Assert.InRange(roundTrip.Y, point.Y - 0.001f, point.Y + 0.001f);
            }
        }
    }

    [Fact]
    public void Right_click_prefers_a_waypoint_hit_and_returns_waypoint_actions()
    {
        var waypoint = new Waypoint(Guid.NewGuid(), "Camp", new Vector3(10f, 70f, 20f), DateTimeOffset.UtcNow);

        var command = _controller.HandleRightClick(new Vector2(10f, 20f), isExplored: false, waypoint);

        Assert.Equal(TravelMapUiCommandKind.ShowWaypointMenu, command.Kind);
        Assert.Equal(waypoint.Id, command.ContextMenu!.WaypointId);
        Assert.Equal(
            [TravelMapContextAction.TeleportToWaypoint, TravelMapContextAction.RenameWaypoint,
                TravelMapContextAction.DeleteWaypoint, TravelMapContextAction.Cancel],
            command.ContextMenu.Actions);
    }

    [Fact]
    public void Right_click_explored_ground_returns_ground_actions()
    {
        var command = _controller.HandleRightClick(new Vector2(10f, 20f), isExplored: true, waypointHit: null);

        Assert.Equal(TravelMapUiCommandKind.ShowGroundMenu, command.Kind);
        Assert.Equal(
            [TravelMapContextAction.TeleportNearby, TravelMapContextAction.AddWaypoint, TravelMapContextAction.Cancel],
            command.ContextMenu!.Actions);
    }

    [Fact]
    public void Right_click_unexplored_ground_only_returns_unexplored_message()
    {
        var command = _controller.HandleRightClick(new Vector2(10f, 20f), isExplored: false, waypointHit: null);

        Assert.Equal(TravelMapUiCommandKind.ShowUnexploredMessage, command.Kind);
        Assert.Null(command.ContextMenu);
    }

    [Fact]
    public void Accepted_minimap_sizes_are_exactly_the_design_values()
    {
        Assert.Equal([160, 192, 256, 320, 384], TravelMapSettings.SupportedMiniMapSizes);
        Assert.All(TravelMapSettings.SupportedMiniMapSizes, size => Assert.True(TravelMapSettings.IsSupportedMiniMapSize(size)));
        Assert.False(TravelMapSettings.IsSupportedMiniMapSize(224));
        Assert.False(TravelMapSettings.IsSupportedMiniMapSize(512));
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")]
    [InlineData(null)]
    public void Unsupported_or_missing_hotkeys_normalize_to_M(string? hotkey)
    {
        var settings = new TravelMapSettings { LargeMapHotkey = hotkey! };

        settings.Normalize();

        Assert.Equal("M", settings.LargeMapHotkey);
    }

    private static void AssertFiniteAndNonnegative(Vector2 position)
    {
        Assert.True(float.IsFinite(position.X));
        Assert.True(float.IsFinite(position.Y));
        Assert.True(position.X >= 0f);
        Assert.True(position.Y >= 0f);
    }
}

public sealed class TravelMapSettingsStoreTests
{
    [Fact]
    public void Future_schema_warning_is_emitted_only_once()
    {
        var gate = new TravelMapSettingsFutureSchemaWarningGate();
        var result = new TravelMapSettingsLoadResult(
            new TravelMapSettings(),
            TravelMapSettingsLoadOutcome.UnsupportedFutureSchemaReadOnly,
            IsReadOnly: true);
        var messages = new List<string>();

        gate.NotifyIfNeeded(result, messages.Add);
        gate.NotifyIfNeeded(result, messages.Add);

        Assert.Single(messages);
    }

    [Fact]
    public async Task Missing_settings_create_schema_three_defaults()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.EndsWith("settings.json", store.SettingsPath, StringComparison.Ordinal);
        Assert.Equal(TravelMapSettingsLoadOutcome.Created, result.Outcome);
        Assert.False(result.IsReadOnly);
        Assert.Equal(160, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("ShowCreatureMarkers").GetBoolean());
        Assert.Equal(5f, document.RootElement.GetProperty("CreatureMarkerSize").GetSingle());
        Assert.Equal(
            "NorthUp",
            document.RootElement.GetProperty("MiniMapOrientation").GetString());
        Assert.Equal(160, document.RootElement.GetProperty("MiniMapSize").GetInt32());
    }

    [Theory]
    [InlineData("NorthUp", MiniMapOrientation.NorthUp)]
    [InlineData("HeadingUp", MiniMapOrientation.HeadingUp)]
    [InlineData("Unknown", MiniMapOrientation.NorthUp)]
    public async Task Current_schema_loads_known_orientation_and_normalizes_unknown_values(
        string persisted,
        MiniMapOrientation expected)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            $"{{\"schemaVersion\":3,\"MiniMapOrientation\":\"{persisted}\"}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(expected, result.Settings.MiniMapOrientation);
    }

    [Theory]
    [InlineData(160, 160)]
    [InlineData(192, 192)]
    [InlineData(256, 256)]
    [InlineData(320, 320)]
    [InlineData(384, 192)]
    public async Task Schema_one_values_are_migrated_once_and_saved_as_schema_three(
        int persistedSize,
        int expectedSize)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            $"{{\"schemaVersion\":1,\"MiniMapSize\":{persistedSize}}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.MigratedPreviousSchema, result.Outcome);
        Assert.False(result.IsReadOnly);
        Assert.Equal(expectedSize, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(expectedSize, document.RootElement.GetProperty("MiniMapSize").GetInt32());
    }

    [Fact]
    public async Task Schema_two_size_384_is_migrated_without_the_schema_one_size_rewrite()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"schemaVersion\":2,\"MiniMapSize\":384}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.MigratedPreviousSchema, result.Outcome);
        Assert.False(result.IsReadOnly);
        Assert.Equal(384, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(384, document.RootElement.GetProperty("MiniMapSize").GetInt32());
    }

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
            $"{{\"schemaVersion\":3,\"MiniMapSize\":{size}}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(size, result.Settings.MiniMapSize);
    }

    [Theory]
    [InlineData(1, TravelMapSettingsLoadOutcome.MigratedPreviousSchema)]
    [InlineData(2, TravelMapSettingsLoadOutcome.MigratedPreviousSchema)]
    [InlineData(3, TravelMapSettingsLoadOutcome.Loaded)]
    public async Task Explicit_schemas_use_the_document_default_when_minimap_size_is_missing(
        int schemaVersion,
        TravelMapSettingsLoadOutcome expectedOutcome)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            $"{{\"schemaVersion\":{schemaVersion},\"futureHint\":{{\"x\":1}}}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.False(result.IsReadOnly);
        Assert.Equal(160, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(160, document.RootElement.GetProperty("MiniMapSize").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("futureHint").GetProperty("x").GetInt32());
    }

    [Theory]
    [InlineData("3.000e0")]
    [InlineData("3000e-3")]
    public async Task Mathematically_exact_schema_three_numbers_are_current(string schemaNumber)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            $"{{\"schemaVersion\":{schemaNumber},\"MiniMapSize\":384,\"futureHint\":true}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.Loaded, result.Outcome);
        Assert.False(result.IsReadOnly);
        Assert.Equal(384, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("futureHint").GetBoolean());
    }

    [Theory]
    [InlineData("3.0000000000000000000000000000000000000001")]
    [InlineData("3e1")]
    [InlineData("1e1000000000")]
    public async Task Any_schema_number_greater_than_three_is_future_and_preserved_byte_for_byte(
        string schemaNumber)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        var original = System.Text.Encoding.UTF8.GetBytes(
            $"{{\r\n  \"schemaVersion\": {schemaNumber},\r\n  \"future\": null\r\n}}");
        await File.WriteAllBytesAsync(
            store.SettingsPath,
            original,
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        await store.SaveAsync(result.Settings, TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.UnsupportedFutureSchemaReadOnly, result.Outcome);
        Assert.True(result.IsReadOnly);
        Assert.True(store.IsReadOnly);
        Assert.Equal(160, result.Settings.MiniMapSize);
        Assert.Equal(
            original,
            await File.ReadAllBytesAsync(store.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("2.9999999999999999999999999999999999999999")]
    [InlineData("1e-1000000000")]
    [InlineData("-3")]
    [InlineData("0")]
    public async Task Numeric_schema_values_other_than_exact_one_two_three_or_greater_than_three_are_invalid(
        string schemaNumber)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            $"{{\"schemaVersion\":{schemaNumber}}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.CorruptIsolated, result.Outcome);
        Assert.False(result.IsReadOnly);
        Assert.Equal(160, result.Settings.MiniMapSize);
        Assert.True(File.Exists(store.SettingsPath + ".corrupt"));
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Future_schema_is_preserved_byte_for_byte_and_all_saves_are_read_only()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        const string future = "{\r\n  \"schemaVersion\": 4,\r\n  \"future\": null\r\n}";
        await File.WriteAllTextAsync(store.SettingsPath, future, TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(160, result.Settings.MiniMapSize);
        result.Settings.MiniMapSize = 384;
        await store.SaveAsync(result.Settings, TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.UnsupportedFutureSchemaReadOnly, result.Outcome);
        Assert.True(result.IsReadOnly);
        Assert.True(store.IsReadOnly);
        Assert.Equal(future, await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Previous_unversioned_filename_is_migrated_without_deleting_source()
    {
        using var directory = new UiTemporaryDirectory();
        var previous = Path.Combine(directory.Path, "travel-map-settings.json");
        await File.WriteAllTextAsync(
            previous,
            "{\"MiniMapSize\":384,\"ShowCoordinates\":false}",
            TestContext.Current.CancellationToken);
        var store = new TravelMapSettingsStore(directory.Path);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.MigratedPreviousPath, result.Outcome);
        Assert.Equal(384, result.Settings.MiniMapSize);
        Assert.False(result.Settings.ShowCoordinates);
        Assert.True(File.Exists(previous));
        Assert.True(File.Exists(store.SettingsPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Corrupt_previous_filename_is_isolated_and_reports_corruption_instead_of_migration()
    {
        using var directory = new UiTemporaryDirectory();
        var previous = Path.Combine(directory.Path, "travel-map-settings.json");
        await File.WriteAllTextAsync(
            previous,
            "{not json",
            TestContext.Current.CancellationToken);
        var store = new TravelMapSettingsStore(directory.Path);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.CorruptIsolated, result.Outcome);
        Assert.True(File.Exists(previous + ".corrupt"));
        Assert.True(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task Previous_schema_preserves_unknown_fields_when_migrating()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"schemaVersion\":1,\"MiniMapSize\":203,\"futureHint\":{\"x\":1}}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.MigratedPreviousSchema, result.Outcome);
        Assert.Equal(192, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("futureHint").GetProperty("x").GetInt32());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":null}")]
    [InlineData("{not json")]
    public async Task Null_invalid_or_malformed_documents_are_isolated(string json)
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        await File.WriteAllTextAsync(store.SettingsPath, json, TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapSettingsLoadOutcome.CorruptIsolated, result.Outcome);
        Assert.True(File.Exists(store.SettingsPath + ".corrupt"));
    }

    [Fact]
    public async Task Atomic_save_failure_preserves_the_last_complete_settings_document()
    {
        using var directory = new UiTemporaryDirectory();
        var fail = false;
        var store = new TravelMapSettingsStore(
            directory.Path,
            legacyPath: null,
            (path, write, token) => fail
                ? Task.FromException(new IOException("disk full"))
                : AtomicFile.ReplaceAsync(path, write, token));
        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(store.SettingsPath, TestContext.Current.CancellationToken);
        settings.MiniMapSize = 384;
        fail = true;

        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllBytesAsync(store.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task First_load_migrates_only_the_two_legacy_flags_and_preserves_the_old_file()
    {
        using var directory = new UiTemporaryDirectory();
        var legacyPath = Path.Combine(directory.Path, "GPSSetting.xml");
        await File.WriteAllTextAsync(
            legacyPath,
            "{\"isDisplayMap\":false,\"isAllowTelePortRequest\":false,\"MiniMapSize\":384,\"ShowCoordinates\":false}",
            TestContext.Current.CancellationToken);
        var store = new TravelMapSettingsStore(directory.Path);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(settings.IsMiniMapVisible);
        Assert.False(settings.AcceptTeleportInvitations);
        Assert.True(settings.ShowCoordinates);
        Assert.Equal(160, settings.MiniMapSize);
        Assert.True(File.Exists(legacyPath));
        Assert.True(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task Legacy_path_can_remain_at_the_original_app_root()
    {
        using var directory = new UiTemporaryDirectory();
        var settingsDirectory = Path.Combine(directory.Path, "SurvivalcraftTravelMap");
        var legacyPath = Path.Combine(directory.Path, "GPSSetting.xml");
        await File.WriteAllTextAsync(
            legacyPath,
            "{\"isDisplayMap\":false}",
            TestContext.Current.CancellationToken);
        var store = new TravelMapSettingsStore(settingsDirectory, legacyPath);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(settings.IsMiniMapVisible);
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public async Task Existing_new_settings_are_normalized_and_corrected_values_are_saved()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"MiniMapSize\":384,\"MiniMapBlocksPerPixel\":99,\"LargeMapBlocksPerPixel\":0.01}",
            TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.MigratedUnversioned, result.Outcome);
        Assert.Equal(384, result.Settings.MiniMapSize);
        Assert.Equal(8f, result.Settings.MiniMapBlocksPerPixel);
        Assert.Equal(0.25f, result.Settings.LargeMapBlocksPerPixel);
        Assert.Equal(384, persisted.RootElement.GetProperty("MiniMapSize").GetInt32());
        Assert.Equal(3, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Corrupt_new_settings_are_isolated_and_replaced_with_safe_defaults()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.SettingsPath, "{not json", TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            store.SettingsPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(TravelMapSettingsLoadOutcome.CorruptIsolated, result.Outcome);
        Assert.Equal(160, result.Settings.MiniMapSize);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(File.Exists(store.SettingsPath + ".corrupt"));
        Assert.True(File.Exists(store.SettingsPath));
    }
}

public sealed class TravelMapRenderModelTests
{
    [Fact]
    public void Overlay_state_preserves_five_parameter_constructor_and_deconstruct_while_exposing_marker_color()
    {
        var constructorArities = typeof(MapOverlayState)
            .GetConstructors()
            .Select(constructor => constructor.GetParameters().Length)
            .Order()
            .ToArray();
        var deconstructArities = typeof(MapOverlayState)
            .GetMethods()
            .Where(method => method.Name == nameof(MapOverlayState.Deconstruct))
            .Select(method => method.GetParameters().Length)
            .Order()
            .ToArray();

        Assert.Equal([5, 6], constructorArities);
        Assert.Equal([5, 6], deconstructArities);

        var position = new Vector3(1f, 2f, 3f);
        IReadOnlyList<Waypoint> waypoints = [];
        var state = new MapOverlayState(position, 0.75f, 32f, waypoints, ShowCoordinates: true);
        var (actualPosition, heading, arrowSize, actualWaypoints, showCoordinates) = state;

        Assert.Equal(position, actualPosition);
        Assert.Equal(0.75f, heading);
        Assert.Equal(32f, arrowSize);
        Assert.Same(waypoints, actualWaypoints);
        Assert.True(showCoordinates);
        Assert.Null(state.PlayerMarkerColor);
    }

    [Fact]
    public void Renderer_emits_terrain_only_for_pixels_already_marked_explored()
    {
        var source = new RecordingPixelSource((x, z) =>
            x == 0 && z == 0 ? new Rgba32(100, 80, 60, 255) : null);
        var sink = new RecordingRenderSink();

        TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 1f, new Vector2(3f, 3f)),
            brightness: 1f,
            sink);

        var terrain = Assert.Single(sink.Terrain);
        Assert.Equal((0, 0), (terrain.WorldX, terrain.WorldZ));
        Assert.True(source.Requested.Count > 1);
    }

    [Fact]
    public void Explored_pixels_do_not_emit_cyan_boundary_artifacts()
    {
        var source = new RecordingPixelSource((x, z) =>
            x == 0 && z == 0 ? new Rgba32(100, 80, 60, 255) : null);
        var sink = new RecordingRenderSink();

        TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 1f, new Vector2(3f, 3f)),
            brightness: 1f,
            sink);

        Assert.Empty(sink.Boundaries);
    }

    [Fact]
    public void Day_night_brightness_tints_terrain_color_only()
    {
        var source = new RecordingPixelSource((x, z) => new Rgba32(100, 80, 60, 255));
        var sink = new RecordingRenderSink();

        TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 1f, Vector2.One),
            brightness: 0.5f,
            sink);
        TravelMapRenderModel.RenderOverlays(
            new MapOverlayState(
                new Vector3(1f, 64f, 2f),
                0f,
                32f,
                [new Waypoint(Guid.NewGuid(), "Camp", new Vector3(2f, 65f, 3f), DateTimeOffset.UtcNow)],
                ShowCoordinates: true),
            sink);

        Assert.All(sink.Terrain, terrain => Assert.Equal(new Rgba32(50, 40, 30, 255), terrain.Color));
        Assert.Equal(TravelMapPalette.SurveyCyan, sink.PlayerColor);
        Assert.Equal(TravelMapPalette.HazardAmber, sink.WaypointColor);
        Assert.Equal(TravelMapPalette.SnowText, sink.LabelColor);
    }

    [Fact]
    public void Overlay_marker_style_can_be_overridden_for_the_minimap_without_changing_large_map_defaults()
    {
        var largeMapSink = new RecordingRenderSink();
        var miniMapSink = new RecordingRenderSink();
        var pose = new Vector3(12f, 64f, -8f);

        TravelMapRenderModel.RenderOverlays(
            new MapOverlayState(pose, 0f, 32f, [], ShowCoordinates: false),
            largeMapSink);
        TravelMapRenderModel.RenderOverlays(
            new MapOverlayState(
                pose,
                0f,
                TravelMapRenderModel.MiniMapPlayerArrowSize(192),
                [],
                ShowCoordinates: false,
                TravelMapPalette.MiniMapPlayer),
            miniMapSink);

        Assert.Equal(32f, largeMapSink.PlayerSize);
        Assert.Equal(TravelMapPalette.SurveyCyan, largeMapSink.PlayerColor);
        Assert.Equal(18f, miniMapSink.PlayerSize);
        Assert.Equal(TravelMapPalette.MiniMapPlayer, miniMapSink.PlayerColor);
    }

    [Theory]
    [InlineData(160, 15f)]
    [InlineData(192, 18f)]
    [InlineData(384, 24f)]
    public void Shared_minimap_arrow_sizing_rule_is_stable(int mapSize, float expected)
    {
        Assert.Equal(expected, TravelMapRenderModel.MiniMapPlayerArrowSize(mapSize));
    }

    [Fact]
    public void Coordinates_and_player_arrow_size_follow_the_design()
    {
        Assert.Equal("X: 123  Y: 64  Z: -456", TravelMapRenderModel.FormatCoordinates(new Vector3(123.9f, 64.2f, -456.1f)));
        Assert.Equal(24f, TravelMapRenderModel.PlayerArrowSize(160));
        Assert.Equal(32f, TravelMapRenderModel.PlayerArrowSize(256));
        Assert.Equal(40f, TravelMapRenderModel.PlayerArrowSize(384));
    }
}

public sealed class TravelMapTeleportRouterTests
{
    [Fact]
    public async Task Local_safe_entry_failure_remains_distinct_from_missing_multiplayer_protocol()
    {
        var router = new TravelMapTeleportRouter(
            new TravelMapRuntimeContext(TravelMapWorkType.Local, IsMainPlayer: true, HasUi: true),
            (_, _) => Task.FromResult(TravelMapTeleportDispatchResult.LocalFailed),
            authoritativeHostRequest: null,
            clientCommand: null);

        var result = await router.RequestAsync(new Vector3(1f, 2f, 3f), TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapTeleportDispatchResult.LocalFailed, result);
    }

    [Fact]
    public async Task Client_without_Task9_command_callback_reports_unavailable_without_local_movement()
    {
        var localCalls = 0;
        var router = new TravelMapTeleportRouter(
            new TravelMapRuntimeContext(TravelMapWorkType.Client, IsMainPlayer: true, HasUi: true),
            (_, _) =>
            {
                localCalls++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            authoritativeHostRequest: null,
            clientCommand: null);

        var result = await router.RequestAsync(new Vector3(1f, 2f, 3f), TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapTeleportDispatchResult.Unavailable, result);
        Assert.Equal(0, localCalls);
    }

    [Fact]
    public async Task Client_with_callback_only_emits_a_Task9_command_request()
    {
        var localCalls = 0;
        TravelMapClientTravelCommand? emitted = null;
        var router = new TravelMapTeleportRouter(
            new TravelMapRuntimeContext(TravelMapWorkType.Client, IsMainPlayer: true, HasUi: true),
            (_, _) =>
            {
                localCalls++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            authoritativeHostRequest: null,
            command => emitted = command);

        var result = await router.RequestAsync(new Vector3(1f, 2f, 3f), TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapTeleportDispatchResult.CommandQueued, result);
        Assert.Equal(new Vector3(1f, 2f, 3f), emitted!.Target);
        Assert.Equal(0, localCalls);
    }

    [Fact]
    public async Task Integrated_server_host_uses_the_authoritative_server_path_not_the_local_writer()
    {
        var localCalls = 0;
        TravelMapClientTravelCommand? authoritativeCommand = null;
        var router = new TravelMapTeleportRouter(
            new TravelMapRuntimeContext(TravelMapWorkType.Server, IsMainPlayer: true, HasUi: true),
            (_, _) =>
            {
                localCalls++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            (command, _) =>
            {
                authoritativeCommand = command;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            clientCommand: null);

        var result = await router.RequestSurfaceAsync(
            new Vector3(12f, 99f, -34f),
            TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapTeleportDispatchResult.LocalRequested, result);
        Assert.Equal(TravelMapClientTravelMode.Surface, authoritativeCommand!.Mode);
        Assert.Equal(new Vector3(12f, 99f, -34f), authoritativeCommand.Target);
        Assert.Equal(0, localCalls);
    }
}

internal sealed class RecordingPixelSource(Func<int, int, Rgba32?> pixel) : IExploredMapPixelSource
{
    private readonly Func<int, int, Rgba32?> _pixel = pixel;
    public List<(int X, int Z)> Requested { get; } = [];

    public IExploredMapReadSession BeginReadSession() => new Session(this);

    private sealed class Session(RecordingPixelSource source) : IExploredMapReadSession
    {
        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
        {
            source.Requested.Add((worldX, worldZ));
            var result = source._pixel(worldX, worldZ);
            color = result ?? default;
            return result.HasValue;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class RecordingRenderSink : ITravelMapRenderSink
{
    public List<MapTerrainCell> Terrain { get; } = [];

    public List<MapBoundaryEdge> Boundaries { get; } = [];

    public Rgba32 PlayerColor { get; private set; }

    public float PlayerSize { get; private set; }

    public Rgba32 WaypointColor { get; private set; }

    public Rgba32 LabelColor { get; private set; }

    public void TerrainCell(MapTerrainCell cell) => Terrain.Add(cell);

    public void ExplorationBoundary(MapBoundaryEdge edge) => Boundaries.Add(edge);

    public void Player(Vector3 position, float heading, float size, Rgba32 color)
    {
        PlayerSize = size;
        PlayerColor = color;
    }

    public void Waypoint(Waypoint waypoint, Rgba32 color) => WaypointColor = color;

    public void Label(string text, Vector3 worldPosition, Rgba32 color) => LabelColor = color;
}

internal sealed class UiTemporaryDirectory : IDisposable
{
    public UiTemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sctm-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
