using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Engine;
using Engine.Input;
using Game;
using HarmonyLib;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.UI;

namespace TravelMapLab;

// Isolated in-game checks for the travel map (plugin edition). Installed only into an isolated runtime by
// tools/isolated/run_isolated.py, never shipped. Mode in lab-mode.txt next to the exe:
//   "season": record a region in winter, leave until it unloads, switch to summer, compare the map's prediction with the
//             terrain the game actually produces on return; then the same from summer to winter. Frame times around each
//             season step are measured with the mini map and the large map on screen.
//   "touch":  a touch whose release never arrives sits in the engine's touch list; a real drag, pinch and long press on
//             the large map must still pan, zoom and open the teleport menu. Uses no type newer than 2.7.1, so the old
//             package can run it as the negative control.
public class Lab : ModLoader
{
    const string WorldName = "SctmLab";
    const float Winter = 0.625f;   // TemperatureCurve: -24
    const float Summer = 0.125f;   // 0
    static string mode = "season";
    static double began;
    static IEnumerator<bool>? script;
    static PlayerData data = null!;
    static readonly List<string> checks = [];
    static readonly Dictionary<string, object> metrics = [];
    static readonly List<string> notes = [];
    static double lastFrame;
    static List<double>? frameLog;

    public override void __ModInitialize()
    {
        new Harmony("local.sctmlab").PatchAll(typeof(Lab).Assembly);
        SettingsManager.FileAssociationEnabled = false;
        var m = Path.Combine(AppContext.BaseDirectory, "lab-mode.txt");
        if (File.Exists(m)) mode = File.ReadAllText(m).Trim();
    }

    public override void OnLoadingFinished(List<Action> actions) =>
        actions.Add(() => { began = Time.RealTime; Window.Frame += Frame; });

    static string Output => Path.Combine(AppContext.BaseDirectory, "lab-result.json");

