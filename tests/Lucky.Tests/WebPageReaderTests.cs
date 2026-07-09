using System.Net;
using System.Text;
using Lucky.Core;

namespace Lucky.Tests;

public sealed class WebPageReaderTests
{
    [Fact]
    public async Task OpenAsync_ReadsOnlyTrustedHtmlAndStripsNonContent()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html><head><title>Lucky &amp; Docs</title><script>SECRET_SCRIPT_TEXT</script></head><body><h1>Hello</h1><p>Useful page text.</p></body></html>",
                Encoding.UTF8,
                "text/html")
        });
        var reader = new WebPageReader(new HttpClient(handler), PublicResolver);
        var settings = new WebBrowserSettings
        {
            Enabled = true,
            AllowedDomains = ["docs.example.test"]
        };

        var result = await reader.OpenAsync(settings, "https://docs.example.test/guide");

        Assert.False(result.IsError);
        Assert.Equal("web.open", result.Tool);
        Assert.Contains("Title: Lucky & Docs", result.Output, StringComparison.Ordinal);
        Assert.Contains("Hello", result.Output, StringComparison.Ordinal);
        Assert.Contains("Useful page text.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_SCRIPT_TEXT", result.Output, StringComparison.Ordinal);
        Assert.Equal("https://docs.example.test/guide", Assert.Single(handler.Requests).RequestUri?.ToString());
    }

    [Fact]
    public async Task OpenAsync_BlocksUntrustedRedirect()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://untrusted.example.test/secret");
            return response;
        });
        var reader = new WebPageReader(new HttpClient(handler), PublicResolver);
        var settings = new WebBrowserSettings
        {
            Enabled = true,
            AllowedDomains = ["trusted.example.test"]
        };

        var result = await reader.OpenAsync(settings, "https://trusted.example.test/start");

        Assert.True(result.IsError);
        Assert.Contains("redirect", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OpenAsync_DoesNotCallNetworkWhenPageReadingDisabled()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Network should not be called."));
        var reader = new WebPageReader(new HttpClient(handler), PublicResolver);

        var result = await reader.OpenAsync(
            new WebBrowserSettings { Enabled = false, AllowedDomains = ["example.test"] },
            "https://example.test/");

        Assert.True(result.IsError);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenAsync_RefusesPrivateResolvedHostsAndNonDefaultPorts()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Network should not be called."));
        var privateReader = new WebPageReader(
            new HttpClient(handler),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var settings = new WebBrowserSettings { Enabled = true, AllowedDomains = ["docs.example.test"] };

        var privateResult = await privateReader.OpenAsync(settings, "https://docs.example.test/guide");

        Assert.True(privateResult.IsError);
        Assert.Contains("private", privateResult.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);

        var portReader = new WebPageReader(new HttpClient(handler), PublicResolver);
        var portResult = await portReader.OpenAsync(settings, "https://docs.example.test:8443/guide");

        Assert.True(portResult.IsError);
        Assert.Contains("default", portResult.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    private static Task<IPAddress[]> PublicResolver(string _, CancellationToken __) =>
        Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
}
