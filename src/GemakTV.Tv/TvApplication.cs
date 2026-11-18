using Android.Runtime;
using GemakTV.Tv.Kiosk;
using GemakTV.Tv.Net;

namespace GemakTV.Tv;

[Application(Name = "io.github.ryusch.gemaktv.TvApplication", Label = "GemakTV", Icon = "@mipmap/ic_launcher")]
public sealed class TvApplication(IntPtr handle, JniHandleOwnership transfer) : Android.App.Application(handle, transfer)
{
    public static AppServices Services { get; private set; } = null!;

    public override void OnCreate()
    {
        base.OnCreate();
        CrashGuard.Install(this);
        TrustStore.Install(this);
        Services = new AppServices(this);

        // A build that keeps crashing must not lock the box: hand the device back to its normal launcher for a while.
        if (CrashGuard.IsCrashLooping(this))
        {
            Services.Kiosk.Suspend(TimeSpan.FromMinutes(30), "the app crashed repeatedly");
            CrashGuard.Clear(this);
        }
    }
}
