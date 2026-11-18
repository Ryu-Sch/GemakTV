namespace GemakTV.Domain;

/// <summary>What changed between two lineups, phrased so the TV can tell the viewer in plain words.</summary>
public sealed record LineupDiff(
    IReadOnlyList<Channel> Added,
    IReadOnlyList<Channel> Removed,
    IReadOnlyList<Channel> Updated,
    bool OrderChanged)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Updated.Count > 0 || OrderChanged;

    public static LineupDiff Between(Lineup before, Lineup after)
    {
        var added = after.Channels.Where(c => !before.Contains(c.Id)).ToList();
        var removed = before.Channels.Where(c => !after.Contains(c.Id)).ToList();
        var updated = after.Channels
            .Where(c => before.Find(c.Id) is { } old && !old.Equals(c))
            .ToList();

        // Order matters only among channels present on both sides.
        var keptBefore = before.Channels.Where(c => after.Contains(c.Id)).Select(c => c.Id.ToUpperInvariant());
        var keptAfter = after.Channels.Where(c => before.Contains(c.Id)).Select(c => c.Id.ToUpperInvariant());
        var orderChanged = !keptBefore.SequenceEqual(keptAfter);

        return new LineupDiff(added, removed, updated, orderChanged);
    }

    /// <summary>A short sentence such as "Added: kykNET &amp; kie. Removed: SABC 2."</summary>
    public string Describe()
    {
        if (!HasChanges) return "No changes.";
        var parts = new List<string>();
        if (Added.Count > 0) parts.Add($"Added: {Names(Added)}.");
        if (Removed.Count > 0) parts.Add($"Removed: {Names(Removed)}.");
        if (Updated.Count > 0) parts.Add($"Updated: {Names(Updated)}.");
        if (parts.Count == 0) parts.Add("The channel order was changed.");
        return string.Join(" ", parts);
    }

    private static string Names(IReadOnlyList<Channel> channels) => string.Join(", ", channels.Select(c => c.Name));
}
