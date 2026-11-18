using System.Text;
using GemakTV.Application.Manager;
using GemakTV.Application.Ports;
using GemakTV.Application.Tv;
using GemakTV.Domain;
using GemakTV.Infrastructure.Crypto;
using GemakTV.Infrastructure.GitHub;
using static GemakTV.Tests.TestData;

namespace GemakTV.Tests.Infrastructure;

/// <summary>The full manager → repository → TV path, over a faithful fake of the GitHub API.</summary>
public class GitHubStoreTests
{
    private static readonly GitHubLocation Location = new("Ryu-Sch", "GemakTV", "published");
    private readonly FakeGitHubHandler _github = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private GitHubClient Client(string? token = null) => new(new HttpClient(_github), Location, token);

    [Fact]
    public async Task Manager_publishes_and_the_tv_picks_up_the_new_lineup()
    {
        _github.RequiredToken = "bot-token";
        var managerStore = new GitHubConfigStore(Client("bot-token"), Key);
        var publisher = new LineupPublisher(managerStore, managerStore, SystemClock.Instance);

        var first = await publisher.PublishAsync(KykNetLineup(), basedOn: null, "gemaktv-bot", _ct);
        Assert.Equal(PublishOutcome.Published, first.Outcome);

        // The file in the public repository is not readable JSON.
        var stored = _github.Files[GitHubLocation.ConfigPath];
        Assert.DoesNotContain("kykNET", Encoding.UTF8.GetString(stored), StringComparison.OrdinalIgnoreCase);

        var loaded = await publisher.LoadAsync(_ct);
        var withBok = new Lineup([.. loaded!.Config.Lineup.Channels, Channel("BOKTV.za", "bok")]);
        Assert.Equal(PublishOutcome.Published, (await publisher.PublishAsync(withBok, loaded, "gemaktv-bot", _ct)).Outcome);

        _github.RequiredToken = null; // the TV reads anonymously
        var tv = new ChannelListSync(new GitHubConfigStore(Client(), Key), new MemoryLineupStore());
        var result = await tv.CheckAsync(KykNetLineup(), _ct);
        Assert.Equal(ChannelSyncOutcome.Updated, result.Outcome);
        Assert.Equal("Added: BOKTV.", result.Diff!.Describe());
    }

