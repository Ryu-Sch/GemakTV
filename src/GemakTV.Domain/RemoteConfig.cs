namespace GemakTV.Domain;

/// <summary>
/// Everything the TV learns from the repository: the channel lineup and the latest app release.
/// Stored as one encrypted file; every save bumps <see cref="Revision"/>.
/// </summary>
public sealed record RemoteConfig(
    long Revision,
    DateTimeOffset UpdatedUtc,
    string UpdatedBy,
    Lineup Lineup,
    AppRelease? App)
{
    /// <summary>Version of the file layout. Readers refuse files from a newer schema instead of misreading them.</summary>
    public const int CurrentSchema = 1;

    public static RemoteConfig Initial(Lineup lineup, string by, DateTimeOffset now) => new(1, now, by, lineup, null);

    public RemoteConfig WithLineup(Lineup lineup, string by, DateTimeOffset now) =>
        this with { Lineup = lineup, Revision = Revision + 1, UpdatedBy = by, UpdatedUtc = now };

    public RemoteConfig WithApp(AppRelease release, string by, DateTimeOffset now) =>
        this with { App = release, Revision = Revision + 1, UpdatedBy = by, UpdatedUtc = now };
}
