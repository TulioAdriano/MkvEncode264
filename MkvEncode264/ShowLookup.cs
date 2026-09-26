using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>A show as found on TVmaze; <see cref="Episodes"/> holds episode titles in absolute order.</summary>
record ShowInfo(string Name, string? Premiered, List<string> Episodes)
{
    public string? TitleOf(int episodeNumber) =>
        episodeNumber >= 1 && episodeNumber <= Episodes.Count && !string.IsNullOrWhiteSpace(Episodes[episodeNumber - 1])
            ? Episodes[episodeNumber - 1]
            : null;
}

/// <summary>Fetches episode titles from TVmaze (https://www.tvmaze.com/api), which needs no API key.</summary>
static class ShowLookup
{
    public static async Task<ShowInfo?> FetchAsync(string query, bool verbose, Action<string> warn)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MkvEncode264/1.0");

            string showUrl = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(query)}";
            if (verbose) Console.WriteLine($"> GET {showUrl}");
            using HttpResponseMessage response = await http.GetAsync(showUrl);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                warn($"TVmaze has no show matching '{query}'; files will be named without episode titles.");
                return null;
            }
            response.EnsureSuccessStatusCode();

            JsonNode? show = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            int     id        = show?["id"]?.GetValue<int>() ?? throw new InvalidDataException("unexpected TVmaze response");
            string  name      = show?["name"]?.GetValue<string>() ?? query;
            string? premiered = show?["premiered"]?.GetValue<string>();

            string episodesUrl = $"https://api.tvmaze.com/shows/{id}/episodes";
            if (verbose) Console.WriteLine($"> GET {episodesUrl}");
            JsonArray episodes = JsonNode.Parse(await http.GetStringAsync(episodesUrl))?.AsArray() ?? [];

            var titles = episodes
                .Select(e => (Season: e?["season"]?.GetValue<int>() ?? 0,
                              Number: e?["number"]?.GetValue<int>() ?? 0,
                              Name:   e?["name"]?.GetValue<string>() ?? ""))
                .OrderBy(e => e.Season).ThenBy(e => e.Number)
                .Select(e => e.Name)
                .ToList();

            return new ShowInfo(name, premiered, titles);
        }
        catch (Exception ex)
        {
            warn($"Episode titles unavailable ({ex.Message}); files will be named without them.");
            return null;
        }
    }

    /// <summary>Makes a string safe to use inside a file name on Windows, Linux and macOS.</summary>
    public static string SafeFileName(string s)
    {
        s = Regex.Replace(s, @"[<>:""/\\|?*\p{C}]", "");
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        return s.Length > 120 ? s[..120].TrimEnd() : s;
    }
}
