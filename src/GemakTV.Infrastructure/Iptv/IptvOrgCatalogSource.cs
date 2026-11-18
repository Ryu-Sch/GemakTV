using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GemakTV.Application;
using GemakTV.Application.Ports;
using GemakTV.Domain;
using GemakTV.Infrastructure.Serialization;

namespace GemakTV.Infrastructure.Iptv;

/// <summary>
/// The iptv-org database (https://github.com/iptv-org/database) through its JSON API: every known channel,
/// joined with the public streams and logos listed for it. Downloads are cached on disk for
/// <c>maxAge</c>; a stale cache is used when the network is down.
/// </summary>
public sealed class IptvOrgCatalogSource(HttpClient http, string? cacheDirectory = null, TimeSpan? maxAge = null) : IChannelCatalogSource
{
    public static readonly Uri DefaultApiBase = new("https://iptv-org.github.io/api/");

    public Uri ApiBase { get; init; } = DefaultApiBase;

    public async Task<IReadOnlyList<CatalogChannel>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var streamsTask = ReadArrayAsync("streams.json", GemakJsonContext.Default.IptvStreamRow, cancellationToken);
        var logosTask = ReadArrayAsync("logos.json", GemakJsonContext.Default.IptvLogoRow, cancellationToken);
        var channelsTask = ReadArrayAsync("channels.json", GemakJsonContext.Default.IptvChannelRow, cancellationToken);
        await Task.WhenAll(streamsTask, logosTask, channelsTask).ConfigureAwait(false);
        return Join(await channelsTask.ConfigureAwait(false), await streamsTask.ConfigureAwait(false), await logosTask.ConfigureAwait(false));
    }

    internal static IReadOnlyList<CatalogChannel> Join(IReadOnlyList<IptvChannelRow> channels, IReadOnlyList<IptvStreamRow> streams, IReadOnlyList<IptvLogoRow> logos)
    {
        var streamsByChannel = streams
            .Where(s => s.Channel is not null && StreamSource.IsValidUrl(s.Url))
            .GroupBy(s => s.Channel!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => OrderStreams(g), StringComparer.OrdinalIgnoreCase);

        var logoByChannel = logos
            .Where(l => l.Channel is not null && l.Url is not null && IsBitmap(l.Format))
            .GroupBy(l => l.Channel!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(l => l.Feed is null)          // the channel's own logo before a feed's
                      .ThenByDescending(l => l.InUse)
                      .ThenBy(l => Math.Abs(l.Width - 512))             // big enough for a TV, not huge
                      .First().Url,
                StringComparer.OrdinalIgnoreCase);

        var result = new List<CatalogChannel>(channels.Count);
        foreach (var row in channels)
        {
            if (row.Id is null || row.Name is null || row.IsNsfw) continue;
            result.Add(new CatalogChannel(
                row.Id,
                row.Name,
                row.AltNames ?? [],
                row.Country ?? "",
                row.Categories ?? [],
                logoByChannel.GetValueOrDefault(row.Id),
                streamsByChannel.GetValueOrDefault(row.Id) ?? [],
                IsClosed: !string.IsNullOrEmpty(row.Closed)));
        }
        return result;
    }

    /// <summary>
    /// Most dependable first: iptv-org marks unreliable streams with labels. Geo-blocked ones go last because
    /// they work only in some countries; "Not 24/7" ones just before them.
    /// </summary>
    internal static IReadOnlyList<StreamSource> OrderStreams(IEnumerable<IptvStreamRow> rows) => rows
        .Select((row, index) => (row, index))
        .OrderBy(x => Penalty(x.row.Labels))
        .ThenBy(x => x.index)
        .Select(x => new StreamSource(x.row.Url!, x.row.UserAgent, x.row.Referrer))
        .ToList();

    private static int Penalty(List<string>? labels)
    {
        if (labels is null) return 0;
        var penalty = 0;
        if (labels.Any(l => l.Contains("geo", StringComparison.OrdinalIgnoreCase))) penalty += 2;
        if (labels.Any(l => l.Contains("24/7", StringComparison.OrdinalIgnoreCase))) penalty += 1;
        return penalty;
    }

    // Android cannot draw SVG without extra libraries.
    private static bool IsBitmap(string? format) => format is null || !format.Equals("SVG", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<T>> ReadArrayAsync<T>(string file, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        await using var stream = await OpenAsync(file, cancellationToken).ConfigureAwait(false);
        var list = new List<T>();
        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable(stream, type, cancellationToken).ConfigureAwait(false))
            if (item is not null)
                list.Add(item);
        return list;
    }

    private async Task<Stream> OpenAsync(string file, CancellationToken cancellationToken)
    {
        if (cacheDirectory is null)
            return await http.GetStreamAsync(new Uri(ApiBase, file), cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(cacheDirectory);
        var cached = Path.Combine(cacheDirectory, file);
        var age = File.Exists(cached) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) : TimeSpan.MaxValue;
        if (age > (maxAge ?? TimeSpan.FromDays(1)))
        {
            try
            {
                var temp = cached + ".download";
                await using (var source = await http.GetStreamAsync(new Uri(ApiBase, file), cancellationToken).ConfigureAwait(false))
                await using (var target = File.Create(temp))
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                File.Move(temp, cached, overwrite: true);
            }
            catch (Exception) when (File.Exists(cached) && !cancellationToken.IsCancellationRequested)
            {
                // Offline or iptv-org unavailable: an older catalogue is better than none.
            }
        }
        return File.OpenRead(cached);
    }
}
