using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IExecutionProbe
{
    Task<ExecutionProbeResult> RunAsync(
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);
}
