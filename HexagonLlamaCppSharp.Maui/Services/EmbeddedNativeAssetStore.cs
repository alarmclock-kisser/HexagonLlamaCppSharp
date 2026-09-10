using System.Reflection;
using System.Security.Cryptography;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class EmbeddedNativeAssetStore : IEmbeddedNativeAssetStore
{
    private static readonly string[] NativeLibraryNames =
    [
        "libc++_shared.so",
        "libggml-base.so",
        "libggml-cpu.so",
        "libggml-hexagon.so",
        "libggml-htp-v73.so",
        "libggml-htp-v75.so",
        "libggml-htp-v79.so",
        "libggml-htp-v81.so",
        "libggml.so",
        "libllama.so"
    ];

    private readonly RuntimeLayout _layout;
    private readonly Assembly _assembly;

    public EmbeddedNativeAssetStore(RuntimeLayout layout)
    {
        _layout = layout;
        _assembly = typeof(EmbeddedNativeAssetStore).Assembly;
    }

    public async Task<IReadOnlyList<ExtractedNativeAsset>> ExtractAsync(
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        _layout.EnsureDirectories();
        var extractedAssets = new List<ExtractedNativeAsset>(NativeLibraryNames.Length);

        for (var index = 0; index < NativeLibraryNames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = NativeLibraryNames[index];
            var percent = 5 + (index * 5);
            progress.Report(new InstallationProgress(percent, "Native Libraries", $"Extrahiere {fileName} ..."));

            var resourceName = FindResourceName(fileName);
            await using var resourceStream = _assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Embedded native library '{fileName}' was not found.");

            var destinationPath = Path.Combine(_layout.NativeLibrariesDirectory, fileName);
            var temporaryPath = destinationPath + ".partial";
            try
            {
                long length;
                string sha256;
                await using (var destinationStream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    length = 0;
                    int bytesRead;
                    while ((bytesRead = await resourceStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        hash.AppendData(buffer, 0, bytesRead);
                        length += bytesRead;
                    }

                    await destinationStream.FlushAsync(cancellationToken);
                    sha256 = Convert.ToHexString(hash.GetHashAndReset());
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
                extractedAssets.Add(new ExtractedNativeAsset(fileName, destinationPath, length, sha256));
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
            }
        }

        progress.Report(new InstallationProgress(55, "Native Libraries", "Alle eingebetteten Hexagon-Bibliotheken wurden extrahiert."));
        return extractedAssets;
    }

    private string FindResourceName(string fileName)
    {
        var resourceName = _assembly
            .GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        return resourceName
            ?? throw new FileNotFoundException($"Embedded native library '{fileName}' was not found.");
    }
}
