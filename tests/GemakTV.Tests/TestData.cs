using System.Security.Cryptography;
using GemakTV.Domain;
using GemakTV.Infrastructure.Crypto;

namespace GemakTV.Tests;

internal static class TestData
{
    public static readonly ConfigKey Key = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public static readonly ConfigKey OtherKey = new(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

    public static StreamSource Stream(string name) => new($"https://cdn.example/{name}.m3u8");

    public static Channel Channel(string id, params string[] streams) =>
        new(id, id.Split('.')[0], null, streams.Select(Stream));

    public static Lineup Lineup(params Channel[] channels) => new(channels);

    public static Lineup KykNetLineup() => Lineup(
        new Channel("KykNet.za", "kykNET", "https://i.imgur.com/4JmQCIt.png", [Stream("kyknet-a"), Stream("kyknet-b")]),
        new Channel("kykNETkie.za", "kykNET & kie", null, [Stream("kie")]));

    public static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
