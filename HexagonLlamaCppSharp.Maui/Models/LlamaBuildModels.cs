namespace HexagonLlamaCppSharp.Maui.Models;

public sealed record LlamaBuildResult(
    bool Succeeded,
    string Message,
    string? ServerPath = null,
    string? CommitSha = null);

public sealed record InstallationManifest(
    string ToolchainVersion,
    string LlamaCommitSha,
    string ServerPath,
    DateTimeOffset InstalledAtUtc,
    IReadOnlyList<ExtractedNativeAsset> NativeLibraries,
    string BuildConfiguration = "");
