using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveVault.Core;

internal static class JsonStorage
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException($"Invalid JSON document: {path}");
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class ProfileStore(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<BackupProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return File.Exists(path)
                ? await JsonStorage.ReadAsync<List<BackupProfile>>(path, cancellationToken)
                : [];
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(IEnumerable<BackupProfile> profiles, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await JsonStorage.WriteAsync(path, profiles.ToList(), cancellationToken); }
        finally { gate.Release(); }
    }
}
