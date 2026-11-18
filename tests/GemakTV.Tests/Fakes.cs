using GemakTV.Application.Ports;
using GemakTV.Domain;

namespace GemakTV.Tests;

internal sealed class FakeRemote : IRemoteConfigSource, IRemoteConfigWriter
{
    private int _version;

    public RemoteConfigDocument? Document { get; set; }

    public Exception? FailWith { get; set; }

    public int Saves { get; private set; }

    public string? LastMessage { get; private set; }

    public void Publish(RemoteConfig config) => Document = new RemoteConfigDocument(config, $"v{++_version}");

    public Task<RemoteConfigDocument?> GetAsync(CancellationToken cancellationToken = default) =>
        FailWith is { } ex ? Task.FromException<RemoteConfigDocument?>(ex) : Task.FromResult(Document);

    public Task<string> SaveAsync(RemoteConfig config, string? expectedVersionTag, string message, CancellationToken cancellationToken = default)
    {
        if (Document is not null && expectedVersionTag != Document.VersionTag)
            throw new ConcurrencyConflictException("changed meanwhile");
        Saves++;
        LastMessage = message;
        Publish(config);
        return Task.FromResult(Document!.VersionTag);
    }
}

internal sealed class MemoryLineupStore : ILocalLineupStore
{
    public Lineup? Saved { get; private set; }

    public Task<Lineup?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);

    public Task SaveAsync(Lineup lineup, CancellationToken cancellationToken = default)
    {
        Saved = lineup;
        return Task.CompletedTask;
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class BytesPackageSource(byte[] bytes) : IReleasePackageSource
{
    public async Task DownloadAsync(AppRelease release, Stream destination, IProgress<long>? bytesWritten, CancellationToken cancellationToken = default)
    {
        await destination.WriteAsync(bytes, cancellationToken);
        bytesWritten?.Report(bytes.Length);
    }
}
