using System.Text;
using GemakTV.Application.Ports;
using GemakTV.Domain;
using GemakTV.Infrastructure.Iptv;
using GemakTV.Infrastructure.Serialization;
using GemakTV.Infrastructure.Storage;
using static GemakTV.Tests.TestData;

namespace GemakTV.Tests.Infrastructure;

public class ConfigSerializerTests
{
    [Fact]
    public void Round_trips_every_field()
    {
        var now = new DateTimeOffset(2026, 11, 18, 10, 30, 0, TimeSpan.Zero);
        var lineup = Lineup(new Channel("KykNet.za", "kykNET", "https://logo/k.png", [new StreamSource("https://a/x.m3u8", "Mozilla/5.0", "https://ref/")]));
        var config = new RemoteConfig(7, now, "bot", lineup, new AppRelease(3, "1.0.2", "apk/GemakTV-3.apk.gtv", "abc", 123));

        var back = ConfigSerializer.Deserialize(ConfigSerializer.Serialize(config));

        Assert.Equal(7, back.Revision);
        Assert.Equal(now, back.UpdatedUtc);
        Assert.Equal(config.App, back.App);
        Assert.True(lineup.ContentEquals(back.Lineup));
        Assert.Equal("Mozilla/5.0", back.Lineup.Channels[0].Streams[0].UserAgent);
    }

    [Fact]
    public void Newer_files_with_extra_fields_still_load()
    {
        // What a future version might publish: unknown fields everywhere, including a newer schema number.
        var json = """
            {
              "schema": 9, "revision": 12, "updatedUtc": "2027-01-01T00:00:00Z", "updatedBy": "bot",
              "theme": "dark",
              "channels": [ { "id": "KykNet.za", "name": "kykNET", "epgUrl": "https://epg", "streams": [ { "url": "https://a/x.m3u8", "drm": null } ] } ],
              "app": { "versionCode": 5, "versionName": "2.0.0", "path": "apk/GemakTV-5.apk.gtv", "sha256": "ff", "size": 10, "minAndroid": 23 }
            }
            """;
        var config = ConfigSerializer.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.Equal(5, config.App!.VersionCode);
        Assert.Equal("kykNET", config.Lineup.Channels.Single().Name);
    }

