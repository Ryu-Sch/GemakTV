using System.Net;
using System.Security.Cryptography;
using GemakTV.Application.Ports;
using GemakTV.Domain;
using GemakTV.Infrastructure.Crypto;
using GemakTV.Infrastructure.Serialization;

namespace GemakTV.Infrastructure.GitHub;

/// <summary>
/// The published config as an encrypted file (<see cref="GitHubLocation.ConfigPath"/>) on the publish branch.
/// The version tag is the file's git blob SHA, which GitHub checks when the file is replaced.
/// </summary>
public sealed class GitHubConfigStore(GitHubClient client, ConfigKey key, bool allowCdnFallback = false) : IRemoteConfigSource, IRemoteConfigWriter
{
    public async Task<RemoteConfigDocument?> GetAsync(CancellationToken cancellationToken = default)
    {
        GitHubFile? file;
        try
        {
            file = await client.GetFileAsync(GitHubLocation.ConfigPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (allowCdnFallback && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
        {
            // The anonymous API allows 60 requests an hour per address; the CDN copy has no such limit.
            var bytes = await client.GetFileFromCdnAsync(GitHubLocation.ConfigPath, cancellationToken).ConfigureAwait(false);
            file = bytes is null ? null : new GitHubFile(bytes, "cdn:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        return file is null ? null : new RemoteConfigDocument(Decode(file.Content, key), file.Sha);
    }

    public async Task<string> SaveAsync(RemoteConfig config, string? expectedVersionTag, string message, CancellationToken cancellationToken = default)
    {
        if (expectedVersionTag?.StartsWith("cdn:", StringComparison.Ordinal) == true)
            throw new ConcurrencyConflictException("The config was read from the CDN copy; reload it before saving.");
        return await client.PutFileAsync(GitHubLocation.ConfigPath, Encode(config, key), expectedVersionTag, message, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Encode(RemoteConfig config, ConfigKey key) =>
        GtvCipher.Encrypt(ConfigSerializer.Serialize(config), key, GtvContent.Config);

    public static RemoteConfig Decode(byte[] envelope, ConfigKey key)
    {
        byte[] json;
        try
        {
            json = GtvCipher.Decrypt(envelope, key, GtvContent.Config);
        }
        catch (CryptographicException ex)
        {
            throw new ConfigUnreadableException("The published config could not be decrypted. Was it made with a different signing key?", ex);
        }
        return ConfigSerializer.Deserialize(json);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        GitHubRateLimitException => true,
        GitHubApiException api => (int)api.Status >= 500,
        HttpRequestException => true,
        TaskCanceledException => true, // HttpClient timeout
        _ => false,
    };
}

/// <summary>Downloads an encrypted APK from the publish branch and decrypts it while it streams in.</summary>
public sealed class GitHubReleasePackageSource(GitHubClient client, ConfigKey key) : IReleasePackageSource
{
    public async Task DownloadAsync(AppRelease release, Stream destination, IProgress<long>? bytesWritten, CancellationToken cancellationToken = default)
    {
        // Download first, then decrypt: GitHub sometimes closes long downloads early, and a truncated
        // envelope must fail as a download error rather than as a "tampered" decryption error.
        var temp = Path.Combine(Path.GetTempPath(), $"gemaktv-{release.VersionCode}-{Guid.NewGuid():N}.gtv");
        try
        {
            await using (var encrypted = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
                await client.DownloadAsync(release.BlobPath, encrypted, cancellationToken).ConfigureAwait(false);

            await using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var counting = new ProgressStream(destination, bytesWritten);
            await GtvCipher.DecryptAsync(input, counting, key, GtvContent.Package, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Report(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(buffer.Length);
        }

        private void Report(int count)
        {
            _written += count;
            progress?.Report(_written);
        }

        // The destination belongs to the caller.
        protected override void Dispose(bool disposing) { }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