    static void Finish(object result)
    {
        File.WriteAllText(Output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Window.Frame -= Frame;
        Window.Close();
    }

    static void Check(bool ok, string description)
    {
        if (!ok) throw new Exception("FAILED: " + description);
        checks.Add(description);
    }

    static void Frame()
    {
        try
        {
            var now = Time.RealTime;
            if (frameLog is not null && lastFrame > 0) frameLog.Add((now - lastFrame) * 1000.0);
            lastFrame = now;
            if (now - began > 900) throw new TimeoutException("lab timed out");
            script ??= (mode == "touch" ? Touch() : Season()).GetEnumerator();
            if (!script.MoveNext())
                Finish(new { passed = true, mode, checks, metrics, notes });
        }
        catch (Exception e)
        {
            Finish(new { passed = false, mode, checks, metrics, notes, error = e.ToString() });
        }
    }

    // ---- shared ----------------------------------------------------------------------------

    static ContainerWidget Games => ScreensManager.FindScreen<GameScreen>("Game").Children.Find<ContainerWidget>("GamesWidget");

    static IEnumerable<bool> Seconds(double seconds, Action? each = null)
    {
        var until = Time.RealTime + seconds;
        while (Time.RealTime < until) { each?.Invoke(); yield return true; }
    }

    static IEnumerable<bool> Until(Func<bool> done, double timeout, string what)
    {
        var until = Time.RealTime + timeout;
        while (!done())
        {
            if (Time.RealTime > until) throw new TimeoutException(what);
            yield return true;
        }
    }

    static IEnumerable<bool> EnterWorld(float timeOfYear)
    {
        while (ScreensManager.CurrentScreen is not MainMenuScreen) yield return true;
        SettingsManager.VisibilityRange = 64;
        SettingsManager.SoundsVolume = 0;
        SettingsManager.MusicVolume = 0;
        var world = WorldsManager.CreateWorld(new WorldSettings
        {
            Name = WorldName,
            Seed = "20261114",
            GameMode = GameMode.Creative,
            TerrainGenerationMode = TerrainGenerationMode.Continent,
            EnvironmentBehaviorMode = EnvironmentBehaviorMode.Living,
            AreWeatherEffectsEnabled = true,
            TimeOfDayMode = TimeOfDayMode.Day,
            AreSeasonsChanging = false,
            TimeOfYear = timeOfYear,
        });
        GameManager.LoadProject(world, Games);
        data = new PlayerData(GameManager.Project) { Name = "Lab", InputDevice = WidgetInputDevice.All };
        GameManager.Project.FindSubsystem<SubsystemPlayers>(true).AddPlayerData(data);
        ScreensManager.SwitchScreen("Game");
        foreach (var x in Until(() => data.IsReadyForPlaying, 120, "player ready")) yield return x;
        var l = data.ComponentPlayer.ComponentLocomotion;
        l.IsCreativeFlyEnabled = true;
        foreach (var x in Seconds(3)) yield return x;
    }

    static TravelMapComponent Map => data.ComponentPlayer.Entity.FindComponent<TravelMapComponent>(true);

    static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    static object? FieldOrNull(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);

    static void Shot(string name)
    {
        SettingsManager.ShowGuiInScreenshots = true;
        ScreenCaptureManager.Capture(1280, 720, name + ".png");
    }

    static void Teleport(Vector3 position)
    {
        var body = data.ComponentPlayer.ComponentBody;
        body.Position = position;
        body.Velocity = Vector3.Zero;
    }

    static Dialog OpenLargeMap(Vector2 world)
    {
        var dialog = Field<Dialog>(Map, "_largeMapDialog");
        if (!DialogsManager.Dialogs.Contains(dialog)) DialogsManager.ShowDialog(data.ComponentPlayer.GuiWidget, dialog);
        dialog.GetType().GetMethod("ResetToWorld")!.Invoke(dialog, [new System.Numerics.Vector2(world.X, world.Y)]);
        return dialog;
    }

    static void CloseLargeMap()
    {
        var dialog = Field<Dialog>(Map, "_largeMapDialog");
        if (DialogsManager.Dialogs.Contains(dialog)) DialogsManager.HideDialog(dialog);
    }

    static object Stats(List<double> frames)
    {
        if (frames.Count == 0) return new { count = 0 };
        var sorted = frames.OrderBy(v => v).ToArray();
        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        return new { count = sorted.Length, p50 = Math.Round(P(.5), 3), p99 = Math.Round(P(.99), 3), max = Math.Round(sorted[^1], 3) };
    }

    // ---- season ----------------------------------------------------------------------------

    static IEnumerable<bool> Season()
    {
        foreach (var x in EnterWorld(Winter)) yield return x;
        var info = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true);
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true);
        foreach (var x in Until(() => terrain.Terrain.SeasonTemperature == -24, 10, "winter season temperature")) yield return x;

        var spawn = data.ComponentPlayer.ComponentBody.Position;
        var home = new Point2((int)MathF.Floor(spawn.X), (int)MathF.Floor(spawn.Z));
        notes.Add($"spawn {home.X},{home.Y}");

        // Let the map record the neighbourhood in winter.
        foreach (var x in Until(() => RecordedCount(home, 40) > 0.9 * 81 * 81, 120, "winter recording around spawn")) yield return x;
        foreach (var x in Seconds(5)) yield return x;
        var region = RegionPixels(home, 40);
        notes.Add($"region pixels {region.Count}, land {region.Count(p => p.Kind == MapSeasonKind.Land)}, water {region.Count(p => p.Kind == MapSeasonKind.Water)}, fixed {region.Count(p => p.Kind == MapSeasonKind.Fixed)}, legacy {region.Count(p => p.Kind == MapSeasonKind.None)}");
        Check(region.Count(p => p.Kind is MapSeasonKind.Land or MapSeasonKind.Water) > region.Count / 4, "winter: most of the recorded neighbourhood is seasonal (land or shore water)");
        metrics["winterSnowObserved"] = region.Count(p => p.ObservedFrozen);
        Check(Agreement(region, MapSeason.Current, out var agreeHere) >= 0.97, $"winter, on site: prediction matches the terrain ({agreeHere})");

