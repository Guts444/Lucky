using System.Net;
using System.Text;
using Lucky.Core;

namespace Lucky.Tests;

public sealed class SearxngSearchClientTests
{
    [Fact]
    public async Task SearchAsync_RequestsJsonAndParsesContentOrSnippetResults()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """
            {
              "results": [
                {
                  "title": "Lucky memory",
                  "url": "https://example.test/memory",
                  "content": "Memory service details"
                },
                {
                  "title": "Lucky search",
                  "url": "https://example.test/search",
                  "content": "",
                  "snippet": "Snippet fallback details"
                },
                {
                  "title": "Missing url",
                  "content": "Should be ignored"
                },
                {
                  "title": "Extra result",
                  "url": "https://example.test/extra",
                  "content": "Past the max result limit"
                }
              ]
            }
            """));
        var client = new SearxngSearchClient(new HttpClient(handler));

        var results = await client.SearchAsync("https://search.example/", "lucky memory", 2);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://search.example/search?q=lucky%20memory&format=json", request.RequestUri?.AbsoluteUri);

        Assert.Equal(2, results.Count);
        Assert.Equal(new SearchResult(
            "Lucky memory",
            "https://example.test/memory",
            "Memory service details"), results[0]);
        Assert.Equal(new SearchResult(
            "Lucky search",
            "https://example.test/search",
            "Snippet fallback details"), results[1]);
    }

    [Fact]
    public async Task SearchAsync_WithBlankQueryDoesNotCallHttp()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Blank queries should not issue HTTP requests."));
        var client = new SearxngSearchClient(new HttpClient(handler));

        var results = await client.SearchAsync("https://search.example", "   ", 3);

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
