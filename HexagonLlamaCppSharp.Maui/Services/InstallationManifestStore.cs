using System.Text.Json;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class InstallationManifestStore : IInstallationManifestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RuntimeLayout _layout;

    public InstallationManifestStore(RuntimeLayout layout)
    {
        _layout = layout;
    }

    public async Task<InstallationManifest?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_layout.ManifestPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            _layout.ManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<InstallationManifest>(stream, SerializerOptions, cancellationToken);
    }
}