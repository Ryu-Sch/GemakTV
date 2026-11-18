using System.Net;
using System.Text;
using GemakTV.Domain;
using GemakTV.Infrastructure.Iptv;

namespace GemakTV.Tests.Infrastructure;

public class StreamProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "#EXTM3U\n#EXT-X-VERSION:3\n", StreamHealth.Working)]
    [InlineData(HttpStatusCode.OK, "﻿\n#extm3u\n", StreamHealth.Working)]
    [InlineData(HttpStatusCode.OK, "<html>Sorry, not in your region</html>", StreamHealth.NotAPlaylist)]
    [InlineData(HttpStatusCode.Forbidden, "", StreamHealth.Blocked)]
    [InlineData(HttpStatusCode.NotFound, "", StreamHealth.NotFound)]
    [InlineData(HttpStatusCode.BadGateway, "", StreamHealth.Unreachable)]
    public async Task Classifies_the_answer(HttpStatusCode status, string body, StreamHealth expected)
    {
        var probe = new StreamProbe(new HttpClient(new Answer(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) })));
        Assert.Equal(expected, await probe.CheckAsync(new StreamSource("https://x/y.m3u8"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_raw_mpeg_ts_stream_counts_as_working()
    {
        var ts = new byte[188];
        ts[0] = 0x47;
        var probe = new StreamProbe(new HttpClient(new Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ts) })));
        Assert.Equal(StreamHealth.Working, await probe.CheckAsync(new StreamSource("http://x/live.ts"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sends_the_streams_user_agent_and_referrer()
    {
        HttpRequestMessage? seen = null;
        var probe = new StreamProbe(new HttpClient(new Answer(r => { seen = r; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U") }; })));
        await probe.CheckAsync(new StreamSource("https://x/y.m3u8", "SpecialPlayer/1.0", "https://site.example/"), TestContext.Current.CancellationToken);
        Assert.Equal("SpecialPlayer/1.0", seen!.Headers.UserAgent.ToString());
        Assert.Equal(new Uri("https://site.example/"), seen.Headers.Referrer);
    }

    [Fact]
    public async Task Silence_is_reported_as_not_responding()
    {
        var probe = new StreamProbe(new HttpClient(new Hang())) { Timeout = TimeSpan.FromMilliseconds(100) };
        Assert.Equal(StreamHealth.Unreachable, await probe.CheckAsync(new StreamSource("https://x/y.m3u8"), TestContext.Current.CancellationToken));
    }

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private sealed class Hang : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
