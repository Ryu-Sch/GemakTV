using System.Security.Cryptography;
using GemakTV.Infrastructure.Crypto;

namespace GemakTV.Tests.Crypto;

/// <summary>The envelope protects every published file, so its failure modes matter more than its happy path.</summary>
public class GtvCipherTests
{
    private const int Chunk = 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]          // exactly one full chunk: the last chunk is full, not empty
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData(5 * Chunk + 17)]
    public void Round_trips_every_size_around_chunk_boundaries(int size)
    {
        var plain = TestData.RandomBytes(size);
        var sealedBytes = GtvCipher.Encrypt(plain, TestData.Key, GtvContent.Package, Chunk);
        Assert.Equal(plain, GtvCipher.Decrypt(sealedBytes, TestData.Key, GtvContent.Package));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var plain = "hello"u8.ToArray();
        Assert.NotEqual(
            GtvCipher.Encrypt(plain, TestData.Key, GtvContent.Config),
            GtvCipher.Encrypt(plain, TestData.Key, GtvContent.Config));
    }

    [Fact]
    public void Wrong_key_is_rejected()
    {
        var sealedBytes = GtvCipher.Encrypt("secret"u8, TestData.Key, GtvContent.Config);
        Assert.Throws<CryptographicException>(() => GtvCipher.Decrypt(sealedBytes, TestData.OtherKey, GtvContent.Config));
    }

    [Theory]
    [InlineData(5)]     // header byte (content kind) - also authenticated as associated data
    [InlineData(14)]    // nonce prefix
    [InlineData(30)]    // ciphertext
    [InlineData(-1)]    // last byte of the final tag
    public void Any_flipped_bit_is_detected(int position)
    {
        var sealedBytes = GtvCipher.Encrypt(TestData.RandomBytes(3000), TestData.Key, GtvContent.Package, Chunk);
        var index = position >= 0 ? position : sealedBytes.Length + position;
        sealedBytes[index] ^= 0x01;
        Assert.Throws<CryptographicException>(() => GtvCipher.Decrypt(sealedBytes, TestData.Key, GtvContent.Package));
    }

    [Fact]
    public void Cutting_off_whole_trailing_chunks_is_detected()
    {
        // Truncating exactly at a chunk boundary leaves only valid chunks; the "last chunk" flag catches it.
        var sealedBytes = GtvCipher.Encrypt(TestData.RandomBytes(3 * Chunk), TestData.Key, GtvContent.Package, Chunk);
        var twoChunks = sealedBytes[..(GtvCipher.HeaderSize + 2 * (Chunk + GtvCipher.TagSize))];
        Assert.Throws<CryptographicException>(() => GtvCipher.Decrypt(twoChunks, TestData.Key, GtvContent.Package));
    }

    [Fact]
    public void Swapping_chunks_is_detected()
    {
        var sealedBytes = GtvCipher.Encrypt(TestData.RandomBytes(3 * Chunk), TestData.Key, GtvContent.Package, Chunk);
        var size = Chunk + GtvCipher.TagSize;
        var first = sealedBytes.AsSpan(GtvCipher.HeaderSize, size).ToArray();
        var second = sealedBytes.AsSpan(GtvCipher.HeaderSize + size, size).ToArray();
        second.CopyTo(sealedBytes, GtvCipher.HeaderSize);
        first.CopyTo(sealedBytes, GtvCipher.HeaderSize + size);
        Assert.Throws<CryptographicException>(() => GtvCipher.Decrypt(sealedBytes, TestData.Key, GtvContent.Package));
    }

    [Fact]
    public void A_package_cannot_be_passed_off_as_a_config()
    {
        var sealedBytes = GtvCipher.Encrypt("{}"u8, TestData.Key, GtvContent.Package);
        Assert.Throws<CryptographicException>(() => GtvCipher.Decrypt(sealedBytes, TestData.Key, GtvContent.Config));
    }

    [Fact]
    public void Garbage_is_reported_as_not_a_gemaktv_file()
    {
        var ex = Assert.Throws<CryptographicException>(() => GtvCipher.Decrypt("<html>rate limited</html>"u8, TestData.Key, GtvContent.Config));
        Assert.Contains("not a GemakTV file", ex.Message);
    }

    [Fact]
    public async Task Streams_large_payloads_with_the_default_chunk_size()
    {
        var plain = TestData.RandomBytes(5 * GtvCipher.DefaultChunkSize + 123);
        using var sealedStream = new MemoryStream();
        await GtvCipher.EncryptAsync(new MemoryStream(plain), sealedStream, TestData.Key, GtvContent.Package, cancellationToken: TestContext.Current.CancellationToken);
        sealedStream.Position = 0;
        using var output = new MemoryStream();
        await GtvCipher.DecryptAsync(sealedStream, output, TestData.Key, GtvContent.Package, TestContext.Current.CancellationToken);
        Assert.Equal(TestData.Sha256(plain), TestData.Sha256(output.ToArray()));
    }
}