    [Fact]
    public void Invalid_stream_addresses_are_dropped_not_fatal()
    {
        var json = """{ "channels": [ { "id": "a.za", "name": "A", "streams": [ { "url": "rtmp://x/y" }, { "url": "https://ok/z.m3u8" } ] } ] }""";
        Assert.Single(ConfigSerializer.Deserialize(Encoding.UTF8.GetBytes(json)).Lineup.Channels[0].Streams);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "channels": [ { "name": "no id" } ] }""")]
    [InlineData("""{ "channels": [ { "id": "a", "name": "A" }, { "id": "A", "name": "dup" } ] }""")]
    public void Broken_files_raise_a_readable_error(string json)
    {
        Assert.Throws<ConfigUnreadableException>(() => ConfigSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task Local_store_round_trips_and_survives_a_corrupt_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gemaktv-{Guid.NewGuid():N}", "lineup.json");
        var store = new JsonFileLineupStore(path);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            Assert.Null(await store.LoadAsync(ct));
            await store.SaveAsync(KykNetLineup(), ct);
            Assert.True(KykNetLineup().ContentEquals((await store.LoadAsync(ct))!));

            await File.WriteAllTextAsync(path, "{ truncated", ct);
            Assert.Null(await store.LoadAsync(ct));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class IptvCatalogTests
{
    [Fact]
    public void Joins_channels_with_their_streams_and_a_usable_logo()
    {
        var channels = new List<IptvChannelRow>
        {
            new() { Id = "WildEarth.za", Name = "WildEarth", Country = "ZA", Categories = ["outdoor"] },
            new() { Id = "KykNet.za", Name = "KykNet", Country = "ZA" },
            new() { Id = "Adult.xx", Name = "Adult", IsNsfw = true },
            new() { Id = "Gone.za", Name = "Gone", Closed = "2020-01-01" },
        };
        var streams = new List<IptvStreamRow>
        {
            new() { Channel = "WildEarth.za", Url = "https://geo/x.m3u8", Labels = ["Geo-blocked"] },
            new() { Channel = "WildEarth.za", Url = "https://part/x.m3u8", Labels = ["Not 24/7"] },
            new() { Channel = "WildEarth.za", Url = "https://good/x.m3u8", Labels = [] },
            new() { Channel = "WildEarth.za", Url = "rtmp://bad/x" },
            new() { Channel = null, Url = "https://orphan/x.m3u8" },
        };
        var logos = new List<IptvLogoRow>
        {
            new() { Channel = "KykNet.za", Format = "SVG", Url = "https://logo/k.svg", Width = 512 },
            new() { Channel = "KykNet.za", Format = "PNG", Url = "https://logo/feed.png", Feed = "SD", Width = 512 },
            new() { Channel = "KykNet.za", Format = "PNG", Url = "https://logo/k.png", Width = 512 },
            new() { Channel = "KykNet.za", Format = "PNG", Url = "https://logo/huge.png", Width = 4000 },
        };

        var catalog = IptvOrgCatalogSource.Join(channels, streams, logos);

        Assert.Equal(["WildEarth.za", "KykNet.za", "Gone.za"], catalog.Select(c => c.Id));
        var wildEarth = catalog[0];
        Assert.Equal(["https://good/x.m3u8", "https://part/x.m3u8", "https://geo/x.m3u8"], wildEarth.Streams.Select(s => s.Url));
        var kyknet = catalog[1];
        Assert.False(kyknet.HasStreams);
        Assert.Equal("https://logo/k.png", kyknet.LogoUrl);
        Assert.True(catalog[2].IsClosed);
    }

    [Fact]
    public async Task Reads_the_real_api_format()
    {
        // Trimmed copies of real rows from https://iptv-org.github.io/api/.
        var handler = new StaticHandler(new()
        {
            ["channels.json"] = """[{"id":"KykNet.za","name":"KykNet","alt_names":[],"network":null,"owners":["Multichoice"],"country":"ZA","categories":["entertainment"],"is_nsfw":false,"launched":"1999-11-15","closed":null,"replaced_by":null,"website":"https://kyknet.dstv.com/"}]""",
            ["streams.json"] = """[{"channel":"KykNet.za","feed":null,"title":"KykNet","url":"https://x/y.m3u8","quality":"720p","labels":["Not 24/7"],"user_agent":"UA","referrer":null}]""",
            ["logos.json"] = """[{"channel":"KykNet.za","feed":null,"in_use":true,"tags":[],"width":512,"height":516,"format":"PNG","url":"https://i.imgur.com/4JmQCIt.png"}]""",
        });
        var catalog = await new IptvOrgCatalogSource(new HttpClient(handler)).LoadAsync(TestContext.Current.CancellationToken);

        var channel = Assert.Single(catalog);
        Assert.Equal("KykNet", channel.Name);
        Assert.Equal("https://i.imgur.com/4JmQCIt.png", channel.LogoUrl);
        Assert.Equal("UA", channel.Streams.Single().UserAgent);
    }

    [Fact]
    public async Task Uses_the_cached_catalogue_when_offline()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"gemaktv-cat-{Guid.NewGuid():N}");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var online = new StaticHandler(new()
            {
                ["channels.json"] = """[{"id":"A.za","name":"A"}]""",
                ["streams.json"] = "[]",
                ["logos.json"] = "[]",
            });
            await new IptvOrgCatalogSource(new HttpClient(online), cache).LoadAsync(ct);

            var offline = new StaticHandler([]);
            var catalog = await new IptvOrgCatalogSource(new HttpClient(offline), cache, maxAge: TimeSpan.Zero).LoadAsync(ct);
            Assert.Equal("A.za", Assert.Single(catalog).Id);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    private sealed class StaticHandler(Dictionary<string, string> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            return Task.FromResult(files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : throw new HttpRequestException("offline"));
        }
    }
}
