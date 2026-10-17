using System.Globalization;

namespace PKForge.Engine.Rogue;

/// <summary>
/// Species, move, ability and item tables for Emerald Rogue EX. Generated from the game's own
/// source by tools/rogue/gen_rogue_data.py (the pinned commit is in each file's header) and embedded
/// as rogue.*.tsv. Rogue ids diverge from national ids past the overlap zone, so every id that
/// reaches the shared editor is bridged to PKHeX's tables by national number or by name.
/// </summary>
internal static class RogueData
{
    internal sealed record Species(int Id, string Name, int NatDex, int BaseId, int Form,
        int[] Base, int Type1, int Type2, int[] Abilities, int GenderRatio, int Growth);

    private static readonly Dictionary<int, Species> SpeciesById = LoadSpecies();
    private static readonly Dictionary<int, (string Name, int National)> Moves = LoadNamed("rogue_moves.tsv");
    private static readonly Dictionary<int, int> MovePps = Rows("rogue_moves.tsv").ToDictionary(c => I(c[0]), c => I(c[3]));
    private static readonly Dictionary<int, (string Name, int National)> Abilities = LoadNamed("rogue_abilities.tsv");
    private static readonly Dictionary<int, (string Name, int National, int Pocket)> Items = LoadItems();
    private static readonly Dictionary<(int, int), int> SpeciesByNational = BuildSpeciesReverse();
    private static readonly Dictionary<int, int> MoveReverse = Reverse(Moves);
    private static readonly Dictionary<int, int> AbilityReverse = Reverse(Abilities);
    private static readonly Dictionary<int, int> ItemReverse = BuildItemReverse();

    public const int MaxLevel = 100;

    internal static IEnumerable<string[]> Rows(string file)
    {
        using var stream = typeof(RogueData).Assembly.GetManifestResourceStream("rogue." + file)
            ?? throw new InvalidOperationException($"Missing embedded Rogue table {file}.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            yield return line.Split('\t');
        }
    }

    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    private static Dictionary<int, Species> LoadSpecies()
    {
        var map = new Dictionary<int, Species>();
        foreach (var c in Rows("rogue_species.tsv"))
        {
            map[I(c[0])] = new Species(I(c[0]), c[1], I(c[2]), I(c[3]), I(c[4]),
                [I(c[5]), I(c[6]), I(c[7]), I(c[9]), I(c[10]), I(c[8])], // file: hp atk def spe spa spd -> app HP Atk Def SpA SpD Spe
                I(c[11]), I(c[12]), [I(c[13]), I(c[14]), I(c[15])], I(c[16]), I(c[17]));
        }
        return map;
    }

    private static Dictionary<int, (string, int)> LoadNamed(string file)
    {
        var map = new Dictionary<int, (string, int)>();
        foreach (var c in Rows(file))
            map[I(c[0])] = (c[1], I(c[2]));
        return map;
    }

    private static Dictionary<int, (string, int, int)> LoadItems()
    {
        var map = new Dictionary<int, (string, int, int)>();
        foreach (var c in Rows("rogue_items.tsv"))
            map[I(c[0])] = (c[1], I(c[3]), I(c[2]));
        return map;
    }

    private static Dictionary<(int, int), int> BuildSpeciesReverse()
    {
        var map = new Dictionary<(int, int), int>();
        foreach (var s in SpeciesById.Values.OrderBy(s => s.Id))
            if (s.NatDex > 0)
                map.TryAdd((s.NatDex, s.Form), s.Id);
        return map;
    }

    private static Dictionary<int, int> Reverse(Dictionary<int, (string Name, int National)> table)
    {
        var map = new Dictionary<int, int>();
        foreach (var (id, entry) in table.OrderBy(e => e.Key))
            if (entry.National > 0)
                map.TryAdd(entry.National, id);
        return map;
    }

    private static Dictionary<int, int> BuildItemReverse()
    {
        var map = new Dictionary<int, int>();
        foreach (var (id, entry) in Items.OrderBy(e => e.Key))
            if (entry.National > 0)
                map.TryAdd(entry.National, id);
        return map;
    }

    // ── species ──
    public static bool IsKnownSpecies(int id) => SpeciesById.TryGetValue(id, out var s) && s.NatDex > 0;
    public static string SpeciesName(int id) => SpeciesById.TryGetValue(id, out var s) ? s.Name : $"#{id}";
    /// <summary>One Rogue species id per national dex number (the base form), for dex counting.</summary>
    public static IReadOnlyList<int> BaseSpeciesIds { get; } =
        [.. SpeciesById.Values.Where(s => s.NatDex > 0 && s.Form == 0).GroupBy(s => s.NatDex).Select(g => g.Min(s => s.Id)).OrderBy(id => id)];

    public static int NationalIdOf(int id) => SpeciesById.TryGetValue(id, out var s) ? s.NatDex : 0;
    public static int FormOf(int id) => SpeciesById.TryGetValue(id, out var s) ? s.Form : 0;

    /// <summary>Rogue species id for a national dex number and form (form 0 as the fallback).</summary>
    public static int SpeciesFromNational(int national, int form = 0) =>
        SpeciesByNational.TryGetValue((national, form), out var id) ? id
        : SpeciesByNational.TryGetValue((national, 0), out var baseId) ? baseId : 0;

