using System.Security.Cryptography;
using Android.Content;
using Android.OS;
using Android.Provider;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class ToolchainArchiveTransferService : IToolchainArchiveTransferService
{
    private const string DownloadDirectoryName = "HexagonLlamaCppSharp";
    private const string DownloadRelativePath = "Download/HexagonLlamaCppSharp/";
    private const string ArchiveMimeType = "application/x-xz";

    private readonly RuntimeLayout _layout;

    public ToolchainArchiveTransferService(RuntimeLayout layout)
    {
        _layout = layout;
    }

    public async Task<bool> ImportFromDownloadsAsync(
        ToolchainManifest manifest,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(output);

        _layout.EnsureDirectories();
        var importedAny = false;
        for (var index = 0; index < manifest.Archives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = manifest.Archives[index];
            var destinationPath = Path.Combine(_layout.RootDirectory, archive.ArchiveFileName);
            var sourcePath = FindDownloadArchive(archive.ArchiveFileName);
            if (sourcePath is not null && await IsArchiveValidAsync(sourcePath, archive.Sha256, cancellationToken))
            {
                if (await IsArchiveValidAsync(destinationPath, archive.Sha256, cancellationToken))
                {
                    output.Report(new ProcessLogLine($"Lokales Toolchain-Archiv bereits im privaten App-Speicher vorhanden: {archive.ArchiveFileName}", false));
                }
                else
                {
                    await CopyFileAsync(
                        sourcePath,
                        destinationPath,
                        archive,
                        index,
                        manifest.Archives.Count,
                        "Übernehme aus Downloads in den privaten App-Speicher",
                        progress,
                        cancellationToken);
                    output.Report(new ProcessLogLine($"Lokales Toolchain-Archiv übernommen: {archive.ArchiveFileName}", false));
                }

                importedAny = true;
                continue;
            }

            var selectedFile = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = $"{archive.Name} aus Downloads auswählen",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = [ArchiveMimeType, "application/octet-stream", "*/*"]
                })
            });
            if (selectedFile is null)
            {
                output.Report(new ProcessLogLine($"Kein lokales Archiv für {archive.Name} ausgewählt. Online-Download bleibt aktiviert.", false));
                continue;
            }

            if (!selectedFile.FileName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Die ausgewählte Datei '{selectedFile.FileName}' ist kein TAR-XZ-Archiv.");
            }

            await using var selectedStream = await selectedFile.OpenReadAsync();
            await CopyStreamAsync(
                selectedStream,
                destinationPath,
                archive,
                index,
                manifest.Archives.Count,
                "Übernehme ausgewähltes Archiv in den privaten App-Speicher",
                progress,
                cancellationToken);
            output.Report(new ProcessLogLine($"Lokales Toolchain-Archiv importiert: {archive.ArchiveFileName}", false));
            importedAny = true;
        }

        return importedAny;
    }

    public async Task<bool> ExportToDownloadsAsync(
        ToolchainManifest manifest,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(output);

        var exportedAny = false;
        for (var index = 0; index < manifest.Archives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = manifest.Archives[index];
            var sourcePath = Path.Combine(_layout.RootDirectory, archive.ArchiveFileName);
            if (!await IsArchiveValidAsync(sourcePath, archive.Sha256, cancellationToken))
            {
                throw new IOException($"Das verifizierte lokale Archiv fehlt: {archive.ArchiveFileName}.");
            }

            if (global::Android.OS.Environment.IsExternalStorageManager)
            {
                var directory = Path.Combine(
                    global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
                        ?? throw new IOException("Android hat keinen öffentlichen Speicherpfad bereitgestellt."),
                    "Download",
                    DownloadDirectoryName);
                Directory.CreateDirectory(directory);
                var destinationPath = Path.Combine(directory, archive.ArchiveFileName);
                if (await IsArchiveValidAsync(destinationPath, archive.Sha256, cancellationToken))
                {
                    var partialPath = destinationPath + ".partial";
                    if (File.Exists(partialPath))
                    {
                        File.Delete(partialPath);
                    }

                    output.Report(new ProcessLogLine($"Toolchain-Archiv bereits in Downloads vorhanden: {archive.ArchiveFileName}; keine erneute Kopie nötig.", false));
                    continue;
                }

                await CopyFileAsync(
                    sourcePath,
                    destinationPath,
                    archive,
                    index,
                    manifest.Archives.Count,
                    "Sichere nach Downloads",
                    progress,
                    cancellationToken);
            }
            else
            {
                await ExportWithMediaStoreAsync(
                    sourcePath,
                    archive,
                    index,
                    manifest.Archives.Count,
                    progress,
                    cancellationToken);
            }

            output.Report(new ProcessLogLine($"Toolchain-Archiv in Downloads gesichert: {archive.ArchiveFileName}", false));
            exportedAny = true;
        }

        return exportedAny;
    }

    public async Task<bool> HasCompleteAppArchivesAsync(
        ToolchainManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var archive in manifest.Archives)
        {
            var path = Path.Combine(_layout.RootDirectory, archive.ArchiveFileName);
            if (!await IsArchiveValidAsync(path, archive.Sha256, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<bool> HasAnyDownloadArchivesAsync(
        ToolchainManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var archive in manifest.Archives)
        {
            var sourcePath = FindDownloadArchive(archive.ArchiveFileName);
            if (sourcePath is not null && await IsArchiveValidAsync(sourcePath, archive.Sha256, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> HasCompleteDownloadArchivesAsync(
        ToolchainManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var archive in manifest.Archives)
        {
            var sourcePath = FindDownloadArchive(archive.ArchiveFileName);
            if (sourcePath is null || !await IsArchiveValidAsync(sourcePath, archive.Sha256, cancellationToken))
            {
                return false;
            }

            var partialPath = sourcePath + ".partial";
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }

        return true;
    }

    private static string? FindDownloadArchive(string fileName)
    {
        if (!global::Android.OS.Environment.IsExternalStorageManager)
        {
            return null;
        }

        var externalRoot = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(externalRoot))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(externalRoot, "Download", DownloadDirectoryName, fileName),
            Path.Combine(externalRoot, "Download", fileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task ExportWithMediaStoreAsync(
        string sourcePath,
        ToolchainArchive archive,
        int archiveIndex,
        int archiveCount,
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver
            ?? throw new IOException("Android konnte den Downloads-Speicheranbieter nicht öffnen.");
        var values = new ContentValues();
        values.Put("_display_name", archive.ArchiveFileName);
        values.Put("mime_type", ArchiveMimeType);
        values.Put("relative_path", DownloadRelativePath);
        values.Put("is_pending", 1);

        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
            ?? throw new IOException($"Android konnte '{archive.ArchiveFileName}' nicht in Downloads anlegen.");

        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = resolver.OpenOutputStream(uri)
                ?? throw new IOException($"Android konnte '{archive.ArchiveFileName}' nicht beschreiben."))
            {
                await CopyStreamWithProgressAsync(
                    input,
                    output,
                    archive,
                    archiveIndex,
                    archiveCount,
                    "Sichere nach Downloads",
                    progress,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var completedValues = new ContentValues();
            completedValues.Put("is_pending", 0);
            resolver.Update(uri, completedValues, null, null);
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    private async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        ToolchainArchive archive,
        int archiveIndex,
        int archiveCount,
        string progressDescription,
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyStreamAsync(input, destinationPath, archive, archiveIndex, archiveCount, progressDescription, progress, cancellationToken);
    }

    private async Task CopyStreamAsync(
        Stream input,
        string destinationPath,
        ToolchainArchive archive,
        int archiveIndex,
        int archiveCount,
        string progressDescription,
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + ".partial";
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await CopyStreamWithProgressAsync(input, output, archive, archiveIndex, archiveCount, progressDescription, progress, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        if (!await IsArchiveValidAsync(temporaryPath, archive.Sha256, cancellationToken))
        {
            File.Delete(temporaryPath);
            throw new CryptographicException($"SHA-256 des lokalen Archivs stimmt nicht: {archive.ArchiveFileName}.");
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static async Task CopyStreamWithProgressAsync(
        Stream input,
        Stream output,
        ToolchainArchive archive,
        int archiveIndex,
        int archiveCount,
        string progressDescription,
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        var totalLength = input.CanSeek ? input.Length : (long?)null;
        var copied = 0L;
        var buffer = new byte[128 * 1024];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            copied += bytesRead;
            var percent = totalLength is > 0
                ? (int)Math.Min(12, copied * 12 / totalLength.Value)
                : 0;
            progress.Report(new InstallationProgress(
                1 + archiveIndex * Math.Max(1, 12 / archiveCount) + percent,
                "Toolchain-Archive",
                $"{progressDescription} {archive.Name}: {FormatBytes(copied)}"));
        }
    }

    private static async Task<bool> IsArchiveValidAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024d:0.0} KiB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.0} MiB",
            _ => $"{bytes / (1024d * 1024 * 1024):0.0} GiB"
        };
    }
}