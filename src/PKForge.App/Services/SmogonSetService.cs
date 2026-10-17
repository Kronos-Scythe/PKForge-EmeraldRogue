using PKForge.App.Views;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>
/// Online Smogon sets: downloads the published set list (pkmn.github.io/smogon, cached on the device for a week),
/// then lets the player pick a species, singles or doubles, and one of its sets. The pick comes back as
/// Showdown text, which the existing set importers already turn into a Pokémon for the open game.
/// </summary>
public static class SmogonSetService
{
    // The repository file is the source of the published site; the site is the fallback.
    private static readonly string[] Urls =
    [
        "https://raw.githubusercontent.com/pkmn/smogon/main/data/sets/gen9.json",
        "https://pkmn.github.io/smogon/data/sets/gen9.json",
    ];
    private static readonly TimeSpan CacheLife = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static string CachePath => Path.Combine(FileSystem.CacheDirectory, "smogon-gen9-sets.json");

    /// <summary>The raw set data: the fresh cache, else a download (kept for next time), else a stale cache.</summary>
    public static async Task<(string? Json, string? Error)> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = CachePath;
        var cached = File.Exists(path) ? new FileInfo(path) : null;
        if (cached is not null && DateTime.UtcNow - cached.LastWriteTimeUtc < CacheLife)
        {
            try { return (await File.ReadAllTextAsync(path, cancellationToken), null); }
            catch (IOException) { /* fall through to a download */ }
        }
        foreach (var url in Urls)
        {
            try
            {
                var json = await Http.GetStringAsync(url, cancellationToken);
                try { await File.WriteAllTextAsync(path, json, cancellationToken); } catch (IOException) { /* cache is a convenience */ }
                return (json, null);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
            {
                // try the next source
            }
        }
        if (cached is not null)
        {
            try { return (await File.ReadAllTextAsync(path, cancellationToken), null); } catch (IOException) { }
        }
        return (null, "Could not reach the Smogon set data. Check the internet connection and try again.");
    }

    public static async Task<(IReadOnlyList<SmogonSetInfo> Sets, string? Error)> FindAsync(
        string species, bool? doubles, CancellationToken cancellationToken = default)
    {
        var (json, error) = await LoadAsync(cancellationToken);
        if (json is null) return ([], error);
        return (await Task.Run(() => SmogonSets.Find(json, species, doubles), cancellationToken), null);
    }

    private static readonly string[] SinglesStats = ["gen9ou", "gen9ubers", "gen9uu"];
    private static readonly string[] DoublesStats = ["gen9doublesou", "gen9vgc2026", "gen9vgc2025"];

    /// <summary>Name spellings worth trying for a species ("Urshifu Rapid Strike" -> "Urshifu-Rapid-Strike" -> "Urshifu").</summary>
    private static IEnumerable<string> NameVariants(string species)
    {
        var name = species.Trim();
        yield return name;
        var hyphenated = name.Replace(' ', '-');
        if (hyphenated != name) yield return hyphenated;
        var first = name.Split(' ', '-')[0];
        if (first.Length > 0 && first != name) yield return first;
    }

    /// <summary>
    /// Every Smogon set for one Pokémon in a single list: all curated strategies (every singles and doubles
    /// format, VGC included) plus the most-used build of each ladder. The pick comes back as Showdown text.
    /// </summary>
    public static async Task<string?> PickForSpeciesAsync(Grid host, string species)
    {
        var overlay = LoadingOverlay.Show(host, $"Smogon sets for {species}…", "Downloading the set list and usage statistics (saved on the device after the first time).");
        var sets = new List<(string Label, SmogonSetInfo Set)>();
        string? error = null;
        string used = species.Trim();
        try
        {
            foreach (var name in NameVariants(species))
            {
                var (curated, loadError) = await FindAsync(name, null);
                error ??= loadError;
                if (curated.Count == 0) continue;
                used = name;
                sets.AddRange(curated.Select(set => ($"{SmogonSets.FormatLabel(set.Format)} · {set.Name}", set)));
                break;
            }
            var usage = await Task.WhenAll(
                FindUsageAsync(used, false), FindUsageAsync(used, true));
            sets.AddRange(usage.SelectMany(list => list).Select(set => ($"{SmogonSets.FormatLabel(set.Format)} · most used", set)));
        }
        finally { overlay.Close(); }

        if (sets.Count == 0)
        {
            await PadMenu.ShowAsync(host, "Smogon sets", error ?? $"No Smogon set or usage data was found for \"{species}\".", "OK");
            return null;
        }
        // Same label twice (two sets with one name in a format): keep both, numbered.
        var labelled = sets.GroupBy(x => x.Label).SelectMany(g => g.Select((x, i) => (Label: g.Count() > 1 ? $"{x.Label} ({i + 1})" : x.Label, x.Set))).ToList();
        var shown = labelled.Take(60).ToList();
        var choice = await PadMenu.ShowAsync(host, $"{used} · {shown.Count} Smogon sets", "Pick a strategy to import.",
            shown.Select(x => new PadOption(x.Label, IconPath: "script")).ToArray());
        return choice is null ? null : shown.First(x => x.Label == choice).Set.Showdown;
    }

