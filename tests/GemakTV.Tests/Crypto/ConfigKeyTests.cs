using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GemakTV.Infrastructure.Crypto;

namespace GemakTV.Tests.Crypto;

public class ConfigKeyTests
{
    [Fact]
    public void Same_signing_key_always_gives_the_same_config_key()
    {
        using var rsa = RSA.Create(2048);
        var copy = RSA.Create();
        copy.ImportParameters(rsa.ExportParameters(true));

        Assert.Equal(ConfigKey.DeriveFromSigningKey(rsa).Bytes, ConfigKey.DeriveFromSigningKey(copy).Bytes);
    }

    [Fact]
    public void Different_signing_keys_give_different_config_keys()
    {
        using var a = RSA.Create(2048);
        using var b = RSA.Create(2048);
        Assert.NotEqual(ConfigKey.DeriveFromSigningKey(a).Bytes, ConfigKey.DeriveFromSigningKey(b).Bytes);
    }

    [Fact]
    public void A_public_key_alone_cannot_derive_it()
    {
        using var rsa = RSA.Create(2048);
        using var publicOnly = RSA.Create();
        publicOnly.ImportParameters(rsa.ExportParameters(false));
        Assert.ThrowsAny<CryptographicException>(() => ConfigKey.DeriveFromSigningKey(publicOnly));
    }

    [Fact]
    public void Derives_from_a_password_protected_pkcs12_keystore()
    {
        // The same kind of file keytool writes for Android signing.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=GemakTV test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"gemaktv-test-{Guid.NewGuid():N}.p12");
        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, "s3cret!"));
            var fromFile = ConfigKey.DeriveFromKeystore(path, "s3cret!");
            Assert.Equal(ConfigKey.DeriveFromSigningKey(rsa).Bytes, fromFile.Bytes);
            Assert.ThrowsAny<CryptographicException>(() => ConfigKey.DeriveFromKeystore(path, "wrong"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fingerprint_is_short_and_does_not_reveal_the_key()
    {
        var fingerprint = TestData.Key.Fingerprint;
        Assert.Equal(12, fingerprint.Length);
        Assert.DoesNotContain(Convert.ToHexStringLower(TestData.Key.Bytes)[..12], fingerprint);
    }
}
