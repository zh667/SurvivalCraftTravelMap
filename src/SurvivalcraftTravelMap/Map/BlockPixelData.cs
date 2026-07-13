using System.Globalization;
using System.Text.Json;

namespace SurvivalcraftTravelMap.Map;

public sealed record BlockPixelData(
    int BlockIndex,
    Rgba32 Color,
    bool NeedChangeWithEnvironment)
{
    public const int MinimumBlockIndex = 0;
    public const int MaximumBlockIndex = 256;
    public const int RequiredEntryCount = MaximumBlockIndex - MinimumBlockIndex + 1;

    public static IReadOnlyDictionary<int, BlockPixelData> LoadDictionary(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Block pixel color data must be a JSON object containing exactly keys 0..256.");
            }

            var entries = new Dictionary<int, BlockPixelData>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var key)
                    || key is < MinimumBlockIndex or > MaximumBlockIndex)
                {
                    throw new InvalidDataException(
                        $"Block pixel color key '{property.Name}' is outside the required range 0..256.");
                }

                var entry = property.Value;
                var blockIndex = entry.GetProperty(nameof(BlockIndex)).GetInt32();
                if (blockIndex != key)
                {
                    throw new InvalidDataException(
                        $"Block pixel color key {key} declares mismatched BlockIndex {blockIndex}.");
                }

                var color = entry.GetProperty(nameof(Color));
                var data = new BlockPixelData(
                    blockIndex,
                    new Rgba32(
                        color.GetProperty(nameof(Rgba32.R)).GetByte(),
                        color.GetProperty(nameof(Rgba32.G)).GetByte(),
                        color.GetProperty(nameof(Rgba32.B)).GetByte(),
                        color.GetProperty(nameof(Rgba32.A)).GetByte()),
                    entry.GetProperty(nameof(NeedChangeWithEnvironment)).GetBoolean());

                if (!entries.TryAdd(key, data))
                {
                    throw new InvalidDataException($"Block pixel color key {key} is duplicated.");
                }
            }

            return ValidateAndCopy(entries);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Block pixel color JSON is invalid.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException("A block pixel color entry is missing a required property.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("A block pixel color property has an invalid value.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("A block pixel color component must be between 0 and 255.", exception);
        }
    }

    internal static IReadOnlyDictionary<int, BlockPixelData> ValidateAndCopy(
        IReadOnlyDictionary<int, BlockPixelData> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var outOfRange = entries.Keys.FirstOrDefault(
            key => key is < MinimumBlockIndex or > MaximumBlockIndex,
            int.MinValue);
        if (outOfRange != int.MinValue)
        {
            throw new InvalidDataException(
                $"Block pixel color key {outOfRange} is outside the required range 0..256.");
        }

        var missing = Enumerable.Range(MinimumBlockIndex, RequiredEntryCount)
            .Where(key => !entries.ContainsKey(key))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Block pixel color dictionary must contain exactly keys 0..256; missing keys: {string.Join(", ", missing)}.");
        }

        if (entries.Count != RequiredEntryCount)
        {
            throw new InvalidDataException(
                $"Block pixel color dictionary must contain exactly {RequiredEntryCount} keys 0..256.");
        }

        var copy = new Dictionary<int, BlockPixelData>(RequiredEntryCount);
        foreach (var key in Enumerable.Range(MinimumBlockIndex, RequiredEntryCount))
        {
            var value = entries[key]
                ?? throw new InvalidDataException($"Block pixel color entry {key} is null.");
            if (value.BlockIndex != key)
            {
                throw new InvalidDataException(
                    $"Block pixel color key {key} declares mismatched BlockIndex {value.BlockIndex}.");
            }

            copy.Add(key, value);
        }

        return copy;
    }
}
