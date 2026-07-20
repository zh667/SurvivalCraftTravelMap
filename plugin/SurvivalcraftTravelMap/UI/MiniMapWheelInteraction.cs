using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;

namespace SurvivalcraftTravelMap.UI;

public sealed class MiniMapWheelInteraction : IDisposable
{
    private readonly TravelMapUiController _controller = new();
    private readonly TravelMapSettings _settings;
    private readonly CoalescingSaveQueue _saveQueue;

    public MiniMapWheelInteraction(
        TravelMapSettings settings,
        Func<CancellationToken, Task> save,
        Action<Exception> reportFailure,
        TimeSpan? debounce = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveQueue = new CoalescingSaveQueue(
            save ?? throw new ArgumentNullException(nameof(save)),
            reportFailure ?? throw new ArgumentNullException(nameof(reportFailure)),
            debounce ?? TimeSpan.FromMilliseconds(300));
    }

    public MapTransform HandleWheel(
        MapTransform transform,
        Vector2 pointer,
        float wheelSteps,
        bool isHovered,
        bool inputBlocked)
    {
        if (inputBlocked)
        {
            return transform;
        }

        var command = _controller.HandleWheel(
            transform,
            pointer,
            wheelSteps,
            isHovered,
            minimumBlocksPerPixel: 0.5f,
            maximumBlocksPerPixel: 8f);
        if (command.Transform is not { } zoomed || command.Kind != TravelMapUiCommandKind.Zoom)
        {
            return transform;
        }

        _settings.MiniMapBlocksPerPixel = zoomed.BlocksPerPixel;
        _saveQueue.RequestSave();
        return zoomed;
    }

    public Task WhenSaveIdleAsync(CancellationToken cancellationToken = default) =>
        _saveQueue.WhenIdleAsync(cancellationToken);

    public void Dispose() => _saveQueue.Dispose();
}
