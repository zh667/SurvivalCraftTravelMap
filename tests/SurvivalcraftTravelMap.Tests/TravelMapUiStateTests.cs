using System.Numerics;
using System.Text.Json;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using SurvivalcraftTravelMap.Waypoints;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapUiStateTests
{
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
    public async Task Canonical_settings_path_uses_schema_one_envelope()
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
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Future_schema_is_preserved_byte_for_byte_and_all_saves_are_read_only()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        const string future = "{\"schemaVersion\":999999999999999999999999999999,\"future\":null}";
        await File.WriteAllTextAsync(store.SettingsPath, future, TestContext.Current.CancellationToken);

        var result = await store.LoadWithOutcomeAsync(TestContext.Current.CancellationToken);
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
    public async Task Current_schema_preserves_unknown_fields_when_normalizing()
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

        Assert.Equal(TravelMapSettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(192, result.Settings.MiniMapSize);
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
        Assert.Equal(256, settings.MiniMapSize);
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
            "{\"MiniMapSize\":203,\"MiniMapBlocksPerPixel\":99,\"LargeMapBlocksPerPixel\":0.01}",
            TestContext.Current.CancellationToken);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);
        var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));

        Assert.Equal(192, settings.MiniMapSize);
        Assert.Equal(8f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(0.25f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(192, persisted.RootElement.GetProperty("MiniMapSize").GetInt32());
    }

    [Fact]
    public async Task Corrupt_new_settings_are_isolated_and_replaced_with_safe_defaults()
    {
        using var directory = new UiTemporaryDirectory();
        var store = new TravelMapSettingsStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.SettingsPath, "{not json", TestContext.Current.CancellationToken);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(256, settings.MiniMapSize);
        Assert.True(File.Exists(store.SettingsPath + ".corrupt"));
        Assert.True(File.Exists(store.SettingsPath));
    }
}

public sealed class TravelMapRenderModelTests
{
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
    public void Isolated_explored_pixel_emits_four_survey_boundary_edges()
    {
        var source = new RecordingPixelSource((x, z) =>
            x == 0 && z == 0 ? new Rgba32(100, 80, 60, 255) : null);
        var sink = new RecordingRenderSink();

        TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 1f, new Vector2(3f, 3f)),
            brightness: 1f,
            sink);

        Assert.Equal(4, sink.Boundaries.Count);
        Assert.All(sink.Boundaries, boundary => Assert.Equal(TravelMapPalette.SurveyCyan, boundary.Color));
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
            TravelMapWorkType.Local,
            (_, _) => Task.FromResult(TravelMapTeleportDispatchResult.LocalFailed),
            clientCommand: null);

        var result = await router.RequestAsync(new Vector3(1f, 2f, 3f), TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapTeleportDispatchResult.LocalFailed, result);
    }

    [Fact]
    public async Task Client_without_Task9_command_callback_reports_unavailable_without_local_movement()
    {
        var localCalls = 0;
        var router = new TravelMapTeleportRouter(
            TravelMapWorkType.Client,
            (_, _) =>
            {
                localCalls++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
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
            TravelMapWorkType.Client,
            (_, _) =>
            {
                localCalls++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            command => emitted = command);

        var result = await router.RequestAsync(new Vector3(1f, 2f, 3f), TestContext.Current.CancellationToken);

        Assert.Equal(TravelMapTeleportDispatchResult.CommandQueued, result);
        Assert.Equal(new Vector3(1f, 2f, 3f), emitted!.Target);
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

    public Rgba32 WaypointColor { get; private set; }

    public Rgba32 LabelColor { get; private set; }

    public void TerrainCell(MapTerrainCell cell) => Terrain.Add(cell);

    public void ExplorationBoundary(MapBoundaryEdge edge) => Boundaries.Add(edge);

    public void Player(Vector3 position, float heading, float size, Rgba32 color) => PlayerColor = color;

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
