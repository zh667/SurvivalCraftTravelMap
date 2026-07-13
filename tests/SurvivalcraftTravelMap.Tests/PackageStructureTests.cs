using System.Text.Json;
using System.Xml.Linq;
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
    public void Shared_build_configuration_targets_net10_and_defaults_game_directory()
    {
        var document = XDocument.Load(TestPaths.BuildProps);
        var properties = document.Descendants("PropertyGroup").Elements().ToList();

        Assert.Equal("net10.0", properties.Single(e => e.Name == "TargetFramework").Value);
        Assert.Equal("latest", properties.Single(e => e.Name == "LangVersion").Value);
        Assert.Equal("enable", properties.Single(e => e.Name == "Nullable").Value);
        var gameDirectory = properties.Single(e => e.Name == "SurvivalcraftDir");
        Assert.Equal("'$(SurvivalcraftDir)' == ''", gameDirectory.Attribute("Condition")?.Value);
        Assert.Equal("$(MSBuildThisFileDirectory)..\\", gameDirectory.Value);
    }

    [Fact]
    public void Mod_project_references_game_assemblies_without_copying_them()
    {
        var document = XDocument.Load(TestPaths.ModProject);
        var references = document.Descendants("Reference").ToDictionary(
            element => element.Attribute("Include")?.Value ?? string.Empty);

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
    public void Loader_only_checks_for_the_legacy_package_conflict()
    {
        var source = File.ReadAllText(TestPaths.Loader);

        Assert.Contains("ModsManager.GetModEntity(\"34GPSFix\"", source, StringComparison.Ordinal);
        Assert.Contains("DialogsManager.Alert", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageManager.RegisterPackage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_xdb_has_no_injections()
    {
        var document = XDocument.Load(TestPaths.Xdb);

        Assert.Equal("SurvivalCraftMap", document.Root?.Name.LocalName);
        Assert.Empty(document.Root?.Elements() ?? []);
    }

    [Fact]
    public void Build_script_packages_only_allowed_files_with_a_fixed_timestamp()
    {
        var source = File.ReadAllText(TestPaths.BuildScript);

        Assert.Contains("SurvivalcraftTravelMap.dll", source, StringComparison.Ordinal);
        Assert.Contains("modinfo.json", source, StringComparison.Ordinal);
        Assert.Contains("mod.netxdb", source, StringComparison.Ordinal);
        Assert.Contains("Assets", source, StringComparison.Ordinal);
        Assert.Contains("2000, 1, 1", source, StringComparison.Ordinal);
        Assert.Contains("LastWriteTime", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_script_defines_all_required_rejections()
    {
        var source = File.ReadAllText(TestPaths.VerifyScript);

        Assert.Contains("OrdinalIgnoreCase", source, StringComparison.Ordinal);
        Assert.Contains("Survivalcraft.dll", source, StringComparison.Ordinal);
        Assert.Contains("Engine.dll", source, StringComparison.Ordinal);
        Assert.Contains("EntitySystem.dll", source, StringComparison.Ordinal);
        Assert.Contains("Newtonsoft.Json.dll", source, StringComparison.Ordinal);
        Assert.Contains("LiteNetLib.dll", source, StringComparison.Ordinal);
        Assert.Contains("AntiCheatReportPackage", source, StringComparison.Ordinal);
        Assert.Contains("Package\\s*Id", source, StringComparison.Ordinal);
        Assert.Contains("outside the package allowlist", source, StringComparison.Ordinal);
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
