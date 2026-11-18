using GemakTV.Domain;

namespace GemakTV.Application;

/// <summary>A channel as listed in the iptv-org database, with whatever streams iptv-org knows for it.</summary>
public sealed record CatalogChannel(
    string Id,
    string Name,
    IReadOnlyList<string> AltNames,
    string Country,
    IReadOnlyList<string> Categories,
    string? LogoUrl,
    IReadOnlyList<StreamSource> Streams,
    bool IsClosed)
{
    public bool HasStreams => Streams.Count > 0;

    public Channel ToChannel() => new(Id, Name, LogoUrl, Streams);
}
