using GemakTV.Domain;
using GemakTV.Domain.Playback;
using static GemakTV.Tests.TestData;

namespace GemakTV.Tests.Domain;

public class StreamFailoverTests
{
    private static readonly StreamSource[] Streams = [Stream("a"), Stream("b"), Stream("c")];

    [Fact]
    public void Tries_each_stream_once_per_round_in_order()
    {
        var failover = new StreamFailover(Streams);
        var tried = new List<string>();
        do tried.Add(failover.Current!.Url);
        while (failover.MoveNext());

        Assert.Equal(Streams.Select(s => s.Url), tried);
        Assert.True(failover.IsExhausted);
    }

    [Fact]
    public void Starts_with_the_stream_that_worked_last_time()
    {
        var failover = new StreamFailover(Streams, preferredUrl: Streams[2].Url);
        Assert.Equal(Streams[2], failover.Current);
        failover.MoveNext();
        Assert.Equal(Streams[0], failover.Current);
        failover.MoveNext();
        Assert.Equal(Streams[1], failover.Current);
        Assert.False(failover.MoveNext());
    }

    [Fact]
    public void A_working_fallback_becomes_first_choice_in_the_next_round()
    {
        var failover = new StreamFailover(Streams);
        failover.MoveNext();            // "a" failed
        failover.MarkWorking();         // "b" plays
        failover.StartRound();          // e.g. the stream dropped later on
        Assert.Equal(Streams[1], failover.Current);
        Assert.Equal(2, failover.Round);
    }

    [Fact]
    public void Preferred_url_that_was_removed_from_the_channel_is_ignored()
    {
        var failover = new StreamFailover(Streams, preferredUrl: "https://gone.example/x.m3u8");
        Assert.Equal(Streams[0], failover.Current);
    }

    [Fact]
    public void Channel_without_streams_is_exhausted_immediately()
    {
        var failover = new StreamFailover([]);
        Assert.True(failover.IsExhausted);
        Assert.False(failover.MoveNext());
    }

    [Fact]
    public void Retries_back_off_but_never_give_up()
    {
        Assert.Equal(TimeSpan.Zero, StreamFailover.RetryDelay(1));
        Assert.True(StreamFailover.RetryDelay(2) < StreamFailover.RetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(2), StreamFailover.RetryDelay(50));
    }
}

public class ChannelNavigatorTests
{
    private static readonly Lineup Three = Lineup(Channel("one.za", "1"), Channel("two.za", "2"), Channel("three.za", "3"));

    [Fact]
    public void Up_and_down_wrap_around()
    {
        var nav = new ChannelNavigator(Three, "three.za");
        Assert.Equal("one.za", nav.Next()!.Id);
        Assert.Equal("three.za", nav.Previous()!.Id);
        Assert.Equal("two.za", nav.Previous()!.Id);
    }

    [Fact]
    public void Zapping_skips_channels_that_have_no_stream_yet()
    {
        var lineup = Lineup(Channel("one.za", "1"), Channel("nostream.za"), Channel("three.za", "3"));
        var nav = new ChannelNavigator(lineup, "one.za");
        Assert.Equal("three.za", nav.Next()!.Id);
        Assert.Equal("one.za", nav.Next()!.Id);
    }

    [Fact]
    public void When_no_channel_has_a_stream_they_can_still_be_browsed()
    {
        var lineup = Lineup(Channel("KykNet.za"), Channel("kykNETkie.za"));
        var nav = new ChannelNavigator(lineup);
        Assert.Equal("KykNet.za", nav.Current!.Id);
        Assert.Equal("kykNETkie.za", nav.Next()!.Id);
    }

    [Fact]
    public void Starts_on_the_last_watched_channel_or_else_the_first_playable_one()
    {
        Assert.Equal("two.za", new ChannelNavigator(Three, "TWO.ZA").Current!.Id);
        var lineup = Lineup(Channel("nostream.za"), Channel("two.za", "2"));
        Assert.Equal("two.za", new ChannelNavigator(lineup, "unknown.za").Current!.Id);
    }

    [Fact]
    public void Number_keys_jump_directly_and_ignore_numbers_out_of_range()
    {
        var nav = new ChannelNavigator(Three);
        Assert.Equal("three.za", nav.GoTo(3)!.Id);
        Assert.Null(nav.GoTo(4));
        Assert.Equal(3, nav.CurrentNumber);
    }

    [Fact]
    public void Updated_lineup_keeps_the_viewer_on_their_channel()
    {
        var nav = new ChannelNavigator(Three, "two.za");
        nav.ReplaceLineup(Lineup(Channel("new.za", "n"), Channel("two.za", "2")));
        Assert.Equal("two.za", nav.Current!.Id);
        Assert.Equal(2, nav.CurrentNumber);

        nav.ReplaceLineup(Lineup(Channel("new.za", "n")));
        Assert.Equal("new.za", nav.Current!.Id);
    }

    [Fact]
    public void Empty_lineup_has_no_current_channel()
    {
        var nav = new ChannelNavigator(GemakTV.Domain.Lineup.Empty);
        Assert.Null(nav.Current);
        Assert.Null(nav.Next());
    }
}
