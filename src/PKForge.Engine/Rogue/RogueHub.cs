using System.Globalization;

namespace PKForge.Engine.Rogue;

/// <summary>One hub building (area) or upgrade with the Build Parts it costs.</summary>
public sealed record HubPart(bool IsArea, int Id, string Name, int Cost, int TargetArea, bool Built);

/// <summary>
/// The Rogue hub's progression: which areas are built and which upgrades are bought. Both live in the
/// Rogue block inside the storage sections as length-prefixed arrays (area bit flags, area coordinates, upgrade
/// bit flags). The block is a stream, so the arrays are found by their shape rather than a fixed offset.
/// </summary>
internal static class RogueHub
{
    public sealed record Layout(int AreaFlags, int AreaCount, int Coords, int CoordSize, int UpgradeFlags, int UpgradeCount);

    public static readonly IReadOnlyList<(bool IsArea, int Id, string Name, int Cost, int Target)> Parts = Load();

    public static int AreaCount => Parts.Count(p => p.IsArea);
    public static int UpgradeCount => Parts.Count(p => !p.IsArea);

    private static List<(bool, int, string, int, int)> Load()
    {
        var list = new List<(bool, int, string, int, int)>();
        foreach (var row in RogueData.Rows("rogue_hub.tsv"))
            if (row.Length >= 5 && int.TryParse(row[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && int.TryParse(row[3], NumberStyles.None, CultureInfo.InvariantCulture, out var cost)
                && int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
                list.Add((row[0] == "area", id, TidyName(row[2]), cost, target));
        return list;
    }

    private static string TidyName(string raw) => raw.Replace("{PKMN}", " Pokémon").Replace("{POKEBLOCK}", "Pokéblock").Replace("  ", " ").Trim();

    /// <summary>Finds the hub arrays in the storage bytes, or null when this save's block has another shape.</summary>
    public static Layout? Locate(byte[] storage)
    {
        var areaBytes = (AreaCount + 7) / 8;
        var upgradeBytes = (UpgradeCount + 7) / 8;
        for (var header = 3; header + 8 < storage.Length; header++)
        {
            // saveVersion u16, blockFormat u8, then the secret id 27615 (0x6BDF).
            if (storage[header] != 0xDF || storage[header + 1] != 0x6B) continue;
            var start = header - 3;
            var limit = Math.Min(storage.Length - 8, start + 9000);
            for (var pos = start + 8; pos < limit; pos++)
            {
                if (U16(storage, pos) != areaBytes) continue;
                var coordsCount = pos + 2 + areaBytes;
                if (coordsCount + 2 > storage.Length || U16(storage, coordsCount) != AreaCount) continue;
                for (var size = 1; size <= 8; size++)
                {
                    var upgradeCount = coordsCount + 2 + AreaCount * size;
                    if (upgradeCount + 2 + upgradeBytes > storage.Length || U16(storage, upgradeCount) != upgradeBytes) continue;
                    return new Layout(pos + 2, AreaCount, coordsCount + 2, size, upgradeCount + 2, UpgradeCount);
                }
            }
            return null;
        }
        return null;
    }

    private static int U16(byte[] data, int at) => data[at] | (data[at + 1] << 8);

    public static bool Get(byte[] storage, int offset, int bit) => (storage[offset + bit / 8] & (1 << (bit % 8))) != 0;

    public static void Set(byte[] storage, int offset, int bit, bool value)
    {
        var mask = (byte)(1 << (bit % 8));
        storage[offset + bit / 8] = (byte)(value ? storage[offset + bit / 8] | mask : storage[offset + bit / 8] & ~mask);
    }
}
