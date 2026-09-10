using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class LlamaBuildInstaller : ILlamaBuildInstaller
{
    private const string AndroidShellPath = "/system/bin/sh";
    private const string AndroidWritableAppExecutionMessage =
        "Android 10+ blockiert execve für ausführbare Dateien im beschreibbaren App-Speicher. " +
        "Die heruntergeladenen CMake/Ninja/Clang-Dateien können dort trotz chmod 755 nicht gestartet werden; " +
        "für einen echten In-App-Build müssen die Buildwerkzeuge als APK-native Dateien eingebettet sein. " +
        "Der aktuelle Archivpfad benötigt daher eine kompatible externe Build-Umgebung wie Termux.";
    private const string RepositoryApiUrl = "https://api.github.com/repos/ggerganov/llama.cpp/commits/master";
    private const string RepositoryArchiveUrlFormat = "https://github.com/ggerganov/llama.cpp/archive/{0}.tar.gz";

    private readonly RuntimeLayout _layout;
    private readonly IInstallationManifestStore _manifestStore;
    private readonly IProcessRunner _processRunner;
    private readonly IFilePermissionService _filePermissionService;
    private readonly HttpClient _httpClient;

    public LlamaBuildInstaller(
        RuntimeLayout layout,
        IInstallationManifestStore manifestStore,
        IProcessRunner processRunner,
        IFilePermissionService filePermissionService,
        HttpClient httpClient)
    {
        _layout = layout;
        _manifestStore = manifestStore;
        _processRunner = processRunner;
        _filePermissionService = filePermissionService;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HexagonLlamaCppSharp/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<LlamaBuildResult> BuildAsync(
        ToolchainBootstrapResult toolchain,
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        ArgumentNullException.ThrowIfNull(nativeAssets);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(output);

        if (!toolchain.Succeeded || toolchain.ToolchainDirectory is null || toolchain.Executables is null || toolchain.Manifest is null)
        {
            return new LlamaBuildResult(false, "Der llama.cpp-Build benötigt eine erfolgreich initialisierte In-App-Toolchain.");
        }

        try
        {
            _layout.EnsureDirectories();
            var reusedBuild = await TryReuseInstalledBuildAsync(toolchain, nativeAssets, progress, output, cancellationToken);
            if (reusedBuild is not null)
            {
                return reusedBuild;
            }

            var commitSha = await DownloadLatestSourceAsync(progress, output, cancellationToken);
            var buildDirectory = _layout.BuildDirectory;
            if (Directory.Exists(buildDirectory))
            {
                Directory.Delete(buildDirectory, recursive: true);
            }

            Directory.CreateDirectory(buildDirectory);

            if (!toolchain.Executables.TryGetValue("cmake", out var cmakePath) ||
                !toolchain.Executables.TryGetValue("ninja", out var ninjaPath) ||
                !toolchain.Executables.TryGetValue("clang", out var clangPath) ||
                !toolchain.Executables.TryGetValue("clang++", out var clangPlusPlusPath))
            {
                return new LlamaBuildResult(false, "Die In-App-Toolchain enthält nicht alle benötigten Buildprogramme.");
            }
            var toolchainFile = GetToolchainFilePath(toolchain);
            var cmakeLauncherPath = PrepareCMakeLauncher(cmakePath, toolchain.ToolchainDirectory, output);
            PrepareNativeToolchainAliases(toolchainFile, toolchain.Executables, output);
            var environment = CreateBuildEnvironment(toolchain.ToolchainDirectory, toolchainFile);
            PrepareBuildExecutables(toolchain.Executables, output);

            if (!toolchain.Executables.ContainsKey("ld.lld") ||
                !toolchain.Executables.ContainsKey("llvm-ar") ||
                !toolchain.Executables.ContainsKey("llvm-ranlib"))
            {
                return new LlamaBuildResult(false, "Die APK-Native-Toolchain enthält keinen vollständigen LLVM-Linker und keine Archivwerkzeuge.");
            }

            var linkerPath = GetNdkToolAliasPath(toolchainFile, "ld.lld");
            var arPath = GetNdkToolAliasPath(toolchainFile, "llvm-ar");
            var ranlibPath = GetNdkToolAliasPath(toolchainFile, "llvm-ranlib");
            var clangResourceDirectory = GetClangResourceDirectory(toolchainFile);

            progress.Report(new InstallationProgress(82, "llama.cpp Build", "Konfiguriere CMake mit GGML_HEXAGON=OFF ..."));
            output.Report(new ProcessLogLine($"CMake toolchain file: {toolchainFile}", false));
            output.Report(new ProcessLogLine($"LLVM-Linker-Alias: {linkerPath}", false));
            var configureResult = await _processRunner.RunAsync(
                    CreateProcessSpec(
                    cmakeLauncherPath,
                    [
                        "-S", _layout.SourceDirectory,
                        "-B", buildDirectory,
                        "-G", "Ninja",
                         $"-DCMAKE_MAKE_PROGRAM={ninjaPath}",
                         $"-DCMAKE_TOOLCHAIN_FILE={toolchainFile}",
                        "-DANDROID_ABI=arm64-v8a",
                        "-DANDROID_PLATFORM=android-35",
                        "-DGGML_HEXAGON=OFF",
                        "-DLLAMA_BUILD_SERVER=ON",
                        "-DCMAKE_BUILD_TYPE=Release",
                         $"-DCMAKE_C_COMPILER={clangPath}",
                         $"-DCMAKE_CXX_COMPILER={clangPlusPlusPath}",
                        "-DCMAKE_C_COMPILER_TARGET=aarch64-linux-android35",
                        "-DCMAKE_CXX_COMPILER_TARGET=aarch64-linux-android35",
                        $"-DCMAKE_C_FLAGS=-resource-dir={clangResourceDirectory}",
                        $"-DCMAKE_CXX_FLAGS=-resource-dir={clangResourceDirectory}",
                         $"-DCMAKE_AR={arPath}",
                         $"-DCMAKE_RANLIB={ranlibPath}",
                        $"-DCMAKE_EXE_LINKER_FLAGS=-fuse-ld={linkerPath}",
                        $"-DCMAKE_SHARED_LINKER_FLAGS=-fuse-ld={linkerPath}",
                        $"-DCMAKE_MODULE_LINKER_FLAGS=-fuse-ld={linkerPath}"
                    ],
                    _layout.SourceDirectory,
                    environment),
                output,
                cancellationToken);
            if (configureResult.ExitCode != 0)
            {
                var message = configureResult.ExitCode == 126
                    ? AndroidWritableAppExecutionMessage
                    : $"CMake-Konfiguration fehlgeschlagen (Exit-Code {configureResult.ExitCode}).";
                return new LlamaBuildResult(false, message, CommitSha: commitSha);
            }

            progress.Report(new InstallationProgress(86, "llama.cpp Build", "Kompiliere llama-server ..."));
            var parallelism = Math.Clamp(Environment.ProcessorCount, 1, 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var buildResult = await _processRunner.RunAsync(
                    CreateProcessSpec(
                    cmakeLauncherPath,
                    ["--build", buildDirectory, "--target", "llama-server", "--parallel", parallelism],
                    _layout.SourceDirectory,
                    environment),
                output,
                cancellationToken);
            if (buildResult.ExitCode != 0)
            {
                var message = buildResult.ExitCode == 126
                    ? AndroidWritableAppExecutionMessage
                    : $"llama-server-Build fehlgeschlagen (Exit-Code {buildResult.ExitCode}).";
                return new LlamaBuildResult(false, message, CommitSha: commitSha);
            }

            var serverPath = Path.Combine(buildDirectory, "bin", "llama-server");
            if (!File.Exists(serverPath))
            {
                return new LlamaBuildResult(false, "Der Build meldete Erfolg, aber build/bin/llama-server wurde nicht gefunden.", CommitSha: commitSha);
            }

            await InstallNativeLibrariesAsync(nativeAssets, buildDirectory, output, cancellationToken);
            _filePermissionService.MakeExecutable(serverPath);
            await WriteInstallationManifestAsync(toolchain, commitSha, serverPath, nativeAssets, cancellationToken);
            progress.Report(new InstallationProgress(100, "llama.cpp Build", "llama-server und Hexagon-Bibliotheken sind installiert."));
            return new LlamaBuildResult(true, "llama-server erfolgreich gebaut und installiert.", serverPath, commitSha);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new LlamaBuildResult(false, "Der aktuelle llama.cpp-Source konnte nicht geladen werden.");
        }
        catch (JsonException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new LlamaBuildResult(false, "Die GitHub-Commit-Antwort für llama.cpp war ungültig.");
        }
        catch (ProcessExecutionException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new LlamaBuildResult(false, "Ein In-App-Buildwerkzeug konnte nicht gestartet werden.");
        }
        catch (IOException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new LlamaBuildResult(false, "Der llama.cpp-Build konnte im App-Speicher nicht abgeschlossen werden.");
        }
        catch (UnauthorizedAccessException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new LlamaBuildResult(false, "Der Zugriff auf den llama.cpp-Buildordner wurde verweigert.");
        }
    }

    private async Task<string> DownloadLatestSourceAsync(
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        progress.Report(new InstallationProgress(70, "llama.cpp Source", "Ermittle den neuesten llama.cpp-Commit ..."));
        using var commitResponse = await _httpClient.GetAsync(RepositoryApiUrl, cancellationToken);
        commitResponse.EnsureSuccessStatusCode();
        await using var commitStream = await commitResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var commitDocument = await JsonDocument.ParseAsync(commitStream, cancellationToken: cancellationToken);
        var commitSha = commitDocument.RootElement.GetProperty("sha").GetString();
        if (string.IsNullOrWhiteSpace(commitSha) || commitSha.Length < 7 || commitSha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new JsonException("Die GitHub-Antwort enthält keinen gültigen Commit-SHA.");
        }

        var archiveUrl = string.Format(RepositoryArchiveUrlFormat, commitSha);
        var archivePath = Path.Combine(_layout.RootDirectory, $"llama.cpp-{commitSha}.tar.gz");
        output.Report(new ProcessLogLine($"llama.cpp Commit: {commitSha}", false));
        await DownloadFileAsync(archiveUrl, archivePath, 70, 10, progress, cancellationToken);
        await ExtractSourceAsync(archivePath, commitSha, progress, output, cancellationToken);
        return commitSha;
    }

    private async Task<LlamaBuildResult?> TryReuseInstalledBuildAsync(
        ToolchainBootstrapResult toolchain,
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        InstallationManifest? manifest;
        try
        {
            manifest = await _manifestStore.LoadAsync(cancellationToken);
        }
        catch (JsonException exception)
        {
            output.Report(new ProcessLogLine($"Vorhandener Installationsmanifest ist ungültig und wird neu erstellt: {exception.Message}", true));
            return null;
        }

        if (manifest is null ||
            !string.Equals(manifest.ToolchainVersion, toolchain.Manifest?.Version, StringComparison.Ordinal) ||
            !IsServerPathInBuildDirectory(manifest.ServerPath) ||
            !File.Exists(manifest.ServerPath) ||
            !await HasCurrentNativeLibrariesAsync(nativeAssets, cancellationToken))
        {
            return null;
        }

        _filePermissionService.MakeExecutable(manifest.ServerPath);
        progress.Report(new InstallationProgress(100, "llama.cpp Build", "Die vorhandene llama-server-Installation ist gültig und wird wiederverwendet."));
        output.Report(new ProcessLogLine($"llama.cpp Commit: {manifest.LlamaCommitSha} (wiederverwendet)", false));
        return new LlamaBuildResult(true, "llama-server-Installation erfolgreich wiederverwendet.", manifest.ServerPath, manifest.LlamaCommitSha);
    }

    private async Task<bool> HasCurrentNativeLibrariesAsync(
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        CancellationToken cancellationToken)
    {
        var binDirectory = Path.Combine(_layout.BuildDirectory, "bin");
        foreach (var asset in nativeAssets)
        {
            var path = Path.Combine(binDirectory, asset.FileName);
            if (!File.Exists(path))
            {
                return false;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsServerPathInBuildDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        var buildRoot = Path.GetFullPath(_layout.BuildDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(buildRoot, StringComparison.Ordinal);
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        int progressStart,
        int progressRange,
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var temporaryPath = destinationPath + ".partial";
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var totalLength = response.Content.Headers.ContentLength;
        var downloaded = 0L;
        var buffer = new byte[128 * 1024];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloaded += bytesRead;
            var percent = totalLength is > 0
                ? progressStart + (int)Math.Min(progressRange, downloaded * progressRange / totalLength.Value)
                : progressStart;
            progress.Report(new InstallationProgress(percent, "llama.cpp Source", $"Source-Download: {FormatBytes(downloaded)}"));
        }

        await output.FlushAsync(cancellationToken);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private async Task ExtractSourceAsync(
        string archivePath,
        string commitSha,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = _layout.SourceDirectory + ".staging";
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzipStream, stagingDirectory, overwriteFiles: true);
        var sourceRoot = Directory
            .EnumerateDirectories(stagingDirectory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "CMakeLists.txt")));
        if (sourceRoot is null)
        {
            throw new IOException("Das llama.cpp-Archiv enthält kein erwartetes CMake-Projekt.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_layout.SourceDirectory))
        {
            Directory.Delete(_layout.SourceDirectory, recursive: true);
        }

        Directory.Move(sourceRoot, _layout.SourceDirectory);
        Directory.Delete(stagingDirectory, recursive: true);
        output.Report(new ProcessLogLine($"llama.cpp Source extrahiert: {commitSha}", false));
        progress.Report(new InstallationProgress(80, "llama.cpp Source", "llama.cpp Source ist bereit."));
    }

    private async Task InstallNativeLibrariesAsync(
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        string buildDirectory,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        var binDirectory = Path.Combine(buildDirectory, "bin");
        Directory.CreateDirectory(binDirectory);
        foreach (var asset in nativeAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(binDirectory, asset.FileName);
            File.Copy(asset.Path, destination, overwrite: true);
            _filePermissionService.MakeExecutable(destination);
            await using var destinationStream = new FileStream(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(destinationStream, cancellationToken));
            if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Integritätsprüfung nach dem Kopieren fehlgeschlagen: {asset.FileName}.");
            }

            output.Report(new ProcessLogLine($"Hexagon-Bibliothek installiert: {asset.FileName}", false));
        }
    }

    private async Task WriteInstallationManifestAsync(
        ToolchainBootstrapResult toolchain,
        string commitSha,
        string serverPath,
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        CancellationToken cancellationToken)
    {
        var manifest = new InstallationManifest(
            toolchain.Manifest?.Version ?? "unknown",
            commitSha,
            serverPath,
            DateTimeOffset.UtcNow,
            nativeAssets);
        var temporaryPath = _layout.ManifestPath + ".partial";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _layout.ManifestPath, overwrite: true);
    }

    private string GetToolchainFilePath(ToolchainBootstrapResult toolchain)
    {
        var relativePath = toolchain.Manifest?.CmakeToolchainFile;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new IOException("Das Toolchain-Manifest enthält keinen gültigen CMake-Toolchain-Dateipfad.");
        }

        var root = Path.GetFullPath(toolchain.ToolchainDirectory!);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("Der CMake-Toolchain-Dateipfad verlässt das Toolchain-Verzeichnis.");
        }

        if (!File.Exists(path))
        {
            throw new IOException($"CMake-Toolchain-Datei nicht gefunden: {relativePath}.");
        }

        return path;
    }

    private static IReadOnlyDictionary<string, string> CreateBuildEnvironment(string toolchainDirectory, string toolchainFile)
    {
        var ndkDirectory = GetNdkDirectory(toolchainFile);
        var nativeLibraryDirectory = AndroidNativeToolchain.GetNativeLibraryDirectory();
        var binDirectories = new List<string>
        {
            Path.Combine(toolchainDirectory, "bin"),
            Path.Combine(toolchainDirectory, "android-sdk", "cmake", "bin"),
            Path.Combine(ndkDirectory, "toolchains", "llvm", "prebuilt", "linux-x86_64", "bin")
        };
        if (!string.IsNullOrWhiteSpace(nativeLibraryDirectory))
        {
            binDirectories.Insert(0, nativeLibraryDirectory);
        }

        var libDirectories = new[]
        {
            nativeLibraryDirectory,
            Path.Combine(toolchainDirectory, "lib"),
            Path.Combine(toolchainDirectory, "lib64"),
            Environment.GetEnvironmentVariable("LD_LIBRARY_PATH")
        }.Where(path => !string.IsNullOrWhiteSpace(path));
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "/system/bin";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = string.Join(Path.PathSeparator, binDirectories.Concat([existingPath])),
            ["LD_LIBRARY_PATH"] = string.Join(Path.PathSeparator, libDirectories),
            ["ANDROID_NDK_HOME"] = ndkDirectory,
            ["ANDROID_NDK_ROOT"] = ndkDirectory,
            ["CMAKE_ROOT"] = GetCMakeRoot(toolchainDirectory)
        };

        return environment;
    }

    private static string GetCMakeRoot(string toolchainDirectory)
    {
        var shareDirectory = Path.Combine(toolchainDirectory, "android-sdk", "cmake", "share");
        if (!Directory.Exists(shareDirectory))
        {
            throw new IOException($"Das CMake-share-Verzeichnis fehlt unter '{shareDirectory}'.");
        }

        var cmakeRoot = Directory
            .EnumerateDirectories(shareDirectory, "cmake-*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "Modules", "CMake.cmake")));
        if (cmakeRoot is null)
        {
            throw new IOException($"Kein CMake-Modules-Verzeichnis unter '{shareDirectory}' gefunden.");
        }

        return cmakeRoot;
    }

    private static string PrepareCMakeLauncher(
        string nativeCmakePath,
        string toolchainDirectory,
        IProgress<ProcessLogLine> output)
    {
        var cmakeDirectory = Path.Combine(toolchainDirectory, "android-sdk", "cmake");
        var launcherDirectory = Path.Combine(cmakeDirectory, "bin");
        var launcherPath = Path.Combine(launcherDirectory, "cmake");
        Directory.CreateDirectory(launcherDirectory);

        if (File.Exists(launcherPath) || Directory.Exists(launcherPath))
        {
            File.Delete(launcherPath);
        }

        File.CreateSymbolicLink(launcherPath, nativeCmakePath);
        output.Report(new ProcessLogLine($"CMake-Ressourcenpfad vorbereitet: {launcherPath}", false));
        return launcherPath;
    }

    private static void PrepareNativeToolchainAliases(
        string toolchainFile,
        IReadOnlyDictionary<string, string> executables,
        IProgress<ProcessLogLine> output)
    {
        var ndkBinDirectory = Path.Combine(
            GetNdkDirectory(toolchainFile),
            "toolchains",
            "llvm",
            "prebuilt",
            "linux-x86_64",
            "bin");
        var aliases = new[]
        {
            (Name: "clang", Key: "clang"),
            (Name: "clang++", Key: "clang++"),
            (Name: "llvm-ar", Key: "llvm-ar"),
            (Name: "llvm-ranlib", Key: "llvm-ranlib"),
            (Name: "ld.lld", Key: "ld.lld"),
            (Name: "llvm-objcopy", Key: "llvm-objcopy")
        };

        foreach (var alias in aliases)
        {
            if (!executables.TryGetValue(alias.Key, out var nativePath))
            {
                continue;
            }

            var aliasPath = Path.Combine(ndkBinDirectory, alias.Name);
            Directory.CreateDirectory(ndkBinDirectory);
            if (File.Exists(aliasPath) || Directory.Exists(aliasPath))
            {
                File.Delete(aliasPath);
            }

            File.CreateSymbolicLink(aliasPath, nativePath);
            output.Report(new ProcessLogLine($"NDK-Buildwerkzeug verknüpft: {aliasPath} -> {nativePath}", false));
        }
    }

    private static string GetNdkToolAliasPath(string toolchainFile, string toolName)
    {
        var aliasPath = Path.Combine(
            GetNdkDirectory(toolchainFile),
            "toolchains",
            "llvm",
            "prebuilt",
            "linux-x86_64",
            "bin",
            toolName);
        if (!File.Exists(aliasPath))
        {
            throw new IOException($"Das NDK-Buildwerkzeug '{toolName}' konnte nicht vorbereitet werden.");
        }

        return aliasPath;
    }

    private void PrepareBuildExecutables(
        IReadOnlyDictionary<string, string> executables,
        IProgress<ProcessLogLine> output)
    {
        foreach (var executable in executables.Values.Distinct(StringComparer.Ordinal))
        {
            if (AndroidNativeToolchain.IsNativePath(executable))
            {
                output.Report(new ProcessLogLine($"APK-Native-Buildwerkzeug bereit: {executable}", false));
                continue;
            }

            _filePermissionService.MakeExecutable(executable);
            output.Report(new ProcessLogLine($"Buildwerkzeug vorbereitet: {executable} ({DescribeFileMode(executable)})", false));
        }

        var cmakePath = executables.TryGetValue("cmake", out var configuredCmakePath)
            ? configuredCmakePath
            : null;
        var cmakeBinDirectory = cmakePath is null || AndroidNativeToolchain.IsNativePath(cmakePath)
            ? null
            : Path.GetDirectoryName(cmakePath);
        if (cmakeBinDirectory is not null && Directory.Exists(cmakeBinDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(cmakeBinDirectory))
            {
                _filePermissionService.MakeExecutable(file);
            }
        }
    }

    private static string GetNdkDirectory(string toolchainFile)
    {
        return Directory.GetParent(
            Directory.GetParent(
                Directory.GetParent(toolchainFile)?.FullName
                    ?? throw new IOException("Der NDK-Root konnte nicht aus der CMake-Toolchain-Datei ermittelt werden.")
            )?.FullName
                ?? throw new IOException("Der NDK-Root konnte nicht aus der CMake-Toolchain-Datei ermittelt werden.")
        )?.FullName
            ?? throw new IOException("Der NDK-Root konnte nicht aus der CMake-Toolchain-Datei ermittelt werden.");
    }

    private static string GetClangResourceDirectory(string toolchainFile)
    {
        var clangRoot = Path.Combine(
            GetNdkDirectory(toolchainFile),
            "toolchains",
            "llvm",
            "prebuilt",
            "linux-x86_64",
            "lib",
            "clang");
        var resourceDirectory = Directory.Exists(clangRoot)
            ? Directory.EnumerateDirectories(clangRoot).OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (resourceDirectory is null)
        {
            throw new IOException($"Das Clang-Resource-Verzeichnis fehlt unter '{clangRoot}'.");
        }

        return resourceDirectory;
    }

    private static string DescribeFileMode(string path)
    {
        var mode = File.GetUnixFileMode(path);
        var header = new byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4, FileOptions.SequentialScan);
        _ = stream.Read(header, 0, header.Length);
        return $"mode={mode}; header={Convert.ToHexString(header)}";
    }

    private static ProcessSpec CreateProcessSpec(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        return AndroidNativeToolchain.IsNativePath(executable)
            ? new ProcessSpec(executable, arguments, workingDirectory, environment)
            : CreateShellProcessSpec(executable, arguments, workingDirectory, environment);
    }

    private static ProcessSpec CreateShellProcessSpec(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var quotedExecutable = QuoteForAndroidShell(executable);
        var quotedArguments = string.Join(" ", arguments.Select(QuoteForAndroidShell));
        var command = $"chmod 755 {quotedExecutable} && exec {quotedExecutable} {quotedArguments}";
        return new ProcessSpec(
            AndroidShellPath,
            ["-c", command],
            workingDirectory,
            environment);
    }

    private static string QuoteForAndroidShell(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
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
