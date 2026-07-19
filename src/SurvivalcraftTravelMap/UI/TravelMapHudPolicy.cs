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
    bool AllowMiniMapInput,
    bool ShowOpenMapButton,
    bool AllowOpenMapInput);

internal static class TravelMapHudPolicy
{
    internal static TravelMapHudState Evaluate(TravelMapHudSignals signals)
    {
        // The map surface is available whenever the runtime owns the local player's
        // HUD and nothing modal is covering it. The mini map adds its own visibility
        // toggle on top of that; the open-map button is only a fallback for players who
        // hid the mini map, since tapping a visible mini map already opens the large map.
        var showBase = signals.HasUi
            && signals.IsMainPlayer
            && signals.IsRuntimeActive
            && !signals.HasModalSurface
            && !signals.IsLargeMapOpen;
        var showHud = showBase && signals.MiniMapSettingEnabled;

        return new TravelMapHudState(
            ShowMiniMap: showHud,
            ShowTeleportButton: showHud
                && signals.InvitationFeatureAvailable
                && signals.HasOtherPlayers,
            AllowMiniMapInput: showHud && !signals.HasTextEntryFocus,
            ShowOpenMapButton: showBase && !signals.MiniMapSettingEnabled,
            AllowOpenMapInput: showBase && !signals.MiniMapSettingEnabled && !signals.HasTextEntryFocus);
    }
}
