using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
    public void Loader_checks_conflict_and_registers_only_travel_map_protocols()
    {
        var source = File.ReadAllText(TestPaths.Loader);

        Assert.Contains("ModsManager.GetModEntity(\"34GPSFix\"", source, StringComparison.Ordinal);
        Assert.Contains("DialogsManager.Alert", source, StringComparison.Ordinal);
        Assert.Contains("PackageManager.RegisterPackage(new LegacyGpsPackage())", source, StringComparison.Ordinal);
        Assert.Contains("PackageManager.RegisterPackage(new CoordinateTeleportPackage())", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageId = 60", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AntiCheat", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initial_xdb_has_no_injections()
    {
        var document = XDocument.Load(TestPaths.Xdb);

        Assert.Equal("SurvivalCraftMap", document.Root?.Name.LocalName);
        Assert.Empty(document.Root?.Elements() ?? []);
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
    public void Verifier_rejects_forbidden_content(string content, string expectedMessage)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var package = PackageFixtures.CreatePackage(
            temporaryDirectory.Path,
            new PackageEntry("Assets/forbidden.txt", content));

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

internal sealed record PackageEntry(string Name, string Content);

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
        var packagePath = Path.Combine(directory, "fixture.netmod");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        AddEntry(archive, new PackageEntry("SurvivalcraftTravelMap.dll", "clean mod assembly"));
        AddEntry(archive, new PackageEntry("modinfo.json", "{}"));
        AddEntry(archive, new PackageEntry("mod.netxdb", "<SurvivalCraftMap />"));

        foreach (var entry in additionalEntries)
        {
            AddEntry(archive, entry);
        }

        return packagePath;
    }

    private static void AddEntry(ZipArchive archive, PackageEntry fixture)
    {
        var entry = archive.CreateEntry(fixture.Name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(fixture.Content);
    }
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
