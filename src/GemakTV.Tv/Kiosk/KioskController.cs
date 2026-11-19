using Android.App;
using Android.App.Admin;
using Android.Content;
using Android.Content.PM;
using Android.Util;

namespace GemakTV.Tv.Kiosk;

/// <summary>
/// Single-app kiosk. GemakTV is always the home screen (so it starts on boot and the HOME button returns to it).
/// When it is also the device owner (set once over adb, see docs/SETUP.md) it goes further: the screen is pinned
/// so no other app can be opened, the lock screen and status bar are disabled, other apps are hidden (a hidden app
/// cannot run at all, which frees memory on a 1 GB box), and updates install without a prompt.
/// </summary>
public sealed class KioskController(Context context, TvPreferences prefs)
{
    private const string Tag = "GemakTV";
    private readonly DevicePolicyManager _dpm = (DevicePolicyManager)context.GetSystemService(Context.DevicePolicyService)!;
    private readonly ComponentName _admin = new(context, Java.Lang.Class.FromType(typeof(AdminReceiver)));
    private readonly string _self = context.PackageName!;

    public bool IsDeviceOwner => _dpm.IsDeviceOwnerApp(_self);

    public bool IsSuspended => prefs.KioskSuspendedUntil > DateTimeOffset.UtcNow;

    public string Describe() =>
        !IsDeviceOwner ? "Home-screen mode (not device owner)"
        : IsSuspended ? $"Kiosk paused until {prefs.KioskSuspendedUntil.ToLocalTime():HH:mm} ({prefs.KioskSuspendReason})"
        : $"Kiosk mode, {prefs.HiddenPackages.Count} other apps hidden";

    /// <summary>Applies every device-owner restriction. Safe to call repeatedly.</summary>
    public void ApplyPolicies()
    {
        if (!IsDeviceOwner || IsSuspended) return;
        try
        {
            _dpm.SetLockTaskPackages(_admin, [_self]);
            var home = new IntentFilter(Intent.ActionMain);
            home.AddCategory(Intent.CategoryHome);
            home.AddCategory(Intent.CategoryDefault);
            _dpm.AddPersistentPreferredActivity(_admin, home, new ComponentName(context, Java.Lang.Class.FromType(typeof(MainActivity))));
            _dpm.SetKeyguardDisabled(_admin, true);
            _dpm.SetStatusBarDisabled(_admin, true);
            HideOtherApps();
        }
        catch (Java.Lang.SecurityException ex)
        {
            Log.Warn(Tag, $"Kiosk policy refused: {ex.Message}");
        }
    }

    public void EnterLockTask(Activity activity)
    {
        if (!IsDeviceOwner || IsSuspended || IsInLockTask) return;
        try { activity.StartLockTask(); }
        catch (Java.Lang.Exception ex) { Log.Warn(Tag, $"Lock task refused: {ex.Message}"); }
    }

    /// <summary>Lets a helper open Settings; the lock returns when GemakTV is back in front.</summary>
    public void LeaveLockTaskBriefly(Activity activity)
    {
        if (IsInLockTask)
        {
            try { activity.StopLockTask(); } catch (Java.Lang.Exception) { }
        }
    }

