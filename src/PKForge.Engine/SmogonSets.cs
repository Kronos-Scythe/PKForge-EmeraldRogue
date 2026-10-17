using System.Text;
using System.Text.Json;

namespace PKForge.Engine;

/// <summary>One recommended Smogon set for a species in one format, ready to paste as Showdown text.</summary>
public sealed record SmogonSetInfo(string Format, string Species, string Name, string Showdown, bool Doubles);

/// <summary>
/// Reads the published Smogon set data (pkmn.github.io/smogon/data/sets/genN.json): formats at the top,
/// then species, then named sets. The parser is deliberately tolerant: list-valued fields take their
/// first choice, missing fields are skipped, and unknown shapes are ignored rather than thrown.
/// </summary>
public static class SmogonSets
{
    private static readonly (string Json, string Label)[] Stats =
        [("hp", "HP"), ("atk", "Atk"), ("def", "Def"), ("spa", "SpA"), ("spd", "SpD"), ("spe", "Spe")];

    public static bool IsDoublesFormat(string format) =>
        format.Contains("doubles", StringComparison.OrdinalIgnoreCase) || format.Contains("vgc", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ou"] = "OU", ["uu"] = "UU", ["ru"] = "RU", ["nu"] = "NU", ["pu"] = "PU", ["zu"] = "ZU", ["lc"] = "LC", ["nfe"] = "NFE",
        ["ubers"] = "Ubers", ["ubersuu"] = "Ubers UU", ["anythinggoes"] = "Anything Goes", ["cap"] = "CAP",
        ["doublesou"] = "Doubles OU", ["battlestadiumsingles"] = "Battle Stadium Singles", ["battlestadiumdoubles"] = "Battle Stadium Doubles",
        ["nationaldex"] = "National Dex", ["nationaldexdoubles"] = "National Dex Doubles", ["nationaldexubers"] = "National Dex Ubers",
        ["nationaldexuu"] = "National Dex UU", ["nationaldexru"] = "National Dex RU", ["nationaldexmonotype"] = "National Dex Monotype",
        ["monotype"] = "Monotype", ["1v1"] = "1v1", ["stabmons"] = "STABmons", ["almostanyability"] = "Almost Any Ability",
        ["godlygift"] = "Godly Gift", ["balancedhackmons"] = "Balanced Hackmons", ["mixandmega"] = "Mix and Mega",
        ["partnersincrime"] = "Partners in Crime", ["inheritance"] = "Inheritance",
    };

    /// <summary>"ou" -> "OU", "doublesou" -> "Doubles OU", "vgc2025" -> "VGC 2025" (a "gen9" prefix, if any, is dropped).</summary>
    public static string FormatLabel(string format)
    {
        var name = format.Length > 4 && format.StartsWith("gen", StringComparison.OrdinalIgnoreCase) && char.IsDigit(format[3]) ? format[4..] : format;
        if (Labels.TryGetValue(name, out var known)) return known;
        if (name.StartsWith("vgc", StringComparison.OrdinalIgnoreCase)) return "VGC " + name[3..];
        if (name.StartsWith("doubles", StringComparison.OrdinalIgnoreCase)) return "Doubles " + name[7..].ToUpperInvariant();
        return name.Length <= 4 ? name.ToUpperInvariant() : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // The mainstream ladders first; the gimmick metagames after.
    private static readonly string[] Priority =
        ["ou", "ubers", "uu", "ru", "nu", "pu", "zu", "lc", "doublesou", "vgc2025", "vgc2024", "vgc2023", "battlestadiumsingles", "battlestadiumdoubles", "anythinggoes", "nationaldex"];

    private static int Rank(string format)
    {
        var name = format.Length > 4 && format.StartsWith("gen", StringComparison.OrdinalIgnoreCase) && char.IsDigit(format[3]) ? format[4..] : format;
        var index = Array.FindIndex(Priority, p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Priority.Length;
    }

    private static string Normalize(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// Every set the data holds for <paramref name="species"/>; <paramref name="doubles"/> null = both kinds.
    /// Reads the published layout (species, then format, then named set) and, tolerantly, the older
    /// format-first layout.
    /// </summary>
    public static IReadOnlyList<SmogonSetInfo> Find(string json, string species, bool? doubles = null)
    {
        var wanted = Normalize(species);
        var found = new List<SmogonSetInfo>();
        if (wanted.Length == 0) return found;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return found; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return found;

            void AddSets(string speciesName, string format, JsonElement sets)
            {
                var isDoubles = IsDoublesFormat(format);
                if (doubles is { } only && only != isDoubles) return;
                if (sets.ValueKind != JsonValueKind.Object) return;
                foreach (var set in sets.EnumerateObject())
                    if (set.Value.ValueKind == JsonValueKind.Object)
                        found.Add(new SmogonSetInfo(format, speciesName, set.Name, ToShowdown(speciesName, set.Value), isDoubles));
            }

            foreach (var top in document.RootElement.EnumerateObject())
            {
                if (top.Value.ValueKind != JsonValueKind.Object) continue;
                if (Normalize(top.Name) == wanted)
                {
                    foreach (var format in top.Value.EnumerateObject())
                        AddSets(top.Name, format.Name, format.Value);
                    continue;
                }
                foreach (var entry in top.Value.EnumerateObject())
                    if (Normalize(entry.Name) == wanted)
                        AddSets(entry.Name, top.Name, entry.Value);
            }
        }
        return [.. found.OrderBy(set => Rank(set.Format))];
    }

    private static string? First(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.String => value[0].GetString(),
            _ => null,
        };
    }

    private static string StatLine(JsonElement stats, int skipValue)
    {
        var parts = new List<string>();
        foreach (var (key, label) in Stats)
            if (stats.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) && number != skipValue)
                parts.Add($"{number} {label}");
        return string.Join(" / ", parts);
    }

