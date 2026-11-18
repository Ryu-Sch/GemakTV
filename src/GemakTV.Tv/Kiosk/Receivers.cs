using Android.App.Admin;
using Android.Content;
using Android.Util;

namespace GemakTV.Tv.Kiosk;

/// <summary>
/// Device-admin receiver. Making GemakTV the device owner is a one-time step over adb:
/// <c>adb shell dpm set-device-owner io.github.ryusch.gemaktv/.AdminReceiver</c>
/// </summary>
[BroadcastReceiver(Name = "io.github.ryusch.gemaktv.AdminReceiver", Permission = "android.permission.BIND_DEVICE_ADMIN", Exported = true)]
[MetaData("android.app.device_admin", Resource = "@xml/device_admin")]
[IntentFilter(new[] { DeviceAdminReceiver.ActionDeviceAdminEnabled })]
public sealed class AdminReceiver : DeviceAdminReceiver;

/// <summary>
/// Brings GemakTV up after the box boots and right after it updated itself. (As the home screen it would usually
/// come up anyway; this covers boxes whose vendor launcher insists on starting first.)
/// </summary>
[BroadcastReceiver(Name = "io.github.ryusch.gemaktv.StartupReceiver", Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, "android.intent.action.QUICKBOOT_POWERON" })]
public sealed class StartupReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        Log.Info("GemakTV", $"Starting after {intent?.Action}");
        var start = new Intent(context, typeof(MainActivity));
        start.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        context.StartActivity(start);
    }
}
