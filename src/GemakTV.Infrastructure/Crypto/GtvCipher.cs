using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GemakTV.Infrastructure.Crypto;

public enum GtvContent : byte
{
    Config = 1,
    Package = 2,
}

/// <summary>
/// The ".gtv" envelope used for everything GemakTV publishes: AES-256-GCM in independently authenticated
/// chunks (the STREAM construction), so a 10 MB APK can be decrypted while it downloads without holding it
/// in memory, and any tampering, truncation, reordering or wrong key is detected.
/// <code>
/// header (20 bytes, also the associated data of every chunk):
///   "GMTV" | version=1 | content kind | 0x0000 | chunk size (u32 LE) | nonce prefix (7 random bytes) | 0x00
/// chunk i:  AES-GCM(key, nonce = prefix | i (u32 BE) | last?1:0, plaintext[i]) || tag (16 bytes)
/// </code>
/// </summary>
public static class GtvCipher
{
    public const int HeaderSize = 20;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 64 * 1024;
    private const int MaxChunkSize = 4 * 1024 * 1024;
    private const byte FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "GMTV"u8;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ConfigKey key, GtvContent content, int chunkSize = DefaultChunkSize)
    {
        using var input = new MemoryStream(plaintext.ToArray());
        using var output = new MemoryStream();
        EncryptAsync(input, output, key, content, chunkSize).GetAwaiter().GetResult();
        return output.ToArray();
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ConfigKey key, GtvContent content)
    {
        using var input = new MemoryStream(envelope.ToArray());
        using var output = new MemoryStream();
        DecryptAsync(input, output, key, content).GetAwaiter().GetResult();
        return output.ToArray();
    }

    public static async Task EncryptAsync(Stream input, Stream output, ConfigKey key, GtvContent content, int chunkSize = DefaultChunkSize, CancellationToken cancellationToken = default)
    {
        if (chunkSize is <= 0 or > MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        header[5] = (byte)content;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)chunkSize);
        RandomNumberGenerator.Fill(header.AsSpan(12, 7));
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        using var aes = new AesGcm(key.Bytes, TagSize);
        var current = new byte[chunkSize];
        var next = new byte[chunkSize];
        var sealedChunk = new byte[chunkSize + TagSize];
        var currentLength = await ReadFullAsync(input, current, cancellationToken).ConfigureAwait(false);
        for (uint index = 0; ; index++)
        {
            // Look ahead one chunk: the last chunk is sealed differently so truncation is detectable.
            var nextLength = currentLength == chunkSize ? await ReadFullAsync(input, next, cancellationToken).ConfigureAwait(false) : 0;
            var isLast = nextLength == 0;

            var nonce = Nonce(header, index, isLast);
            aes.Encrypt(nonce, current.AsSpan(0, currentLength), sealedChunk.AsSpan(0, currentLength), sealedChunk.AsSpan(currentLength, TagSize), header);
            await output.WriteAsync(sealedChunk.AsMemory(0, currentLength + TagSize), cancellationToken).ConfigureAwait(false);

            if (isLast) break;
            (current, next) = (next, current);
            currentLength = nextLength;
        }
    }

    /// <exception cref="CryptographicException">Wrong key, wrong content kind, or the data was altered or cut short.</exception>
    public static async Task DecryptAsync(Stream input, Stream output, ConfigKey key, GtvContent content, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        if (await ReadFullAsync(input, header, cancellationToken).ConfigureAwait(false) != HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("This is not a GemakTV file.");
        if (header[4] != FormatVersion)
            throw new CryptographicException($"Unsupported GemakTV file version {header[4]}.");
        if (header[5] != (byte)content)
            throw new CryptographicException($"Expected a {content} file but found kind {header[5]}.");
        var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        if (chunkSize is <= 0 or > MaxChunkSize)
            throw new CryptographicException("Corrupt GemakTV header.");

        using var aes = new AesGcm(key.Bytes, TagSize);
        var sealedSize = chunkSize + TagSize;
        var current = new byte[sealedSize];
        var next = new byte[sealedSize];
        var plain = new byte[chunkSize];
        var currentLength = await ReadFullAsync(input, current, cancellationToken).ConfigureAwait(false);
        for (uint index = 0; ; index++)
        {
            if (currentLength < TagSize)
                throw new CryptographicException("The GemakTV file is truncated.");
            var nextLength = currentLength == sealedSize ? await ReadFullAsync(input, next, cancellationToken).ConfigureAwait(false) : 0;
            var isLast = nextLength == 0;

            var dataLength = currentLength - TagSize;
            try
            {
                aes.Decrypt(Nonce(header, index, isLast), current.AsSpan(0, dataLength), current.AsSpan(dataLength, TagSize), plain.AsSpan(0, dataLength), header);
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException("The GemakTV file could not be decrypted: wrong key, or it was altered or cut short.", ex);
            }
            await output.WriteAsync(plain.AsMemory(0, dataLength), cancellationToken).ConfigureAwait(false);

            if (isLast) break;
            (current, next) = (next, current);
            currentLength = nextLength;
        }
    }

    private static byte[] Nonce(byte[] header, uint index, bool isLast)
    {
        var nonce = new byte[12];
        header.AsSpan(12, 7).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(7, 4), index);
        nonce[11] = isLast ? (byte)1 : (byte)0;
        return nonce;
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
