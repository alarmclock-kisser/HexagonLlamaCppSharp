using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IInAppToolchainBootstrapper
{
    Task<ToolchainBootstrapResult> InitializeAsync(
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);
}
