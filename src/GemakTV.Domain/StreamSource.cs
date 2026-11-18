namespace GemakTV.Domain;

/// <summary>One playable address for a channel. A channel can have several; they are tried in order.</summary>
public sealed record StreamSource
{
    public StreamSource(string url, string? userAgent = null, string? referrer = null)
    {
        if (!IsValidUrl(url))
            throw new ArgumentException($"'{url}' is not an http(s) address.", nameof(url));
        Url = url.Trim();
        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
        Referrer = string.IsNullOrWhiteSpace(referrer) ? null : referrer.Trim();
    }

    public string Url { get; }

    /// <summary>Some providers only serve players that send a particular User-Agent.</summary>
    public string? UserAgent { get; }

    /// <summary>Some providers only serve requests that carry their own site as Referer.</summary>
    public string? Referrer { get; }

    public static bool IsValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(uri.Host);
}
