using System.Security.Cryptography;
using GemakTV.Application.Ports;
using GemakTV.Domain;

namespace GemakTV.Application.Tv;

public sealed record AppUpdateCheck(AppUpdateStatus Status, AppRelease? Release, string? Error = null)
{
    public bool Failed => Error is not null;
}

/// <summary>Thrown when a downloaded release does not match what the config promised.</summary>
public sealed class ReleaseVerificationException(string message) : Exception(message);

/// <summary>
/// "Check For Updates": compares the installed version with the published release and downloads it.
/// Installing is platform work and happens outside this class.
/// </summary>
public sealed class AppUpdater(IRemoteConfigSource remote, IReleasePackageSource packages, int installedVersionCode)
{
    public async Task<AppUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var document = await remote.GetAsync(cancellationToken).ConfigureAwait(false);
            var release = document?.Config.App;
            return new AppUpdateCheck(AppUpdatePolicy.Evaluate(installedVersionCode, release), release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AppUpdateCheck(AppUpdateStatus.UpToDate, null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads and decrypts <paramref name="release"/> to <paramref name="destinationPath"/>, then checks its size
    /// and SHA-256. The file is deleted if anything is wrong, so a half-written package is never installed.
    /// </summary>
    /// <param name="progress">Receives 0..1.</param>
    public async Task DownloadAsync(AppRelease release, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        try
        {
            await using (var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var bytes = progress is null ? null : new Progress<long>(b => progress.Report(release.Size > 0 ? Math.Min(1.0, (double)b / release.Size) : 0));
                await packages.DownloadAsync(release, file, bytes, cancellationToken).ConfigureAwait(false);
            }

            var length = new FileInfo(destinationPath).Length;
            if (length != release.Size)
                throw new ReleaseVerificationException($"The update is {length} bytes; expected {release.Size}.");

            var actual = await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ReleaseVerificationException("The update's checksum does not match the published one.");

            progress?.Report(1.0);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
