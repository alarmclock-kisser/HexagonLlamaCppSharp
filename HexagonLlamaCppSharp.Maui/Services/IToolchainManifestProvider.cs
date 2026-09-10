using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IToolchainManifestProvider
{
    Task<ToolchainManifest?> LoadAsync(CancellationToken cancellationToken);
}
