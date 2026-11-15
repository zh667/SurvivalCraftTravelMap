using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Engine;
using Engine.Input;
using Game;
#if TM_NET
using Game.NetWork;
#else
using HarmonyLib;
#endif
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Persistence;
using GameEntitySystem;
using SurvivalcraftTravelMap.UI;

namespace TravelMapLab;

// Isolated in-game checks for the travel map: plugin edition (SCAPI) and, built with -p:TmFlavor=net, the multiplayer
// edition (SurvivalcraftNet 2.4, run as a local game). Installed only into an isolated runtime by
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
#if !TM_NET
        new Harmony("local.sctmlab").PatchAll(typeof(Lab).Assembly);
#endif
#if !TM_NET
        SettingsManager.FileAssociationEnabled = false;
#endif
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
            script ??= (mode switch { "touch" => Touch(), "markers" => Markers(), _ => Season() }).GetEnumerator();
            if (!script.MoveNext())
            {
                Finish(new { passed = true, mode, checks, metrics, notes });
                return;
            }

#if TM_NET
            // No Harmony here: hand the touches to the engine's dispatcher from another thread, which queues them for
            // the next frame's Dispatcher.BeforeFrame — before any widget updates, like real touch events.
            if (PendingTouches.Count > 0)
                Task.Run(() => Dispatcher.Dispatch(DrainTouches)).Wait();