    [Fact]
    public async Task Stale_save_is_reported_as_a_conflict()
    {
        var store = new GitHubConfigStore(Client(), Key);
        var tag = await store.SaveAsync(RemoteConfig.Initial(KykNetLineup(), "a", DateTimeOffset.UtcNow), null, "first", _ct);
        await store.SaveAsync(RemoteConfig.Initial(GemakTV.Domain.Lineup.Empty, "b", DateTimeOffset.UtcNow), tag, "second", _ct);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            store.SaveAsync(RemoteConfig.Initial(KykNetLineup(), "a", DateTimeOffset.UtcNow), tag, "stale", _ct));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            store.SaveAsync(RemoteConfig.Initial(KykNetLineup(), "a", DateTimeOffset.UtcNow), null, "blind overwrite", _ct));
    }

    [Fact]
    public async Task Version_tag_is_the_git_blob_sha()
    {
        var store = new GitHubConfigStore(Client(), Key);
        var tag = await store.SaveAsync(RemoteConfig.Initial(KykNetLineup(), "a", DateTimeOffset.UtcNow), null, "first", _ct);
        var document = await store.GetAsync(_ct);
        Assert.Equal(tag, document!.VersionTag);
        Assert.Equal(FakeGitHubHandler.BlobSha(_github.Files[GitHubLocation.ConfigPath]), tag);
    }

    [Fact]
    public async Task Missing_branch_means_nothing_published()
    {
        _github.BranchExists = false;
        Assert.Null(await new GitHubConfigStore(Client(), Key).GetAsync(_ct));
    }

    [Fact]
    public async Task Config_made_with_another_key_is_reported_clearly()
    {
        _github.Files[GitHubLocation.ConfigPath] = GitHubConfigStore.Encode(RemoteConfig.Initial(KykNetLineup(), "x", DateTimeOffset.UtcNow), OtherKey);
        var ex = await Assert.ThrowsAsync<ConfigUnreadableException>(() => new GitHubConfigStore(Client(), Key).GetAsync(_ct));
        Assert.Contains("different signing key", ex.Message);
    }

    [Fact]
    public async Task Tv_falls_back_to_the_cdn_when_the_api_rate_limit_is_hit()
    {
        var config = RemoteConfig.Initial(KykNetLineup(), "x", DateTimeOffset.UtcNow);
        _github.CdnFiles[GitHubLocation.ConfigPath] = GitHubConfigStore.Encode(config, Key);
        _github.RateLimited = true;

        await Assert.ThrowsAsync<GitHubRateLimitException>(() => new GitHubConfigStore(Client(), Key).GetAsync(_ct));
        var document = await new GitHubConfigStore(Client(), Key, allowCdnFallback: true).GetAsync(_ct);
        Assert.Equal(2, document!.Config.Lineup.Count);
        Assert.StartsWith("cdn:", document.VersionTag);
    }

    [Fact]
    public async Task Bad_token_is_an_auth_error()
    {
        _github.RequiredToken = "right";
        await Assert.ThrowsAsync<GitHubAuthException>(() => Client("wrong").GetUserAsync(_ct));
        Assert.Equal("gemaktv-bot", (await Client("right").GetUserAsync(_ct)).Login);
        Assert.True((await Client("right").GetRepositoryAccessAsync(_ct)).CanPush);
    }

    [Fact]
    public async Task Release_commit_writes_apk_and_config_together_and_the_tv_downloads_it()
    {
        _github.BranchExists = false;
        var client = Client("owner");
        var apk = RandomBytes(200_000);
        var encryptedApk = GtvCipher.Encrypt(apk, Key, GtvContent.Package);
        var release = new AppRelease(2, "1.0.1", "apk/GemakTV-2.apk.gtv", Sha256(apk), apk.Length);
        var config = RemoteConfig.Initial(KykNetLineup(), "ryu", DateTimeOffset.UtcNow).WithApp(release, "ryu", DateTimeOffset.UtcNow);

        // First publish creates the branch with no history.
        await client.CommitAsync([new FileChange(release.BlobPath, encryptedApk), new FileChange(GitHubLocation.ConfigPath, GitHubConfigStore.Encode(config, Key))], "release 2", expectedHead: null, _ct);
        Assert.True(_github.BranchExists);

        var tvStore = new GitHubConfigStore(Client(), Key);
        var updater = new AppUpdater(tvStore, new GitHubReleasePackageSource(Client(), Key), installedVersionCode: 1);
        var check = await updater.CheckAsync(_ct);
        Assert.Equal(AppUpdateStatus.UpdateAvailable, check.Status);

        var path = Path.Combine(Path.GetTempPath(), $"gemaktv-{Guid.NewGuid():N}.apk");
        try
        {
            await updater.DownloadAsync(check.Release!, path, cancellationToken: _ct);
            Assert.Equal(apk, await File.ReadAllBytesAsync(path, _ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Release_commit_against_a_moved_branch_changes_nothing()
    {
        var client = Client("owner");
        var head = await client.GetBranchHeadAsync(_ct);
        _github.Head = "someone-elses-commit";

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            client.CommitAsync([new FileChange("apk/x.gtv", [1, 2, 3])], "release", head, _ct));
        Assert.Empty(_github.Files);
    }

    [Fact]
    public async Task Commit_can_delete_old_packages()
    {
        _github.Files["apk/GemakTV-1.apk.gtv"] = [1];
        var client = Client("owner");
        var head = await client.GetBranchHeadAsync(_ct);
        await client.CommitAsync([new FileChange("apk/GemakTV-2.apk.gtv", [2]), new FileChange("apk/GemakTV-1.apk.gtv", null)], "release 2", head, _ct);
        Assert.Equal(["apk/GemakTV-2.apk.gtv"], _github.Files.Keys);
    }

    [Theory]
    [InlineData("Ryu-Sch/GemakTV@published", "Ryu-Sch", "GemakTV", "published")]
    [InlineData("a/b@feature/x", "a", "b", "feature/x")]
    public void Location_is_parsed_from_build_metadata(string text, string owner, string repo, string branch)
    {
        Assert.Equal(new GitHubLocation(owner, repo, branch), GitHubLocation.Parse(text));
    }
}
