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
    private const string Url = "https://pkmn.github.io/smogon/data/sets/gen9.json";
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
        try
        {
            var json = await Http.GetStringAsync(Url, cancellationToken);
            try { await File.WriteAllTextAsync(path, json, cancellationToken); } catch (IOException) { /* cache is a convenience */ }
            return (json, null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cached is not null)
            {
                try { return (await File.ReadAllTextAsync(path, cancellationToken), null); } catch (IOException) { }
            }
            return (null, "Could not reach the Smogon set data. Check the internet connection and try again.");
        }
    }

    public static async Task<(IReadOnlyList<SmogonSetInfo> Sets, string? Error)> FindAsync(
        string species, bool? doubles, CancellationToken cancellationToken = default)
    {
        var (json, error) = await LoadAsync(cancellationToken);
        if (json is null) return ([], error);
        return (await Task.Run(() => SmogonSets.Find(json, species, doubles), cancellationToken), null);
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