    /// <summary>One monthly usage file (a few MB), cached for three days; null when it cannot be had.</summary>
    private static async Task<string?> LoadUsageAsync(string format, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, $"smogon-usage-{format}.json");
        var cached = File.Exists(path) ? new FileInfo(path) : null;
        try
        {
            if (cached is not null && DateTime.UtcNow - cached.LastWriteTimeUtc < TimeSpan.FromDays(3))
                return await File.ReadAllTextAsync(path, cancellationToken);
            var json = await Http.GetStringAsync($"https://raw.githubusercontent.com/pkmn/smogon/main/data/stats/{format}.json", cancellationToken);
            try { await File.WriteAllTextAsync(path, json, cancellationToken); } catch (IOException) { }
            return json;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cached is not null)
            {
                try { return await File.ReadAllTextAsync(path, cancellationToken); } catch (IOException) { }
            }
            return null;
        }
    }

    /// <summary>The most-used build of a species in each singles or doubles ladder where it has usage.</summary>
    public static async Task<IReadOnlyList<SmogonSetInfo>> FindUsageAsync(string species, bool doubles, CancellationToken cancellationToken = default)
    {
        var found = new List<SmogonSetInfo>();
        foreach (var format in doubles ? DoublesStats : SinglesStats)
        {
            var json = await LoadUsageAsync(format, cancellationToken);
            if (json is null) continue;
            if (await Task.Run(() => SmogonSets.FromUsage(json, species, format), cancellationToken) is { } set) found.Add(set);
        }
        return found;
    }

    /// <summary>Species -> singles/doubles -> set; the chosen set as Showdown text, or null when the player backs out.</summary>
    public static async Task<string?> PickAsync(Grid host)
    {
        var species = await TextPopup.ShowAsync(host, "Smogon sets", "Which Pokémon? (for example Garchomp)");
        if (string.IsNullOrWhiteSpace(species)) return null;
        var kind = await PadMenu.ShowAsync(host, "Smogon sets", "Which battle format?",
            new PadOption("Singles", IconPath: "script", Detail: "OU, Ubers, UU and the other singles tiers."),
            new PadOption("Doubles", IconPath: "script", Detail: "Doubles OU and VGC sets."),
            new PadOption("Both", IconPath: "script"));
        if (kind is null) return null;
        bool? doubles = kind == "Singles" ? false : kind == "Doubles" ? true : null;
        var source = await PadMenu.ShowAsync(host, "Smogon sets", "Which kind of set?",
            new PadOption("Curated sets", IconPath: "script", Detail: "Smogon's recommended sets from the strategy dex."),
            new PadOption("Most used", IconPath: "script", Detail: "Built from the latest usage statistics: top item, ability, spread and moves."));
        if (source is null) return null;
        if (source == "Most used")
        {
            var wait = LoadingOverlay.Show(host, "Fetching usage stats…", "Downloading the usage files (saved on the device).");
            var usage = new List<SmogonSetInfo>();
            try
            {
                if (doubles != true) usage.AddRange(await FindUsageAsync(species.Trim(), false));
                if (doubles != false) usage.AddRange(await FindUsageAsync(species.Trim(), true));
            }
            finally { wait.Close(); }
            if (usage.Count == 0)
            {
                await PadMenu.ShowAsync(host, "Smogon sets", $"No usage data for \"{species.Trim()}\" (or the internet is unreachable).", "OK");
                return null;
            }
            string UsageLabel(SmogonSetInfo set) => $"{SmogonSets.FormatLabel(set.Format)} · most used";
            var pick = await PadMenu.ShowAsync(host, $"{usage[0].Species} · most used", null,
                usage.Select(set => new PadOption(UsageLabel(set), IconPath: "script")).ToArray());
            return pick is null ? null : usage.First(set => UsageLabel(set) == pick).Showdown;
        }

        var overlay = LoadingOverlay.Show(host, "Fetching Smogon sets…", "Downloading the set list (saved on the device after the first time).");
        (IReadOnlyList<SmogonSetInfo> Sets, string? Error) result;
        try { result = await FindAsync(species.Trim(), doubles); }
        finally { overlay.Close(); }

        if (result.Error is { } error)
        {
            await PadMenu.ShowAsync(host, "Smogon sets", error, "OK");
            return null;
        }
        if (result.Sets.Count == 0)
        {
            await PadMenu.ShowAsync(host, "Smogon sets", $"No Smogon set was found for \"{species.Trim()}\" in {kind.ToLowerInvariant()}.", "OK");
            return null;
        }
        var shown = result.Sets.Take(24).ToList();
        string Label(SmogonSetInfo set) => $"{SmogonSets.FormatLabel(set.Format)} · {set.Name}";
        var choice = await PadMenu.ShowAsync(host, $"{shown[0].Species} · Smogon sets", null,
            shown.Select(set => new PadOption(Label(set), IconPath: "script")).ToArray());
        return choice is null ? null : shown.First(set => Label(set) == choice).Showdown;
    }
}
