using Game;

namespace SurvivalcraftTravelMap.Map;

public sealed class SurvivalcraftTerrainMapSource(SubsystemTerrain terrain) : ITerrainMapSource
{
    private readonly SubsystemTerrain _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));

    public bool IsColumnReady(int x, int z) =>
        _terrain.Terrain.GetChunkAtCell(x, z) is { State: TerrainChunkState.Valid };

    public bool IsChunkSurfaceReady(TerrainChunkCoordinate chunk) =>
        _terrain.Terrain.GetChunkAtCell(chunk.OriginX, chunk.OriginZ) is { } terrainChunk
        && IsSurfaceReadable(terrainChunk.State);

    internal static bool IsSurfaceReadable(TerrainChunkState state) =>
        state >= TerrainChunkState.InvalidPropagatedLight;

    public int GetTopHeight(int x, int z) => _terrain.Terrain.GetTopHeight(x, z);

    public int GetContent(int x, int y, int z) => _terrain.Terrain.GetCellContents(x, y, z);

    public int GetSeasonalTemperature(int x, int z) => _terrain.Terrain.GetSeasonalTemperature(x, z);

    public int GetSeasonalHumidity(int x, int z) => _terrain.Terrain.GetSeasonalHumidity(x, z);
}
