using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface ITermuxFallbackService
{
    bool IsInstalled();

    Task<TermuxFallbackResult> StartBuildAsync(
        string primaryFailureReason,
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);

    Task OpenInstallPageAsync(TermuxStoreSource source);
}
