using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using GemakTV.Domain;

namespace GemakTV.Platform;

/// <summary>
/// Channel logos, downloaded once and kept in app storage so they still show without internet.
/// Until a logo is available the channel's first letter is shown instead.
/// </summary>
public sealed class LogoLoader(Context context, HttpClient http)
{
    private const int MaxSize = 256;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap?> _memory = new();
    private readonly string _directory = System.IO.Path.Combine(context.FilesDir!.AbsolutePath, "logos");

    public void Bind(ImageView image, TextView letter, Channel channel) => Bind(image, letter, channel.Name, channel.LogoUrl);

    public void Bind(ImageView image, TextView letter, string name, string? logoUrl)
    {
        letter.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        letter.Visibility = ViewStates.Visible;
        image.SetImageDrawable(null);
        image.Tag = logoUrl;
        if (logoUrl is null) return;
        _ = BindAsync(image, letter, logoUrl);
    }

    public void Prefetch(Lineup lineup)
    {
        foreach (var channel in lineup.Channels)
            if (channel.LogoUrl is { } url)
                _ = LoadAsync(url);
    }

    public async Task<Bitmap?> LoadAsync(string url)
    {
        if (_memory.TryGetValue(url, out var cached)) return cached;
        var file = System.IO.Path.Combine(_directory, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url))));
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(_directory);
                var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(file + ".tmp", bytes).ConfigureAwait(false);
                File.Move(file + ".tmp", file, overwrite: true);
            }
            var bitmap = await Task.Run(() => Decode(file)).ConfigureAwait(false);
            _memory[url] = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null; // try again next time
        }
    }

    private async Task BindAsync(ImageView image, TextView letter, string url)
    {
        var bitmap = await LoadAsync(url);
        // The view may have been reused for another channel meanwhile.
        if (bitmap is null || !Equals(image.Tag?.ToString(), url)) return;
        image.Post(() =>
        {
            image.SetImageBitmap(bitmap);
            letter.Visibility = ViewStates.Gone;
        });
    }

    private static Bitmap? Decode(string file)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(file, bounds);
        if (bounds.OutWidth <= 0) return null;
        var sample = 1;
        while (bounds.OutWidth / (sample * 2) >= MaxSize && bounds.OutHeight / (sample * 2) >= MaxSize) sample *= 2;
        return BitmapFactory.DecodeFile(file, new BitmapFactory.Options { InSampleSize = sample });
    }
}
