using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GemakTV.Infrastructure.Crypto;

/// <summary>
/// The 32-byte key that encrypts the published config and APKs. It is derived one-way (HKDF-SHA256) from the
/// private part of the app signing key, so the signing keystore is the only secret to keep safe, and someone who
/// digs this key out of an APK still learns nothing about the signing key.
/// </summary>
public sealed class ConfigKey
{
    public const int Size = 32;
    public const string ResourceName = "GemakTV.config.key";
    private static readonly byte[] Salt = "GemakTV/config-key/v1"u8.ToArray();
    private readonly byte[] _bytes;

    public ConfigKey(byte[] bytes)
    {
        if (bytes.Length != Size) throw new ArgumentException($"A config key is {Size} bytes, not {bytes.Length}.", nameof(bytes));
        _bytes = (byte[])bytes.Clone();
    }

    public byte[] Bytes => _bytes;

    /// <summary>Short public fingerprint, safe to show in logs and the UI to confirm two builds share a key.</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(_bytes))[..12];

    public static ConfigKey DeriveFromSigningKey(RSA signingKey)
    {
        var p = signingKey.ExportParameters(includePrivateParameters: true);
        if (p.D is null || p.P is null || p.Q is null)
            throw new ArgumentException("The signing key has no private part.", nameof(signingKey));
        var material = new byte[p.D.Length + p.P.Length + p.Q.Length];
        p.D.CopyTo(material, 0);
        p.P.CopyTo(material, p.D.Length);
        p.Q.CopyTo(material, p.D.Length + p.P.Length);
        try
        {
            // The modulus ties the info to this particular key pair.
            var info = Encoding.ASCII.GetBytes("config-encryption:" + Convert.ToHexStringLower(SHA256.HashData(p.Modulus!)));
            return new ConfigKey(HKDF.DeriveKey(HashAlgorithmName.SHA256, material, Size, Salt, info));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    /// <summary>Derives the key from a PKCS#12 keystore created by keytool.</summary>
    public static ConfigKey DeriveFromKeystore(string keystorePath, string password)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(keystorePath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The keystore does not contain an RSA private key.");
        return DeriveFromSigningKey(rsa);
    }

    public static ConfigKey FromFile(string path) => new(File.ReadAllBytes(path));

    /// <summary>Loads the key the build embedded into the app (see build/Secrets.props).</summary>
    public static ConfigKey FromEmbeddedResource(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{assembly.GetName().Name} was built without the config key.");
        var bytes = new byte[Size];
        stream.ReadExactly(bytes);
        return new ConfigKey(bytes);
    }
}