    /// <summary>Turns the kiosk off for a while: apps reappear and the normal launcher is reachable again.</summary>
    public void Suspend(TimeSpan duration, string reason)
    {
        prefs.KioskSuspendedUntil = DateTimeOffset.UtcNow + duration;
        prefs.KioskSuspendReason = reason;
        Log.Info(Tag, $"Kiosk suspended for {duration}: {reason}");
        if (!IsDeviceOwner) return;
        try
        {
            RestoreHiddenApps();
            _dpm.ClearPackagePersistentPreferredActivities(_admin, _self);
            _dpm.SetLockTaskPackages(_admin, []);
            _dpm.SetKeyguardDisabled(_admin, false);
            _dpm.SetStatusBarDisabled(_admin, false);
        }
        catch (Java.Lang.SecurityException ex)
        {
            Log.Warn(Tag, $"Could not release kiosk: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks Android to stop the cached background processes of every other app. Works without device owner;
    /// apps with persistent services may come back, which is why hiding them (device owner) matters more.
    /// </summary>
    public int TrimBackgroundApps()
    {
        var activityManager = (ActivityManager)context.GetSystemService(Context.ActivityService)!;
        var count = 0;
        foreach (var app in context.PackageManager!.GetInstalledApplications(0))
        {
            if (app.PackageName is null || app.PackageName == _self) continue;
            activityManager.KillBackgroundProcesses(app.PackageName);
            count++;
        }
        return count;
    }

    private bool IsInLockTask =>
        ((ActivityManager)context.GetSystemService(Context.ActivityService)!).LockTaskModeState != LockTaskMode.None;

    private void HideOtherApps()
    {
        var keep = PackagesToKeep();
        var hidden = new HashSet<string>(prefs.HiddenPackages);

        // Show again anything an earlier version hid but that is now on the keep list.
        foreach (var package in hidden.Where(keep.Contains).ToList())
        {
            try
            {
                _dpm.SetApplicationHidden(_admin, package, false);
                hidden.Remove(package);
                Log.Info(Tag, $"Unhid {package}");
            }
            catch (Java.Lang.Exception ex)
            {
                Log.Warn(Tag, $"Could not unhide {package}: {ex.Message}");
            }
        }
        foreach (var package in LauncherPackages())
        {
            if (keep.Contains(package) || hidden.Contains(package)) continue;
            try
            {
                if (_dpm.SetApplicationHidden(_admin, package, true))
                {
                    hidden.Add(package);
                    Log.Info(Tag, $"Hid {package}");
                }
            }
            catch (Java.Lang.Exception ex)
            {
                Log.Warn(Tag, $"Could not hide {package}: {ex.Message}");
            }
        }
        prefs.HiddenPackages = hidden;
    }

    private void RestoreHiddenApps()
    {
        foreach (var package in prefs.HiddenPackages)
        {
            try { _dpm.SetApplicationHidden(_admin, package, false); }
            catch (Java.Lang.Exception ex) { Log.Warn(Tag, $"Could not unhide {package}: {ex.Message}"); }
        }
        prefs.HiddenPackages = [];
    }

    /// <summary>Every app that can be opened from a launcher, plus other home screens.</summary>
    private HashSet<string> LauncherPackages()
    {
        var packages = new HashSet<string>();
        foreach (var category in new[] { Intent.CategoryLauncher, Intent.CategoryLeanbackLauncher, Intent.CategoryHome })
        {
            var intent = new Intent(Intent.ActionMain).AddCategory(category);
            foreach (var info in context.PackageManager!.QueryIntentActivities(intent, PackageInfoFlags.MatchDisabledComponents))
                if (info.ActivityInfo?.PackageName is { } name)
                    packages.Add(name);
        }
        return packages;
    }

    /// <summary>
    /// Settings, keyboards and the package installer stay, so a helper can fix Wi-Fi (and type its password)
    /// and updates can install.
    /// </summary>
    private HashSet<string> PackagesToKeep()
    {
        var keep = new HashSet<string>
        {
            _self,
            "com.android.settings", "com.android.tv.settings", "com.google.android.tv.settings",
            "com.android.packageinstaller", "com.google.android.packageinstaller",
            "com.android.systemui", "com.google.android.gms", "com.google.android.gsf",
            "com.android.managedprovisioning", "com.android.shell",
        };
        // Keyboards, found by their input-method service. Including hidden apps matters: a keyboard hidden by an
        // older version no longer shows up in the system's keyboard list, so it would never come back otherwise.
        var keyboards = context.PackageManager!.QueryIntentServices(
            new Intent("android.view.InputMethod"), PackageInfoFlags.MatchUninstalledPackages | PackageInfoFlags.MatchDisabledComponents);
        foreach (var info in keyboards)
            if (info.ServiceInfo?.PackageName is { } name)
                keep.Add(name);
        foreach (var action in new[] { Android.Provider.Settings.ActionSettings, Android.Provider.Settings.ActionWifiSettings })
            foreach (var info in context.PackageManager!.QueryIntentActivities(new Intent(action), 0))
                if (info.ActivityInfo?.PackageName is { } name)
                    keep.Add(name);
        return keep;
    }
}
