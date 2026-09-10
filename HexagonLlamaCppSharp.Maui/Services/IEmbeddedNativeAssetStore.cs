using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IEmbeddedNativeAssetStore
{
    Task<IReadOnlyList<ExtractedNativeAsset>> ExtractAsync(
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken);
}
