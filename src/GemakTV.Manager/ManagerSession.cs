using Android.Content;
using GemakTV.Application;
using GemakTV.Application.Manager;
using GemakTV.Application.Ports;
using GemakTV.Domain;
using GemakTV.Infrastructure.Crypto;
using GemakTV.Infrastructure.GitHub;
using GemakTV.Infrastructure.Iptv;
using GemakTV.Platform;
using Xamarin.Android.Net;

namespace GemakTV.Manager;

public sealed class SignInException(string message) : Exception(message);

/// <summary>
/// Everything the manager screens share while signed in: the GitHub connection, the published config,
/// the working copy being edited, the iptv-org catalogue and stream health results.
/// </summary>
public sealed class ManagerSession
{
    private static readonly HttpClient Http = new(new AndroidMessageHandler()) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly Context _context;
    private Task<CatalogSearch>? _catalog;

    private ManagerSession(Context context, Credentials credentials, GitHubClient client)
    {
        _context = context.ApplicationContext ?? context;
        Credentials = credentials;
        Client = client;
        var store = new GitHubConfigStore(client, ConfigKey.FromEmbeddedResource(typeof(ManagerSession).Assembly));
        Publisher = new LineupPublisher(store, store, SystemClock.Instance);
        Logos = new LogoLoader(_context, Http);
        Probe = new StreamProbe(Http);
    }

    public static ManagerSession? Current { get; private set; }

    public Credentials Credentials { get; }

    public GitHubClient Client { get; }

    public LineupPublisher Publisher { get; }

    public LineupEditor Editor { get; } = new(Lineup.Empty);

    public RemoteConfigDocument? Published { get; private set; }

    public LogoLoader Logos { get; }

    public StreamProbe Probe { get; }

    /// <summary>Last known health per stream address.</summary>
    public Dictionary<string, StreamHealth> Health { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks the credentials against GitHub: the token must belong to the user, and the user must be allowed to push.</summary>
    public static async Task<ManagerSession> SignInAsync(Context context, Credentials credentials)
    {
        var location = GitHubLocation.FromAssembly(typeof(ManagerSession).Assembly);
        if (!string.IsNullOrWhiteSpace(credentials.ApiBase))
            location = location with { ApiBase = new Uri(credentials.ApiBase.TrimEnd('/') + "/"), RawBase = new Uri(credentials.ApiBase.TrimEnd('/') + "/raw/") };
        var client = new GitHubClient(Http, location, credentials.Token.Trim());

        GitHubUser user;
        try
        {
            user = await client.GetUserAsync();
        }
        catch (GitHubAuthException)
        {
            throw new SignInException("GitHub did not accept this username and password. Check the access token.");
        }
        catch (HttpRequestException)
        {
            throw new SignInException("Could not reach GitHub. Is the phone connected to the internet?");
        }
        if (!string.Equals(user.Login, credentials.Username.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new SignInException($"This token belongs to “{user.Login}”, not “{credentials.Username}”.");

        var access = await client.GetRepositoryAccessAsync();
        if (!access.CanPush)
            throw new SignInException($"“{user.Login}” may not change {location.Owner}/{location.Repository}. Ask the owner to add it as a collaborator.");

        var session = new ManagerSession(context, credentials with { Username = user.Login }, client);
        await session.ReloadAsync();
        Current = session;
        return session;
    }

    public static void SignOut(Context context)
    {
        new CredentialStore(context).Clear();
        Current = null;
    }

    /// <summary>Loads the published config and starts a fresh working copy from it.</summary>
    public async Task ReloadAsync()
    {
        Published = await Publisher.LoadAsync();
        var lineup = Published?.Config.Lineup ?? Lineup.Empty;
        Editor.Reset(lineup); // same editor instance, so screens listening to it stay connected
        Logos.Prefetch(lineup);
    }

    public async Task<PublishResult> SaveAsync()
    {
        var result = await Publisher.PublishAsync(Editor.Current, Published, Credentials.Username);
        if (result.Outcome == PublishOutcome.Published)
        {
            Published = result.Document;
            Editor.MarkPublished();
        }
        return result;
    }

    /// <summary>The iptv-org catalogue, downloaded once a day and cached on the phone.</summary>
    public Task<CatalogSearch> CatalogAsync() => _catalog ??= LoadCatalogAsync();

    private async Task<CatalogSearch> LoadCatalogAsync()
    {
        try
        {
            var source = new IptvOrgCatalogSource(Http, Path.Combine(_context.CacheDir!.AbsolutePath, "iptv-org"), TimeSpan.FromDays(1));
            var channels = await Task.Run(() => source.LoadAsync());
            return await Task.Run(() => new CatalogSearch(channels));
        }
        catch
        {
            _catalog = null; // allow a retry
            throw;
        }
    }

    public string DescribePublished()
    {
        if (Published is not { } doc) return "Nothing published yet";
        var c = doc.Config;
        return $"Revision {c.Revision} · {c.UpdatedUtc.ToLocalTime():d MMM HH:mm} · {c.UpdatedBy}";
    }
}
