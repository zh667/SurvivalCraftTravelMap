using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Engine;
using Engine.Input;
using Game;
#if TM_NET
using Game.NetWork;
#endif
using HarmonyLib;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Persistence;
using GameEntitySystem;
using SurvivalcraftTravelMap.Settings;
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

    static string packageVersion = "";

    public override void __ModInitialize()
    {
#if TM_NET
        // The multiplayer edition ships no Harmony: run_isolated.py puts the plugin edition's Harmony and MonoMod
        // assemblies in labdeps/. Resolve them before any method that uses Harmony is compiled.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "labdeps", new System.Reflection.AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
#endif
        ApplyPatches();
        var v = Path.Combine(AppContext.BaseDirectory, "lab-package-version.txt");
        if (File.Exists(v)) packageVersion = File.ReadAllText(v).Trim();
#if !TM_NET
        SettingsManager.FileAssociationEnabled = false;
#endif
        var m = Path.Combine(AppContext.BaseDirectory, "lab-mode.txt");
        if (File.Exists(m)) mode = File.ReadAllText(m).Trim();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void ApplyPatches() => new Harmony("local.sctmlab").PatchAll(typeof(Lab).Assembly);

    // The package under test predates 2.9.0 (upgrade-old with the 2.8.0 package).
    static bool OlderPackage => packageVersion.Length > 0 && packageVersion != "2.9.0";

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
            script ??= (mode switch
            {
                "touch" => Touch(),
                "markers" => Markers(),
                "shading" => Shading(),
                "upgrade-old" => UpgradeOld(),
                "upgrade-new" => UpgradeNew(),
                "attack" => Attack(),
                _ => Season(),
            }).GetEnumerator();
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

    static IEnumerable<bool> EnterWorld(
        float timeOfYear,
        TerrainGenerationMode generation = TerrainGenerationMode.Continent,
        bool existing = false,
        GameMode gameMode = GameMode.Creative)
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
        WorldInfo world;
        if (existing)
        {
            // Reopen the world an earlier run saved (upgrade test: recorded by 2.8.0, opened by 2.9.0).
            WorldsManager.UpdateWorldsList();
            world = WorldsManager.WorldInfos.FirstOrDefault(w => w.WorldSettings.Name == WorldName)
                ?? throw new InvalidOperationException("no saved world " + WorldName);
        }
        else
        {
            world = WorldsManager.CreateWorld(new WorldSettings
            {
                Name = WorldName,
                Seed = "20261114",
                GameMode = gameMode,
                TerrainGenerationMode = generation,
                EnvironmentBehaviorMode = EnvironmentBehaviorMode.Living,
                AreWeatherEffectsEnabled = true,
                TimeOfDayMode = TimeOfDayMode.Day,
                AreSeasonsChanging = false,
                TimeOfYear = timeOfYear,
            });
        }
#if TM_NET
        CommonLib.Net.StartLocal();   // normally done by the loading screen, which the lab skips
#endif
        // Also normally set by the loading screen: the Ghoul mod reads the world from it while its subsystems load.
        if (ScreensManager.FindScreen<Screen>("GameLoading") is { } loading
            && loading.GetType().GetField("m_worldInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } worldField)
            worldField.SetValue(loading, world);
        GameManager.LoadProject(world, Games);
        var players = GameManager.Project.FindSubsystem<SubsystemPlayers>(true);
        if (existing && players.PlayersData.Count > 0)
        {
            // The saved player, so the map opens the same per-player storage it wrote before.
            data = players.PlayersData[0];
        }
        else
        {
            data = new PlayerData(GameManager.Project) { Name = "Lab", InputDevice = WidgetInputDevice.All };
#if TM_NET
            data.SetMain();
#endif
            players.AddPlayerData(data);
        }
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

    // ---- shading ---------------------------------------------------------------------------
    //   "shading":     flat world, a 5x5 platform 3 high and a 5x5 pit 3 deep; on the north-up large map the platform's
    //                  screen top-left neighbours must be brighter than its bottom-right ones and the pit's top-left inner
    //                  walls darker than its bottom-right ones, in the map data and (checked by run_isolated.py) in the
    //                  screenshot pixels.
    //   "upgrade-old": the same site recorded and saved by an older package (2.8.0); uses nothing newer than 2.8.0.
    //   "upgrade-new": 2.9.0 reopens that world: the old area must show mirrored shading and be flagged for re-recording
    //                  until the player comes near, then carry freshly computed shades; the 2.8.0 files stay untouched.

    const int Dirt = 2, Grass = 8, Air = 0;
    static Point2 platform, pit;
    static readonly List<object> pixelChecks = [];

    static string SiteFile => Path.Combine(AppContext.BaseDirectory, "lab-site.json");

    // The eight cells compared: the platform's outside neighbours (distance 3 from its centre) and the pit's inner edge
    // cells (distance 2), each on the four sides. Which one is screen-left etc. is decided through the map transform.
    static IEnumerable<(string Name, Point2 Cell)> SampleCells()
    {
        foreach (var (site, name, d) in new[] { (platform, "platform", 3), (pit, "pit", 2) })
        {
            yield return (name + "+x", new Point2(site.X + d, site.Y));
            yield return (name + "-x", new Point2(site.X - d, site.Y));
            yield return (name + "+z", new Point2(site.X, site.Y + d));
            yield return (name + "-z", new Point2(site.X, site.Y - d));
        }
    }

    static IEnumerable<bool> BuildSite(SubsystemTerrain terrain, Point2 home)
    {
        // Two chunks east of the player's, side by side, well inside the 64-block visibility range.
        var chunkX = (int)MathF.Floor(home.X / 16f) + 2;
        var chunkZ = (int)MathF.Floor(home.Y / 16f);
        platform = new Point2((chunkX * 16) + 8, (chunkZ * 16) + 8);
        pit = new Point2((chunkX * 16) + 8, ((chunkZ + 1) * 16) + 8);
        foreach (var x in Until(() => ColumnsReady(terrain, platform, 8) && ColumnsReady(terrain, pit, 8), 120, "site chunks loaded")) yield return x;

        foreach (var (site, delta) in new[] { (platform, 3), (pit, -3) })
        {
            // Level a 9x9 patch of grass first (flat worlds may still carry plants or snow), then raise or dig 5x5.
            var heights = new List<int>();
            for (var dz = -4; dz <= 4; dz++)
            for (var dx = -4; dx <= 4; dx++)
                heights.Add(terrain.Terrain.GetTopHeight(site.X + dx, site.Y + dz));
            var ground = heights.GroupBy(h => h).OrderByDescending(g => g.Count()).First().Key;
            for (var dz = -4; dz <= 4; dz++)
            for (var dx = -4; dx <= 4; dx++)
            {
                for (var y = ground + 1; y <= ground + 8; y++) terrain.ChangeCell(site.X + dx, y, site.Y + dz, Air);
                terrain.ChangeCell(site.X + dx, ground, site.Y + dz, Grass);
            }

            for (var dz = -2; dz <= 2; dz++)
            for (var dx = -2; dx <= 2; dx++)
            {
                if (delta > 0)
                    for (var y = ground + 1; y <= ground + delta; y++) terrain.ChangeCell(site.X + dx, y, site.Y + dz, Dirt);
                else
                    for (var y = ground + delta + 1; y <= ground; y++) terrain.ChangeCell(site.X + dx, y, site.Y + dz, Air);
            }

            notes.Add($"site {site.X},{site.Y}: ground {ground}, {(delta > 0 ? "platform" : "pit")} {Math.Abs(delta)}");
        }

        foreach (var x in Seconds(2)) yield return x;
    }

    // The mini map records every chunk it shows as soon as the chunk loads, so both site chunks were recorded before the
    // site was built. Mark them unexplored again (public store API, present in 2.8.0 too) and let the map record them.
    static IEnumerable<bool> VisitSite(SubsystemTerrain terrain, Point2 home)
    {
        foreach (var site in new[] { platform, pit })
        {
            var c = TileCoordinate.FromWorld(site.X - 8, site.Y - 8);
            using var lease = Store.AcquireMutation(c.TileX, c.TileZ);
            lease.Tile.SetRegion(c.LocalX, c.LocalZ, 16, 16, new Rgba32[256]);
        }

        foreach (var site in new[] { platform, pit })
        {
            Teleport(new Vector3(site.X + 0.5f, terrain.Terrain.GetTopHeight(site.X, site.Y) + 25, site.Y + 0.5f));
            foreach (var x in Until(() => RecordedCount(site, 4) == 81, 90, $"site {site.X},{site.Y} recorded")) yield return x;
        }

        Teleport(new Vector3(home.X + 0.5f, terrain.Terrain.GetTopHeight(home.X, home.Y) + 20, home.Y + 0.5f));
        foreach (var x in Seconds(2)) yield return x;
    }

    static Dictionary<string, MapTerrainPixel> ReadSamples()
    {
        var result = new Dictionary<string, MapTerrainPixel>();
        foreach (var (name, cell) in SampleCells())
        {
            var c = TileCoordinate.FromWorld(cell.X, cell.Y);
            Check(Store.GetOrLoad(c.TileX, c.TileZ).TryGetTerrainPixel(c.LocalX, c.LocalZ, out var p), $"{name} cell {cell.X},{cell.Y} is on the map");
            result[name] = p;
        }

        return result;
    }

    // What the current shading code makes of the terrain as it stands (the sites are level grass around them).
    static byte FreshShade(SubsystemTerrain terrain, Point2 cell)
    {
        int H(int x, int z) => terrain.Terrain.GetTopHeight(x, z);
        return TerrainHeightShading.Calculate(H(cell.X - 1, cell.Y), H(cell.X, cell.Y - 1), H(cell.X + 1, cell.Y), H(cell.X, cell.Y + 1), H(cell.X, cell.Y));
    }

    /// <summary>
    /// Opens the north-up large map over the site, takes a screenshot at window size and returns, per sample cell, its
    /// screen side ("platform left" ...), map shade and screenshot pixel position. Checks the light direction in the map
    /// data; run_isolated.py checks it again in the screenshot's pixels.
    /// </summary>
    static IEnumerable<bool> LookAtSite(string shot, bool expectTopLeftLight, string label)
    {
        Field<TravelMapSettings>(Map, "_settings").ShowCreatureMarkers = false;
        var center = new Vector2(platform.X, (platform.Y + pit.Y) / 2f);
        var dialog = OpenLargeMap(center);
        var settings = FieldOrNull(dialog, "_settings")!;
        settings.GetType().GetProperty("LargeMapBlocksPerPixel")!.SetValue(settings, 0.25f);
        foreach (var x in Seconds(3)) yield return x;
        dialog.GetType().GetMethod("ResetToWorld")!.Invoke(dialog, [new System.Numerics.Vector2(center.X, center.Y)]);
        foreach (var x in Seconds(3)) yield return x;

        var surface = Field<MapSurfaceWidget>(dialog, "_surface");
        var transform = surface.Transform;
        Check(transform.RotationRadians == 0f, "large map is north-up");
        var samples = ReadSamples();
        var screen = new Dictionary<string, Vector2>();
        foreach (var (name, cell) in SampleCells())
        {
            var local = transform.WorldToScreen(new System.Numerics.Vector2(cell.X + 0.5f, cell.Y + 0.5f));
            screen[name] = surface.WidgetToScreen(new Vector2(local.X, local.Y));
        }

        var size = Window.Size;
        var width = size.X > 0 ? size.X : 1280;
        var height = size.Y > 0 ? size.Y : 720;
        SettingsManager.ShowGuiInScreenshots = true;
        SettingsManager.ShowLogoInScreenshots = false;
        ScreenCaptureManager.Capture(width, height, shot + ".png");
        yield return true;

        // Name the four cells of each site by where they land on screen.
        var sides = new Dictionary<string, object>();
        foreach (var site in new[] { "platform", "pit" })
        {
            var names = new[] { "+x", "-x", "+z", "-z" }.Select(n => site + n).ToArray();
            string Pick(Func<string, float> key, bool max) => (max ? names.OrderByDescending(key) : names.OrderBy(key)).First();
            var left = Pick(n => screen[n].X, false);
            var right = Pick(n => screen[n].X, true);
            var top = Pick(n => screen[n].Y, false);
            var bottom = Pick(n => screen[n].Y, true);
            object Point(string n) => new
            {
                cell = n,
                x = (int)MathF.Round(screen[n].X),
                y = (int)MathF.Round(screen[n].Y),
                shade = (int)samples[n].HeightShade,
                color = new[] { (int)samples[n].Color.R, samples[n].Color.G, samples[n].Color.B },
            };
            sides[site] = new { left = Point(left), right = Point(right), top = Point(top), bottom = Point(bottom) };

            var lighter = site == "platform" == expectTopLeftLight;   // platform: top-left brighter; pit: darker
            byte S(string n) => samples[n].HeightShade;
            var horizontal = lighter ? S(left) > S(right) : S(left) < S(right);
            var vertical = lighter ? S(top) > S(bottom) : S(top) < S(bottom);
            Check(horizontal && vertical,
                $"{label}: map data, {site} screen-left {S(left)} vs right {S(right)}, top {S(top)} vs bottom {S(bottom)} "
                + (lighter ? "(top-left brighter)" : "(top-left darker)"));
        }

        pixelChecks.Add(new { shot = shot + ".png", width, height, topLeftLight = expectTopLeftLight, label, sides });
        metrics["pixelChecks"] = pixelChecks;
        metrics["window"] = $"{size.X}x{size.Y}";
        CloseLargeMap();
    }

    static void NoteDataRoot() => notes.Add("map data under " + Field<string>(Store, "_directory"));

    static IEnumerable<bool> Shading()
    {
        foreach (var x in EnterWorld(Summer, TerrainGenerationMode.FlatContinent)) yield return x;
        NoteDataRoot();
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true);
        var spawn = data.ComponentPlayer.ComponentBody.Position;
        var home = new Point2((int)MathF.Floor(spawn.X), (int)MathF.Floor(spawn.Z));
        foreach (var x in BuildSite(terrain, home)) yield return x;
        foreach (var x in VisitSite(terrain, home)) yield return x;
        foreach (var (name, cell) in SampleCells())
        {
            var mapShade = ReadSamples()[name].HeightShade;
            Check(mapShade == FreshShade(terrain, cell), $"{name}: map shade {mapShade} equals the shade of the terrain as built");
        }

        foreach (var x in LookAtSite("shading_large_map", expectTopLeftLight: true, "2.9.0")) yield return x;
    }

    static IEnumerable<bool> UpgradeOld()
    {
        foreach (var x in EnterWorld(Summer, TerrainGenerationMode.FlatContinent)) yield return x;
        NoteDataRoot();
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true);
        var spawn = data.ComponentPlayer.ComponentBody.Position;
        var home = new Point2((int)MathF.Floor(spawn.X), (int)MathF.Floor(spawn.Z));
        foreach (var x in BuildSite(terrain, home)) yield return x;
        foreach (var x in VisitSite(terrain, home)) yield return x;

        // The bug being fixed: the older package lights relief from the screen's bottom-right.
        // (Run with the current package too, on the multiplayer edition, where the lab cannot load next to 2.8.0.)
        foreach (var x in LookAtSite("upgrade_old_large_map", expectTopLeftLight: !OlderPackage, OlderPackage ? "older package" : "current package")) yield return x;

        // Save with the player far away, so the site is neither loaded nor on the mini map when the world reopens.
        Teleport(new Vector3(home.X + 400.5f, 150, home.Y + 0.5f));
        foreach (var x in Seconds(8)) yield return x;   // the tile store flushes every 5 s
        GameManager.SaveProject(true, false);
        foreach (var x in Seconds(2)) yield return x;

        var directory = Field<string>(Store, "_directory");
        File.WriteAllText(SiteFile, JsonSerializer.Serialize(new
        {
            platform = new[] { platform.X, platform.Y },
            pit = new[] { pit.X, pit.Y },
            home = new[] { home.X, home.Y },
            shades = ReadSamples().ToDictionary(kv => kv.Key, kv => (int)kv.Value.HeightShade),
            legacy = OlderPackage,
            tiles = directory,
            worldDirectory = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true).DirectoryName,
            files = Directory.GetFiles(directory, "*.sctm").ToDictionary(Path.GetFileName, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))),
        }));
        metrics["tileFiles"] = Directory.GetFiles(directory, "*.sctm").Select(Path.GetFileName).ToArray();
    }

    static IEnumerable<bool> UpgradeNew()
    {
        var site = JsonDocument.Parse(File.ReadAllText(SiteFile)).RootElement;
        platform = new Point2(site.GetProperty("platform")[0].GetInt32(), site.GetProperty("platform")[1].GetInt32());
        pit = new Point2(site.GetProperty("pit")[0].GetInt32(), site.GetProperty("pit")[1].GetInt32());
        var oldShades = site.GetProperty("shades").EnumerateObject().ToDictionary(p => p.Name, p => (byte)p.Value.GetInt32());
        var oldFiles = site.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var legacy = site.GetProperty("legacy").GetBoolean();
        var oldTiles = site.GetProperty("tiles").GetString()!;

        // Where the map cache lived before it was keyed by the world's full path. A 2.8.0 run left it there already;
        // a current-package run (multiplayer edition) wrote it under the new key, so move it back to the old one.
        var playerDirectory = Path.GetDirectoryName(oldTiles)!;
        var mapsDirectory = Path.GetDirectoryName(Path.GetDirectoryName(playerDirectory)!)!;
        var legacyKey = WorldKey.ForLocal(site.GetProperty("worldDirectory").GetString()!);
        var legacyPlayerDirectory = Path.Combine(mapsDirectory, legacyKey, Path.GetFileName(playerDirectory));
        if (!string.Equals(Path.GetFullPath(playerDirectory), Path.GetFullPath(legacyPlayerDirectory), StringComparison.OrdinalIgnoreCase))
        {
            Check(!Directory.Exists(Path.Combine(mapsDirectory, legacyKey)), "no old-key cache before the move");
            Directory.Move(Path.GetDirectoryName(playerDirectory)!, Path.Combine(mapsDirectory, legacyKey));
            notes.Add($"moved the current-key cache to the old key {legacyKey} to stand in for an older build's");
        }

        var legacyTiles = Path.Combine(legacyPlayerDirectory, "tiles");
        Check(oldFiles.All(f => File.Exists(Path.Combine(legacyTiles, f.Key))), "the old-key cache holds the recorded tiles");

        foreach (var x in EnterWorld(Summer, existing: true)) yield return x;
        NoteDataRoot();
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true);
        foreach (var x in Seconds(3)) yield return x;
        notes.Add($"player at {data.ComponentPlayer.ComponentBody.Position}");

        // The cache was found under the new, path-based key: copied from the old key, which stays as it was.
        var newTiles = Field<string>(Store, "_directory");
        Check(!string.Equals(Path.GetFullPath(newTiles), Path.GetFullPath(legacyTiles), StringComparison.OrdinalIgnoreCase),
            $"the map now lives under a new key ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(newTiles)!)!)})");
        foreach (var (file, hash) in oldFiles)
            Check(File.Exists(Path.Combine(newTiles, file)) && Hash(Path.Combine(newTiles, file)) == hash, $"{file} was copied to the new key unchanged");
        metrics["cacheKeys"] = new { legacy = legacyTiles, current = newTiles };

        if (!legacy)
        {
            // Data already in the current format: the far-away site must be on the map straight away (it was not
            // recorded again: the player is 400 blocks away), with the shades it had.
            var shown = ReadSamples();
            foreach (var (name, cell) in SampleCells())
                Check(shown[name].HeightShade == oldShades[name], $"{name}: migrated map shows shade {shown[name].HeightShade} as recorded");
            Check(ShadeCurrent(platform) && ShadeCurrent(pit), "current-format chunks are not flagged for re-recording");
            foreach (var x in LookAtSite("cachekey_migrated", expectTopLeftLight: true, "current package, migrated cache")) yield return x;
            foreach (var x in Seconds(8)) yield return x;
            foreach (var (file, hash) in oldFiles)
                Check(File.Exists(Path.Combine(legacyTiles, file)) && Hash(Path.Combine(legacyTiles, file)) == hash, $"old-key file {file} is still there, unchanged");
            yield break;
        }

        // Before the player comes near: mirrored old shades, chunks flagged.
        var before = ReadSamples();
        foreach (var (name, _) in SampleCells())
            Check(before[name].HeightShade == Mirror(oldShades[name]),
                $"{name}: old shade {oldShades[name]} shows mirrored as {before[name].HeightShade}");
        Check(!ShadeCurrent(platform) && !ShadeCurrent(pit), "the old site's chunks are flagged for re-recording");
        foreach (var x in LookAtSite("upgrade_before_rerecord", expectTopLeftLight: true, "2.9.0 on 2.8.0 data, before re-recording")) yield return x;

        // The player comes near: the chunks are recorded again with the new light.
        foreach (var s in new[] { platform, pit })
        {
            Teleport(new Vector3(s.X + 0.5f, 90, s.Y + 0.5f));   // the site may not be loaded yet; flat ground is at 64
            foreach (var x in Until(() => ShadeCurrent(s), 90, $"site {s.X},{s.Y} re-recorded")) yield return x;
        }

        var home = site.GetProperty("home");
        Teleport(new Vector3(home[0].GetInt32() + 0.5f, 85, home[1].GetInt32() + 0.5f));
        foreach (var x in Seconds(2)) yield return x;
        var after = ReadSamples();
        foreach (var (name, cell) in SampleCells())
            Check(after[name].HeightShade == FreshShade(terrain, cell),
                $"{name}: re-recorded shade {after[name].HeightShade} equals the new shade of the terrain (was {before[name].HeightShade} mirrored)");
        metrics["shades"] = SampleCells().ToDictionary(c => c.Name, c => $"2.8.0 {oldShades[c.Name]} -> mirrored {before[c.Name].HeightShade} -> re-recorded {after[c.Name].HeightShade}");
        foreach (var x in LookAtSite("upgrade_after_rerecord", expectTopLeftLight: true, "2.9.0 after re-recording")) yield return x;

        foreach (var x in Seconds(8)) yield return x;
        var directory = Field<string>(Store, "_directory");
        foreach (var (file, hash) in oldFiles)
        {
            Check(File.Exists(Path.Combine(directory, file)) && Hash(Path.Combine(directory, file)) == hash,
                $"2.8.0 file {file} (copied to the new key) is still there, unchanged");
            Check(File.Exists(Path.Combine(legacyTiles, file)) && Hash(Path.Combine(legacyTiles, file)) == hash,
                $"2.8.0 file {file} under the old key is still there, unchanged");
        }

        var v5 = Directory.GetFiles(directory, "*.v5.sctm").Select(Path.GetFileName).ToArray();
        metrics["v5Files"] = v5;
        foreach (var s in new[] { platform, pit })
        {
            var c = TileCoordinate.FromWorld(s.X, s.Y);
            Check(v5.Contains($"{c.TileX}_{c.TileZ}.v5.sctm"), $"re-recorded tile {c.TileX}_{c.TileZ} is written as .v5");
        }
    }

    static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    static byte Mirror(byte shade) => shade == 0 ? (byte)0 : (byte)Math.Clamp(256 - shade, 1, 255);

    // 2.9.0-only API, kept out of the methods the older package runs.
    static bool ShadeCurrent(Point2 site)
    {
        var c = TileCoordinate.FromWorld(site.X, site.Y);
        return Store.IsChunkShadeCurrent(c.TileX, c.TileZ, c.LocalX, c.LocalZ);
    }

    // ---- attack ----------------------------------------------------------------------------
    //   "attack": a challenging-mode world. (1) A cow, green by the marker rules, is provoked through the game's own chase
    //             behaviour and hits the player; (2) an arrow fired with a camel as its owner hits the player (projectiles
    //             must lead back to whoever fired them). Each attacker must show red right after the hit, its template must
    //             be remembered in hostile-templates.txt in the world's map cache, and another of its kind far away, not
    //             chasing, must show yellow instead of green.

    static readonly List<string> attackers = [];

    static void NoteAttacker(ComponentCreature? attacker) =>
        attackers.Add(attacker is null ? "-" : attacker.Entity.ValuesDictionary.DatabaseObject.Name);

    static Entity? SpawnAt(string template, Vector3 position)
    {
        Entity? entity;
        try { entity = DatabaseManager.CreateEntity(GameManager.Project, template, false); }
        catch (Exception e) { notes.Add($"{template}: {e.GetType().Name} {e.Message}"); return null; }
        entity.FindComponent<ComponentBody>(true).Position = position;
        if (entity.FindComponent<ComponentSpawn>() is { } spawn) spawn.SpawnDuration = 0f;
        GameManager.Project.AddEntity(entity);
        return entity;
    }

    static string KindOf(ComponentCreature creature) =>
        typeof(TravelMapComponent).GetMethod("GetCreatureMarkerKind", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Map, [creature])!.ToString()!;

    static IEnumerable<bool> Attack()
    {
        foreach (var x in EnterWorld(Summer, gameMode: GameMode.Challenging)) yield return x;
        NoteDataRoot();
        var player = data.ComponentPlayer;
        var health = player.ComponentHealth;
#if TM_NET
        health.Attacked += NoteAttacker;
#else
        health.Injured += injury => NoteAttacker(injury.Attacker);
#endif
        var terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true);
        var time = GameManager.Project.FindSubsystem<SubsystemTime>(true);
        var at = player.ComponentBody.Position;
        var ground = terrain.Terrain.GetTopHeight((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z)) + 1;
        player.ComponentLocomotion.IsCreativeFlyEnabled = false;
        var stand = new Vector3(MathF.Floor(at.X) + 0.5f, ground + 0.1f, MathF.Floor(at.Z) + 0.5f);
        Teleport(stand);
        void KeepAlive() { health.Health = 1f; }
        foreach (var x in Seconds(2, KeepAlive)) yield return x;
        var hostility = Field<CreatureHostilityMemory>(Map, "_hostility");
        var file = Path.Combine(Path.GetDirectoryName(Field<string>(Store, "_directory"))!, CreatureHostilityMemory.FileName);

        foreach (var (template, how) in new[] { ("Cow_Brown", "melee"), ("Camel", "arrow") })
        {
            var entity = SpawnAt(template, stand + new Vector3(3f, 0.5f, 0f));
            Check(entity is not null, $"{template} spawned");
            var creature = entity!.FindComponent<ComponentCreature>(true);
            foreach (var x in Seconds(1, KeepAlive)) yield return x;
            var before = KindOf(creature);
            metrics[$"{template}_beforeHit"] = before;
            Check(before == "Passive/Land", $"{template} starts green ({before})");

            double hitAt = 0;
            var kindAtHit = "";
            var until = Time.RealTime + 60;
            var lastShot = 0.0;
            while (Time.RealTime < until)
            {
                KeepAlive();
                if (how == "melee")
                {
                    // Provoke it as hitting it would (the game's chase behaviour, "chase when attacked").
                    var chase = entity.FindComponent<ComponentChaseBehavior>(true);
                    if (chase.Target is null) chase.Attack(player, 30f, 60f, true);
                }
                else if (Time.RealTime - lastShot > 1.5)
                {
                    // An arrow owned by the camel, fired from just beside the player at its chest.
                    lastShot = Time.RealTime;
                    var from = player.ComponentBody.Position + new Vector3(1.5f, 1.2f, 0f);
                    var to = player.ComponentBody.Position + new Vector3(0f, 1.0f, 0f);
                    GameManager.Project.FindSubsystem<SubsystemProjectiles>(true)
                        .FireProjectile(Terrain.MakeBlockValue(192, 0, 2), from, Vector3.Normalize(to - from) * 30f, Vector3.Zero, creature);
                }

                if (hostility.IsRecentAttacker(creature, time.GameTime))
                {
                    hitAt = time.GameTime;
                    kindAtHit = KindOf(creature);
                    break;
                }

                yield return true;
            }

            metrics[$"{template}_attackersSeen"] = attackers.Distinct().ToArray();
            Check(hitAt > 0, $"{template} ({how}) hit the player and the map took it as the attacker (injuries came from: {string.Join(", ", attackers.Distinct())})");
            Check(kindAtHit.StartsWith("Chasing/"), $"{template} shows red right after the hit ({kindAtHit})");
            Check(hostility.IsAttackerTemplate(template), $"{template} is remembered as a template that attacks");
            Check(File.Exists(file) && File.ReadAllLines(file).Contains(template), $"{CreatureHostilityMemory.FileName} in the world's map cache names {template}");

            GameManager.Project.RemoveEntity(entity, true);
            var far = SpawnAt(template, stand + new Vector3(40f, 0.5f, 40f));
            Check(far is not null, $"second {template} spawned far away");
            foreach (var x in Seconds(2, KeepAlive)) yield return x;
            var farKind = KindOf(far!.FindComponent<ComponentCreature>(true));
            metrics[$"{template}_anotherOfItsKind"] = farKind;
            Check(farKind == "Provokable/Land", $"another {template}, far away and not chasing, shows yellow ({farKind})");
            GameManager.Project.RemoveEntity(far, true);
        }

        metrics["rememberedTemplates"] = File.ReadAllLines(file);
    }

    // ---- markers ---------------------------------------------------------------------------

    // Template, expected marker kind (attitude/body; "a|b" = either, for creatures that may
    // already be chasing the player when asked). Mod templates are only checked when their mod is
    // installed.
    static readonly (string Template, string Expected)[] MarkerCases =
    [
        ("Wolf_Gray", "Provokable/Land|Chasing/Land"),
        ("Bull_Black", "Provokable/Land|Chasing/Land"),
        ("Cow_Brown", "Passive/Land"),
        ("Camel", "Passive/Land"),
        ("Horse_Bay_Saddled", "Friendly/Land"),
        ("Horse_Bay", "Passive/Land"),
        ("Pigeon", "Passive/Bird"),
        ("Bass_Freshwater", "Passive/Fish"),
        ("Shark_Bull", "Provokable/Fish|Chasing/Fish"),
        ("SAGhoul_Weak_01", "Provokable/Land|Chasing/Land"),        // Ghoul mod: registered as a bird
        // Ghoul mod: the shooting head, registered as a bird. It has no chase behaviour and no component the map knows
        // (only ComponentCorpsificeHead), so the 2.9.0 rules make it green; see the plan's execution record.
        // Since it may aim at the player when spawned, red is accepted too; the attack mode checks what happens after a hit.
        ("SAG_CorpsificeHead", "Passive/Land|Chasing/Land"),
        ("ScTacticalEnemy", "Chasing/Land"),        // CS weapons: bandit, registered as LandOther
        ("ScTacticalCT", "Friendly/Land"),          // CS weapons: teammate, registered as LandOther
        ("ScTacticalHostage", "Friendly/Land"),
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
            why[template] = $"predator={traits.IsPredator} aquatic={traits.IsAquatic} flies={traits.Flies} "
                + $"chase={traits.DayChaseRange}/{traits.NightChaseRange} touch={traits.ChaseOnTouchProbability} attacked={traits.ChaseWhenAttackedProbability} "
                + $"attacksPlayer={traits.AttacksPlayer} template={traits.TemplateName} "
                + $"target={(chase?.Target is { } t ? (ReferenceEquals(t, data.ComponentPlayer) ? "player" : "other creature") : "-")} "
                + $"components={string.Join(",", traits.ComponentTypeNames.Where(n => n.Contains("Tactical") || n.Contains("Chase") || n.Contains("MD")))}";
        }

        metrics["why"] = why;
        foreach (var (template, expected, _) in spawned)
            Check(expected.Split('|').Contains(results[template]), $"{template} shows as {results[template]} (expected {expected})");
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

// data:/ is %LOCALAPPDATA%/Survivalcraft, shared with the user's own games: the travel map keeps its settings and every
// world's map there. Point it at a folder inside the isolated runtime instead (run_isolated.py clears it per run, except
// for upgrade-new, which reopens what upgrade-old left). Both editions (the multiplayer one through labdeps/).
[HarmonyPatch(typeof(Engine.Storage), "GetDataDirectory")]
public static class IsolatedDataDirectory
{
    public static bool Prefix(ref string __result)
    {
        __result = Path.Combine(AppContext.BaseDirectory, "isolated-data");
        Directory.CreateDirectory(__result);
        return false;
    }
}
