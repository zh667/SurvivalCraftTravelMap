using System.Text.Json;
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

            Assert.Equal(132, keys.Length);
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
}
