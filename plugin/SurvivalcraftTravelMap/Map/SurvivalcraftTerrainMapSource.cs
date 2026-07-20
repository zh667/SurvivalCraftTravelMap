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

    public bool IsPassableCell(int x, int y, int z)
    {
        if (!_terrain.Terrain.IsCellValid(x, y, z))
        {
            return false;
        }

        var value = _terrain.Terrain.GetCellValue(x, y, z);
        var content = Terrain.ExtractContents(value);
        return content == 0
            || (uint)content < (uint)BlocksManager.Blocks.Length
            && !BlocksManager.Blocks[content].IsCollidable_(value);
    }

    public bool IsCollidableCell(int x, int y, int z)
    {
        if (!_terrain.Terrain.IsCellValid(x, y, z))
        {
            return false;
        }

        var value = _terrain.Terrain.GetCellValue(x, y, z);
        var content = Terrain.ExtractContents(value);
        return content != 0
            && (uint)content < (uint)BlocksManager.Blocks.Length
            && BlocksManager.Blocks[content].IsCollidable_(value);
    }

    public bool IsFluidCell(int x, int y, int z)
    {
        if (!_terrain.Terrain.IsCellValid(x, y, z))
        {
            return false;
        }

        var content = _terrain.Terrain.GetCellContents(x, y, z);
        return (uint)content < (uint)BlocksManager.Blocks.Length
            && BlocksManager.Blocks[content] is FluidBlock;
    }

    public int GetSeasonalTemperature(int x, int z) => _terrain.Terrain.GetSeasonalTemperature(x, z);

    public int GetSeasonalHumidity(int x, int z) => _terrain.Terrain.GetSeasonalHumidity(x, z);

    public bool IsCrossPlant(int content) =>
        (uint)content < (uint)BlocksManager.Blocks.Length
        && BlocksManager.Blocks[content] is CrossBlock;

    public bool TryGetSolidHeight(int x, int z, out int height)
    {
        var chunk = _terrain.Terrain.GetChunkAtCell(x, z);
        if (chunk is null || !IsSurfaceReadable(chunk.State))
        {
            height = 0;
            return false;
        }

        var topHeight = _terrain.Terrain.GetTopHeight(x, z);
        var content = _terrain.Terrain.GetCellContents(x, topHeight, z);
        height = IsCrossPlant(content) ? Math.Max(0, topHeight - 1) : topHeight;
        return true;
    }

    public bool TryGetEnvironmentColor(
        int content,
        int temperature,
        int humidity,
        out Rgba32 color)
    {
        Engine.Color gameColor;
        switch (content)
        {
            case 8:
            case 19:
                gameColor = BlockColorsMap.Grass.Lookup(temperature, humidity);
                break;
            case 12:
                gameColor = BlockColorsMap.OakLeaves.Lookup(temperature, humidity);
                break;
            case 13:
                gameColor = BlockColorsMap.BirchLeaves.Lookup(temperature, humidity);
                break;
            case 14:
                gameColor = BlockColorsMap.SpruceLeaves.Lookup(temperature, humidity);
                break;
            case 18:
                gameColor = BlockColorsMap.Water.Lookup(temperature, humidity);
                break;
            case 225:
                gameColor = BlockColorsMap.TallSpruceLeaves.Lookup(temperature, humidity);
                break;
            case 256:
                gameColor = BlockColorsMap.MimosaLeaves.Lookup(temperature, humidity);
                break;
            default:
                color = default;
                return false;
        }

        color = new Rgba32(gameColor.R, gameColor.G, gameColor.B, gameColor.A);
        return true;
    }
}
