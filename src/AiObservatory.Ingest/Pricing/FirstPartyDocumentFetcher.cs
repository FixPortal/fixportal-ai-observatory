using System.Net;
using System.Text;

namespace AiObservatory.Ingest.Pricing;

public sealed class FirstPartyDocumentFetcher
{
    private const int MaximumRedirects = 3;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ProductionRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HashSet<string> _allowedHosts;
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly Uri _source;

    public FirstPartyDocumentFetcher(Uri source, IEnumerable<string> allowedHosts)
        : this(source, allowedHosts, null) { }

    internal FirstPartyDocumentFetcher(
        Uri source,
        IEnumerable<string> allowedHosts,
        HttpMessageHandler? handler,
        TimeSpan? requestTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        if (_allowedHosts.Count == 0 || _allowedHosts.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new ArgumentException("At least one valid host is required.", nameof(allowedHosts));
        }

        ValidateDestination(source);
        _source = source;
        _requestTimeout = requestTimeout ?? ProductionRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<FirstPartyDocument> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        var current = _source;
        var redirects = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FirstPartyDocument(current, null, true);
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirects == MaximumRedirects || response.Headers.Location is null)
                {
                    throw new InvalidDataException("The first-party document exceeded its redirect limit.");
                }

                var destination = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                try
                {
                    ValidateDestination(destination);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The redirect left the HTTPS host allowlist.", exception);
                }
                current = destination;
                redirects++;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var content = await ReadUtf8Async(response.Content, timeout.Token);
            return new FirstPartyDocument(current, content, false);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status
            is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadUtf8Async(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The first-party document exceeded the response size limit.");
        }

        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (
            charset is not null
            && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(charset, "utf8", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("The first-party document is not UTF-8.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("The first-party document exceeded the response size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private void ValidateDestination(Uri destination)
    {
        if (
            !destination.IsAbsoluteUri
            || destination.Scheme != Uri.UriSchemeHttps
            || !destination.IsDefaultPort
            || destination.UserInfo.Length != 0
            || destination.Query.Length != 0
            || destination.Fragment.Length != 0
            || !_allowedHosts.Contains(destination.Host)
        )
        {
            throw new ArgumentException("The first-party document URI is outside the HTTPS host allowlist.");
        }
    }
}

public sealed record FirstPartyDocument(Uri FinalUri, string? Content, bool NotModified);