        foreach (var phase in new[] { (Summer, 0, "summer"), (Winter, -24, "winter") })
        {
            // Leave until the region is unloaded, step the season, look from afar, then go back.
            Teleport(new Vector3(home.X + 3000, 150, home.Y));
            foreach (var x in Until(() => terrain.Terrain.GetChunkAtCell(home.X, home.Y) == null, 120, "region unloaded")) yield return x;
            foreach (var x in Seconds(2)) yield return x;

            frameLog = [];
            foreach (var x in Seconds(3)) yield return x;
            var before = frameLog;
            frameLog = [];
            info.WorldSettings.TimeOfYear = phase.Item1;
            foreach (var x in Until(() => terrain.Terrain.SeasonTemperature == phase.Item2, 10, phase.Item3 + " season temperature")) yield return x;
            foreach (var x in Seconds(3)) yield return x;
            metrics[$"frames_minimap_{phase.Item3}"] = new { before = Stats(before), after = Stats(frameLog) };
            frameLog = null;
            Check(MapSeason.Current.Temperature == phase.Item2, $"{phase.Item3}: the map sees season temperature {phase.Item2}");

            var predicted = RegionPixels(home, 40);
            var predictedFrozen = predicted.Count(p => IsFrozenOnMap(p, MapSeason.Current));
            metrics[$"{phase.Item3}_predictedFrozen"] = predictedFrozen;

            // The large map over the region, from afar, with a season step while it is open.
            OpenLargeMap(new Vector2(home.X, home.Y));
            foreach (var x in Seconds(3)) yield return x;
            Shot($"largemap_from_afar_{phase.Item3}");
            yield return true;
            frameLog = [];
            info.WorldSettings.TimeOfYear = phase.Item1 == Summer ? Winter : Summer;
            foreach (var x in Seconds(3)) yield return x;
            var flipped = frameLog;
            frameLog = [];
            info.WorldSettings.TimeOfYear = phase.Item1;
            foreach (var x in Seconds(3)) yield return x;
            metrics[$"frames_largemap_{phase.Item3}"] = new { stepAway = Stats(flipped), stepBack = Stats(frameLog) };
            frameLog = null;
            Shot($"largemap_from_afar_{phase.Item3}_again");
            yield return true;
            CloseLargeMap();

            Teleport(new Vector3(home.X + 0.5f, 150, home.Y + 0.5f));
            foreach (var x in Until(() => ColumnsReady(terrain, home, 40), 120, "region reloaded")) yield return x;
            foreach (var x in Seconds(4)) yield return x;
            var agreement = Agreement(predicted, MapSeason.Current, out var detail);
            metrics[$"{phase.Item3}_agreement"] = detail;
            Check(agreement >= 0.95, $"{phase.Item3}: map predicted from afar matches the terrain on return ({detail})");
            OpenLargeMap(new Vector2(home.X, home.Y));
            foreach (var x in Seconds(3)) yield return x;
            Shot($"largemap_on_site_{phase.Item3}");
            yield return true;
            CloseLargeMap();
        }

