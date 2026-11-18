using GemakTV.Domain;

namespace GemakTV.Application.Manager;

/// <summary>
/// The manager's working copy of the lineup. Changes stay local until they are published, and
/// <see cref="IsDirty"/> says whether there is anything to publish.
/// </summary>
public sealed class LineupEditor
{
    private readonly List<Channel> _channels;

    public LineupEditor(Lineup original)
    {
        Original = original;
        _channels = [.. original.Channels];
    }

    public Lineup Original { get; private set; }

    public Lineup Current => new(_channels);

    public IReadOnlyList<Channel> Channels => _channels;

    public bool IsDirty => !Original.ContentEquals(Current);

    public event EventHandler? Changed;

    public bool Contains(string id) => IndexOf(id) >= 0;

    public Channel? Find(string id) => IndexOf(id) is var i and >= 0 ? _channels[i] : null;

    /// <summary>Adds the channel at the end. Returns false if it is already in the lineup.</summary>
    public bool Add(Channel channel)
    {
        if (Contains(channel.Id)) return false;
        _channels.Add(channel);
        OnChanged();
        return true;
    }

    public bool Remove(string id)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        _channels.RemoveAt(index);
        OnChanged();
        return true;
    }

    /// <summary>Moves a channel up (negative) or down (positive) in the list, clamped to the ends.</summary>
    public bool Move(string id, int offset)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        var target = Math.Clamp(index + offset, 0, _channels.Count - 1);
        if (target == index) return false;
        var channel = _channels[index];
        _channels.RemoveAt(index);
        _channels.Insert(target, channel);
        OnChanged();
        return true;
    }

    /// <summary>Adds a stream address to a channel, first in line when <paramref name="preferFirst"/> is set.</summary>
    public bool AddStream(string id, StreamSource stream, bool preferFirst = false)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        var channel = _channels[index];
        if (channel.Streams.Any(s => string.Equals(s.Url, stream.Url, StringComparison.OrdinalIgnoreCase))) return false;
        _channels[index] = channel.WithStreams(preferFirst ? [stream, .. channel.Streams] : [.. channel.Streams, stream]);
        OnChanged();
        return true;
    }

    public bool RemoveStream(string id, string url)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        var channel = _channels[index];
        var remaining = channel.Streams.Where(s => !string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count == channel.Streams.Count) return false;
        _channels[index] = channel.WithStreams(remaining);
        OnChanged();
        return true;
    }

    public bool MoveStream(string id, string url, int offset)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        var streams = _channels[index].Streams.ToList();
        var from = streams.FindIndex(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return false;
        var to = Math.Clamp(from + offset, 0, streams.Count - 1);
        if (to == from) return false;
        var stream = streams[from];
        streams.RemoveAt(from);
        streams.Insert(to, stream);
        _channels[index] = _channels[index].WithStreams(streams);
        OnChanged();
        return true;
    }

    public bool Rename(string id, string name)
    {
        var index = IndexOf(id);
        if (index < 0 || string.IsNullOrWhiteSpace(name) || _channels[index].Name == name.Trim()) return false;
        _channels[index] = _channels[index].WithName(name);
        OnChanged();
        return true;
    }

    /// <summary>Discards local edits and starts again from <paramref name="published"/>.</summary>
    public void Reset(Lineup published)
    {
        Original = published;
        _channels.Clear();
        _channels.AddRange(published.Channels);
        OnChanged();
    }

    /// <summary>After a successful publish: the current list becomes the new baseline.</summary>
    public void MarkPublished()
    {
        Original = Current;
        OnChanged();
    }

    private int IndexOf(string id) => _channels.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
