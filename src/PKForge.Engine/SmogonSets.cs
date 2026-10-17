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
        format.Contains("doubles", StringComparison.OrdinalIgnoreCase) || format.Contains("vgc", StringComparison.OrdinalIgnoreCase)
        || format.Contains("battlespot", StringComparison.OrdinalIgnoreCase);

    /// <summary>"gen9ou" -> "OU", "gen9doublesou" -> "Doubles OU", "gen9vgc2025regg" -> "VGC 2025 Reg G".</summary>
    public static string FormatLabel(string format)
    {
        var name = format.StartsWith("gen", StringComparison.OrdinalIgnoreCase) && format.Length > 4 ? format[4..] : format;
        if (name.StartsWith("doubles", StringComparison.OrdinalIgnoreCase))
            return "Doubles " + name[7..].ToUpperInvariant();
        if (name.StartsWith("vgc", StringComparison.OrdinalIgnoreCase))
            return "VGC " + name[3..].ToUpperInvariant();
        return name.ToUpperInvariant();
    }

    private static string Normalize(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>Every set the data holds for <paramref name="species"/>; <paramref name="doubles"/> null = both kinds.</summary>
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
            foreach (var format in document.RootElement.EnumerateObject())
            {
                if (format.Value.ValueKind != JsonValueKind.Object) continue;
                var isDoubles = IsDoublesFormat(format.Name);
                if (doubles is { } only && only != isDoubles) continue;
                foreach (var entry in format.Value.EnumerateObject())
                {
                    if (Normalize(entry.Name) != wanted || entry.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var set in entry.Value.EnumerateObject())
                        if (set.Value.ValueKind == JsonValueKind.Object)
                            found.Add(new SmogonSetInfo(format.Name, entry.Name, set.Name, ToShowdown(entry.Name, set.Value), isDoubles));
                }
            }
        }
        return found;
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
}
