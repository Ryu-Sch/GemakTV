using Android.Content;
using Android.Runtime;
using Android.Util;

namespace GemakTV.Tv.Kiosk;

/// <summary>
/// Records crashes so a broken build cannot trap the box: three crashes within three minutes and the kiosk
/// steps aside (see <see cref="TvApplication"/>). The last crashes are kept in files/crash.log for diagnosis.
/// </summary>
public static class CrashGuard
{
    private const string Tag = "GemakTV";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    public static void Install(Context context)
    {
        var app = context.ApplicationContext ?? context;
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Record(app, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(app, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn(Tag, $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    public static bool IsCrashLooping(Context context)
    {
        var cutoff = DateTimeOffset.UtcNow - Window;
        return Times(context).Count(t => t > cutoff) >= 3;
    }

    public static void Clear(Context context) => Prefs(context).Edit()!.Remove("times")!.Commit();

    private static void Record(Context context, Exception? exception)
    {
        try
        {
            Log.Error(Tag, $"Crash: {exception}");
            var times = Times(context).Append(DateTimeOffset.UtcNow).TakeLast(5).Select(t => t.ToUnixTimeMilliseconds());
            Prefs(context).Edit()!.PutString("times", string.Join(',', times))!.Commit();

            var log = Path.Combine(context.FilesDir!.AbsolutePath, "crash.log");
            if (File.Exists(log) && new FileInfo(log).Length > 64 * 1024) File.Delete(log);
            File.AppendAllText(log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {exception}\n\n");
        }
        catch
        {
            // Never let crash bookkeeping cause a second crash.
        }
    }

    private static IEnumerable<DateTimeOffset> Times(Context context) =>
        (Prefs(context).GetString("times", "") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue);

    private static ISharedPreferences Prefs(Context context) => context.GetSharedPreferences("gemaktv-crashes", FileCreationMode.Private)!;
}
