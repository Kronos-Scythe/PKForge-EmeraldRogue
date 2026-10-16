using System.Globalization;

namespace PKForge.Engine.Rogue;

/// <summary>
/// Emerald Rogue's event flags and script variables: bit and u16 arrays inside SaveBlock1
/// (flags at +0x14D0, 300 bytes; vars right after at +0x15FC, 256 x u16 from VAR 0x4000),
/// offsets measured from the game's own struct layout and cross-checked on a real save.
/// Names come from the Rogue source (constants/flags.h, vars.h).
/// </summary>
internal static class RogueEvents
{
    public const int FlagsOffset = 0x14D0;
    public const int FlagBytes = 300;
    public const int FlagCount = FlagBytes * 8;
    public const int VarsOffset = FlagsOffset + FlagBytes;
    public const int VarCount = 256;
    /// <summary>Flags and vars are contiguous: one slice carries both for the "as opened" baseline.</summary>
    public const int RegionLength = FlagBytes + VarCount * 2;

    public static readonly IReadOnlyDictionary<int, string> FlagNames = Load("rogue_flags.tsv");
    public static readonly IReadOnlyDictionary<int, string> VarNames = Load("rogue_vars.tsv");

    private static Dictionary<int, string> Load(string file)
    {
        var map = new Dictionary<int, string>();
        foreach (var row in RogueData.Rows(file))
            if (row.Length >= 2 && int.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                map.TryAdd(id, row[1]);
        return map;
    }

    /// <summary>"ROGUE_RUN_ACTIVE" -> section "ROGUE", label "Rogue Run Active".</summary>
    public static (string Category, string Label) Describe(string raw)
    {
        var category = raw.Contains('_') ? raw[..raw.IndexOf('_')] : raw;
        var label = string.Join(' ', raw.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length <= 1 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        return (category, label);
    }
}
