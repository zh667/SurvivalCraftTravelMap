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
    public void Hud_overlays_are_positioned_in_gui_logical_coordinates()
    {
        var component = File.ReadAllText(TestPaths.Component);
        var miniMapStart = component.IndexOf("private void UpdateMiniMapPosition()", StringComparison.Ordinal);
        var miniMapEnd = component.IndexOf("private void InitializeInvitationUi()", miniMapStart, StringComparison.Ordinal);
        var invitationStart = component.IndexOf("private void PositionInvitationButton()", StringComparison.Ordinal);
        var invitationEnd = component.IndexOf("private ", invitationStart + 8, StringComparison.Ordinal);

        Assert.True(miniMapStart >= 0 && miniMapEnd > miniMapStart);
        Assert.True(invitationStart >= 0 && invitationEnd > invitationStart);
        var miniMapPositioning = component[miniMapStart..miniMapEnd];
        var invitationPositioning = component[invitationStart..invitationEnd];

        Assert.Contains("Player.GuiWidget.ActualSize", miniMapPositioning, StringComparison.Ordinal);
        Assert.Contains("Player.GuiWidget.ActualSize", invitationPositioning, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveCamera.ViewportSize", miniMapPositioning, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveCamera.ViewportSize", invitationPositioning, StringComparison.Ordinal);
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
            Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) > 0);
            Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) > 0);
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
