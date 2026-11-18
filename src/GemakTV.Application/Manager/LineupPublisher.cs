using GemakTV.Application.Ports;
using GemakTV.Domain;

namespace GemakTV.Application.Manager;

public enum PublishOutcome
{
    Published,
    NoChanges,
    Conflict,
}

public sealed record PublishResult(PublishOutcome Outcome, RemoteConfigDocument? Document, string? Message = null);

/// <summary>
/// Saves the manager's lineup into the published config. The app release section is carried over untouched,
/// and an edit made elsewhere in the meantime is reported as a conflict instead of being overwritten.
/// </summary>
public sealed class LineupPublisher(IRemoteConfigSource source, IRemoteConfigWriter writer, IClock clock)
{
    public Task<RemoteConfigDocument?> LoadAsync(CancellationToken cancellationToken = default) => source.GetAsync(cancellationToken);

    public async Task<PublishResult> PublishAsync(Lineup lineup, RemoteConfigDocument? basedOn, string user, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        if (basedOn is not null && basedOn.Config.Lineup.ContentEquals(lineup))
            return new PublishResult(PublishOutcome.NoChanges, basedOn);

        var next = basedOn is null
            ? RemoteConfig.Initial(lineup, user, now)
            : basedOn.Config.WithLineup(lineup, user, now);

        var message = basedOn is null
            ? $"Channel list: first publish ({lineup.Count} channels)"
            : $"Channel list rev {next.Revision}: {LineupDiff.Between(basedOn.Config.Lineup, lineup).Describe()}";

        try
        {
            var tag = await writer.SaveAsync(next, basedOn?.VersionTag, message, cancellationToken).ConfigureAwait(false);
            return new PublishResult(PublishOutcome.Published, new RemoteConfigDocument(next, tag), message);
        }
        catch (ConcurrencyConflictException ex)
        {
            return new PublishResult(PublishOutcome.Conflict, basedOn, ex.Message);
        }
    }
}
