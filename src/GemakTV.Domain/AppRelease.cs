namespace GemakTV.Domain;

/// <summary>A published build of the TV app, stored encrypted in the repository.</summary>
/// <param name="VersionCode">Android versionCode; strictly increasing between releases.</param>
/// <param name="VersionName">Human-readable version, e.g. 1.2.0.</param>
/// <param name="BlobPath">Repository path of the encrypted APK on the publish branch.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the decrypted APK.</param>
/// <param name="Size">Size in bytes of the decrypted APK.</param>
public sealed record AppRelease(int VersionCode, string VersionName, string BlobPath, string Sha256, long Size);

public enum AppUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NoReleasePublished,
}

public static class AppUpdatePolicy
{
    /// <summary>
    /// An update is offered only for a higher versionCode: Android refuses downgrades, and a test build
    /// that is newer than the published one should stay put.
    /// </summary>
    public static AppUpdateStatus Evaluate(int installedVersionCode, AppRelease? published) =>
        published is null ? AppUpdateStatus.NoReleasePublished
        : published.VersionCode > installedVersionCode ? AppUpdateStatus.UpdateAvailable
        : AppUpdateStatus.UpToDate;
}
