using GemakTV.Application.Ports;
using GemakTV.Domain;
using GemakTV.Infrastructure.Serialization;

namespace GemakTV.Infrastructure.Storage;

/// <summary>The TV's own copy of the lineup, as a JSON file. Writes go to a temporary file first, so a power cut never leaves half a file.</summary>
public sealed class JsonFileLineupStore(string path) : ILocalLineupStore
{
    public async Task<Lineup?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return ConfigSerializer.DeserializeLineup(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged file must not stop the TV from starting; the caller falls back to the built-in list.
            return null;
        }
    }

    public async Task SaveAsync(Lineup lineup, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, ConfigSerializer.SerializeLineup(lineup), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