    public static int[] BaseStats(int id) => SpeciesById.TryGetValue(id, out var s) ? s.Base : [1, 1, 1, 1, 1, 1];

    public static IReadOnlyList<int> TypesOf(int id)
    {
        if (!SpeciesById.TryGetValue(id, out var s)) return [0, 0];
        return [PkHeXType(s.Type1), PkHeXType(s.Type2)];
    }

    /// <summary>Expansion types skip ???(9); PKHeX's Fire..Fairy follow Steel directly.</summary>
    private static int PkHeXType(int type) => type >= 10 ? type - 1 : type;

    public static (int A1, int A2, int Hidden) AbilityIds(int id) =>
        SpeciesById.TryGetValue(id, out var s) ? (s.Abilities[0], s.Abilities[1], s.Abilities[2]) : (0, 0, 0);

    /// <summary>The ability in slot 0/1/2 (slot 1 falls back to slot 0 when the species has one).</summary>
    public static int AbilityOf(int species, int slot)
    {
        var (a1, a2, hidden) = AbilityIds(species);
        return slot switch { 2 when hidden != 0 => hidden, 1 when a2 != 0 => a2, _ => a1 };
    }

    /// <summary>Gender ratio byte: 0 male only, 254 female only, 255 genderless, else the female threshold.</summary>
    public static int GenderRatio(int id) => SpeciesById.TryGetValue(id, out var s) ? s.GenderRatio : 255;

    // ── moves / abilities / items ──
    public static string MoveName(int id) => Moves.TryGetValue(id, out var e) ? e.Name : $"#{id}";
    public static int MoveToNational(int id) => Moves.TryGetValue(id, out var e) ? e.National : 0;
    public static int MoveFromNational(int national) => MoveReverse.GetValueOrDefault(national);
    public static int MoveBasePp(int id) => MovePps.GetValueOrDefault(id);
    public static string AbilityName(int id) => Abilities.TryGetValue(id, out var e) ? e.Name : $"#{id}";
    public static int AbilityToNational(int id) => Abilities.TryGetValue(id, out var e) ? e.National : 0;
    public static int AbilityFromNational(int national) => AbilityReverse.GetValueOrDefault(national);
    public static string ItemName(int id) => id == 0 ? "None" : Items.TryGetValue(id, out var e) ? e.Name : $"#{id}";
    public static int ItemToNational(int id) => Items.TryGetValue(id, out var e) ? e.National : 0;
    public static int ItemFromNational(int national) => ItemReverse.GetValueOrDefault(national);
    public static int ItemPocket(int id) => Items.TryGetValue(id, out var e) ? e.Pocket : 0;
    public static int ItemCount => Items.Count == 0 ? 0 : Items.Keys.Max() + 1;
    public static IEnumerable<int> ItemIds => Items.Keys.Where(id => id != 0);

    // ── experience / stats ──
    /// <summary>Rogue replaces every growth curve with a flat 300 exp per level (Rogue_ModifyExperienceTables).</summary>
    public static uint ExperienceAtLevel(int level) => (uint)(Math.Clamp(level, 1, MaxLevel) * 300);

    public static int LevelForExperience(uint experience) => (int)Math.Clamp(experience / 300, 1, MaxLevel);

    private static readonly int[] NatureStatOrder = [1, 2, 5, 3, 4]; // Atk, Def, Spe, SpA, SpD in app order

    public static int[] ComputeStats(int species, int level, int nature, IReadOnlyList<int> ivs, IReadOnlyList<int> evs)
    {
        var b = BaseStats(species);
        var stats = new int[6];
        stats[0] = species == 292 ? 1 : (2 * b[0] + ivs[0] + evs[0] / 4) * level / 100 + level + 10;
        for (var i = 1; i < 6; i++)
            stats[i] = (2 * b[i] + ivs[i] + evs[i] / 4) * level / 100 + 5;
        var up = nature / 5;
        var down = nature % 5;
        if (up != down)
        {
            stats[NatureStatOrder[up]] = stats[NatureStatOrder[up]] * 110 / 100;
            stats[NatureStatOrder[down]] = stats[NatureStatOrder[down]] * 90 / 100;
        }
        return stats;
    }

    /// <summary>Rogue ball index (BALL_POKE = 0 ... BALL_ROGUE = 27) -> PKHeX Ball id (0 = no equivalent).</summary>
    private static readonly int[] BallToPkHeX =
        [4, 3, 2, 1, 12, 14, 6, 8, 7, 13, 10, 15, 9, 11, 18, 19, 23, 22, 21, 17, 20, 25, 5, 24, 0, 26, 16, 0];

    public static int BallToDisplay(int ball) => (uint)ball < BallToPkHeX.Length && BallToPkHeX[ball] > 0 ? BallToPkHeX[ball] : 4;

    public static bool TryStoreBall(int pkhexBall, out int ball)
    {
        for (var i = 0; i < BallToPkHeX.Length; i++)
        {
            if (BallToPkHeX[i] != pkhexBall || pkhexBall == 0) continue;
            ball = i;
            return true;
        }
        ball = 0;
        return false;
    }
}
