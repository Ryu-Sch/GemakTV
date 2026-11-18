using GemakTV.Domain;

namespace GemakTV.Application.Ports;

/// <summary>The published config together with an opaque tag identifying that exact version (used to detect concurrent edits).</summary>
public sealed record RemoteConfigDocument(RemoteConfig Config, string VersionTag);

/// <summary>Reads the published, encrypted config. Implemented over the GitHub repository.</summary>
public interface IRemoteConfigSource
{
    /// <summary>Returns null when nothing has been published yet.</summary>
    Task<RemoteConfigDocument?> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Writes the config back. Only the manager app and the publisher tool hold credentials for this.</summary>
public interface IRemoteConfigWriter
{
    /// <summary>
    /// Saves <paramref name="config"/>. When <paramref name="expectedVersionTag"/> is given and the stored file
    /// has changed since, throws <see cref="ConcurrencyConflictException"/> instead of overwriting someone else's edit.
    /// </summary>
    /// <returns>The new version tag.</returns>
    Task<string> SaveAsync(RemoteConfig config, string? expectedVersionTag, string message, CancellationToken cancellationToken = default);
}

/// <summary>The lineup stored on the TV itself, so it works offline and survives restarts.</summary>
public interface ILocalLineupStore
{
    Task<Lineup?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Lineup lineup, CancellationToken cancellationToken = default);
}

/// <summary>Fetches a published release, already decrypted, into <paramref name="destination"/>.</summary>
public interface IReleasePackageSource
{
    Task DownloadAsync(AppRelease release, Stream destination, IProgress<long>? bytesWritten, CancellationToken cancellationToken = default);
}

/// <summary>The searchable list of channels the manager can choose from (iptv-org).</summary>
public interface IChannelCatalogSource
{
    Task<IReadOnlyList<CatalogChannel>> LoadAsync(CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Someone else saved the config after we read it.</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>The published file exists but could not be decrypted or understood.</summary>
public sealed class ConfigUnreadableException(string message, Exception? inner = null) : Exception(message, inner);
