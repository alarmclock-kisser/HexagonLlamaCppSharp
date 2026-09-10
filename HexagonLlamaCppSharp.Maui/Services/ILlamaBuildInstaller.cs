using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface ILlamaBuildInstaller
{
    Task<LlamaBuildResult> BuildAsync(
        ToolchainBootstrapResult toolchain,
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);
}
