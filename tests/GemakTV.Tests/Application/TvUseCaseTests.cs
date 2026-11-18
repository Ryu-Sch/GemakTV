using GemakTV.Application.Tv;
using GemakTV.Domain;
using static GemakTV.Tests.TestData;

namespace GemakTV.Tests.Application;

public class ChannelListSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeRemote _remote = new();
    private readonly MemoryLineupStore _local = new();

    [Fact]
    public async Task New_channels_are_stored_on_the_tv()
    {
        var published = Lineup(Channel("KykNet.za", "a"), Channel("kykNETkie.za", "b"));
        _remote.Publish(RemoteConfig.Initial(published, "bot", Now));

        var result = await new ChannelListSync(_remote, _local).CheckAsync(Lineup(Channel("KykNet.za", "a")), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelSyncOutcome.Updated, result.Outcome);
        Assert.Same(published, _local.Saved);
        Assert.Equal("Added: kykNETkie.", result.Diff!.Describe());
    }

    [Fact]
    public async Task Unchanged_list_is_not_rewritten()
    {
        _remote.Publish(RemoteConfig.Initial(KykNetLineup(), "bot", Now));
        var result = await new ChannelListSync(_remote, _local).CheckAsync(KykNetLineup(), TestContext.Current.CancellationToken);
        Assert.Equal(ChannelSyncOutcome.AlreadyUpToDate, result.Outcome);
        Assert.Null(_local.Saved);
    }

    [Fact]
    public async Task Failure_keeps_the_current_channels()
    {
        _remote.FailWith = new HttpRequestException("no internet");
        var current = KykNetLineup();
        var result = await new ChannelListSync(_remote, _local).CheckAsync(current, TestContext.Current.CancellationToken);
        Assert.Equal(ChannelSyncOutcome.Failed, result.Outcome);
        Assert.Same(current, result.Lineup);
        Assert.Null(_local.Saved);
    }

    [Fact]
    public async Task Nothing_published_yet_is_not_an_error()
    {
        var result = await new ChannelListSync(_remote, _local).CheckAsync(KykNetLineup(), TestContext.Current.CancellationToken);
        Assert.Equal(ChannelSyncOutcome.NothingPublished, result.Outcome);
    }
}

public class AppUpdaterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gemaktv-update-{Guid.NewGuid():N}.apk");
    private readonly FakeRemote _remote = new();

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task Offers_a_newer_published_version()
    {
        _remote.Publish(RemoteConfig.Initial(KykNetLineup(), "bot", DateTimeOffset.UtcNow).WithApp(Release([1, 2, 3], 7), "bot", DateTimeOffset.UtcNow));
        var check = await new AppUpdater(_remote, new BytesPackageSource([]), installedVersionCode: 6).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AppUpdateStatus.UpdateAvailable, check.Status);
        Assert.Equal(7, check.Release!.VersionCode);
    }

    [Fact]
    public async Task Check_failure_is_reported_not_thrown()
    {
        _remote.FailWith = new HttpRequestException("offline");
        var check = await new AppUpdater(_remote, new BytesPackageSource([]), 1).CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(check.Failed);
    }

    [Fact]
    public async Task Verified_download_is_kept()
    {
        var apk = RandomBytes(4096);
        var progress = new List<double>();
        await new AppUpdater(_remote, new BytesPackageSource(apk), 1).DownloadAsync(Release(apk, 2), _path, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);
        Assert.Equal(apk, await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken));
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task Package_with_wrong_checksum_is_deleted_and_never_installed()
    {
        var apk = RandomBytes(4096);
        var release = Release(apk, 2) with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            new AppUpdater(_remote, new BytesPackageSource(apk), 1).DownloadAsync(release, _path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task Short_download_is_rejected()
    {
        var apk = RandomBytes(4096);
        var release = Release(apk, 2);
        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            new AppUpdater(_remote, new BytesPackageSource(apk[..100]), 1).DownloadAsync(release, _path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_path));
    }

    private static AppRelease Release(byte[] apk, int versionCode) =>
        new(versionCode, $"1.0.{versionCode}", $"apk/GemakTV-{versionCode}.apk.gtv", Sha256(apk), apk.Length);

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