    /// <summary>The set as the Showdown paste format the rest of the app already understands.</summary>
    public static string ToShowdown(string species, JsonElement set)
    {
        var text = new StringBuilder();
        var item = First(set, "item");
        text.Append(species);
        if (!string.IsNullOrWhiteSpace(item)) text.Append(" @ ").Append(item);
        text.AppendLine();
        if (First(set, "ability") is { Length: > 0 } ability) text.Append("Ability: ").AppendLine(ability);
        if (First(set, "teratypes") is { Length: > 0 } tera) text.Append("Tera Type: ").AppendLine(tera);
        if (set.TryGetProperty("evs", out var evs) && evs.ValueKind == JsonValueKind.Object && StatLine(evs, 0) is { Length: > 0 } evLine)
            text.Append("EVs: ").AppendLine(evLine);
        if (First(set, "nature") is { Length: > 0 } nature) text.Append(nature).AppendLine(" Nature");
        if (set.TryGetProperty("ivs", out var ivs) && ivs.ValueKind == JsonValueKind.Object && StatLine(ivs, 31) is { Length: > 0 } ivLine)
            text.Append("IVs: ").AppendLine(ivLine);
        if (set.TryGetProperty("moves", out var moves) && moves.ValueKind == JsonValueKind.Array)
            foreach (var move in moves.EnumerateArray())
            {
                var name = move.ValueKind == JsonValueKind.String ? move.GetString()
                    : move.ValueKind == JsonValueKind.Array && move.GetArrayLength() > 0 ? move[0].GetString() : null;
                if (!string.IsNullOrWhiteSpace(name)) text.Append("- ").AppendLine(name);
            }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The most-used build for a species from a monthly usage-statistics file (pkmn/smogon data/stats/genNformat.json):
    /// its top ability, item, Tera type, EV spread and nature, and the four most-used moves. Null when the
    /// species has no usage in that format.
    /// </summary>
    public static SmogonSetInfo? FromUsage(string json, string species, string format)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("pokemon", out var all) || all.ValueKind != JsonValueKind.Object) return null;
        JsonElement? entry = null; var key = species;
        foreach (var p in all.EnumerateObject())
            if (string.Equals(p.Name, species, StringComparison.OrdinalIgnoreCase)) { entry = p.Value; key = p.Name; break; }
        if (entry is not { } mon) return null;

        static string? Top(JsonElement parent, string name, Func<string, bool>? skip = null)
        {
            if (!parent.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return null;
            string? best = null; var bestValue = double.MinValue;
            foreach (var p in map.EnumerateObject())
            {
                if (skip?.Invoke(p.Name) == true || p.Value.ValueKind != JsonValueKind.Number) continue;
                if (p.Value.GetDouble() > bestValue) { best = p.Name; bestValue = p.Value.GetDouble(); }
            }
            return best;
        }

        var text = new StringBuilder();
        var item = Top(mon, "items", n => n.Equals("Nothing", StringComparison.OrdinalIgnoreCase));
        text.Append(key);
        if (item is not null) text.Append(" @ ").Append(item);
        text.AppendLine();
        if (Top(mon, "abilities") is { } ability) text.Append("Ability: ").AppendLine(ability);
        if (Top(mon, "teraTypes", n => n.Equals("Nothing", StringComparison.OrdinalIgnoreCase)) is { } tera) text.Append("Tera Type: ").AppendLine(tera);
        if (Top(mon, "spreads") is { } spread && spread.Split(':') is { Length: 2 } parts)
        {
            var values = parts[1].Split('/');
            if (values.Length == 6)
            {
                var labels = new[] { "HP", "Atk", "Def", "SpA", "SpD", "Spe" };
                var line = new List<string>();
                for (var k = 0; k < 6; k++)
                    if (int.TryParse(values[k], out var ev) && ev > 0) line.Add($"{ev} {labels[k]}");
                if (line.Count > 0) text.Append("EVs: ").AppendLine(string.Join(" / ", line));
            }
            text.Append(parts[0]).AppendLine(" Nature");
        }
        if (mon.TryGetProperty("moves", out var moves) && moves.ValueKind == JsonValueKind.Object)
            foreach (var move in moves.EnumerateObject()
                         .Where(p => p.Value.ValueKind == JsonValueKind.Number && !p.Name.Equals("Nothing", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(p => p.Value.GetDouble()).Take(4))
                text.Append("- ").AppendLine(move.Name);
        return new SmogonSetInfo(format, key, "Most used", text.ToString().TrimEnd(), IsDoublesFormat(format));
    }
}
