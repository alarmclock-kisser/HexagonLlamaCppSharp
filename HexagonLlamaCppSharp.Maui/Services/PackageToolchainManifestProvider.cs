using System.Text.Json;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class PackageToolchainManifestProvider : IToolchainManifestProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<ToolchainManifest?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("toolchain-manifest.json");
            var manifest = await JsonSerializer.DeserializeAsync<ToolchainManifest>(stream, SerializerOptions, cancellationToken)
                ?? throw new ToolchainManifestException("Die Toolchain-Manifestdatei ist leer oder ungültig.");

            Validate(manifest);
            return manifest;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static void Validate(ToolchainManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new ToolchainManifestException("Im Toolchain-Manifest fehlt 'version'.");
        }

        if (manifest.Archives is null || manifest.Archives.Count == 0)
        {
            throw new ToolchainManifestException("Das Toolchain-Manifest benötigt mindestens ein Archiv.");
        }

        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archiveFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in manifest.Archives)
        {
            if (archive is null || string.IsNullOrWhiteSpace(archive.Name))
            {
                throw new ToolchainManifestException("Ein Toolchain-Archiv benötigt einen Namen.");
            }

            if (!archiveNames.Add(archive.Name))
            {
                throw new ToolchainManifestException($"Der Toolchain-Archivname '{archive.Name}' ist doppelt vorhanden.");
            }

            if (!Uri.TryCreate(archive.ArchiveUrl, UriKind.Absolute, out var archiveUri) ||
                !string.Equals(archiveUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolchainManifestException($"Die URL des Toolchain-Archivs '{archive.Name}' muss eine HTTPS-URL sein.");
            }

            if (string.IsNullOrWhiteSpace(archive.ArchiveFileName) ||
                archive.ArchiveFileName.Contains(Path.DirectorySeparatorChar) ||
                archive.ArchiveFileName.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new ToolchainManifestException($"Der Archivname des Toolchain-Archivs '{archive.Name}' ist ungültig.");
            }

            if (!archiveFileNames.Add(archive.ArchiveFileName))
            {
                throw new ToolchainManifestException($"Der Dateiname '{archive.ArchiveFileName}' ist in mehreren Toolchain-Archiven vorhanden.");
            }

            if (string.IsNullOrWhiteSpace(archive.Sha256) ||
                archive.Sha256.Length != 64 ||
                archive.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ToolchainManifestException($"Das Toolchain-Archiv '{archive.Name}' benötigt einen SHA-256-Hash mit 64 Hex-Zeichen.");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.CmakeToolchainFile) ||
            Path.IsPathRooted(manifest.CmakeToolchainFile) ||
            manifest.CmakeToolchainFile.Contains("..", StringComparison.Ordinal))
        {
            throw new ToolchainManifestException("Das Toolchain-Manifest benötigt einen relativen CMake-Toolchain-Dateipfad.");
        }

    }
}
