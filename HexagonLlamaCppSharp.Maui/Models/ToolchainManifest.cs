using System.Text.Json.Serialization;

namespace HexagonLlamaCppSharp.Maui.Models;

public sealed record ToolchainArchive(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archiveUrl")] string ArchiveUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("archiveFileName")] string ArchiveFileName);

public sealed record ToolchainManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("archives")] IReadOnlyList<ToolchainArchive> Archives,
    [property: JsonPropertyName("cmakeToolchainFile")] string CmakeToolchainFile);

public sealed record ToolchainBootstrapResult(
    bool Succeeded,
    string Message,
    string? ToolchainDirectory = null,
    IReadOnlyDictionary<string, string>? Executables = null,
    ToolchainManifest? Manifest = null);

public sealed class ToolchainManifestException : Exception
{
    public ToolchainManifestException(string message)
        : base(message)
    {
    }
}
