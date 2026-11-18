using Android.Runtime;
using GemakTV.Platform;

namespace GemakTV.Manager;

[Application(Name = "io.github.ryusch.gemaktv.manager.ManagerApplication", Label = "GemakTV Manager", Icon = "@mipmap/ic_launcher")]
public sealed class ManagerApplication(IntPtr handle, JniHandleOwnership transfer) : Android.App.Application(handle, transfer)
{
    public override void OnCreate()
    {
        base.OnCreate();
        TrustStore.Install(this); // older phones lack some root certificates too
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Android.Util.Log.Error("GemakTV.Manager", e.Exception.ToString());
    }
}
