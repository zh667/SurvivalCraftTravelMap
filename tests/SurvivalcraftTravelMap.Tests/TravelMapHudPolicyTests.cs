using System.Text.Json;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapHudPolicyTests
{
    [Fact]
    public void Single_player_shows_the_minimap_without_the_teleport_button()
    {
        var state = TravelMapHudPolicy.Evaluate(VisibleHudSignals() with
        {
            HasOtherPlayers = false,
        });

        Assert.Equal(new TravelMapHudState(
            ShowMiniMap: true,
            ShowTeleportButton: false,
            AllowMiniMapInput: true), state);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Teleport_button_requires_the_invitation_feature_and_another_player(
        bool invitationFeatureAvailable,
        bool hasOtherPlayers,
        bool expected)
    {
        var state = TravelMapHudPolicy.Evaluate(VisibleHudSignals() with
        {
            InvitationFeatureAvailable = invitationFeatureAvailable,
            HasOtherPlayers = hasOtherPlayers,
        });

        Assert.True(state.ShowMiniMap);
        Assert.Equal(expected, state.ShowTeleportButton);
        Assert.True(state.AllowMiniMapInput);
    }

    [Theory]
    [InlineData(false, true, true, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(true, true, true, true, false, true)]
    public void Base_gate_hides_all_hud_and_disables_input(
        bool hasUi,
        bool isMainPlayer,
        bool isRuntimeActive,
        bool miniMapSettingEnabled,
        bool hasModalSurface,
        bool isLargeMapOpen)
    {
        var state = TravelMapHudPolicy.Evaluate(VisibleHudSignals() with
        {
            HasUi = hasUi,
            IsMainPlayer = isMainPlayer,
            IsRuntimeActive = isRuntimeActive,
            MiniMapSettingEnabled = miniMapSettingEnabled,
            HasModalSurface = hasModalSurface,
            IsLargeMapOpen = isLargeMapOpen,
        });

        Assert.Equal(new TravelMapHudState(false, false, false), state);
    }

    [Fact]
    public void Text_entry_focus_disables_only_minimap_input()
    {
        var state = TravelMapHudPolicy.Evaluate(VisibleHudSignals() with
        {
            HasTextEntryFocus = true,
        });

        Assert.Equal(new TravelMapHudState(
            ShowMiniMap: true,
            ShowTeleportButton: true,
            AllowMiniMapInput: false), state);
    }

    [Fact]
    public void Removing_a_modal_reenables_hud_without_mutating_the_original_settings()
    {
        var settings = new TravelMapSettings
        {
            IsMiniMapVisible = true,
            ShowCoordinates = false,
            UseDayNightTint = false,
            AcceptTeleportInvitations = false,
            MiniMapSize = 320,
            MiniMapBlocksPerPixel = 3.5f,
            LargeMapBlocksPerPixel = 7f,
            LargeMapHotkey = "K",
            NightMinimumBrightness = 0.75f,
        };
        var settingsBefore = JsonSerializer.Serialize(settings);
        var signals = VisibleHudSignals() with
        {
            MiniMapSettingEnabled = settings.IsMiniMapVisible,
            HasModalSurface = true,
        };

        var whileModal = TravelMapHudPolicy.Evaluate(signals);
        var afterModal = TravelMapHudPolicy.Evaluate(signals with { HasModalSurface = false });

        Assert.Equal(new TravelMapHudState(false, false, false), whileModal);
        Assert.Equal(new TravelMapHudState(true, true, true), afterModal);
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
    }

    private static TravelMapHudSignals VisibleHudSignals() => new(
        HasUi: true,
        IsMainPlayer: true,
        IsRuntimeActive: true,
        MiniMapSettingEnabled: true,
        HasModalSurface: false,
        IsLargeMapOpen: false,
        HasOtherPlayers: true,
        InvitationFeatureAvailable: true,
        HasTextEntryFocus: false);
}