#endif
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

    internal static void DrainTouches()
    {
        while (PendingTouches.Count > 0) PendingTouches.Dequeue()();
    }

    static IEnumerable<bool> EnterWorld(float timeOfYear)
    {
        while (ScreensManager.CurrentScreen is not MainMenuScreen) yield return true;
#if TM_NET
        // Switching screens during the menu's entry animation lands back on the menu; also close the update notice.
        foreach (var x in Seconds(3)) yield return x;
        DialogsManager.HideAllDialogs();
#endif
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
#if TM_NET
        CommonLib.Net.StartLocal();   // normally done by the loading screen, which the lab skips
#endif
        // Also normally set by the loading screen: the Ghoul mod reads the world from it while its subsystems load.
        if (ScreensManager.FindScreen<Screen>("GameLoading") is { } loading
            && loading.GetType().GetField("m_worldInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } worldField)
            worldField.SetValue(loading, world);
        GameManager.LoadProject(world, Games);
        data = new PlayerData(GameManager.Project) { Name = "Lab", InputDevice = WidgetInputDevice.All };
#if TM_NET
        data.SetMain();
#endif
        GameManager.Project.FindSubsystem<SubsystemPlayers>(true).AddPlayerData(data);
        ScreensManager.SwitchScreen("Game");
        var entered = Time.RealTime;
        while (!data.IsReadyForPlaying)
        {
            if (Time.RealTime - entered > 120) throw new TimeoutException("player ready");
            if (ScreensManager.CurrentScreen is MainMenuScreen && Time.RealTime - entered > 3)
            {
                DialogsManager.HideAllDialogs();
                ScreensManager.SwitchScreen("Game");
                entered = Time.RealTime;
                notes.Add("retried entering the game");
            }

            yield return true;
        }
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
        notes.Add($"region pixels {region.Count}, land {region.Count(p => p.Kind == (int)MapSeasonKind.Land)}, water {region.Count(p => p.Kind == (int)MapSeasonKind.Water)}, fixed {region.Count(p => p.Kind == (int)MapSeasonKind.Fixed)}, legacy {region.Count(p => p.Kind == (int)MapSeasonKind.None)}");
        Check(region.Count(p => p.Kind is (int)MapSeasonKind.Land or (int)MapSeasonKind.Water) > region.Count / 4, "winter: most of the recorded neighbourhood is seasonal (land or shore water)");
        metrics["winterSnowObserved"] = region.Count(p => p.ObservedFrozen);
        // Informational only: chunks around spawn were generated before the terrain updater first published the
        // winter temperature (it does so once a second), so they carry no snow yet although the map, rightly, predicts
        // what they will look like the next time they load.
        Agreement(region, MapSeason.Current, out var agreeHere, out _);
        metrics["winter_on_site_first_load"] = agreeHere;

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

            var predicted = RegionPixels(home, 56);
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
            var reloadStarted = Time.RealTime;
            while (!ColumnsReady(terrain, home, 40))
            {
                if (Time.RealTime - reloadStarted > 180)
                {
                    var states = new Dictionary<string, int>();
                    for (var z = home.Y - 40; z <= home.Y + 40; z += 16)
                    for (var x = home.X - 40; x <= home.X + 40; x += 16)
                    {
                        var key = terrain.Terrain.GetChunkAtCell(x, z) is { } c ? c.State.ToString() : "null";
                        states[key] = states.GetValueOrDefault(key) + 1;
                    }

                    throw new TimeoutException($"region reloaded: player at {data.ComponentPlayer.ComponentBody.Position}, home {home}, chunk states "
                        + string.Join(", ", states.Select(kv => $"{kv.Key}={kv.Value}")));
                }

                yield return true;
            }
            foreach (var x in Seconds(4)) yield return x;
            var agreement = Agreement(predicted, MapSeason.Current, out var detail, out var land);
            metrics[$"{phase.Item3}_agreement"] = detail;
            Check(land >= 0.99, $"{phase.Item3}: snow on land predicted from afar matches the terrain on return ({detail})");

            // Water: melting is certain, freezing on load is random (about half), so the map never guesses new ice and
            // only the summer comparison covers water.
            if (phase.Item3 == "summer")
                Check(agreement >= 0.99, $"summer: everything predicted from afar, water included, matches on return ({detail})");
            OpenLargeMap(new Vector2(home.X, home.Y));
            foreach (var x in Seconds(3)) yield return x;
            Shot($"largemap_on_site_{phase.Item3}");
            yield return true;
            CloseLargeMap();
        }

        Check(metrics["summer_predictedFrozen"] is int s && metrics["winter_predictedFrozen"] is int w && w > s,
            "more of the region shows frozen in winter than in summer");
    }

    // Kind is a MapSeasonKind stored as int: the game enumerates every type of this assembly, and a field typed with a
    // 2.8-only enum would stop the lab loading next to the 2.7.1 package (the touch negative control).
    readonly record struct RegionPixel(int X, int Z, int Kind, bool ObservedFrozen, MapTerrainPixel Pixel);

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
            list.Add(new RegionPixel(x, z, (int)kind, s.LandFrozen != 0 || s.WaterFrozen != 0, p));
        }

        return list;
    }

    static bool IsFrozenOnMap(RegionPixel p, MapSeason season) =>
        SeasonalTerrainColor.Resolve(p.Pixel.Color, p.Pixel.Season, season) != p.Pixel.Color;

    static bool ColumnsReady(SubsystemTerrain terrain, Point2 center, int radius)
    {
        for (var z = center.Y - radius; z <= center.Y + radius; z += 16)
        for (var x = center.X - radius; x <= center.X + radius; x += 16)
            // Freezing and snowing happen when the chunk initialises, long before it is fully lit; on 1.9.5.1 some chunks
            // under a player hovering high up stayed short of Valid for minutes, so wait only until the surface is readable
            // (the threshold the map itself records at).
            if (terrain.Terrain.GetChunkAtCell(x, z) is not { } chunk || chunk.State < TerrainChunkState.InvalidPropagatedLight) return false;
        return true;
    }

    static double Agreement(List<RegionPixel> pixels, MapSeason season, out string detail, out double landRatio)
    {
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true).Terrain;
        int agree = 0, total = 0, mapOnly = 0, terrainOnly = 0, landTotal = 0, landAgree = 0, waterTotal = 0, waterAgree = 0;
        var groups = new Dictionary<string, int>();
        var skipped = 0;
        foreach (var p in pixels)
        {
            // The square's corners lie past the 64-block view range; only loaded columns can be checked.
            if (terrain.GetChunkAtCell(p.X, p.Z) is not { } chunk || chunk.State < TerrainChunkState.InvalidPropagatedLight) { skipped++; continue; }
            var top = terrain.GetTopHeight(p.X, p.Z);
            var content = terrain.GetCellContents(p.X, top, p.Z);
            var actual = content is 61 or 62;
            var shown = IsFrozenOnMap(p, season);
            total++;
            if (actual == shown) agree++;
            else
            {
                if (shown) mapOnly++; else terrainOnly++;
                var below = terrain.GetCellContents(p.X, top - 1, p.Z);
                var key = $"{(shown ? "mapOnly" : "terrainOnly")} kind={(MapSeasonKind)p.Kind} seenFrozen={p.ObservedFrozen} limit={p.Pixel.Season.LandLimit}/{p.Pixel.Season.WaterLimit} top={content} below={below} y={top} t={terrain.GetTemperature(p.X, p.Z)}";
                groups[key] = groups.GetValueOrDefault(key) + 1;
            }
            if (p.Kind == (int)MapSeasonKind.Land) { landTotal++; if (actual == shown) landAgree++; }
            if (p.Kind == (int)MapSeasonKind.Water) { waterTotal++; if (actual == shown) waterAgree++; }
        }

        detail = $"{agree}/{total} agree ({skipped} not loaded); map-only frozen {mapOnly}, terrain-only frozen {terrainOnly}; land {landAgree}/{landTotal}, shore water {waterAgree}/{waterTotal}";
        notes.Add("mismatch groups: " + string.Join(" | ", groups.OrderByDescending(g => g.Value).Take(14).Select(g => $"{g.Value}x {g.Key}")));
        landRatio = landTotal == 0 ? 0 : landAgree / (double)landTotal;
        return total == 0 ? 0 : agree / (double)total;
    }

    // ---- markers ---------------------------------------------------------------------------

    // Template, expected marker kind. Mod templates are only checked when their mod is installed.
    static readonly (string Template, string Expected)[] MarkerCases =
    [
        ("Wolf_Gray", "Hostile"),
        ("Cow_Brown", "Animal"),
        ("Pigeon", "Bird"),
        ("Bass_Freshwater", "Fish"),
        ("Shark_Bull", "Hostile"),
        ("SAGhoul_Weak_01", "Hostile"),        // Ghoul mod: registered as a bird
        ("SAG_CorpsificeHead", "Hostile"),     // Ghoul mod: the shooting head, registered as a bird
        ("ScTacticalEnemy", "Hostile"),        // CS weapons: bandit, registered as LandOther
        ("ScTacticalCT", "Ally"),              // CS weapons: teammate, registered as LandOther
        ("ScTacticalHostage", "Ally"),
    ];

    static IEnumerable<bool> Markers()
    {
        foreach (var x in EnterWorld(Summer)) yield return x;
        var origin = data.ComponentPlayer.ComponentBody.Position;
        var spawned = new List<(string Template, string Expected, Entity Entity)>();
        var missing = new List<string>();
        for (var i = 0; i < MarkerCases.Length; i++)
        {
            var (template, expected) = MarkerCases[i];
            Entity? entity = null;
            try { entity = DatabaseManager.CreateEntity(GameManager.Project, template, false); }
            catch (Exception e) { notes.Add($"{template}: {e.GetType().Name} {e.Message}"); }
            if (entity is null) { missing.Add(template); continue; }
            var position = origin + new Vector3(6f + (i % 5) * 7f, 2f, (i / 5) * 9f - 4f);
            entity.FindComponent<ComponentBody>(true).Position = position;
            if (entity.FindComponent<ComponentSpawn>() is { } spawn) spawn.SpawnDuration = 0f;
            GameManager.Project.AddEntity(entity);
            spawned.Add((template, expected, entity));
        }

        notes.Add("not installed: " + string.Join(", ", missing));
        foreach (var x in Seconds(1.5)) yield return x;

        // Ask the map for each spawned entity's kind directly (matching markers by position mixed up creatures that had
        // already walked towards each other), and record what the classifier saw.
        var kindOf = typeof(TravelMapComponent).GetMethod("GetCreatureMarkerKind", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var traitsOf = typeof(TravelMapComponent).GetMethod("ReadCreatureMarkerTraits", BindingFlags.Static | BindingFlags.NonPublic)!;
        var results = new Dictionary<string, string>();
        var why = new Dictionary<string, string>();
        foreach (var (template, expected, entity) in spawned)
        {
            var creature = entity.FindComponent<ComponentCreature>(true);
            var chase = entity.FindComponent<ComponentChaseBehavior>();
            var kind = kindOf.Invoke(Map, [creature])!.ToString()!;
            var traits = (CreatureMarkerTraits)traitsOf.Invoke(null, [creature, chase])!;
            results[template] = kind;
            why[template] = $"predator={traits.IsPredator} aquatic={traits.IsAquatic} chases={traits.ChasesPlayer} flies={traits.Flies} "
                + $"target={(chase?.Target is { } t ? (ReferenceEquals(t, data.ComponentPlayer) ? "player" : "other creature") : "-")} "
                + $"components={string.Join(",", traits.ComponentTypeNames.Where(n => n.Contains("Tactical") || n.Contains("Chase") || n.Contains("MD")))}";
        }

        metrics["why"] = why;
        foreach (var (template, expected, _) in spawned)
            Check(results[template] == expected, $"{template} shows as {results[template]} (expected {expected})");
        metrics["kinds"] = results;
        OpenLargeMap(new Vector2(origin.X + 18f, origin.Z));
        var dialog = Field<Dialog>(Map, "_largeMapDialog");
        var settings = FieldOrNull(dialog, "_settings")!;
        settings.GetType().GetProperty("LargeMapBlocksPerPixel")!.SetValue(settings, 0.25f);
        foreach (var x in Seconds(2)) yield return x;
        Shot("markers_large_map");
        yield return true;
    }

    // ---- touch -----------------------------------------------------------------------------

    static IEnumerable<bool> Touch()
    {
        foreach (var x in EnterWorld(Summer)) yield return x;
        foreach (var x in Seconds(5)) yield return x;
        var spawn = data.ComponentPlayer.ComponentBody.Position;
        var dialog = OpenLargeMap(new Vector2(spawn.X, spawn.Z));

        // The runtime is reused between runs and the map remembers its zoom; start each run at 2 blocks per pixel so
        // the pinch below always has room to zoom in.
        var settings = FieldOrNull(dialog, "_settings")!;
        settings.GetType().GetProperty("LargeMapBlocksPerPixel")!.SetValue(settings, 2f);
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
        var trace = new List<string>();
        void Trace(string tag)
        {
            var gesture = FieldOrNull(dialog, "_touchGesture");
            var mode = gesture?.GetType().GetProperty("Mode")?.GetValue(gesture);
            var live = FieldOrNull(dialog, "_liveTouches") is System.Collections.ICollection c ? c.Count : -1;
            var engine = string.Join(",", Engine.Input.Touch.TouchLocations.Select(t => $"{t.Id}:{t.State}"));
            trace.Add($"{tag} mode={mode} live={live} engine=[{engine}] zoom={Zoom()}");
        }

        Trace("pressed-queued");
        for (var i = 1; i <= 10; i++) { Move(2, Screen(.45f - (i * .02f), .5f)); Move(3, Screen(.55f + (i * .02f), .5f)); yield return true; Trace("move" + i); }
        metrics["pinch_trace"] = trace;
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

        // A long press on explored ground (back over the player: the drags and the zoom left unexplored land in the
        // middle, where a long press only says "not explored yet") opens the teleport menu.
        dialog.GetType().GetMethod("ResetToPlayer")!.Invoke(dialog, null);
        foreach (var x in Seconds(0.5)) yield return x;
        var ground = Screen(.5f, .5f) + new Vector2(24, 0);
        Press(5, ground);
        foreach (var x in Seconds(0.9, () => Move(5, ground))) yield return x;
        var menu = FieldOrNull(dialog, "_activeMenu") is not null;
        Release(5, ground);
        metrics["long_press_menu"] = menu;
        Shot("touch_after_gestures");
        yield return true;

        Check(panned, "with a ghost touch present: one-finger drag pans the large map");
        Check(zoomed, "with a ghost touch present: pinch zooms the large map");
        Check(pannedAgain, "with a ghost touch present: drag after the pinch still pans (no stuck gesture)");
        Check(menu, "with a ghost touch present: long press opens the teleport menu");
    }

    static void Press(int id, Vector2 p) => PendingTouches.Enqueue(() => Inject(id, p, TouchLocationState.Pressed));
    static void Move(int id, Vector2 p) => PendingTouches.Enqueue(() => Inject(id, p, TouchLocationState.Moved));
    static void Release(int id, Vector2 p) => PendingTouches.Enqueue(() => Inject(id, p, TouchLocationState.Released));

    /// <summary>
    /// Applied at the start of the next frame (Touch.BeforeFrame), where the OS delivers real touch events — before any
    /// widget updates. Injecting from Window.Frame instead runs after the game's own update in that frame, so the engine
    /// turns "pressed" into "moved" before a widget ever sees the press.
    /// </summary>
    internal static readonly Queue<Action> PendingTouches = new();

    /// <summary>
    /// Writes the engine's touch list the way Touch.ProcessTouch* would (those return early while the window is not
    /// active, which a hidden test window may not be).
    /// </summary>
    static void Inject(int id, Vector2 p, TouchLocationState state)
    {
        var list = (List<TouchLocation>)typeof(Engine.Input.Touch)
            .GetField("m_touchLocations", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
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
#if TM_NET
            TouchLocationState.Released => new TouchLocation { Id = id, Position = p, State = TouchLocationState.Released },
#else
            TouchLocationState.Released => current.State == TouchLocationState.Pressed
                ? new TouchLocation { Id = id, Position = p, State = TouchLocationState.Pressed, ReleaseQueued = true }
                : new TouchLocation { Id = id, Position = p, State = TouchLocationState.Released },
#endif
            _ => current.State == TouchLocationState.Moved
                ? new TouchLocation { Id = id, Position = p, State = TouchLocationState.Moved }
                : current,
        };
    }
}

#if !TM_NET
[HarmonyPatch(typeof(Engine.Input.Touch), "BeforeFrame")]
public static class InjectTouchesAtFrameStart
{
    public static void Prefix()
    {
        while (Lab.PendingTouches.Count > 0) Lab.PendingTouches.Dequeue()();
    }
}

// The test game runs while the user keeps working: keep the system cursor free (see MechDefense's isolated tests).
[HarmonyPatch(typeof(Engine.Input.Mouse), "BeforeFrame")]
public static class KeepCursorFree { public static void Prefix() => Engine.Input.Mouse.IsMouseVisible = true; }

[HarmonyPatch(typeof(Engine.Input.Mouse), "AfterFrame")]
public static class KeepCursorFreeAfter { public static void Prefix() => Engine.Input.Mouse.IsMouseVisible = true; }
#endif
