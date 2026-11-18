using GemakTV.Application.Ports;
using GemakTV.Domain;

namespace GemakTV.Application.Tv;

public enum ChannelSyncOutcome
{
    Updated,
    AlreadyUpToDate,
    NothingPublished,
    Failed,
}

public sealed record ChannelSyncResult(ChannelSyncOutcome Outcome, Lineup Lineup, LineupDiff? Diff = null, string? Error = null);

/// <summary>
/// "Check For Updated Channel List": fetch the published lineup and, when it differs from the one on the TV,
/// store it locally. A failed check never touches the local list.
/// </summary>
public sealed class ChannelListSync(IRemoteConfigSource remote, ILocalLineupStore local)
{
    public async Task<ChannelSyncResult> CheckAsync(Lineup current, CancellationToken cancellationToken = default)
    {
        RemoteConfigDocument? document;
        try
        {
            document = await remote.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ChannelSyncResult(ChannelSyncOutcome.Failed, current, Error: ex.Message);
        }

        if (document is null)
            return new ChannelSyncResult(ChannelSyncOutcome.NothingPublished, current);

        var published = document.Config.Lineup;
        var diff = LineupDiff.Between(current, published);
        if (!diff.HasChanges)
            return new ChannelSyncResult(ChannelSyncOutcome.AlreadyUpToDate, current, diff);

        await local.SaveAsync(published, cancellationToken).ConfigureAwait(false);
        return new ChannelSyncResult(ChannelSyncOutcome.Updated, published, diff);
    }
}
