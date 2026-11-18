using System.Text.Json;
using System.Text.Json.Serialization;
using GemakTV.Application.Ports;
using GemakTV.Domain;

namespace GemakTV.Infrastructure.Serialization;

/// <summary>
/// JSON for the published config and the TV's local lineup file. Compatibility rule: only ever add fields.
/// Older TVs ignore what they do not know, and the "app" section must stay readable by every version that is
/// out there, because it is how they update themselves.
/// </summary>
public static class ConfigSerializer
{
    public static byte[] Serialize(RemoteConfig config) =>
        JsonSerializer.SerializeToUtf8Bytes(ToDto(config), GemakJsonContext.Default.ConfigDto);

    public static RemoteConfig Deserialize(ReadOnlySpan<byte> json)
    {
        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, GemakJsonContext.Default.ConfigDto);
        }
        catch (JsonException ex)
        {
            throw new ConfigUnreadableException("The published config is not valid JSON.", ex);
        }
        if (dto is null) throw new ConfigUnreadableException("The published config is empty.");
        try
        {
            return new RemoteConfig(
                dto.Revision,
                dto.UpdatedUtc,
                dto.UpdatedBy ?? "unknown",
                ToLineup(dto.Channels),
                dto.App is { } app ? new AppRelease(app.VersionCode, app.VersionName ?? app.VersionCode.ToString(), app.Path ?? "", app.Sha256 ?? "", app.Size) : null);
        }
        catch (ArgumentException ex)
        {
            throw new ConfigUnreadableException($"The published config is invalid: {ex.Message}", ex);
        }
    }

    public static byte[] SerializeLineup(Lineup lineup) =>
        JsonSerializer.SerializeToUtf8Bytes(new LineupDto { Schema = RemoteConfig.CurrentSchema, Channels = ToDtos(lineup) }, GemakJsonContext.Default.LineupDto);

    public static Lineup DeserializeLineup(ReadOnlySpan<byte> json)
    {
        var dto = JsonSerializer.Deserialize(json, GemakJsonContext.Default.LineupDto)
            ?? throw new ConfigUnreadableException("The channel list file is empty.");
        return ToLineup(dto.Channels);
    }

    private static ConfigDto ToDto(RemoteConfig config) => new()
    {
        Schema = RemoteConfig.CurrentSchema,
        Revision = config.Revision,
        UpdatedUtc = config.UpdatedUtc,
        UpdatedBy = config.UpdatedBy,
        Channels = ToDtos(config.Lineup),
        App = config.App is { } app
            ? new AppDto { VersionCode = app.VersionCode, VersionName = app.VersionName, Path = app.BlobPath, Sha256 = app.Sha256, Size = app.Size }
            : null,
    };

    private static List<ChannelDto> ToDtos(Lineup lineup) => lineup.Channels.Select(c => new ChannelDto
    {
        Id = c.Id,
        Name = c.Name,
        Logo = c.LogoUrl,
        Streams = c.Streams.Select(s => new StreamDto { Url = s.Url, UserAgent = s.UserAgent, Referrer = s.Referrer }).ToList(),
    }).ToList();

    private static Lineup ToLineup(List<ChannelDto>? channels) => new((channels ?? []).Select(c => new Channel(
        c.Id ?? throw new ArgumentException("a channel has no id"),
        c.Name ?? c.Id,
        c.Logo,
        (c.Streams ?? []).Where(s => StreamSource.IsValidUrl(s.Url)).Select(s => new StreamSource(s.Url!, s.UserAgent, s.Referrer)))));
}

internal sealed class ConfigDto
{
    public int Schema { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public List<ChannelDto>? Channels { get; set; }
    public AppDto? App { get; set; }
}

internal sealed class LineupDto
{
    public int Schema { get; set; }
    public List<ChannelDto>? Channels { get; set; }
}

internal sealed class ChannelDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Logo { get; set; }
    public List<StreamDto>? Streams { get; set; }
}

internal sealed class StreamDto
{
    public string? Url { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
}

internal sealed class AppDto
{
    public int VersionCode { get; set; }
    public string? VersionName { get; set; }
    public string? Path { get; set; }
    public string? Sha256 { get; set; }
    public long Size { get; set; }
}

// iptv-org API rows (https://iptv-org.github.io/api/)
internal sealed class IptvChannelRow
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("alt_names")] public List<string>? AltNames { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("is_nsfw")] public bool IsNsfw { get; set; }
    [JsonPropertyName("closed")] public string? Closed { get; set; }
}

internal sealed class IptvStreamRow
{
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
    [JsonPropertyName("labels")] public List<string>? Labels { get; set; }
    [JsonPropertyName("user_agent")] public string? UserAgent { get; set; }
    [JsonPropertyName("referrer")] public string? Referrer { get; set; }
}

internal sealed class IptvLogoRow
{
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("feed")] public string? Feed { get; set; }
    [JsonPropertyName("in_use")] public bool InUse { get; set; } = true;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ConfigDto))]
[JsonSerializable(typeof(LineupDto))]
[JsonSerializable(typeof(IptvChannelRow))]
[JsonSerializable(typeof(IptvStreamRow))]
[JsonSerializable(typeof(IptvLogoRow))]
internal sealed partial class GemakJsonContext : JsonSerializerContext;
