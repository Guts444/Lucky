using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucky.Core;

public interface IWebPageReader
{
    Task<ToolExecutionResult> OpenAsync(
        WebBrowserSettings settings,
        string url,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches a trusted page as bounded, cookie-free readable text. This is deliberately a
/// document reader rather than an interactive browser; interactive automation can be supplied
/// through an explicitly configured MCP server.
/// </summary>
public sealed class WebPageReader : IWebPageReader
{
    private const int MaxDownloadBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int MaxTitleCharacters = 1024;

    private static readonly Regex TitlePattern = new(
        "<title[^>]*>(?<title>[\\s\\S]*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex NonContentElements = new(
        "<(script|style|noscript|template|svg|canvas|iframe|object|embed)[^>]*>[\\s\\S]*?</\\1\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex HtmlComment = new(
        "<!--[\\s\\S]*?-->",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex HtmlTags = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Whitespace = new(
        "[\\t\\f\\v ]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHostAsync;

    public WebPageReader(
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHostAsync = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _resolveHostAsync = resolveHostAsync ?? ResolveHostAsync;
    }

    public async Task<ToolExecutionResult> OpenAsync(
        WebBrowserSettings settings,
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return Error(url, "Browser page reading is disabled in Settings.");
        }

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var current) ||
            (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps))
        {
            return Error(url, "Provide an absolute http or https URL.");
        }

        var initialValidation = await ValidateUrlAsync(current, settings.AllowedDomains, cancellationToken).ConfigureAwait(false);
        if (initialValidation is not null)
        {
            return Error(url, initialValidation);
        }

        try
        {
            for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("Lucky/1.0 (+local-first-page-reader)");
                request.Headers.Accept.ParseAdd("text/html, text/plain, application/xhtml+xml, application/json;q=0.8, */*;q=0.1");

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= MaxRedirects)
                    {
                        return Error(url, $"The page exceeded Lucky's redirect limit ({MaxRedirects}).");
                    }

                    var next = response.Headers.Location;
                    if (next is null)
                    {
                        return Error(url, "The page returned a redirect without a destination.");
                    }

                    current = next.IsAbsoluteUri ? next : new Uri(current, next);
                    if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                    {
                        return Error(url, "A redirect left Lucky's trusted browser-domain list.");
                    }

                    var redirectValidation = await ValidateUrlAsync(current, settings.AllowedDomains, cancellationToken).ConfigureAwait(false);
                    if (redirectValidation is not null)
                    {
                        return Error(url, $"A redirect was refused: {redirectValidation}");
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Error(url, $"The page returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (!IsSupportedTextMediaType(mediaType))
                {
                    return Error(url, $"Lucky only reads text and HTML pages; this response is '{mediaType}'.");
                }

                var source = await ReadBoundedTextAsync(response, cancellationToken).ConfigureAwait(false);
                var maxChars = Math.Clamp(settings.MaxPageChars, 1000, 40000);
                var title = ExtractTitle(source);
                var text = ExtractReadableText(source, mediaType);
                if (text.Length > maxChars)
                {
                    text = $"{text[..maxChars].TrimEnd()}\n\n... page text capped at {maxChars:N0} characters";
                }

                var output = new StringBuilder();
                output.AppendLine($"Page: {current}");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    output.AppendLine($"Title: {title}");
                }

                output.AppendLine($"Content type: {mediaType}");
                output.AppendLine();
                output.Append(string.IsNullOrWhiteSpace(text) ? "The page had no readable text." : text);
                return new ToolExecutionResult("web.open", url, output.ToString().Trim());
            }

            return Error(url, $"The page exceeded Lucky's redirect limit ({MaxRedirects}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RegexMatchTimeoutException ex)
        {
            return Error(url, $"The page markup was too complex to read safely: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return Error(url, ex.Message);
        }
    }

    private static bool IsAllowed(Uri uri, IReadOnlyCollection<string>? allowedDomains)
    {
        if (allowedDomains is null || allowedDomains.Count == 0)
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.');
        return allowedDomains.Any(domain =>
        {
            var normalized = domain.Trim().Trim('.');
            return normalized.Length > 0 &&
                   (host.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith($".{normalized}", StringComparison.OrdinalIgnoreCase));
        });
    }

    private async Task<string?> ValidateUrlAsync(
        Uri uri,
        IReadOnlyCollection<string>? allowedDomains,
        CancellationToken cancellationToken)
    {
        if (!IsAllowed(uri, allowedDomains))
        {
            return "This URL is not in Lucky's trusted browser-domain list.";
        }

        if (!uri.IsDefaultPort)
        {
            return "Trusted page reading only permits default HTTP (80) or HTTPS (443) ports.";
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHostAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return $"Lucky could not resolve the trusted page host safely: {ex.Message}";
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReservedAddress))
        {
            return "The trusted page host resolved to a private, loopback, link-local, or reserved network address, so Lucky refused the request.";
        }

        return null;
    }

    private static async Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivateOrReservedAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return IPAddress.IsLoopback(address) ||
                   address.IsIPv6LinkLocal ||
                   address.IsIPv6Multicast ||
                   (bytes[0] & 0xFE) == 0xFC;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 0 ||
               octets[0] == 10 ||
               octets[0] == 127 ||
               (octets[0] == 100 && octets[1] is >= 64 and <= 127) ||
               (octets[0] == 169 && octets[1] == 254) ||
               (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
               (octets[0] == 192 && octets[1] == 168) ||
               octets[0] >= 224;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsSupportedTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType is "application/xhtml+xml" or "application/json" or "application/xml";

    private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"Page is larger than {MaxDownloadBytes:N0} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"Page is larger than {MaxDownloadBytes:N0} bytes.");
            }

            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // A bad charset should not turn a readable page into a failed agent turn.
            }
        }

        return encoding.GetString(buffer.ToArray());
    }

    private static string ExtractTitle(string source)
    {
        var match = TitlePattern.Match(source);
        if (!match.Success)
        {
            return "";
        }

        var title = NormalizeText(WebUtility.HtmlDecode(HtmlTags.Replace(match.Groups["title"].Value, " ")));
        return title.Length <= MaxTitleCharacters
            ? title
            : $"{title[..MaxTitleCharacters].TrimEnd()} ... title capped by Lucky";
    }

    private static string ExtractReadableText(string source, string mediaType)
    {
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return source.Trim();
        }

        var withoutContent = NonContentElements.Replace(source, " ");
        var withoutComments = HtmlComment.Replace(withoutContent, " ");
        return NormalizeText(WebUtility.HtmlDecode(HtmlTags.Replace(withoutComments, " ")));
    }

    private static string NormalizeText(string value)
    {
        var lineNormalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = lineNormalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Whitespace.Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static ToolExecutionResult Error(string? input, string message) =>
        new("web.open", input ?? "", message, IsError: true);
}
