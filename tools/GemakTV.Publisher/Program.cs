using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GemakTV.Application.Manager;
using GemakTV.Application.Ports;
using GemakTV.Domain;
using GemakTV.Infrastructure.Crypto;
using GemakTV.Infrastructure.GitHub;
using GemakTV.Infrastructure.Iptv;
using GemakTV.Infrastructure.Serialization;
using GemakTV.Publisher;

// GemakTV publisher: the owner's desktop tool. Run through the dotnet host:
//   dotnet tools/GemakTV.Publisher/bin/Release/net10.0/GemakTV.Publisher.dll <command>

Console.OutputEncoding = Encoding.UTF8;
var options = Options.Parse(args);
var secrets = Secrets.Default();

try
{
    return options.Command switch
    {
        ["keys", "create"] => KeysCreate(),
        ["keys", "derive"] => KeysDerive(),
        ["keys", "show"] => KeysShow(),
        ["config", "show"] => await ConfigShow(),
        ["config", "init"] => await ConfigInit(),
        ["config", "export"] => await ConfigExport(),
        ["config", "import"] => await ConfigImport(),
        ["release"] => await Release(),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is InvalidOperationException or GitHubApiException or ConcurrencyConflictException or ConfigUnreadableException or IOException or CryptographicException or ArgumentException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        GemakTV publisher

          keys create                 Create the app signing key and derive the config key (first time only)
          keys derive                 Re-derive config.key from the signing key
          keys show                   Show the secrets folder and key fingerprints

          config show                 Decrypt and print the published config
          config init --channels a,b  Publish a first channel list from the iptv-org catalogue
          config export <file.json>   Save the published channel list as plain JSON
          config import <file.json>   Publish a channel list from a plain JSON file

          release --apk <file> --version-code <n> --version-name <x.y.z> [--keep 2]
                                      Encrypt a signed TV app build and publish it as the latest release

        Options: --repo owner/name@branch (default Ryu-Sch/GemakTV@published), --force
        GitHub access: GITHUB_TOKEN / GH_TOKEN, or the GitHub CLI's login (gh auth token).
        Secrets folder: GEMAKTV_SECRETS, default %USERPROFILE%\.gemaktv
        """);
    return options.Command.Length == 0 ? 0 : 2;
}

int KeysCreate()
{
    if (secrets.HasKeystore && !options.Force)
        throw new InvalidOperationException($"A signing key already exists in {secrets.Directory}. Replacing it would make every installed TV unable to update; pass --force only if you are sure.");
    var keytool = options.Get("keytool") ?? Secrets.FindKeytool();
    secrets.CreateKeystore(keytool, options.Get("dname") ?? "CN=GemakTV, O=GemakTV, C=NA");
    var key = secrets.DeriveConfigKey();
    Console.WriteLine($"Created signing key {secrets.KeystorePath}");
    Console.WriteLine($"Derived config key   {secrets.ConfigKeyPath} (fingerprint {key.Fingerprint})");
    Console.WriteLine("Back up this folder somewhere safe: without it no update can ever be installed on the TV.");
    return 0;
}

int KeysDerive()
{
    var key = secrets.DeriveConfigKey();
    Console.WriteLine($"Config key fingerprint {key.Fingerprint} -> {secrets.ConfigKeyPath}");
    return 0;
}

int KeysShow()
{
    Console.WriteLine($"Secrets folder:     {secrets.Directory}");
    Console.WriteLine($"Signing key:        {(secrets.HasKeystore ? secrets.KeystorePath : "missing")}");
    if (secrets.HasKeystore) Console.WriteLine($"Certificate SHA256: {secrets.CertificateSha256()}");
    Console.WriteLine($"Config key:         {(File.Exists(secrets.ConfigKeyPath) ? secrets.LoadConfigKey().Fingerprint : "missing")}");
    return 0;
}

async Task<int> ConfigShow()
{
    var (_, store) = Connect(needsToken: false);
    var document = await store.GetAsync();
    if (document is null)
    {
        Console.WriteLine("Nothing has been published yet. Use 'config init'.");
        return 0;
    }
    var c = document.Config;
    Console.WriteLine($"Revision {c.Revision}, saved {c.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} by {c.UpdatedBy} (blob {document.VersionTag[..Math.Min(10, document.VersionTag.Length)]})");
    Console.WriteLine(c.App is { } app ? $"App: {app.VersionName} (code {app.VersionCode}), {app.Size / 1024.0 / 1024:F1} MB at {app.BlobPath}" : "App: no release published");
    Console.WriteLine($"Channels ({c.Lineup.Count}):");
    var n = 1;
    foreach (var channel in c.Lineup.Channels)
    {
        Console.WriteLine($"  {n++,2}. {channel.Name} [{channel.Id}]");
        foreach (var stream in channel.Streams) Console.WriteLine($"        {stream.Url}");
        if (!channel.IsPlayable) Console.WriteLine("        (no stream yet)");
    }
    return 0;
}

async Task<int> ConfigInit()
{
    var ids = (options.Get("channels") ?? throw new InvalidOperationException("Pass --channels with iptv-org ids, e.g. --channels KykNet.za,kykNETkie.za"))
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var (client, store) = Connect(needsToken: true);
    if (await store.GetAsync() is not null && !options.Force)
        throw new InvalidOperationException("A config is already published. Use the manager app (or 'config import') to change channels, or pass --force to start over.");

    Console.WriteLine("Loading the iptv-org catalogue...");
    using var http = new HttpClient();
    var catalog = await new IptvOrgCatalogSource(http).LoadAsync();
    var channels = ids.Select(id => catalog.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.ToChannel()
        ?? throw new InvalidOperationException($"'{id}' is not in the iptv-org catalogue.")).ToList();
    var lineup = new Lineup(channels);
    foreach (var channel in channels)
        Console.WriteLine($"  {channel.Name}: {channel.Streams.Count} stream(s) in iptv-org");

    var config = RemoteConfig.Initial(lineup, await WhoAmI(client), DateTimeOffset.UtcNow);
    var head = await client.GetBranchHeadAsync();
    var readme = Encoding.UTF8.GetBytes(PublishBranchReadme);
    await client.CommitAsync(
        [new FileChange(GitHubLocation.ConfigPath, GitHubConfigStore.Encode(config, secrets.LoadConfigKey())), new FileChange("README.md", readme)],
        $"Channel list: first publish ({lineup.Count} channels)", head);
    Console.WriteLine($"Published {lineup.Count} channels to {client.Location}.");
    return 0;
}

async Task<int> ConfigExport()
{
    var file = options.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Pass the file to write.");
    var (_, store) = Connect(needsToken: false);
    var document = await store.GetAsync() ?? throw new InvalidOperationException("Nothing published yet.");
    await File.WriteAllBytesAsync(file, ConfigSerializer.SerializeLineup(document.Config.Lineup));
    Console.WriteLine($"Wrote {document.Config.Lineup.Count} channels (revision {document.Config.Revision}) to {file}");
    return 0;
}

async Task<int> ConfigImport()
{
    var file = options.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Pass the JSON file to publish.");
    var lineup = ConfigSerializer.DeserializeLineup(await File.ReadAllBytesAsync(file));
    var (client, store) = Connect(needsToken: true);
    var publisher = new LineupPublisher(store, store, SystemClock.Instance);
    var current = await publisher.LoadAsync();
    var result = await publisher.PublishAsync(lineup, current, await WhoAmI(client));
    Console.WriteLine(result.Outcome switch
    {
        PublishOutcome.Published => $"Published revision {result.Document!.Config.Revision}: {result.Message}",
        PublishOutcome.NoChanges => "The published list already matches; nothing to do.",
        _ => $"Not published: {result.Message}",
    });
    return result.Outcome == PublishOutcome.Conflict ? 1 : 0;
}

async Task<int> Release()
{
    var apkPath = options.Get("apk") ?? throw new InvalidOperationException("Pass --apk with the signed TV app build.");
    var versionCode = int.Parse(options.Get("version-code") ?? throw new InvalidOperationException("Pass --version-code."));
    var versionName = options.Get("version-name") ?? throw new InvalidOperationException("Pass --version-name.");
    var keep = int.Parse(options.Get("keep") ?? "2");
    var key = secrets.LoadConfigKey();
    var (client, store) = Connect(needsToken: true);

    var head = await client.GetBranchHeadAsync() ?? throw new InvalidOperationException("Nothing is published yet; run 'config init' first.");
    var current = await store.GetAsync() ?? throw new InvalidOperationException("Nothing is published yet; run 'config init' first.");
    if (current.Config.App is { } published && published.VersionCode >= versionCode && !options.Force)
        throw new InvalidOperationException($"Version code {versionCode} is not newer than the published {published.VersionCode}; TVs would not install it.");

    var apk = await File.ReadAllBytesAsync(apkPath);
    var blobPath = $"apk/GemakTV-{versionCode}.apk.gtv";
    var release = new AppRelease(versionCode, versionName, blobPath, Convert.ToHexStringLower(SHA256.HashData(apk)), apk.Length);
    var config = current.Config.WithApp(release, await WhoAmI(client), DateTimeOffset.UtcNow);

    using var encrypted = new MemoryStream();
    await GtvCipher.EncryptAsync(new MemoryStream(apk), encrypted, key, GtvContent.Package);

    var changes = new List<FileChange>
    {
        new(blobPath, encrypted.ToArray()),
        new(GitHubLocation.ConfigPath, GitHubConfigStore.Encode(config, key)),
    };
    // Keep the newest few packages so a TV that is mid-download is not cut off; drop the rest.
    var old = (await client.ListFolderAsync("apk"))
        .Where(p => p != blobPath)
        .OrderByDescending(p => ParseVersion(p))
        .Skip(Math.Max(0, keep - 1));
    changes.AddRange(old.Select(p => new FileChange(p, null)));

    Console.WriteLine($"Publishing {versionName} (code {versionCode}), {apk.Length / 1024.0 / 1024:F1} MB...");
    var commit = await client.CommitAsync(changes, $"Release {versionName} (code {versionCode})", head);
    Console.WriteLine($"Done: commit {commit[..10]} on {client.Location}. TVs get it with 'Check For Updates'.");
    return 0;
}

(GitHubClient Client, GitHubConfigStore Store) Connect(bool needsToken)
{
    var location = GitHubLocation.Parse(options.Get("repo") ?? typeof(Secrets).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "GemakRepo").Value!);
    var token = Token();
    if (needsToken && token is null)
        throw new InvalidOperationException("No GitHub access: set GITHUB_TOKEN or sign in with 'gh auth login'.");
    var client = new GitHubClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, location, token);
    return (client, new GitHubConfigStore(client, secrets.LoadConfigKey()));
}

async Task<string> WhoAmI(GitHubClient client) => (await client.GetUserAsync()).Login;

static string? Token()
{
    foreach (var name in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            return value;
    try
    {
        using var gh = Process.Start(new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var token = gh.StandardOutput.ReadToEnd().Trim();
        gh.WaitForExit();
        return gh.ExitCode == 0 && token.Length > 0 ? token : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null; // gh not installed
    }
}

static int ParseVersion(string path)
{
    var name = Path.GetFileName(path);
    var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out var v) ? v : 0;
}

partial class Program
{
    private const string PublishBranchReadme = """
        # GemakTV published files

        This branch is written by the GemakTV manager app and publisher tool. Do not edit it by hand.

        - `config.gtv` - the channel list and latest app release, encrypted (AES-256-GCM) with a key derived from the app signing key
        - `apk/` - encrypted builds of the TV app

        The TV app reads these with "Check For Updated Channel List" and "Check For Updates".
        """;
}

internal sealed record Options(string[] Command, Dictionary<string, string> Named, List<string> Positional, bool Force)
{
    public string? Get(string name) => Named.GetValueOrDefault(name);

    public static Options Parse(string[] args)
    {
        var command = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--force") force = true;
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) named[arg[2..]] = args[++i];
            else if (command.Count < 2 && (command.Count == 0 || command[0] is "keys" or "config")) command.Add(arg);
            else positional.Add(arg);
        }
        return new Options([.. command], named, positional, force);
    }
}
