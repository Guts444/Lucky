using System.Text.Json;

namespace Lucky.Core;

public interface IWebSearchClient
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string searxngUrl,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);
}

public sealed class SearxngSearchClient : IWebSearchClient
{
    private readonly HttpClient _httpClient;

    public SearxngSearchClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string searxngUrl,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var baseUrl = string.IsNullOrWhiteSpace(searxngUrl) ? "http://127.0.0.1:8080" : searxngUrl.Trim();
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{baseUrl.TrimEnd('/')}/search{separator}q={Uri.EscapeDataString(query)}&format=json";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Select(ParseResult)
            .Where(result => !string.IsNullOrWhiteSpace(result.Url))
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }

    private static SearchResult ParseResult(JsonElement element)
    {
        static string Read(JsonElement source, string name)
        {
            return source.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        }

        var title = Read(element, "title");
        var url = Read(element, "url");
        var snippet = Read(element, "content");
        if (string.IsNullOrWhiteSpace(snippet))
        {
            snippet = Read(element, "snippet");
        }

        return new SearchResult(title, url, snippet);
    }
}
