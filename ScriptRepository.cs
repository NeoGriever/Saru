using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Saru;

public sealed class ScriptRepository
{
    private const string Owner = "NeoGriever";
    private const string Repository = "SaruScripts";
    private const string Branch = "main";
    private static readonly HttpClient Client = CreateClient();

    public async Task<IReadOnlyList<RemoteScript>> GetCatalogAsync()
    {
        var contents = await GetAsync<List<ContentItem>>($"https://api.github.com/repos/{Owner}/{Repository}/contents?ref={Branch}");
        var scripts = contents.Where(item => item.Type == "file" && item.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)).ToArray();
        var catalog = new List<RemoteScript>();
        foreach (var item in scripts)
        {
            var name = item.Name[..^3];
            var versions = await GetAsync<List<CommitItem>>($"https://api.github.com/repos/{Owner}/{Repository}/commits?path={Uri.EscapeDataString(item.Name)}&per_page=100");
            var availableVersions = versions
                .Select(commit => new RemoteScriptVersion(commit.Sha, commit.Commit.Author.Date.ToLocalTime()))
                .ToArray();
            if (availableVersions.Length == 0) continue;
            var description = await GetOptionalTextAsync(RawUrl($"{name}.md", availableVersions[0].Sha));
            catalog.Add(new RemoteScript(name, description, availableVersions));
        }
        return catalog.OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<string> DownloadScriptAsync(string name, string sha) => GetTextAsync(RawUrl(name + ".js", sha));

    private static string RawUrl(string path, string revision) => $"https://raw.githubusercontent.com/{Owner}/{Repository}/{revision}/{Uri.EscapeDataString(path)}";
    private static async Task<T> GetAsync<T>(string url)
    {
        var text = await GetTextAsync(url);
        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("The repository returned invalid data.");
    }
    private static async Task<string> GetOptionalTextAsync(string url)
    {
        using var response = await Client.GetAsync(url);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }
    private static async Task<string> GetTextAsync(string url)
    {
        using var response = await Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Saru");
        return client;
    }

    private sealed class ContentItem { public string Name { get; set; } = ""; public string Type { get; set; } = ""; }
    private sealed class CommitItem { public string Sha { get; set; } = ""; public CommitDetails Commit { get; set; } = new(); }
    private sealed class CommitDetails { public CommitAuthor Author { get; set; } = new(); }
    private sealed class CommitAuthor { public DateTime Date { get; set; } }
}

public sealed record RemoteScript(string Name, string Description, IReadOnlyList<RemoteScriptVersion> Versions);
public sealed record RemoteScriptVersion(string Sha, DateTime Timestamp);
