using GemakTV.Domain;
using static GemakTV.Tests.TestData;

namespace GemakTV.Tests.Domain;

public class LineupTests
{
    [Fact]
    public void Duplicate_channel_ids_are_refused_regardless_of_case()
    {
        Assert.Throws<ArgumentException>(() => Lineup(Channel("KykNet.za"), Channel("kyknet.za")));
    }

    [Fact]
    public void Duplicate_stream_urls_within_a_channel_are_dropped()
    {
        var channel = new Channel("a.za", "A", null, [Stream("x"), Stream("x"), Stream("y")]);
        Assert.Equal(2, channel.Streams.Count);
    }

    [Theory]
    [InlineData("rtmp://example.com/live")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("file:///sdcard/x.m3u8")]
    public void Only_http_streams_are_accepted(string url)
    {
        Assert.False(StreamSource.IsValidUrl(url));
        Assert.Throws<ArgumentException>(() => new StreamSource(url));
    }

    [Fact]
    public void Identical_content_is_not_a_change()
    {
        Assert.False(LineupDiff.Between(KykNetLineup(), KykNetLineup()).HasChanges);
    }

    [Fact]
    public void Reports_added_removed_and_improved_channels_by_name()
    {
        var before = Lineup(Channel("KykNet.za", "a"), Channel("SABC2.za", "s"));
        var after = Lineup(Channel("KykNet.za", "a", "b"), Channel("kykNETkie.za", "k"));

        var diff = LineupDiff.Between(before, after);

        Assert.Equal(["kykNETkie.za"], diff.Added.Select(c => c.Id));
        Assert.Equal(["SABC2.za"], diff.Removed.Select(c => c.Id));
        Assert.Equal(["KykNet.za"], diff.Updated.Select(c => c.Id));
        Assert.Equal("Added: kykNETkie. Removed: SABC2. Improved: KykNet.", diff.Describe());
    }

    [Fact]
    public void Reordering_alone_counts_as_a_change()
    {
        var diff = LineupDiff.Between(Lineup(Channel("a.za"), Channel("b.za")), Lineup(Channel("b.za"), Channel("a.za")));
        Assert.True(diff is { HasChanges: true, OrderChanged: true });
        Assert.Equal("The channel order was changed.", diff.Describe());
    }

    [Fact]
    public void Adding_a_channel_in_the_middle_is_not_a_reorder()
    {
        var diff = LineupDiff.Between(Lineup(Channel("a.za"), Channel("c.za")), Lineup(Channel("a.za"), Channel("b.za"), Channel("c.za")));
        Assert.False(diff.OrderChanged);
    }

    [Theory]
    [InlineData(1, null, AppUpdateStatus.NoReleasePublished)]
    [InlineData(1, 2, AppUpdateStatus.UpdateAvailable)]
    [InlineData(2, 2, AppUpdateStatus.UpToDate)]
    [InlineData(3, 2, AppUpdateStatus.UpToDate)] // never downgrade
    public void App_update_is_offered_only_for_a_higher_version(int installed, int? published, AppUpdateStatus expected)
    {
        var release = published is { } v ? new AppRelease(v, $"1.0.{v}", "apk/x.gtv", "00", 1) : null;
        Assert.Equal(expected, AppUpdatePolicy.Evaluate(installed, release));
    }
}
