namespace GemakTV.Domain;

/// <summary>A TV channel as shown to the viewer: a name, an optional logo and the streams that carry it.</summary>
public sealed class Channel : IEquatable<Channel>
{
    public Channel(string id, string name, string? logoUrl, IEnumerable<StreamSource>? streams = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A channel needs an id.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A channel needs a name.", nameof(name));
        Id = id.Trim();
        Name = name.Trim();
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
        Streams = DistinctByUrl(streams ?? []);
    }

    /// <summary>Stable identifier. For catalogue channels this is the iptv-org id, e.g. <c>KykNet.za</c>.</summary>
    public string Id { get; }

    public string Name { get; }

    public string? LogoUrl { get; }

    /// <summary>Streams in the order they should be tried.</summary>
    public IReadOnlyList<StreamSource> Streams { get; }

    /// <summary>False when nobody has supplied a stream yet; the TV skips such channels when zapping.</summary>
    public bool IsPlayable => Streams.Count > 0;

    public Channel WithStreams(IEnumerable<StreamSource> streams) => new(Id, Name, LogoUrl, streams);

    public Channel WithName(string name) => new(Id, name, LogoUrl, Streams);

    public bool Equals(Channel? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(LogoUrl, other.LogoUrl, StringComparison.Ordinal)
        && Streams.SequenceEqual(other.Streams);

    public override bool Equals(object? obj) => Equals(obj as Channel);

    public override int GetHashCode() => HashCode.Combine(Id, Name, LogoUrl, Streams.Count);

    public override string ToString() => $"{Name} ({Id}, {Streams.Count} stream(s))";

    private static IReadOnlyList<StreamSource> DistinctByUrl(IEnumerable<StreamSource> streams)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<StreamSource>();
        foreach (var stream in streams)
            if (seen.Add(stream.Url))
                list.Add(stream);
        return list.AsReadOnly();
    }
}
