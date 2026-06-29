using System.Net.Http.Headers;

namespace Lucky.Tests;

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    AuthenticationHeaderValue? Authorization,
    string? Body,
    string? ContentType);

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, CancellationToken, HttpResponseMessage> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> handler)
        : this((request, body, _) => handler(request, body))
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, string?, CancellationToken, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    public List<CapturedHttpRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new CapturedHttpRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization,
            body,
            request.Content?.Headers.ContentType?.MediaType));

        return _handler(request, body, cancellationToken);
    }
}
