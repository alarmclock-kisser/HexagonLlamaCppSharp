using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IInstallationManifestStore
{
    Task<InstallationManifest?> LoadAsync(CancellationToken cancellationToken);
}