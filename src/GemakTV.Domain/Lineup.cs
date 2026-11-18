namespace GemakTV.Domain;

/// <summary>The ordered list of channels on the TV. Channel ids are unique (case-insensitively).</summary>
public sealed class Lineup
{
    public static readonly Lineup Empty = new([]);

    public Lineup(IEnumerable<Channel> channels)
    {
        var list = channels.ToList();
        var duplicate = list.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Channel '{duplicate.Key}' appears more than once.", nameof(channels));
        Channels = list.AsReadOnly();
    }

    public IReadOnlyList<Channel> Channels { get; }

    public int Count => Channels.Count;

    public bool HasPlayableChannel => Channels.Any(c => c.IsPlayable);

    public int IndexOf(string? id)
    {
        if (id is null) return -1;
        for (var i = 0; i < Channels.Count; i++)
            if (string.Equals(Channels[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public Channel? Find(string? id) => IndexOf(id) is var i and >= 0 ? Channels[i] : null;

    public bool Contains(string? id) => IndexOf(id) >= 0;

    /// <summary>True when both lineups hold the same channels, with the same details, in the same order.</summary>
    public bool ContentEquals(Lineup other) => Channels.SequenceEqual(other.Channels);
}
