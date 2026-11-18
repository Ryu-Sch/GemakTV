using Android.Content;

namespace GemakTV.Tv;

/// <summary>Small settings the TV remembers across restarts.</summary>
public sealed class TvPreferences(Context context)
{
    private readonly ISharedPreferences _prefs = context.GetSharedPreferences("gemaktv", FileCreationMode.Private)!;

    /// <summary>The channel to start on after a restart.</summary>
    public string? LastChannelId
    {
        get => _prefs.GetString("last_channel", null);
        set => _prefs.Edit()!.PutString("last_channel", value)!.Apply();
    }

    /// <summary>The stream of a channel that played most recently; tried first next time.</summary>
    public string? GetPreferredStream(string channelId) => _prefs.GetString("stream:" + channelId, null);

    public void SetPreferredStream(string channelId, string url)
    {
        if (GetPreferredStream(channelId) != url)
            _prefs.Edit()!.PutString("stream:" + channelId, url)!.Apply();
    }

    /// <summary>Apps the kiosk hid, so it can show them again.</summary>
    public IReadOnlyCollection<string> HiddenPackages
    {
        get => _prefs.GetStringSet("hidden_packages", null)?.ToList() ?? [];
        set => _prefs.Edit()!.PutStringSet("hidden_packages", value.ToList())!.Commit();
    }

    /// <summary>While in the future, kiosk restrictions stay off (helper visit or crash recovery).</summary>
    public DateTimeOffset KioskSuspendedUntil
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(_prefs.GetLong("kiosk_suspended_until", 0));
        set => _prefs.Edit()!.PutLong("kiosk_suspended_until", value.ToUnixTimeMilliseconds())!.Commit();
    }

    public string? KioskSuspendReason
    {
        get => _prefs.GetString("kiosk_suspend_reason", null);
        set => _prefs.Edit()!.PutString("kiosk_suspend_reason", value)!.Commit();
    }
}
