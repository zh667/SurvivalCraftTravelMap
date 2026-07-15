using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;

namespace SurvivalcraftTravelMap.Persistence;

internal sealed class CaveExplorationStore
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly Dictionary<int, LayerEntry> _layers = [];

    public CaveExplorationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public TimeSpan FlushInterval => TimeSpan.FromSeconds(5);

    public IExploredMapPixelSource GetPixelSource(int centerY) => GetLayer(centerY).Source;

    public bool IsChunkFullyExplored(
        int centerY,
        TerrainChunkCoordinate chunk)
    {
        var store = GetLayer(centerY).Store;
        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        return store.IsRegionFullyExplored(
            coordinate.TileX,
            coordinate.TileZ,
            coordinate.LocalX,
            coordinate.LocalZ,
            TerrainChunkCoordinate.Size,
            TerrainChunkCoordinate.Size)
            && store.IsRegionFullyHeightShaded(
                coordinate.TileX,
                coordinate.TileZ,
                coordinate.LocalX,
                coordinate.LocalZ,
                TerrainChunkCoordinate.Size,
                TerrainChunkCoordinate.Size);
    }

    public ExplorationRecordResult RecordChunk(
        int centerY,
        TerrainChunkCoordinate chunk,
        ReadOnlySpan<Rgba32> colors,
        ReadOnlySpan<byte> heightShades)
    {
        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        var store = GetLayer(centerY).Store;
        if (store.TryAcquireMutation(coordinate.TileX, coordinate.TileZ, out var lease)
            == TileMutationAdmission.Pressure)
        {
            return ExplorationRecordResult.Pressure;
        }

        var admittedLease = lease
            ?? throw new InvalidOperationException("Mutation admission returned no lease.");
        using (admittedLease)
        {
            admittedLease.Tile.SetRegion(
                coordinate.LocalX,
                coordinate.LocalZ,
                TerrainChunkCoordinate.Size,
                TerrainChunkCoordinate.Size,
                colors,
                heightShades);
        }

        return ExplorationRecordResult.Recorded;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        LayerEntry[] layers;
        lock (_sync)
        {
            layers = _layers.Values.ToArray();
        }

        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await layer.Store.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private LayerEntry GetLayer(int centerY)
    {
        centerY = CaveLayer.ClampCenter(centerY);
        lock (_sync)
        {
            if (_layers.TryGetValue(centerY, out var existing))
            {
                return existing;
            }

            var store = new ExplorationTileStore(
                Path.Combine(_directory, "projection_v2", $"y_{centerY:D3}"),
                capacity: 32);
            var created = new LayerEntry(store, new TileStoreMapPixelSource(store, snapshotCapacity: 32));
            _layers.Add(centerY, created);
            return created;
        }
    }

    private sealed record LayerEntry(
        ExplorationTileStore Store,
        TileStoreMapPixelSource Source);
}
