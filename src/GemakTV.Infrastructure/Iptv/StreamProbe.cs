using System.Net;
using System.Text;
using GemakTV.Domain;

namespace GemakTV.Infrastructure.Iptv;

public enum StreamHealth
{
    Working,
    NotAPlaylist,
    Blocked,
    NotFound,
    Unreachable,
}

/// <summary>
/// Quick check of a stream address from the manager app: does it answer, and with an HLS playlist?
/// It cannot prove the video plays, but it catches dead, moved and geo-blocked links before they reach the TV.
/// </summary>
public sealed class StreamProbe(HttpClient http)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<StreamHealth> CheckAsync(StreamSource stream, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
            request.Headers.UserAgent.ParseAdd(stream.UserAgent ?? "Mozilla/5.0 (Linux; Android 6.0.1; GemakTV) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
            if (stream.Referrer is { } referrer) request.Headers.Referrer = new Uri(referrer);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or (HttpStatusCode)451)
                return StreamHealth.Blocked;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return StreamHealth.NotFound;
            if (!response.IsSuccessStatusCode)
                return StreamHealth.Unreachable;

            // Only the first bytes are needed: an HLS playlist starts with #EXTM3U.
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[512];
            var read = await body.ReadAtLeastAsync(buffer, 7, throwOnEndOfStream: false, timeout.Token).ConfigureAwait(false);
            var start = Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (start.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return StreamHealth.Working;

            // Not a playlist, but a video container (MPEG-TS sync byte or MP4 box) can still play.
            var isMedia = read > 0 && (buffer[0] == 0x47 || (read >= 8 && Encoding.ASCII.GetString(buffer, 4, 4) is "ftyp" or "styp"));
            return isMedia ? StreamHealth.Working : StreamHealth.NotAPlaylist;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StreamHealth.Unreachable; // timed out
        }
        catch (HttpRequestException)
        {
            return StreamHealth.Unreachable;
        }
    }

    public static string Describe(StreamHealth health) => health switch
    {
        StreamHealth.Working => "Working",
        StreamHealth.NotAPlaylist => "Answers, but is not a video stream",
        StreamHealth.Blocked => "Refused (may be blocked in this country)",
        StreamHealth.NotFound => "Gone (404)",
        _ => "Not responding",
    };
}
