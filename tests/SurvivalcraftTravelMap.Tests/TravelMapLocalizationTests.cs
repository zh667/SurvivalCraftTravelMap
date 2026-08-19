using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapLocalizationTests
{
    // The five languages Survivalcraft ships built-in plus the additional catalogs
    // maintained through Crowdin. The extra languages rely on the companion
    // SurvivalcraftLangPack for their base-game strings; without it they appear as
    // orphan languages in-game, but the mod's own catalogs must still stay complete.
    private static readonly string[] Languages =
    [
        "zh-CN",
        "en-US",
        "es-MX",
        "pt-BR",
        "ru-RU",
        "ar-SA",
        "de-DE",
        "fr-FR",
        "hi-IN",
        "id-ID",
        "it-IT",
        "ja-JP",
        "ko-KR",
        "pl-PL",
        "th-TH",
        "tr-TR",
        "uk-UA",
        "vi-VN",
    ];

    [Fact]
    public void All_language_catalogs_have_identical_non_empty_keys()
    {
        string[]? baseline = null;
        foreach (var language in Languages)
        {
            var path = Path.Combine(
                TestPaths.RepositoryRoot,
                "src",
                "SurvivalcraftTravelMap",
                "Assets",
                "Lang",
                $"{language}.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var travelMap = document.RootElement.GetProperty("TravelMap");
            var entries = travelMap.EnumerateObject().ToArray();
            var keys = entries.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(137, keys.Length);
            Assert.All(entries, entry =>
            {
                Assert.Equal(JsonValueKind.String, entry.Value.ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(entry.Value.GetString()));
            });
            baseline ??= keys;
            Assert.Equal(baseline, keys);
        }
    }

    [Fact]
    public void Format_placeholders_are_consistent_in_every_language()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["scaleFormat"] = ["{0:0.00}"],
            ["settingsUnavailableFormat"] = ["{0}"],
            ["persistenceUnavailableFormat"] = ["{0}"],
            ["currentPositionWaypointFormat"] = ["{0:0.##}", "{1:0.##}", "{2:0.##}"],
            ["mapPointWaypointFormat"] = ["{0:0.##}", "{1:0.##}", "{2:0.##}"],
            ["invitePlayerFormat"] = ["{0}"],
            ["invitationPromptFormat"] = ["{0}"],
        };

        foreach (var language in Languages)
        {
            var path = Path.Combine(
                TestPaths.RepositoryRoot,
                "src",
                "SurvivalcraftTravelMap",
                "Assets",
                "Lang",
                $"{language}.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var travelMap = document.RootElement.GetProperty("TravelMap");
            foreach (var pair in expected)
            {
                var value = travelMap.GetProperty(pair.Key).GetString()!;
                Assert.All(pair.Value, placeholder => Assert.Contains(placeholder, value, StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData("src")]
    [InlineData("plugin")]
    public void Every_key_the_edition_asks_for_exists_in_all_its_catalogs(string edition)
    {
        // The two editions ship separate copies of every UI string, so a string added on one side
        // and forgotten on the other silently falls back to the hard-coded Chinese default.
        var editionRoot = Path.Combine(TestPaths.RepositoryRoot, edition, "SurvivalcraftTravelMap");
        var requested = Directory
            .EnumerateFiles(editionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), """Get\(\s*"([A-Za-z0-9_]+)"\s*,"""))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(requested);
        var languages = Directory
            .EnumerateFiles(Path.Combine(editionRoot, "Assets", "Lang"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToArray();

        Assert.NotEmpty(languages);
        foreach (var language in languages)
        {
            var present = ReadKeys(edition, language).ToHashSet(StringComparer.Ordinal);
            var missing = requested.Except(present).Order(StringComparer.Ordinal).ToArray();
            Assert.True(
                missing.Length == 0,
                $"{edition}/{language}.json is missing: {string.Join(", ", missing)}");
        }
    }

    private static string[] ReadKeys(string edition, string language)
    {
        var path = Path.Combine(
            TestPaths.RepositoryRoot,
            edition,
            "SurvivalcraftTravelMap",
            "Assets",
            "Lang",
            $"{language}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .GetProperty("TravelMap")
            .EnumerateObject()
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

}
