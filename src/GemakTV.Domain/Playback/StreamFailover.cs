namespace GemakTV.Domain.Playback;

/// <summary>
/// Chooses which stream of a channel to try. A round starts with the stream that last worked (if any),
/// then tries every other stream once, in the configured order. When a round is exhausted the caller
/// waits (see <see cref="RetryDelay"/>) and starts a new round.
/// </summary>
public sealed class StreamFailover
{
    private readonly IReadOnlyList<StreamSource> _streams;
    private List<StreamSource> _order = [];
    private int _position;

    public StreamFailover(IReadOnlyList<StreamSource> streams, string? preferredUrl = null)
    {
        _streams = streams;
        PreferredUrl = preferredUrl;
        StartRound();
    }

    /// <summary>The stream that most recently played successfully; tried first in every round.</summary>
    public string? PreferredUrl { get; private set; }

    /// <summary>The stream to play now, or null when the channel has no streams or the round is exhausted.</summary>
    public StreamSource? Current => _position < _order.Count ? _order[_position] : null;

    /// <summary>1-based attempt number within the current round.</summary>
    public int Attempt => _position + 1;

    public int Round { get; private set; }

    public int StreamCount => _streams.Count;

    public bool IsExhausted => Current is null;

    /// <summary>Records that <see cref="Current"/> failed and moves to the next one. False when none are left this round.</summary>
    public bool MoveNext()
    {
        if (_position < _order.Count) _position++;
        return Current is not null;
    }

    /// <summary>Records that <see cref="Current"/> is playing, so it is tried first next time.</summary>
    public void MarkWorking()
    {
        if (Current is { } current) PreferredUrl = current.Url;
    }

    /// <summary>Begins a fresh round, starting from the preferred stream.</summary>
    public void StartRound()
    {
        Round++;
        _position = 0;
        var preferred = _streams.FirstOrDefault(s => string.Equals(s.Url, PreferredUrl, StringComparison.OrdinalIgnoreCase));
        _order = preferred is null
            ? [.. _streams]
            : [preferred, .. _streams.Where(s => !ReferenceEquals(s, preferred))];
    }

    /// <summary>How long to wait before the given round (1-based): quick at first, then every two minutes.</summary>
    public static TimeSpan RetryDelay(int round) => round switch
    {
        <= 1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(10),
        3 => TimeSpan.FromSeconds(30),
        4 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromMinutes(2),
    };
}
