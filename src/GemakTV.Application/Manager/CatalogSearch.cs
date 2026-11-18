using System.Globalization;
using System.Text;

namespace GemakTV.Application.Manager;

/// <summary>
/// Finds channels in the iptv-org catalogue by name, alternative name or id. Accents and punctuation are
/// ignored ("kyknet kie" finds "kykNET&amp;kie"). Exact matches rank first, then prefixes, then the rest;
/// channels that actually have streams rank above those that do not.
/// </summary>
public sealed class CatalogSearch
{
    private readonly IReadOnlyList<Entry> _entries;

    public CatalogSearch(IEnumerable<CatalogChannel> channels)
    {
        _entries = channels
            .Select(c => new Entry(c, [Normalize(c.Name), .. c.AltNames.Select(Normalize), Normalize(c.Id)]))
            .ToList();
    }

    public int Count => _entries.Count;

    public IReadOnlyList<CatalogChannel> Search(string? query, bool onlyWithStreams = false, string? country = null, int limit = 200)
    {
        var needle = Normalize(query ?? "");
        IEnumerable<Entry> candidates = _entries;
        if (onlyWithStreams) candidates = candidates.Where(e => e.Channel.HasStreams);
        if (!string.IsNullOrWhiteSpace(country))
            candidates = candidates.Where(e => string.Equals(e.Channel.Country, country, StringComparison.OrdinalIgnoreCase));

        if (needle.Length == 0)
        {
            return candidates
                .Where(e => !e.Channel.IsClosed)
                .OrderByDescending(e => e.Channel.HasStreams)
                .ThenBy(e => e.Channel.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(e => e.Channel)
                .ToList();
        }

        return candidates
            .Select(e => (e.Channel, Score: Score(e, needle)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Channel.HasStreams)
            .ThenBy(x => x.Channel.IsClosed)
            .ThenBy(x => x.Channel.Name.Length)
            .ThenBy(x => x.Channel.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Channel)
            .ToList();
    }

    private static int Score(Entry entry, string needle)
    {
        var best = 0;
        foreach (var key in entry.Keys)
        {
            if (key.Length == 0) continue;
            var score = key == needle ? 100
                : key.StartsWith(needle, StringComparison.Ordinal) ? 60
                : key.Contains(needle, StringComparison.Ordinal) ? 30
                : 0;
            best = Math.Max(best, score);
        }
        return best;
    }

    /// <summary>Lower-case, accents removed, only letters and digits.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private sealed record Entry(CatalogChannel Channel, string[] Keys);
}
