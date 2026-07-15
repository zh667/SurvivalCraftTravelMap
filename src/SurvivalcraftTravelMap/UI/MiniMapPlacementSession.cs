using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal sealed class MiniMapPlacementSession
{
    private readonly Vector2 _originalPosition;
    private Vector2? _dragOffset;

    public MiniMapPlacementSession(Vector2 originalPosition)
    {
        _originalPosition = originalPosition;
        PreviewPosition = originalPosition;
    }

    public Vector2 PreviewPosition { get; private set; }

    public bool IsDragging => _dragOffset.HasValue;

    public bool TryBeginDrag(Vector2 pointer, float miniMapSize)
    {
        var size = TravelMapOverlayLayout.MiniMapFootprint(miniMapSize);
        if (!float.IsFinite(pointer.X)
            || !float.IsFinite(pointer.Y)
            || pointer.X < PreviewPosition.X
            || pointer.Y < PreviewPosition.Y
            || pointer.X > PreviewPosition.X + size.X
            || pointer.Y > PreviewPosition.Y + size.Y)
        {
            return false;
        }

        _dragOffset = pointer - PreviewPosition;
        return true;
    }

    public void DragTo(Vector2 pointer, Vector2 guiLogicalSize, float miniMapSize)
    {
        if (!_dragOffset.HasValue || !float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y))
        {
            return;
        }

        PreviewPosition = TravelMapOverlayLayout.ClampToGui(
            pointer - _dragOffset.Value,
            TravelMapOverlayLayout.MiniMapFootprint(miniMapSize),
            guiLogicalSize);
    }

    public void EndDrag() => _dragOffset = null;

    public void Cancel()
    {
        PreviewPosition = _originalPosition;
        _dragOffset = null;
    }

    public Vector2 CreateNormalizedAnchor(Vector2 guiLogicalSize, float miniMapSize) =>
        TravelMapOverlayLayout.NormalizeCustomPosition(
            PreviewPosition,
            guiLogicalSize,
            TravelMapOverlayLayout.MiniMapFootprint(miniMapSize));
}
