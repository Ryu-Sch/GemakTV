namespace GemakTV.Domain.Playback;

/// <summary>
/// Channel up/down and "go to channel N" over a lineup. Zapping skips channels that have no stream yet,
/// unless none of them has one (then the viewer still sees the channel and a friendly message).
/// </summary>
public sealed class ChannelNavigator
{
    private Lineup _lineup;

    public ChannelNavigator(Lineup lineup, string? currentId = null)
    {
        _lineup = lineup;
        CurrentIndex = PickStart(lineup, currentId);
    }

    public Lineup Lineup => _lineup;

    /// <summary>-1 when the lineup is empty.</summary>
    public int CurrentIndex { get; private set; }

    public Channel? Current => CurrentIndex >= 0 ? _lineup.Channels[CurrentIndex] : null;

    /// <summary>1-based number shown to the viewer.</summary>
    public int CurrentNumber => CurrentIndex + 1;

    public Channel? Next() => Step(+1);

    public Channel? Previous() => Step(-1);

    /// <summary>Jumps to the 1-based channel number; returns null (and stays put) when there is no such channel.</summary>
    public Channel? GoTo(int number)
    {
        if (number < 1 || number > _lineup.Count) return null;
        CurrentIndex = number - 1;
        return Current;
    }

    public Channel? GoTo(string id)
    {
        var index = _lineup.IndexOf(id);
        if (index < 0) return null;
        CurrentIndex = index;
        return Current;
    }

    /// <summary>Swaps in an updated lineup, staying on the current channel when it still exists.</summary>
    public void ReplaceLineup(Lineup lineup)
    {
        var currentId = Current?.Id;
        _lineup = lineup;
        CurrentIndex = PickStart(lineup, currentId);
    }

    private Channel? Step(int direction)
    {
        var count = _lineup.Count;
        if (count == 0) return null;
        var anyPlayable = _lineup.HasPlayableChannel;
        var index = CurrentIndex;
        for (var i = 0; i < count; i++)
        {
            index = ((index + direction) % count + count) % count;
            if (!anyPlayable || _lineup.Channels[index].IsPlayable)
            {
                CurrentIndex = index;
                return Current;
            }
        }
        return Current;
    }

    private static int PickStart(Lineup lineup, string? preferredId)
    {
        if (lineup.Count == 0) return -1;
        var index = lineup.IndexOf(preferredId);
        if (index >= 0) return index;
        for (var i = 0; i < lineup.Count; i++)
            if (lineup.Channels[i].IsPlayable)
                return i;
        return 0;
    }
}
