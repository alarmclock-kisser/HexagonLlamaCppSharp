namespace HexagonLlamaCppSharp.Maui.Models;

public sealed record LlamaServerOptions(
    string ModelPath,
    string? MmprojPath = null,
    string? MtpPath = null,
    int? Port = null,
    int ContextSize = 65532,
    int GpuLayers = 0,
    int Threads = 4,
    float Temperature = 0.8f,
    float TopP = 0.95f,
    int TopK = 40,
    float RepeatPenalty = 1.1f,
    int Parallel = 1,
    string? CacheTypeK = "q4_0",
    string? CacheTypeV = "q4_0",
    string? SpecType = null,
    int SpecDraftNMax = 0);

public sealed record LlamaServerStartResult(
    bool Succeeded,
    string Message,
    int? Port = null,
    Uri? BaseAddress = null);

public sealed record LlamaServerHealth(
    bool IsHealthy,
    int? StatusCode,
    string Message);