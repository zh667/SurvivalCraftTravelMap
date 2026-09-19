using System.Reflection;
using System.Runtime.CompilerServices;
using Engine;
using Engine.Input;
using Engine.Media;
using Game;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

[CollectionDefinition("Mouse input", DisableParallelization = true)]
public sealed class MouseInputCollection;

[Collection("Mouse input")]
public sealed class MiniMapCombatInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_combat_clicks_at_stale_minimap_position_do_not_open_or_consume_input(bool hasDeath)
    {
        using var fixture = new InputFixture(cursorVisible: false, hasDeath);
        fixture.SetLeftClick();
        for (var frame = 0; frame < 30; frame++)
        {
            fixture.Map.Update();
            Assert.Equal(0, fixture.Opened);
            Assert.Equal(0, fixture.LocatedDeath);
            Assert.True(fixture.Input.IsMouseButtonDownOnce(MouseButton.Left));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Visible_cursor_still_opens_map_or_locates_death(bool hasDeath)
    {
        using var fixture = new InputFixture(cursorVisible: true, hasDeath);
        fixture.SetLeftClick();
        fixture.Map.Update();
        Assert.Equal(hasDeath ? 0 : 1, fixture.Opened);
        Assert.Equal(hasDeath ? 1 : 0, fixture.LocatedDeath);
        Assert.False(fixture.Input.IsMouseButtonDownOnce(MouseButton.Left));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wheel_zoom_requires_visible_cursor_and_preserves_combat_hotbar_input(bool cursorVisible)
    {
        using var fixture = new InputFixture(cursorVisible);
        var zoom = fixture.Settings.MiniMapBlocksPerPixel;
        fixture.SetWheel(120);
        fixture.Map.Update();
        Assert.Equal(cursorVisible, fixture.Settings.MiniMapBlocksPerPixel != zoom);
        Assert.Equal(cursorVisible ? 0 : 120, fixture.Input.MouseWheelMovement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Engine_touch_tap_opens_map_without_mouse_cursor(bool hasDeath)
    {
        using var fixture = new InputFixture(cursorVisible: false, hasDeath);
        fixture.Input.Tap = new Vector2(80f);
        fixture.Map.Update();
        Assert.Equal(hasDeath ? 0 : 1, fixture.Opened);
        Assert.Equal(hasDeath ? 1 : 0, fixture.LocatedDeath);
        Assert.Null(fixture.Input.Tap);
    }

    [Fact]
    public void Blocked_map_does_not_consume_mouse_or_touch()
    {
        using var fixture = new InputFixture(cursorVisible: true, blocked: true);
        fixture.SetLeftClick();
        fixture.Input.Tap = new Vector2(80f);
        fixture.Map.Update();
        Assert.Equal(0, fixture.Opened);
        Assert.NotNull(fixture.Input.Tap);
        Assert.True(fixture.Input.IsMouseButtonDownOnce(MouseButton.Left));
    }

    private sealed class InputFixture : IDisposable
    {
        private readonly InputTestDirectory _directory = new();
        private readonly FieldInfo _buttonsField = typeof(Mouse).GetField(
            "m_mouseButtonsDownOnceArray", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly PropertyInfo _wheelProperty = typeof(Mouse).GetProperty(nameof(Mouse.MouseWheelMovement))!;
        private readonly object? _oldButtons;
        private readonly object? _oldWheel;

        public TravelMapSettings Settings { get; } = new() { MiniMapSize = 160 };
        public WidgetInput Input { get; }
        public MiniMapRenderer Map { get; }
        public int Opened { get; private set; }
        public int LocatedDeath { get; private set; }

        public InputFixture(bool cursorVisible, bool hasDeath = false, bool blocked = false)
        {
            _oldButtons = _buttonsField.GetValue(null);
            _oldWheel = _wheelProperty.GetValue(null);
            _buttonsField.SetValue(null, new bool[Enum.GetValues<MouseButton>().Length]);
            SetWheel(0);
            Input = new WidgetInput(WidgetInputDevice.Mouse)
            {
                UseSoftMouseCursor = true,
                IsMouseCursorVisible = cursorVisible,
                m_softMouseCursorPosition = new Vector2(80f),
            };
            Map = new MiniMapRenderer(
                new EmptyPixelSource(), Settings, new TravelMapSettingsStore(_directory.Path),
                () => new PlayerMapPose(new System.Numerics.Vector3(0f, 64f, 0f), 0f),
                () => [], () => [],
                () => hasDeath ? new DeathMapMarker(new System.Numerics.Vector3(0f, 64f, 0f), 1d, "test") : null,
                () => 1f, () => blocked, () => Opened++, _ => { }, _ => LocatedDeath++,
                (BitmapFont)RuntimeHelpers.GetUninitializedObject(typeof(BitmapFont)))
            {
                WidgetsHierarchyInput = Input,
            };
            Map.Measure(new Vector2(160f, 202f));
            Map.Arrange(Vector2.Zero, new Vector2(160f, 202f));
            Map.Transform = Map.Transform with { ViewportSize = new System.Numerics.Vector2(160f) };
        }

        public void SetLeftClick() => ((bool[])_buttonsField.GetValue(null)!)[(int)MouseButton.Left] = true;
        public void SetWheel(int amount) => _wheelProperty.SetValue(null, amount);

        public void Dispose()
        {
            Map.Dispose();
            _buttonsField.SetValue(null, _oldButtons);
            _wheelProperty.SetValue(null, _oldWheel);
            _directory.Dispose();
        }
    }

    private sealed class EmptyPixelSource : IExploredMapPixelSource, IExploredMapReadSession
    {
        public IExploredMapReadSession BeginReadSession() => this;
        public bool TryGetExploredPixel(int x, int z, out Rgba32 color)
        {
            color = default;
            return false;
        }
        public void Dispose() { }
    }

    private sealed class InputTestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SurvivalcraftTravelMap.InputTests", Guid.NewGuid().ToString("N"));

        public InputTestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
