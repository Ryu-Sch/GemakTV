using GemakTV.Application;
using GemakTV.Application.Manager;
using GemakTV.Domain;
using static GemakTV.Tests.TestData;

namespace GemakTV.Tests.Application;

public class LineupPublisherTests
{
    private readonly FakeRemote _remote = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Publishing_keeps_the_app_release_and_bumps_the_revision()
    {
        var release = new AppRelease(3, "1.0.2", "apk/GemakTV-3.apk.gtv", "ab", 10);
        _remote.Publish(RemoteConfig.Initial(KykNetLineup(), "bot", _clock.UtcNow).WithApp(release, "ryu", _clock.UtcNow));
        var publisher = new LineupPublisher(_remote, _remote, _clock);
        var loaded = await publisher.LoadAsync(TestContext.Current.CancellationToken);

        var edited = Lineup([.. loaded!.Config.Lineup.Channels, Channel("BOKTV.za", "bok")]);
        var result = await publisher.PublishAsync(edited, loaded, "gemaktv-bot", TestContext.Current.CancellationToken);

        Assert.Equal(PublishOutcome.Published, result.Outcome);
        var stored = _remote.Document!.Config;
        Assert.Equal(release, stored.App);
        Assert.Equal(3, stored.Revision);
        Assert.Equal("gemaktv-bot", stored.UpdatedBy);
        Assert.Equal(3, stored.Lineup.Count);
        Assert.Contains("Added: BOKTV.", _remote.LastMessage);
    }

    [Fact]
    public async Task Someone_elses_newer_save_is_not_overwritten()
    {
        _remote.Publish(RemoteConfig.Initial(KykNetLineup(), "bot", _clock.UtcNow));
        var publisher = new LineupPublisher(_remote, _remote, _clock);
        var mine = await publisher.LoadAsync(TestContext.Current.CancellationToken);
        _remote.Publish(mine!.Config.WithLineup(Lineup(Channel("other.za", "x")), "someone", _clock.UtcNow));

        var result = await publisher.PublishAsync(Lineup(Channel("mine.za", "y")), mine, "me", TestContext.Current.CancellationToken);

        Assert.Equal(PublishOutcome.Conflict, result.Outcome);
        Assert.Equal("other.za", _remote.Document!.Config.Lineup.Channels[0].Id);
    }

    [Fact]
    public async Task Saving_without_changes_does_not_create_a_commit()
    {
        _remote.Publish(RemoteConfig.Initial(KykNetLineup(), "bot", _clock.UtcNow));
        var publisher = new LineupPublisher(_remote, _remote, _clock);
        var loaded = await publisher.LoadAsync(TestContext.Current.CancellationToken);
        var result = await publisher.PublishAsync(KykNetLineup(), loaded, "me", TestContext.Current.CancellationToken);
        Assert.Equal(PublishOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, _remote.Saves);
    }
}

public class LineupEditorTests
{
    [Fact]
    public void Tracks_whether_there_is_anything_to_publish()
    {
        var editor = new LineupEditor(KykNetLineup());
        Assert.False(editor.IsDirty);

        editor.Add(Channel("BOKTV.za", "bok"));
        Assert.True(editor.IsDirty);

        editor.Remove("BOKTV.za");
        Assert.False(editor.IsDirty); // back to what is published
    }

    [Fact]
    public void Adding_a_channel_twice_is_ignored()
    {
        var editor = new LineupEditor(KykNetLineup());
        Assert.False(editor.Add(Channel("kyknet.za", "x")));
        Assert.Equal(2, editor.Channels.Count);
    }

    [Fact]
    public void Custom_stream_can_be_put_first_and_reordered()
    {
        var editor = new LineupEditor(KykNetLineup());
        var mine = new StreamSource("https://my-provider.example/kyknet/index.m3u8");

        Assert.True(editor.AddStream("KykNet.za", mine, preferFirst: true));
        Assert.Equal(mine, editor.Find("KykNet.za")!.Streams[0]);

        Assert.True(editor.MoveStream("KykNet.za", mine.Url, +5));
        Assert.Equal(mine, editor.Find("KykNet.za")!.Streams[^1]);

        Assert.True(editor.RemoveStream("KykNet.za", mine.Url));
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Moving_channels_is_clamped_to_the_list()
    {
        var editor = new LineupEditor(KykNetLineup());
        Assert.False(editor.Move("KykNet.za", -1));
        Assert.True(editor.Move("KykNet.za", +10));
        Assert.Equal("kykNETkie.za", editor.Channels[0].Id);
    }

    [Fact]
    public void Raises_changed_for_the_ui()
    {
        var editor = new LineupEditor(KykNetLineup());
        var raised = 0;
        editor.Changed += (_, _) => raised++;
        editor.Rename("KykNet.za", "kykNET (HD)");
        editor.MarkPublished();
        Assert.Equal(2, raised);
        Assert.False(editor.IsDirty);
    }
}

public class CatalogSearchTests
{
    private static CatalogChannel Cat(string id, string name, int streams = 0, string country = "ZA", string[]? alt = null, bool closed = false) =>
        new(id, name, alt ?? [], country, ["entertainment"], null, Enumerable.Range(0, streams).Select(i => Stream($"{id}-{i}")).ToList(), closed);

    private static readonly CatalogSearch Search = new([
        Cat("KykNet.za", "KykNet"),
        Cat("kykNETkie.za", "kykNET&kie"),
        Cat("kykNETNOU.za", "kykNET NOU!"),
        Cat("KykNetLekker.za", "KykNet Lekker!"),
        Cat("BOKTV.za", "BOKTV", streams: 1),
        Cat("SABC2.za", "SABC 2", streams: 1),
        Cat("Sky.uk", "Sky One", streams: 2, country: "UK"),
        Cat("Kanal.de", "Sport Kanal", alt: ["Kykkanal"]),
        Cat("Old.za", "KykNet Classic", closed: true),
    ]);

    [Fact]
    public void Punctuation_and_case_do_not_matter()
    {
        Assert.Equal("kykNETkie.za", Search.Search("kyknet kie")[0].Id);
        Assert.Equal("kykNETkie.za", Search.Search("KYKNET & KIE")[0].Id);
    }

    [Fact]
    public void Exact_name_ranks_above_longer_names()
    {
        var ids = Search.Search("kyknet").Select(c => c.Id).ToList();
        Assert.Equal("KykNet.za", ids[0]);
        Assert.Equal(["KykNet.za", "kykNETkie.za", "kykNETNOU.za", "KykNetLekker.za", "Old.za"], ids);
    }

    [Fact]
    public void Matches_alternative_names_and_accents()
    {
        Assert.Equal("Kanal.de", Search.Search("kykkanal").Single().Id);
        Assert.Equal("SABC2.za", Search.Search("SÁBC 2").Single().Id);
    }

    [Fact]
    public void Can_filter_to_channels_with_streams_and_by_country()
    {
        Assert.Equal(["BOKTV.za", "SABC2.za"], Search.Search("", onlyWithStreams: true, country: "za").Select(c => c.Id));
    }

    [Fact]
    public void Empty_query_lists_streamable_channels_first_and_hides_closed_ones()
    {
        var all = Search.Search("");
        Assert.True(all[0].HasStreams);
        Assert.DoesNotContain(all, c => c.IsClosed);
    }
}
