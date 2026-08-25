using System.IO;
using System.Net.Http;
using System.Text.Json;
using RetroShelf.Models;

namespace RetroShelf.Services;

public sealed class CompatibilityService
{
    private static readonly Uri FeedUri = new("https://raw.githubusercontent.com/robby-gridpoint/RetroShelf/refs/heads/master/assets/compatibility.json");
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyDictionary<string, CompatibilityInfo>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(
            FeedUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        Dictionary<string, CompatibilityInfo> entries =
            await JsonSerializer.DeserializeAsync<Dictionary<string, CompatibilityInfo>>(
                stream,
                JsonOptions,
                cancellationToken) ?? [];

        return entries.Values
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ExecutableName))
            .GroupBy(entry => entry.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }
}
