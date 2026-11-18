using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;

namespace GemakTV.Tv.Updates;

/// <summary>
/// Installs a downloaded update of GemakTV over itself. As device owner this needs no confirmation; otherwise
/// Android shows its install screen. Either way Android itself refuses an APK that is not signed with the same key.
/// </summary>
public static class SelfInstaller
{
    /// <summary>Checks the package before handing it to Android: same app, same signing key, expected version.</summary>
    /// <returns>Null when it is fine, otherwise the reason.</returns>
    public static string? Verify(Context context, string apkPath, int expectedVersionCode)
    {
        var pm = context.PackageManager!;
#pragma warning disable CA1422, CS0618 // the pre-API 28 signature APIs are what Android 6 has
        var archive = pm.GetPackageArchiveInfo(apkPath, PackageInfoFlags.Signatures);
        if (archive is null) return "The download is not a valid app package.";
        if (archive.PackageName != context.PackageName) return $"The download is a different app ({archive.PackageName}).";
        if (archive.VersionCode != expectedVersionCode) return $"The download is version {archive.VersionCode}, expected {expectedVersionCode}.";

        var mine = pm.GetPackageInfo(context.PackageName!, PackageInfoFlags.Signatures)!.Signatures;
        var theirs = archive.Signatures;
#pragma warning restore CA1422, CS0618
        // Some Android versions do not report archive signatures; the installer still enforces them.
        if (mine is null || theirs is null || theirs.Count == 0) return null;
        var same = mine.Select(s => Convert.ToBase64String(s.ToByteArray()!)).ToHashSet()
            .SetEquals(theirs.Select(s => Convert.ToBase64String(s.ToByteArray()!)));
        return same ? null : "The download is not signed with GemakTV's key.";
    }

    public static void Install(Context context, string apkPath)
    {
        var installer = context.PackageManager!.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        var sessionId = installer.CreateSession(parameters);
        using var session = installer.OpenSession(sessionId);
        using (var input = File.OpenRead(apkPath))
        using (var output = session.OpenWrite("GemakTV.apk", 0, input.Length))
        {
            input.CopyTo(output);
            session.Fsync(output);
        }

        var flags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S) flags |= PendingIntentFlags.Mutable;
        var result = PendingIntent.GetBroadcast(context, sessionId, new Intent(context, typeof(InstallResultReceiver)), flags)!;
        Log.Info("GemakTV", $"Committing install session {sessionId}");
        session.Commit(result.IntentSender);
    }
}

/// <summary>Receives the installer's verdict. On success the process is replaced and GemakTV restarts by itself.</summary>
[BroadcastReceiver(Name = "io.github.ryusch.gemaktv.InstallResultReceiver", Exported = false)]
public sealed class InstallResultReceiver : BroadcastReceiver
{
    private const int StatusPendingUserAction = -1;
    private const int StatusSuccess = 0;

    /// <summary>(succeeded or waiting for the user, message)</summary>
    public static event Action<InstallOutcome, string?>? Finished;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
        var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
        Log.Info("GemakTV", $"Install status {status}: {message}");

        if (status == StatusPendingUserAction)
        {
#pragma warning disable CA1422, CS0618
            if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422, CS0618
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
            }
            Finished?.Invoke(InstallOutcome.WaitingForUser, message);
            return;
        }
        Finished?.Invoke(status == StatusSuccess ? InstallOutcome.Installed : InstallOutcome.Failed, message);
    }
}

public enum InstallOutcome
{
    Installed,
    WaitingForUser,
    Failed,
}
