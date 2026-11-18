using Android.Content;
using Android.Content.PM;
using GemakTV.Application.Tv;
using GemakTV.Domain;
using GemakTV.Infrastructure.Crypto;
using GemakTV.Infrastructure.GitHub;
using GemakTV.Infrastructure.Serialization;
using GemakTV.Infrastructure.Storage;
using GemakTV.Tv.Kiosk;
using GemakTV.Tv.Ui;
using Xamarin.Android.Net;

namespace GemakTV.Tv;

/// <summary>Composition root: wires the shared use cases to Android and the GitHub repository.</summary>
public sealed class AppServices
{
    private readonly Context _context;

    public AppServices(Context context)
    {
        _context = context;
        var info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
        VersionName = info.VersionName ?? "?";
#pragma warning disable CA1422 // VersionCode is the only option on Android 6
        VersionCode = info.VersionCode;
#pragma warning restore CA1422

        Prefs = new TvPreferences(context);
        Http = new HttpClient(new AndroidMessageHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        Key = ConfigKey.FromEmbeddedResource(typeof(AppServices).Assembly);
        Location = GitHubLocation.FromAssembly(typeof(AppServices).Assembly);

        ConfigStore = new GitHubConfigStore(new GitHubClient(Http, Location), Key, allowCdnFallback: true);
        LocalLineup = new JsonFileLineupStore(Path.Combine(context.FilesDir!.AbsolutePath, "lineup.json"));
        ChannelSync = new ChannelListSync(ConfigStore, LocalLineup);

        var downloads = new HttpClient(new AndroidMessageHandler()) { Timeout = TimeSpan.FromMinutes(15) };
        Updater = new AppUpdater(ConfigStore, new GitHubReleasePackageSource(new GitHubClient(downloads, Location), Key), VersionCode);

        Logos = new LogoLoader(context, Http);
        Kiosk = new KioskController(context, Prefs);
    }

    public string VersionName { get; }

    public int VersionCode { get; }

    public TvPreferences Prefs { get; }

    public HttpClient Http { get; }

    public ConfigKey Key { get; }

    public GitHubLocation Location { get; }

    public GitHubConfigStore ConfigStore { get; }

    public JsonFileLineupStore LocalLineup { get; }

    public ChannelListSync ChannelSync { get; }

    public AppUpdater Updater { get; }

    public LogoLoader Logos { get; }

    public KioskController Kiosk { get; }

    /// <summary>The lineup saved on the TV, or the one built into the app on first start.</summary>
    public async Task<Lineup> LoadLineupAsync()
    {
        var saved = await LocalLineup.LoadAsync().ConfigureAwait(false);
        if (saved is not null) return saved;
        using var asset = _context.Assets!.Open("default-lineup.json");
        using var buffer = new MemoryStream();
        await asset.CopyToAsync(buffer).ConfigureAwait(false);
        return ConfigSerializer.DeserializeLineup(buffer.ToArray());
    }
}
