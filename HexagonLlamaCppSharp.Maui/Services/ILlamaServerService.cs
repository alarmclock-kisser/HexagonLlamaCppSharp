using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface ILlamaServerService
{
    bool IsRunning { get; }

    int? Port { get; }

    Uri? BaseAddress { get; }

    event Action<ProcessLogLine>? OutputReceived;

    event Action<LlamaServerHealth>? HealthChanged;

    Task<LlamaServerStartResult> StartServerAsync(
        LlamaServerOptions options,
        CancellationToken cancellationToken);

    Task StopServerAsync(CancellationToken cancellationToken);

    Task<LlamaServerHealth> CheckHealthAsync(CancellationToken cancellationToken);
}