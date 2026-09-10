using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using HexagonLlamaCppSharp.Maui.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;
using SystemCryptographicException = System.Security.Cryptography.CryptographicException;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class InAppToolchainBootstrapper : IInAppToolchainBootstrapper
{
    private static readonly string[] RequiredExecutables = ["cmake", "ninja", "clang", "clang++"];

    private sealed record ArchiveLink(string EntryPath, string LinkTarget);

    private readonly RuntimeLayout _layout;
    private readonly IToolchainManifestProvider _manifestProvider;
    private readonly IFilePermissionService _filePermissionService;
    private readonly HttpClient _httpClient;

    public InAppToolchainBootstrapper(
        RuntimeLayout layout,
        IToolchainManifestProvider manifestProvider,
        IFilePermissionService filePermissionService,
        HttpClient httpClient)
    {
        _layout = layout;
        _manifestProvider = manifestProvider;
        _filePermissionService = filePermissionService;
        _httpClient = httpClient;
    }

    public async Task<ToolchainBootstrapResult> InitializeAsync(
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            progress.Report(new InstallationProgress(56, "In-App Toolchain", "Lade das verifizierte Toolchain-Manifest ..."));
            var manifest = await _manifestProvider.LoadAsync(cancellationToken);
            if (manifest is null)
            {
                return new ToolchainBootstrapResult(
                    false,
                    "Kein verifiziertes In-App-Toolchain-Manifest ist im App-Paket konfiguriert.");
            }

            _layout.EnsureDirectories();
            if (!AndroidNativeToolchain.TryResolve(out _, out var nativeToolchainMessage))
            {
                output.Report(new ProcessLogLine(nativeToolchainMessage, true));
                return new ToolchainBootstrapResult(false, nativeToolchainMessage);
            }

            output.Report(new ProcessLogLine(nativeToolchainMessage, false));
            if (TryReuseToolchain(manifest, out var existingToolchainDirectory, out var existingExecutables))
            {
                progress.Report(new InstallationProgress(70, "In-App Toolchain", "Die verifizierte In-App-Toolchain ist bereits installiert."));
                output.Report(new ProcessLogLine($"Toolchain-Version: {manifest.Version} (wiederverwendet)", false));
                return new ToolchainBootstrapResult(true, "In-App-Toolchain erfolgreich wiederverwendet.", existingToolchainDirectory, existingExecutables, manifest);
            }

            var archivePaths = new List<string>(manifest.Archives.Count);
            for (var index = 0; index < manifest.Archives.Count; index++)
            {
                var archive = manifest.Archives[index];
                var archivePath = Path.Combine(_layout.RootDirectory, archive.ArchiveFileName);
                archivePaths.Add(archivePath);
                if (!File.Exists(archivePath) || !await IsArchiveValidAsync(archivePath, archive.Sha256, cancellationToken))
                {
                    await DownloadArchiveAsync(archive, index, manifest.Archives.Count, archivePath, progress, output, cancellationToken);
                }
                else
                {
                    output.Report(new ProcessLogLine($"Verifiziertes Toolchain-Archiv wiederverwendet: {archive.ArchiveFileName}", false));
                }

                await VerifyArchiveAsync(archivePath, archive.Sha256, output, cancellationToken);
            }

            var toolchainDirectory = await ExtractArchivesAsync(archivePaths, progress, output, cancellationToken);
            var executables = ValidateExecutables(toolchainDirectory);
            foreach (var executable in executables)
            {
                output.Report(new ProcessLogLine($"Toolchain-Werkzeug {executable.Key}: {executable.Value}", false));
            }

            var toolchainFile = Path.GetFullPath(Path.Combine(toolchainDirectory, manifest.CmakeToolchainFile));
            if (!toolchainFile.StartsWith(Path.GetFullPath(toolchainDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(toolchainFile))
            {
                throw new IOException($"Die CMake-Toolchain-Datei '{manifest.CmakeToolchainFile}' fehlt im Archiv.");
            }

            await WriteToolchainStateAsync(manifest.Version, cancellationToken);

            progress.Report(new InstallationProgress(70, "In-App Toolchain", "CMake/Clang/Ninja sind verfügbar."));
            output.Report(new ProcessLogLine($"Toolchain-Version: {manifest.Version}", false));
            return new ToolchainBootstrapResult(true, "In-App-Toolchain erfolgreich initialisiert.", toolchainDirectory, executables, manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToolchainManifestException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, exception.Message);
        }
        catch (JsonException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, "Die In-App-Toolchain-Manifestdatei ist ungültig.");
        }
        catch (HttpRequestException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, "Die In-App-Toolchain konnte nicht heruntergeladen werden.");
        }
        catch (SystemCryptographicException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, "Die Integritätsprüfung der In-App-Toolchain ist fehlgeschlagen.");
        }
        catch (ArchiveOperationException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, "Das In-App-Toolchain-Archiv konnte nicht entpackt werden.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            output.Report(new ProcessLogLine($"Archiv-Extraktion fehlgeschlagen ({exception.GetType().Name}): {exception.Message}", true));
            return new ToolchainBootstrapResult(false, "Das In-App-Toolchain-Archiv konnte nicht gelesen oder entpackt werden.");
        }
        catch (IOException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, "Die In-App-Toolchain konnte im App-Speicher nicht eingerichtet werden.");
        }
        catch (UnauthorizedAccessException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ToolchainBootstrapResult(false, "Der Zugriff auf den App-Speicher für die In-App-Toolchain wurde verweigert.");
        }
    }

    private async Task DownloadArchiveAsync(
        ToolchainArchive archive,
        int archiveIndex,
        int archiveCount,
        string archivePath,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        output.Report(new ProcessLogLine($"Download ({archive.Name}): {archive.ArchiveUrl}", false));
        using var response = await _httpClient.GetAsync(
            archive.ArchiveUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var temporaryPath = archivePath + ".partial";
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var totalLength = response.Content.Headers.ContentLength;
        var downloaded = 0L;
        var buffer = new byte[128 * 1024];
        var progressStart = 56 + archiveIndex * 6;
        var progressRange = Math.Max(1, 12 / archiveCount);
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloaded += bytesRead;
            var percent = totalLength is > 0
                ? progressStart + (int)Math.Min(progressRange, downloaded * progressRange / totalLength.Value)
                : progressStart;
            progress.Report(new InstallationProgress(percent, "In-App Toolchain", $"{archive.Name}: {FormatBytes(downloaded)}"));
        }

        await outputStream.FlushAsync(cancellationToken);
        File.Move(temporaryPath, archivePath, overwrite: true);
    }

    private static async Task<bool> IsArchiveValidAsync(
        string archivePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(actualHash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        string expectedSha256,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualSha256 = Convert.ToHexString(actualHash);
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SystemCryptographicException($"SHA-256 mismatch: erwartet {expectedSha256}, erhalten {actualSha256}.");
        }

        output.Report(new ProcessLogLine($"SHA-256 bestätigt: {actualSha256}", false));
    }

    private async Task<string> ExtractArchivesAsync(
        IReadOnlyList<string> archivePaths,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stagingDirectory = _layout.ToolchainDirectory + ".staging";
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);
        await Task.Run(() =>
        {
            var extractedEntries = 0L;
            var lastProgressTimestamp = Stopwatch.GetTimestamp();

            foreach (var archivePath in archivePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archiveName = Path.GetFileName(archivePath);
                ExtractArchive(
                    archivePath,
                    stagingDirectory,
                    cancellationToken,
                    () =>
                    {
                        extractedEntries++;
                        if (extractedEntries == 1 || Stopwatch.GetElapsedTime(lastProgressTimestamp) >= TimeSpan.FromMilliseconds(250))
                        {
                            progress.Report(new InstallationProgress(
                                68,
                                "In-App Toolchain",
                                $"Entpacke {archiveName} ... ({extractedEntries:N0} Einträge)"));
                            lastProgressTimestamp = Stopwatch.GetTimestamp();
                        }
                    });
            }
        }, cancellationToken);
        progress.Report(new InstallationProgress(68, "In-App Toolchain", "Toolchain-Archiv extrahiert."));
        output.Report(new ProcessLogLine($"Extrahiert nach: {stagingDirectory}", false));

        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_layout.ToolchainDirectory))
        {
            Directory.Delete(_layout.ToolchainDirectory, recursive: true);
        }

        Directory.Move(stagingDirectory, _layout.ToolchainDirectory);
        return _layout.ToolchainDirectory;
    }

    private static void ExtractArchive(
        string archivePath,
        string stagingDirectory,
        CancellationToken cancellationToken,
        Action entryExtracted)
    {
        var symbolicLinks = new List<ArchiveLink>();
        if (archivePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            using var compressedStream = File.OpenRead(archivePath);
            using var decompressedStream = new XZStream(compressedStream);
            using var reader = ReaderFactory.OpenReader(
                decompressedStream,
                new ReaderOptions
                {
                    LeaveStreamOpen = true
                });

            ExtractReaderEntries(reader, stagingDirectory, symbolicLinks, cancellationToken, entryExtracted);
            MaterializeSymbolicLinks(symbolicLinks, stagingDirectory, cancellationToken);
            return;
        }

        using var fileArchive = ArchiveFactory.OpenArchive(
            archivePath,
            new ReaderOptions
            {
                LookForHeader = true
            });
        ExtractEntries(fileArchive, stagingDirectory, symbolicLinks, cancellationToken, entryExtracted);
        MaterializeSymbolicLinks(symbolicLinks, stagingDirectory, cancellationToken);
    }

    private static void ExtractReaderEntries(
        IReader reader,
        string stagingDirectory,
        ICollection<ArchiveLink> symbolicLinks,
        CancellationToken cancellationToken,
        Action entryExtracted)
    {
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalEntryPath = reader.Entry.Key ?? throw new IOException("Das Archiv enthält einen Eintrag ohne Pfad.");
            var entryPath = originalEntryPath.Replace('\\', '/');
            ValidateArchiveEntryPath(entryPath, originalEntryPath);

            if (reader.Entry.LinkTarget is { Length: > 0 } linkTarget)
            {
                symbolicLinks.Add(new ArchiveLink(entryPath, linkTarget));
            }
            else if (!reader.Entry.IsDirectory)
            {
                reader.WriteEntryToDirectory(
                    stagingDirectory,
                    new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
            }

            entryExtracted();
        }
    }

    private static void ExtractEntries(
        IArchive archive,
        string stagingDirectory,
        ICollection<ArchiveLink> symbolicLinks,
        CancellationToken cancellationToken,
        Action entryExtracted)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalEntryPath = entry.Key ?? throw new IOException("Das Archiv enthält einen Eintrag ohne Pfad.");
            var entryPath = originalEntryPath.Replace('\\', '/');
            ValidateArchiveEntryPath(entryPath, originalEntryPath);

            if (entry.LinkTarget is { Length: > 0 } linkTarget)
            {
                symbolicLinks.Add(new ArchiveLink(entryPath, linkTarget));
            }
            else if (!entry.IsDirectory)
            {
                entry.WriteToDirectory(
                    stagingDirectory,
                    new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
            }

            entryExtracted();
        }
    }

    private static void MaterializeSymbolicLinks(
        IReadOnlyList<ArchiveLink> symbolicLinks,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var rootDirectory = Path.GetFullPath(stagingDirectory);
        var rootPrefix = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;

        var pendingLinks = new List<ArchiveLink>(symbolicLinks);
        while (pendingLinks.Count > 0)
        {
            var materializedAnyLink = false;
            for (var index = pendingLinks.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var symbolicLink = pendingLinks[index];
                var linkPath = Path.GetFullPath(Path.Combine(
                    rootDirectory,
                    symbolicLink.EntryPath.Replace('/', Path.DirectorySeparatorChar)));
                var linkTarget = symbolicLink.LinkTarget.Replace('\\', '/');
                if (Path.IsPathRooted(linkTarget))
                {
                    throw new IOException($"Das Archiv enthält einen absoluten Symlink: '{symbolicLink.EntryPath}'.");
                }

                var linkParent = Path.GetDirectoryName(linkPath);
                if (linkParent is null)
                {
                    throw new IOException($"Der Symlink-Pfad '{symbolicLink.EntryPath}' ist ungültig.");
                }

                var targetPath = Path.GetFullPath(Path.Combine(
                    linkParent,
                    linkTarget.Replace('/', Path.DirectorySeparatorChar)));
                if (!targetPath.StartsWith(rootPrefix, StringComparison.Ordinal))
                {
                    throw new IOException($"Der Symlink '{symbolicLink.EntryPath}' verweist außerhalb des Archivs.");
                }

                // Links can point to another link (for example ld -> ld.lld -> lld).
                // Defer them until the target link has been materialized.
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                {
                    continue;
                }

                Directory.CreateDirectory(linkParent);
                if (File.Exists(linkPath) || Directory.Exists(linkPath))
                {
                    throw new IOException($"Der Symlink-Pfad '{symbolicLink.EntryPath}' ist bereits belegt.");
                }

                if (Directory.Exists(targetPath))
                {
                    Directory.CreateSymbolicLink(linkPath, linkTarget);
                }
                else
                {
                    // Android can retain the mode of a file symlink rather than the
                    // target. Copy executable file links so chmod and Process.Start
                    // operate on a regular file (clang, ld, and cmake use these).
                    File.Copy(targetPath, linkPath);
                }

                pendingLinks.RemoveAt(index);
                materializedAnyLink = true;
            }

            if (!materializedAnyLink)
            {
                var unresolved = string.Join(", ", pendingLinks.Select(link => $"'{link.EntryPath}' -> '{link.LinkTarget}'"));
                throw new IOException($"Die Toolchain enthält nicht auflösbare Symlinks: {unresolved}");
            }
        }
    }

    private static void ValidateArchiveEntryPath(string entryPath, string originalEntryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath) ||
            Path.IsPathRooted(entryPath) ||
            entryPath.Split('/').Any(segment => segment == ".."))
        {
            throw new IOException($"Das Archiv enthält einen ungültigen Pfad: '{originalEntryPath}'.");
        }
    }

    private bool TryReuseToolchain(
        ToolchainManifest manifest,
        out string toolchainDirectory,
        out IReadOnlyDictionary<string, string> executables)
    {
        toolchainDirectory = _layout.ToolchainDirectory;
        executables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_layout.ToolchainStatePath) || !Directory.Exists(toolchainDirectory))
        {
            return false;
        }

        var installedVersion = File.ReadAllText(_layout.ToolchainStatePath).Trim();
        if (!installedVersion.Equals(manifest.Version, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            executables = ValidateExecutables(toolchainDirectory);
            var root = Path.GetFullPath(toolchainDirectory) + Path.DirectorySeparatorChar;
            var toolchainFile = Path.GetFullPath(Path.Combine(toolchainDirectory, manifest.CmakeToolchainFile));
            return toolchainFile.StartsWith(root, StringComparison.Ordinal) && File.Exists(toolchainFile);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task WriteToolchainStateAsync(string version, CancellationToken cancellationToken)
    {
        var temporaryPath = _layout.ToolchainStatePath + ".partial";
        await File.WriteAllTextAsync(temporaryPath, version, cancellationToken);
        File.Move(temporaryPath, _layout.ToolchainStatePath, overwrite: true);
    }

    private IReadOnlyDictionary<string, string> ValidateExecutables(string toolchainDirectory)
    {
        if (AndroidNativeToolchain.TryResolve(out var nativeExecutables, out _))
        {
            return nativeExecutables;
        }

        if (OperatingSystem.IsAndroid())
        {
            throw new IOException("Die APK-Native-Toolchain für CMake, Ninja und Clang ist unvollständig.");
        }

        var executables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var executable in RequiredExecutables)
        {
            var path = Directory
                .EnumerateFiles(toolchainDirectory, executable, SearchOption.AllDirectories)
                .FirstOrDefault(File.Exists);
            if (path is null)
            {
                throw new IOException($"Die Toolchain enthält kein ausführbares '{executable}'.");
            }

            _filePermissionService.MakeExecutable(path);
            executables[executable] = path;
        }

        return executables;
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
