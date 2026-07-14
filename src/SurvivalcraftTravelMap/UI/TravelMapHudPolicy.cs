namespace SurvivalcraftTravelMap.UI;

internal readonly record struct TravelMapHudSignals(
    bool HasUi,
    bool IsMainPlayer,
    bool IsRuntimeActive,
    bool MiniMapSettingEnabled,
    bool HasModalSurface,
    bool IsLargeMapOpen,
    bool HasOtherPlayers,
    bool InvitationFeatureAvailable,
    bool HasTextEntryFocus);

internal readonly record struct TravelMapHudState(
    bool ShowMiniMap,
    bool ShowTeleportButton,
    bool AllowMiniMapInput);

internal static class TravelMapHudPolicy
{
    internal static TravelMapHudState Evaluate(TravelMapHudSignals signals)
    {
        var showHud = signals.HasUi
            && signals.IsMainPlayer
            && signals.IsRuntimeActive
            && signals.MiniMapSettingEnabled
            && !signals.HasModalSurface
            && !signals.IsLargeMapOpen;

        return new TravelMapHudState(
            ShowMiniMap: showHud,
            ShowTeleportButton: showHud
                && signals.InvitationFeatureAvailable
                && signals.HasOtherPlayers,
            AllowMiniMapInput: showHud && !signals.HasTextEntryFocus);
    }
}
