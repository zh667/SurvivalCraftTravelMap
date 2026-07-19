using System.Text.Json;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapLocalizationTests
{
    // Only the languages Survivalcraft ships built-in, so our catalogs merge into
    // the game's full text tables instead of creating orphan languages that leave
    // the rest of the game UI untranslated.
    private static readonly string[] Languages =
    [
        "zh-CN",
        "en-US",
        "es-MX",
        "pt-BR",
        "ru-RU",
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

            Assert.Equal(130, keys.Length);
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
}
