using Game;

namespace SurvivalcraftTravelMap.Map;

public sealed class SurvivalcraftTerrainMapSource(SubsystemTerrain terrain) : ITerrainMapSource
{
    private readonly SubsystemTerrain _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));

    public int GetTopHeight(int x, int z) => _terrain.Terrain.GetTopHeight(x, z);

    public int GetContent(int x, int y, int z) => _terrain.Terrain.GetCellContents(x, y, z);

    public int GetSeasonalTemperature(int x, int z) => _terrain.Terrain.GetSeasonalTemperature(x, z);

    public int GetSeasonalHumidity(int x, int z) => _terrain.Terrain.GetSeasonalHumidity(x, z);
}
