using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace GemakTV.Manager;

public sealed record Credentials(string Username, string Token, string? ApiBase);

/// <summary>
/// Remembers the sign-in on this phone. The token is encrypted with an AES key that lives in the Android Keystore
/// (hardware-backed where the phone supports it) and never leaves it, so a copy of the app's files is useless.
/// </summary>
public sealed class CredentialStore(Context context)
{
    private const string KeyAlias = "gemaktv-manager-credentials";
    private readonly ISharedPreferences _prefs = context.GetSharedPreferences("credentials", FileCreationMode.Private)!;

    public Credentials? Load()
    {
        var user = _prefs.GetString("user", null);
        var sealedToken = _prefs.GetString("token", null);
        if (user is null || sealedToken is null) return null;
        try
        {
            return new Credentials(user, Decrypt(sealedToken), _prefs.GetString("api", null));
        }
        catch (Java.Lang.Exception)
        {
            Clear(); // key lost (e.g. the phone's lock screen was reset): sign in again
            return null;
        }
    }

    public string? LastUsername => _prefs.GetString("user", null);

    public void Save(Credentials credentials) =>
        _prefs.Edit()!
            .PutString("user", credentials.Username)!
            .PutString("token", Encrypt(credentials.Token))!
            .PutString("api", credentials.ApiBase)!
            .Commit();

    /// <summary>Forgets the token but keeps the username for convenience.</summary>
    public void Clear() => _prefs.Edit()!.Remove("token")!.Remove("api")!.Commit();

    private static string Encrypt(string text)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV()!;
        var data = cipher.DoFinal(Encoding.UTF8.GetBytes(text))!;
        return Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(data);
    }

    private static string Decrypt(string sealedText)
    {
        var parts = sealedText.Split(':');
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, Convert.FromBase64String(parts[0])));
        return Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(parts[1]))!);
    }

    private static IKey Key()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
