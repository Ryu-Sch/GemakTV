using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GemakTV.Infrastructure.Crypto;

namespace GemakTV.Publisher;

/// <summary>
/// The secrets folder (default %USERPROFILE%\.gemaktv, override with GEMAKTV_SECRETS). Never inside the repository.
/// </summary>
internal sealed class Secrets(string directory)
{
    public const string Alias = "gemaktv";

    public string Directory { get; } = directory;

    public string KeystorePath => Path.Combine(Directory, "gemaktv.keystore");

    public string PasswordPath => Path.Combine(Directory, "keystore.pass");

    public string ConfigKeyPath => Path.Combine(Directory, "config.key");

    public bool HasKeystore => File.Exists(KeystorePath) && File.Exists(PasswordPath);

    public static Secrets Default()
    {
        var dir = Environment.GetEnvironmentVariable("GEMAKTV_SECRETS");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemaktv");
        return new Secrets(dir);
    }

    public string ReadPassword() => File.ReadLines(PasswordPath).First().Trim();

    public ConfigKey LoadConfigKey()
    {
        if (!File.Exists(ConfigKeyPath))
            throw new InvalidOperationException($"No config key at {ConfigKeyPath}. Run 'keys create' (first time) or 'keys derive'.");
        return ConfigKey.FromFile(ConfigKeyPath);
    }

    /// <summary>Creates a 4096-bit RSA signing key with keytool (so Android's signing tools read it without fuss).</summary>
    public void CreateKeystore(string keytool, string distinguishedName)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        // Twice: apksigner reads the next line each time the same file is named (store password, then key password).
        File.WriteAllText(PasswordPath, password + "\n" + password + "\n");
        RestrictToCurrentUser(PasswordPath);

        var start = new ProcessStartInfo(keytool) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[]
        {
            "-genkeypair", "-keystore", KeystorePath, "-storetype", "PKCS12", "-alias", Alias,
            "-keyalg", "RSA", "-keysize", "4096", "-validity", "36500", "-dname", distinguishedName,
            "-storepass:file", PasswordPath, "-keypass:file", PasswordPath,
        })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"keytool failed ({process.ExitCode}): {stderr}");
        RestrictToCurrentUser(KeystorePath);
    }

    public ConfigKey DeriveConfigKey()
    {
        var key = ConfigKey.DeriveFromKeystore(KeystorePath, ReadPassword());
        File.WriteAllBytes(ConfigKeyPath, key.Bytes);
        RestrictToCurrentUser(ConfigKeyPath);
        return key;
    }

    public string CertificateSha256()
    {
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(KeystorePath, ReadPassword());
        return Convert.ToHexString(SHA256.HashData(cert.RawData));
    }

    public static string FindKeytool()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
            candidates.Add(Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "keytool.exe" : "keytool"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { @"C:\Program Files\Android\openjdk", @"C:\Program Files\Microsoft", @"C:\Program Files\Java" })
                if (System.IO.Directory.Exists(root))
                    candidates.AddRange(System.IO.Directory.GetDirectories(root, "jdk*").OrderDescending().Select(d => Path.Combine(d, "bin", "keytool.exe")));
        }
        candidates.Add("keytool");
        return candidates.FirstOrDefault(File.Exists) ?? "keytool";
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // On Windows the user profile folder is already private to the user.
    }
}