        Check(metrics["summer_predictedFrozen"] is int s && metrics["winter_predictedFrozen"] is int w && w > s,
            "more of the region shows frozen in winter than in summer");
    }

    readonly record struct RegionPixel(int X, int Z, MapSeasonKind Kind, bool ObservedFrozen, MapTerrainPixel Pixel);

    static ExplorationTileStore Store => Field<ExplorationTileStore>(Map, "_tileStore");

    static int RecordedCount(Point2 center, int radius) => RegionPixels(center, radius).Count;

    static List<RegionPixel> RegionPixels(Point2 center, int radius)
    {
        var list = new List<RegionPixel>();
        var store = Store;
        for (var z = center.Y - radius; z <= center.Y + radius; z++)
        for (var x = center.X - radius; x <= center.X + radius; x++)
        {
            var c = TileCoordinate.FromWorld(x, z);
            if (!store.GetOrLoad(c.TileX, c.TileZ).TryGetTerrainPixel(c.LocalX, c.LocalZ, out var p)) continue;
            var s = p.Season;
            var kind = s.Land != 0 ? MapSeasonKind.Land : s.Water != 0 ? MapSeasonKind.Water : (s.IsSeasonal ? MapSeasonKind.None : MapSeasonKind.Fixed);
            list.Add(new RegionPixel(x, z, kind, s.LandFrozen != 0 || s.WaterFrozen != 0, p));
        }

        return list;
    }

    static bool IsFrozenOnMap(RegionPixel p, MapSeason season) =>
        SeasonalTerrainColor.Resolve(p.Pixel.Color, p.Pixel.Season, season) != p.Pixel.Color;

    static bool ColumnsReady(SubsystemTerrain terrain, Point2 center, int radius)
    {
        for (var z = center.Y - radius; z <= center.Y + radius; z += 16)
        for (var x = center.X - radius; x <= center.X + radius; x += 16)
            if (terrain.Terrain.GetChunkAtCell(x, z) is not { State: TerrainChunkState.Valid }) return false;
        return true;
    }

    static double Agreement(List<RegionPixel> pixels, MapSeason season, out string detail)
    {
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true).Terrain;
        int agree = 0, total = 0, mapOnly = 0, terrainOnly = 0, landTotal = 0, landAgree = 0, waterTotal = 0, waterAgree = 0;
        foreach (var p in pixels)
        {
            var top = terrain.GetTopHeight(p.X, p.Z);
            var content = terrain.GetCellContents(p.X, top, p.Z);
            var actual = content is 61 or 62;
            var shown = IsFrozenOnMap(p, season);
            total++;
            if (actual == shown) agree++;
            else if (shown) mapOnly++;
            else terrainOnly++;
            if (p.Kind == MapSeasonKind.Land) { landTotal++; if (actual == shown) landAgree++; }
            if (p.Kind == MapSeasonKind.Water) { waterTotal++; if (actual == shown) waterAgree++; }
        }

        detail = $"{agree}/{total} agree; map-only frozen {mapOnly}, terrain-only frozen {terrainOnly}; land {landAgree}/{landTotal}, shore water {waterAgree}/{waterTotal}";
        return total == 0 ? 0 : agree / (double)total;
    }

    // ---- touch -----------------------------------------------------------------------------

    static IEnumerable<bool> Touch()
    {
        foreach (var x in EnterWorld(Summer)) yield return x;
        foreach (var x in Seconds(5)) yield return x;
        var spawn = data.ComponentPlayer.ComponentBody.Position;
        var dialog = OpenLargeMap(new Vector2(spawn.X, spawn.Z));
        foreach (var x in Seconds(2)) yield return x;
        var surface = Field<MapSurfaceWidget>(dialog, "_surface");
        var size = surface.ActualSize;
        Vector2 Screen(float fx, float fy) => surface.WidgetToScreen(new Vector2(size.X * fx, size.Y * fy));
        float Zoom() => surface.Transform.BlocksPerPixel;
        System.Numerics.Vector2 Center() => surface.Transform.Center;
        notes.Add($"surface {size}, window active {Window.IsActive}");

        // The ghost: a finger that went down on the top bar, its release dropped (keyboard up, window inactive).
        Press(99, dialog.WidgetToScreen(new Vector2(dialog.ActualSize.X * 0.9f, 10)));
        foreach (var x in Seconds(0.3)) yield return x;
        Check(Engine.Input.Touch.TouchLocations.Count == 1, "ghost touch sits in the engine's touch list");

        // A drag with one finger pans.
        var center0 = Center();
        Press(1, Screen(.5f, .5f));
        yield return true; yield return true;
        for (var i = 1; i <= 10; i++) { Move(1, Screen(.5f + (i * .02f), .5f)); yield return true; }
        Release(1, Screen(.7f, .5f));
        foreach (var x in Seconds(0.3)) yield return x;
        var panned = Center() != center0;
        metrics["drag_panned"] = panned;

        // A pinch with two fingers zooms in.
        var zoom0 = Zoom();
        Press(2, Screen(.45f, .5f)); Press(3, Screen(.55f, .5f));
        yield return true; yield return true;
        for (var i = 1; i <= 10; i++) { Move(2, Screen(.45f - (i * .02f), .5f)); Move(3, Screen(.55f + (i * .02f), .5f)); yield return true; }
        Release(2, Screen(.25f, .5f)); Release(3, Screen(.75f, .5f));
        foreach (var x in Seconds(0.3)) yield return x;
        var zoomed = Zoom() < zoom0 * 0.8f;
        metrics["pinch_zoom"] = $"{zoom0} -> {Zoom()}";

        // A second drag after the pinch still pans (the stuck state used to start right here).
        var center1 = Center();
        Press(4, Screen(.5f, .5f));
        yield return true; yield return true;
        for (var i = 1; i <= 10; i++) { Move(4, Screen(.5f, .5f + (i * .02f))); yield return true; }
        Release(4, Screen(.5f, .7f));
        foreach (var x in Seconds(0.3)) yield return x;
        var pannedAgain = Center() != center1;
        metrics["drag_after_pinch_panned"] = pannedAgain;

        // A long press opens the teleport menu.
        Press(5, Screen(.5f, .5f));
        foreach (var x in Seconds(0.9, () => Move(5, Screen(.5f, .5f)))) yield return x;
        var menu = FieldOrNull(dialog, "_activeMenu") is not null;
        Release(5, Screen(.5f, .5f));
        metrics["long_press_menu"] = menu;
        Shot("touch_after_gestures");
        yield return true;

        Check(panned, "with a ghost touch present: one-finger drag pans the large map");
        Check(zoomed, "with a ghost touch present: pinch zooms the large map");
        Check(pannedAgain, "with a ghost touch present: drag after the pinch still pans (no stuck gesture)");
        Check(menu, "with a ghost touch present: long press opens the teleport menu");
    }

    static void Press(int id, Vector2 p) => Inject(id, p, TouchLocationState.Pressed);
    static void Move(int id, Vector2 p) => Inject(id, p, TouchLocationState.Moved);
    static void Release(int id, Vector2 p) => Inject(id, p, TouchLocationState.Released);

    /// <summary>
    /// Writes the engine's touch list the way Touch.ProcessTouch* would (those return early while the window is not
    /// active, which a hidden test window may not be).
    /// </summary>
    static void Inject(int id, Vector2 p, TouchLocationState state)
    {
        var list = (List<TouchLocation>)typeof(Engine.Input.Touch).GetField("m_touchLocations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var index = list.FindIndex(t => t.Id == id);
        if (index < 0)
        {
            if (state == TouchLocationState.Released) return;
            list.Add(new TouchLocation { Id = id, Position = p, State = TouchLocationState.Pressed });
            return;
        }

        var current = list[index];
        list[index] = state switch
        {
            TouchLocationState.Released => current.State == TouchLocationState.Pressed
                ? new TouchLocation { Id = id, Position = p, State = TouchLocationState.Pressed, ReleaseQueued = true }
                : new TouchLocation { Id = id, Position = p, State = TouchLocationState.Released },
            _ => current.State == TouchLocationState.Moved
                ? new TouchLocation { Id = id, Position = p, State = TouchLocationState.Moved }
                : current,
        };
    }
}

// The test game runs while the user keeps working: keep the system cursor free (see MechDefense's isolated tests).
[HarmonyPatch(typeof(Engine.Input.Mouse), "BeforeFrame")]
public static class KeepCursorFree { public static void Prefix() => Engine.Input.Mouse.IsMouseVisible = true; }

[HarmonyPatch(typeof(Engine.Input.Mouse), "AfterFrame")]
public static class KeepCursorFreeAfter { public static void Prefix() => Engine.Input.Mouse.IsMouseVisible = true; }
