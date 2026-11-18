using Android.Content;
using Android.Util;
using Java.Security;
using Java.Security.Cert;
using Javax.Net.Ssl;

namespace GemakTV.Platform;

/// <summary>
/// Android 6 shipped in 2015 and does not trust certificate authorities that many sites use today, most notably
/// Let's Encrypt (ISRG Root X1). Without this, a large share of IPTV streams and logos fail with
/// "trust anchor not found". We trust the system's CAs plus the public roots bundled in Assets/certs,
/// for every HTTPS connection the app makes (the player's and HttpClient's).
/// </summary>
public static class TrustStore
{
    public static void Install(Context context)
    {
        try
        {
            var store = KeyStore.GetInstance(KeyStore.DefaultType)!;
            store.Load(null, null);

            var system = KeyStore.GetInstance("AndroidCAStore")!;
            system.Load(null, null);
            var aliases = system.Aliases()!;
            while (aliases.HasMoreElements)
            {
                var alias = aliases.NextElement()!.ToString();
                store.SetCertificateEntry(alias, system.GetCertificate(alias));
            }

            var factory = CertificateFactory.GetInstance("X.509")!;
            var added = 0;
            foreach (var name in context.Assets!.List("certs") ?? [])
            {
                using var stream = context.Assets.Open("certs/" + name);
                store.SetCertificateEntry("gemaktv:" + name, factory.GenerateCertificate(stream));
                added++;
            }

            var trust = TrustManagerFactory.GetInstance(TrustManagerFactory.DefaultAlgorithm)!;
            trust.Init(store);
            var ssl = SSLContext.GetInstance("TLS")!;
            ssl.Init(null, trust.GetTrustManagers(), null);
            HttpsURLConnection.DefaultSSLSocketFactory = ssl.SocketFactory;
            SSLContext.Default = ssl;
            Log.Info("GemakTV", $"Trust store ready: system CAs + {added} bundled roots.");
        }
        catch (Exception ex)
        {
            // Fall back to the system trust store rather than not starting at all.
            Log.Warn("GemakTV", $"Could not extend the trust store: {ex}");
        }
    }
}
