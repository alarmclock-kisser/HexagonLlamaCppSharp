using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class LlamaServerService : ILlamaServerService, IAsyncDisposable
{
    private const string AndroidDynamicLinkerPath = "/system/bin/linker64";
    private const string HexagonBackendPluginName = "libggml-hexagon-prebuilt.so";
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    private readonly RuntimeLayout _layout;
    private readonly IInstallationManifestStore _manifestStore;
    private readonly HttpClient _httpClient;
    private readonly IFilePermissionService _filePermissionService;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateLock = new();

    private Process? _process;
    private TaskCompletionSource<int>? _exitCompletion;
    private Task? _standardOutputTask;
    private Task? _standardErrorTask;
    private CancellationTokenSource? _healthCancellation;
    private Task? _healthTask;
    private bool _isRunning;
    private bool _stopRequested;
    private int? _port;
    private Uri? _baseAddress;
    private string? _serverLogPath;
    private LlamaServerHealth _lastHealth = new(false, null, "Server ist nicht gestartet.");
    private bool _disposed;

    public LlamaServerService(
        RuntimeLayout layout,
        IInstallationManifestStore manifestStore,
        HttpClient httpClient,
        IFilePermissionService filePermissionService)
    {
        _layout = layout;
        _manifestStore = manifestStore;
        _httpClient = httpClient;
        _filePermissionService = filePermissionService;
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _isRunning;
            }
        }
    }

    public int? Port
    {
        get
        {
            lock (_stateLock)
            {
                return _port;
            }
        }
    }

    public Uri? BaseAddress
    {
        get
        {
            lock (_stateLock)
            {
                return _baseAddress;
            }
        }
    }

    public event Action<ProcessLogLine>? OutputReceived;

    public event Action<LlamaServerHealth>? HealthChanged;

    public async Task<LlamaServerStartResult> StartServerAsync(
        LlamaServerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ValidateOptions(options);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (IsRunning)
            {
                return new LlamaServerStartResult(
                    true,
                    "llama-server läuft bereits.",
                    Port,
                    BaseAddress);
            }

            await CleanupExitedProcessAsync();

            var manifest = await _manifestStore.LoadAsync(cancellationToken)
                ?? throw new InvalidOperationException("Es ist keine erfolgreiche llama-server-Installation vorhanden.");
            var serverPath = ValidateServerPath(manifest.ServerPath);
            await ValidateNativeLibrariesAsync(manifest, serverPath, cancellationToken);
            await EnsureToolchainRuntimeLibraryAsync(serverPath, cancellationToken);
            var port = options.Port ?? FindAvailablePort();
            var baseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
            var linkerCheckExitCode = await RunLinkerDependencyCheckAsync(serverPath, cancellationToken);
            if (linkerCheckExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Der Android-Linker konnte die llama-server-Abhängigkeiten nicht laden (Exit-Code {linkerCheckExitCode}).");
            }

            var serverLogPath = Path.Combine(_layout.RootDirectory, "server.log");
            if (File.Exists(serverLogPath))
            {
                File.Delete(serverLogPath);
            }

            var process = CreateProcess(serverPath, options, port, serverLogPath);
            var exitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += OnProcessExited;

            lock (_stateLock)
            {
                _process = process;
                _exitCompletion = exitCompletion;
                _stopRequested = false;
                _port = port;
                _baseAddress = baseAddress;
                _serverLogPath = serverLogPath;
                _lastHealth = new LlamaServerHealth(false, null, "llama-server wird gestartet.");
            }

            var foregroundServiceStarted = false;
            var processStarted = false;
            try
            {
                    StartLlamaServerForegroundService();
                    foregroundServiceStarted = true;
                    PublishOutput(new ProcessLogLine(
                        $"Starte llama-server über {AndroidDynamicLinkerPath}: {serverPath}",
                        false));
                    if (!process.Start())
                    {
                        throw new ProcessExecutionException(serverPath, new InvalidOperationException("Process.Start returned false."));
                    }

                    processStarted = true;

                    _standardOutputTask = ForwardOutputAsync(process.StandardOutput, isError: false);
                    _standardErrorTask = ForwardOutputAsync(process.StandardError, isError: true);
                    process.EnableRaisingEvents = true;
                    PublishOutput(new ProcessLogLine($"llama-server-Prozess gestartet (PID {process.Id}).", false));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                throw new ProcessExecutionException(serverPath, exception);
            }
            finally
            {
                if (!processStarted)
                {
                    if (foregroundServiceStarted)
                    {
                        StopLlamaServerForegroundService();
                    }

                    CleanupProcess(process);
                }
            }
            lock (_stateLock)
            {
                _isRunning = !process.HasExited;
            }

            _healthCancellation = new CancellationTokenSource();
            _healthTask = MonitorHealthAsync(baseAddress, _healthCancellation.Token);
            PublishOutput(new ProcessLogLine($"llama-server gestartet: {baseAddress}", false));
        }
        finally
        {
            _lifecycleGate.Release();
        }

        LlamaServerHealth readyResult;
        try
        {
            readyResult = await WaitForReadyAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopServerAsync(CancellationToken.None);
            throw;
        }

        if (!readyResult.IsHealthy)
        {
            await StopServerAsync(CancellationToken.None);
            return new LlamaServerStartResult(false, readyResult.Message, Port, BaseAddress);
        }

        return new LlamaServerStartResult(true, "llama-server ist auf localhost bereit.", Port, BaseAddress);
    }

    public async Task StopServerAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            Process? process;
            Task? healthTask;
            Task? standardOutputTask;
            Task? standardErrorTask;
            CancellationTokenSource? healthCancellation;
            lock (_stateLock)
            {
                process = _process;
                healthTask = _healthTask;
                standardOutputTask = _standardOutputTask;
                standardErrorTask = _standardErrorTask;
                healthCancellation = _healthCancellation;
            }

            if (process is null)
            {
                StopLlamaServerForegroundService();
                return;
            }

            healthCancellation?.Cancel();
            if (!process.HasExited)
            {
                PublishOutput(new ProcessLogLine("Stoppe llama-server ...", false));
                lock (_stateLock)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _stopRequested = true;
                    }
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            process.WaitForExit();
            await DrainOutputAsync(standardOutputTask, standardErrorTask);
            if (healthTask is not null)
            {
                try
                {
                    await healthTask;
                }
                catch (OperationCanceledException) when (healthCancellation?.IsCancellationRequested == true)
                {
                }
            }

            CleanupProcess(process);
            healthCancellation?.Dispose();
            PublishHealth(new LlamaServerHealth(false, null, "Server wurde beendet."));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LlamaServerHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Uri? baseAddress;
        lock (_stateLock)
        {
            baseAddress = _baseAddress;
        }

        if (baseAddress is null || !IsRunning)
        {
            return new LlamaServerHealth(false, null, "Server ist nicht gestartet.");
        }

        return await ProbeHealthAsync(baseAddress, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopServerAsync(CancellationToken.None);
        _disposed = true;
        _lifecycleGate.Dispose();
    }

    private Process CreateProcess(string serverPath, LlamaServerOptions options, int port, string serverLogPath)
    {
        var binDirectory = Path.GetDirectoryName(serverPath)
            ?? throw new IOException("Das llama-server-Verzeichnis konnte nicht ermittelt werden.");
        _filePermissionService.MakeExecutable(serverPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = AndroidDynamicLinkerPath,
            WorkingDirectory = binDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Android app-private storage is commonly mounted noexec. linker64 is an
        // executable system binary and can load the generated dynamic ELF directly.
        startInfo.ArgumentList.Add(serverPath);
        foreach (var argument in BuildArguments(options, port, serverLogPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var existingPath = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[] { binDirectory, existingPath }.Where(value => !string.IsNullOrWhiteSpace(value)));
        SetLibraryPath(startInfo, binDirectory, preloadHexagonDriver: true);
        var hexagonBackendPluginPath = Path.Combine(binDirectory, HexagonBackendPluginName);
        if (File.Exists(hexagonBackendPluginPath))
        {
            startInfo.Environment["GGML_BACKEND_PATH"] = hexagonBackendPluginPath;
        }

        return new Process { StartInfo = startInfo };
    }

    private static void StartLlamaServerForegroundService()
    {
#if ANDROID
        var context = global::Android.App.Application.Context;
        var intent = new global::Android.Content.Intent(
            context,
            typeof(global::HexagonLlamaCppSharp.Maui.Platforms.Android.LlamaServerForegroundService));
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
#endif
    }

    private static void StopLlamaServerForegroundService()
    {
#if ANDROID
        var context = global::Android.App.Application.Context;
        var intent = new global::Android.Content.Intent(
            context,
            typeof(global::HexagonLlamaCppSharp.Maui.Platforms.Android.LlamaServerForegroundService));
        context.StopService(intent);
#endif
    }

    private async Task<int> RunLinkerDependencyCheckAsync(string serverPath, CancellationToken cancellationToken)
    {
        var binDirectory = Path.GetDirectoryName(serverPath)
            ?? throw new IOException("Das llama-server-Verzeichnis konnte nicht ermittelt werden.");
        var startInfo = new ProcessStartInfo
        {
            FileName = AndroidDynamicLinkerPath,
            WorkingDirectory = binDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add(serverPath);
        SetLibraryPath(startInfo, binDirectory);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ProcessExecutionException(
                    AndroidDynamicLinkerPath,
                    new InvalidOperationException("Process.Start returned false."));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new ProcessExecutionException(AndroidDynamicLinkerPath, exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        PublishOutput(new ProcessLogLine(
            $"Android-Linker-Abhängigkeitsprüfung beendet (Exit-Code {process.ExitCode}).",
            process.ExitCode != 0));
        PublishDiagnosticLines(standardOutput, isError: false);
        PublishDiagnosticLines(standardError, isError: true);
        if (string.IsNullOrWhiteSpace(standardOutput) && string.IsNullOrWhiteSpace(standardError))
        {
            PublishOutput(new ProcessLogLine("linker64 --list hat keine Diagnoseausgabe geliefert.", false));
        }
        return process.ExitCode;
    }

    private static void SetLibraryPath(
        ProcessStartInfo startInfo,
        string binDirectory,
        bool preloadHexagonDriver = false)
    {
        var existingLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        startInfo.Environment["LD_LIBRARY_PATH"] = string.Join(
            Path.PathSeparator,
            new[]
            {
                binDirectory,
                "/vendor/lib64",
                "/vendor/lib",
                "/odm/lib64",
                "/odm/lib",
                existingLibraryPath
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        startInfo.Environment["ADSP_LIBRARY_PATH"] = string.Join(
            ";",
            new[]
            {
                binDirectory,
                "/vendor/lib/rfsa/adsp",
                "/system/lib/rfsa/adsp",
                "/dsp"
            });

        if (preloadHexagonDriver)
        {
            var driverPath = new[]
            {
                "/vendor/lib64/libcdsprpc.so",
                "/vendor/lib64/libadsprpc.so",
                "/vendor/lib/libcdsprpc.so",
                "/vendor/lib/libadsprpc.so"
            }.FirstOrDefault(File.Exists);
            if (driverPath is not null)
            {
                startInfo.Environment["LD_PRELOAD"] = driverPath;
            }
        }
    }

    private async Task CleanupExitedProcessAsync()
    {
        Process? process;
        Task? healthTask;
        Task? standardOutputTask;
        Task? standardErrorTask;
        CancellationTokenSource? healthCancellation;
        lock (_stateLock)
        {
            process = _process;
            healthTask = _healthTask;
            standardOutputTask = _standardOutputTask;
            standardErrorTask = _standardErrorTask;
            healthCancellation = _healthCancellation;
        }

        if (process is null || !process.HasExited)
        {
            return;
        }

        healthCancellation?.Cancel();
        if (healthTask is not null)
        {
            try
            {
                await healthTask;
            }
            catch (OperationCanceledException) when (healthCancellation?.IsCancellationRequested == true)
            {
            }
        }

        await DrainOutputAsync(standardOutputTask, standardErrorTask);
        CleanupProcess(process);
        healthCancellation?.Dispose();
    }

    private static IReadOnlyList<string> BuildArguments(LlamaServerOptions options, int port, string serverLogPath)
    {
        var arguments = ParseAdditionalArguments(options.AdditionalArgs).ToList();
        arguments.AddRange(
        [
            "-m", options.ModelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "-c", options.ContextSize.ToString(CultureInfo.InvariantCulture),
            "-ngl", options.GpuLayers.ToString(CultureInfo.InvariantCulture),
            "-t", options.Threads.ToString(CultureInfo.InvariantCulture),
            "--temp", options.Temperature.ToString(CultureInfo.InvariantCulture),
            "--top-p", options.TopP.ToString(CultureInfo.InvariantCulture),
            "--top-k", options.TopK.ToString(CultureInfo.InvariantCulture),
            "--repeat-penalty", options.RepeatPenalty.ToString(CultureInfo.InvariantCulture),
            "--parallel", options.Parallel.ToString(CultureInfo.InvariantCulture),
            "--log-file", serverLogPath,
            options.EnableWebUi ? "--ui" : "--no-ui"
        ]);

        AddOptionalArgument(arguments, "--mmproj", options.MmprojPath);
        AddOptionalArgument(arguments, "-md", options.MtpPath);
        AddOptionalArgument(arguments, "--cache-type-k", options.CacheTypeK);
        AddOptionalArgument(arguments, "--cache-type-v", options.CacheTypeV);
        if (!ContainsOption(arguments, "--flash-attn", "-fa"))
        {
            arguments.Add("--flash-attn");
            arguments.Add("on");
        }
        if (options.GpuLayers > 0 && UsesUnsupportedHexagonKvCache(options) &&
            !ContainsArgument(arguments, "--kv-offload", "-kvo", "--no-kv-offload", "-nkvo"))
        {
            arguments.Add("--no-kv-offload");
        }
        AddOptionalArgument(arguments, "--spec-type", options.SpecType);
        if (options.SpecDraftNMax > 0)
        {
            arguments.Add("--spec-draft-n-max");
            arguments.Add(options.SpecDraftNMax.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    private static bool UsesUnsupportedHexagonKvCache(LlamaServerOptions options) =>
        string.Equals(options.CacheTypeK, "q4_0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(options.CacheTypeV, "q4_0", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsArgument(IEnumerable<string> arguments, params string[] candidates) =>
        arguments.Any(argument => candidates.Any(candidate =>
            string.Equals(argument, candidate, StringComparison.OrdinalIgnoreCase)));

    private static bool ContainsOption(IEnumerable<string> arguments, params string[] candidates) =>
        arguments.Any(argument => candidates.Any(candidate =>
            string.Equals(argument, candidate, StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(candidate + "=", StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> ParseAdditionalArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;
        var tokenStarted = false;

        foreach (var character in text)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                tokenStarted = true;
                continue;
            }

            if (character == '\\' && quote != '\'')
            {
                escaped = true;
                tokenStarted = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                tokenStarted = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                current.Append(character);
                tokenStarted = true;
            }
        }

        if (escaped)
        {
            throw new ArgumentException("Die zusätzlichen Serverargumente enden mit einem unvollständigen Escape-Zeichen.", nameof(text));
        }

        if (quote != '\0')
        {
            throw new ArgumentException("Die zusätzlichen Serverargumente enthalten ein nicht geschlossenes Anführungszeichen.", nameof(text));
        }

        if (tokenStarted)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private static void AddOptionalArgument(List<string> arguments, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add(name);
            arguments.Add(value);
        }
    }

    private async Task<LlamaServerHealth> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(StartupTimeout);
        while (true)
        {
            timeoutCancellation.Token.ThrowIfCancellationRequested();
            Task<int>? exitTask;
            Uri? baseAddress;
            lock (_stateLock)
            {
                exitTask = _exitCompletion?.Task;
                baseAddress = _baseAddress;
            }

            if (exitTask?.IsCompleted == true)
            {
                var exitCode = await exitTask;
                return new LlamaServerHealth(
                    false,
                    null,
                    $"llama-server wurde während des Starts beendet (Exit-Code {exitCode}).");
            }

            if (baseAddress is not null)
            {
                var health = await ProbeHealthAsync(baseAddress, timeoutCancellation.Token);
                if (health.IsHealthy)
                {
                    PublishHealth(health);
                    return health;
                }
            }

            var delayTask = Task.Delay(TimeSpan.FromMilliseconds(500), timeoutCancellation.Token);
            if (exitTask is not null)
            {
                await Task.WhenAny(delayTask, exitTask);
            }
            else
            {
                await delayTask;
            }
        }
    }

    private async Task MonitorHealthAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var health = await ProbeHealthAsync(baseAddress, cancellationToken);
            PublishHealth(health);
            try
            {
                await Task.Delay(HealthInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<LlamaServerHealth> ProbeHealthAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(HealthRequestTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(baseAddress, "health"),
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token);
            var statusCode = (int)response.StatusCode;
            var memoryBytes = GetServerMemoryBytes();
            return response.IsSuccessStatusCode
                ? new LlamaServerHealth(true, statusCode, "Server ist gesund.", memoryBytes)
                : new LlamaServerHealth(false, statusCode, $"Health-Endpunkt meldet HTTP {statusCode}.", memoryBytes);
        }
        catch (HttpRequestException exception)
        {
            return new LlamaServerHealth(false, null, exception.Message, GetServerMemoryBytes());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlamaServerHealth(false, null, "Health-Prüfung ist abgelaufen.", GetServerMemoryBytes());
        }
    }

    private long? GetServerMemoryBytes()
    {
        Process? process;
        lock (_stateLock)
        {
            process = _process;
        }

        if (process is null)
        {
            return null;
        }

        try
        {
            if (process.HasExited)
            {
                return null;
            }

            var statusPath = $"/proc/{process.Id}/status";
            foreach (var line in File.ReadLines(statusPath))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    continue;
                }

                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 ||
                    !string.Equals(fields[2], "kB", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes) ||
                    kilobytes < 0 ||
                    kilobytes > long.MaxValue / 1024)
                {
                    return null;
                }

                return kilobytes * 1024;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private string ValidateServerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            throw new IOException("Der Installationsmanifest enthält keinen absoluten llama-server-Pfad.");
        }

        var buildRoot = Path.GetFullPath(_layout.BuildDirectory);
        var serverPath = Path.GetFullPath(path);
        if (!serverPath.StartsWith(buildRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(serverPath))
        {
            throw new IOException("Der installierte llama-server liegt nicht im erwarteten privaten Build-Verzeichnis.");
        }

        return serverPath;
    }

    private static async Task ValidateNativeLibrariesAsync(
        InstallationManifest manifest,
        string serverPath,
        CancellationToken cancellationToken)
    {
        if (manifest.NativeLibraries is null || manifest.NativeLibraries.Count == 0)
        {
            throw new IOException("Der Installationsmanifest enthält keine verifizierten nativen Bibliotheken.");
        }

        var binDirectory = Path.GetDirectoryName(serverPath)
            ?? throw new IOException("Das llama-server-Verzeichnis konnte nicht ermittelt werden.");
        foreach (var library in manifest.NativeLibraries)
        {
            if (library is null || string.IsNullOrWhiteSpace(library.FileName) ||
                library.FileName.Contains(Path.DirectorySeparatorChar) ||
                library.FileName.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new IOException("Der Installationsmanifest enthält einen ungültigen Bibliotheksnamen.");
            }

            var libraryPath = Path.Combine(binDirectory, library.FileName);
            if (!File.Exists(libraryPath))
            {
                throw new IOException($"Die native Bibliothek '{library.FileName}' fehlt im llama-server-Verzeichnis.");
            }

            await using var stream = new FileStream(
                libraryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(library.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException($"Integritätsprüfung fehlgeschlagen: {library.FileName}.");
            }
        }
    }

    private async Task EnsureToolchainRuntimeLibraryAsync(
        string serverPath,
        CancellationToken cancellationToken)
    {
        var binDirectory = Path.GetDirectoryName(serverPath)
            ?? throw new IOException("Das llama-server-Verzeichnis konnte nicht ermittelt werden.");
        var destinationPath = Path.Combine(binDirectory, "libomp.so");
        if (File.Exists(destinationPath))
        {
            return;
        }

        if (!Directory.Exists(_layout.ToolchainDirectory))
        {
            throw new IOException("libomp.so fehlt und die installierte NDK-Toolchain ist nicht vorhanden.");
        }

        var sourcePath = Directory
            .EnumerateFiles(_layout.ToolchainDirectory, "libomp.so", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                                        path.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (sourcePath is null)
        {
            throw new IOException("libomp.so fehlt im llama-server-Verzeichnis und wurde auch nicht in der NDK-Toolchain gefunden.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = destinationPath + ".partial";
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            PublishOutput(new ProcessLogLine(
                $"Fehlende libomp.so aus der NDK-Toolchain ergänzt: {sourcePath}",
                false));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void ValidateOptions(LlamaServerOptions options)
    {
        ValidateRequiredModelPath(options.ModelPath, "Modell");
        ValidateOptionalModelPath(options.MmprojPath, "mmproj");
        ValidateOptionalModelPath(options.MtpPath, "MTP-Modell");
        if (options.Port is not null and (< 1 or > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port), "Der Port muss zwischen 1 und 65535 liegen.");
        }

        if (options.ContextSize <= 0 || options.GpuLayers < 0 || options.Threads <= 0 || options.Parallel <= 0 ||
            options.TopK < 0 || options.SpecDraftNMax < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Serverparameter müssen positive Werte enthalten.");
        }

        if (options.Temperature < 0 || options.TopP <= 0 || options.TopP > 1 || options.RepeatPenalty <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sampling-Parameter liegen außerhalb des gültigen Bereichs.");
        }

        _ = ParseAdditionalArguments(options.AdditionalArgs);
    }

    private static void ValidateRequiredModelPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Der {description}-Pfad muss auf eine vorhandene GGUF-Datei zeigen.", nameof(path));
        }
    }

    private static void ValidateOptionalModelPath(string? path, string description)
    {
        if (path is not null)
        {
            ValidateRequiredModelPath(path, description);
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process process)
        {
            return;
        }

        var exitCode = process.ExitCode;
        bool stopRequested;
        lock (_stateLock)
        {
            _isRunning = false;
            stopRequested = ReferenceEquals(_process, process) && _stopRequested;
        }

        StopLlamaServerForegroundService();

        _exitCompletion?.TrySetResult(exitCode);
        PublishOutput(new ProcessLogLine(
            $"llama-server beendet (Exit-Code {exitCode}).",
            stopRequested ? ProcessLogSeverity.Information : (exitCode == 0 ? ProcessLogSeverity.Information : ProcessLogSeverity.Error)));
        PublishHealth(new LlamaServerHealth(false, null, $"llama-server wurde beendet (Exit-Code {exitCode})."));
    }

    private void CleanupProcess(Process process)
    {
        StopLlamaServerForegroundService();
        process.Exited -= OnProcessExited;
        process.Dispose();
        lock (_stateLock)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _exitCompletion = null;
                _standardOutputTask = null;
                _standardErrorTask = null;
                _serverLogPath = null;
                _healthTask = null;
                _healthCancellation = null;
                _isRunning = false;
                _stopRequested = false;
                _port = null;
                _baseAddress = null;
            }
        }
    }

    private async Task ForwardOutputAsync(StreamReader reader, bool isError)
    {
        var buffer = new char[4096];
        var pending = new StringBuilder();
        while (true)
        {
            var charactersRead = await reader.ReadAsync(buffer.AsMemory());
            if (charactersRead == 0)
            {
                break;
            }

            var fragmentStart = 0;
            for (var index = 0; index < charactersRead; index++)
            {
                if (buffer[index] is not ('\r' or '\n'))
                {
                    continue;
                }

                if (index > fragmentStart)
                {
                    pending.Append(buffer, fragmentStart, index - fragmentStart);
                }

                if (pending.Length > 0)
                {
                    var text = pending.ToString();
                    PublishServerOutput(new ProcessLogLine(
                        text,
                        ProcessLogSeverityClassifier.Classify(text, isError)));
                    pending.Clear();
                }

                fragmentStart = index + 1;
            }

            if (fragmentStart < charactersRead)
            {
                pending.Append(buffer, fragmentStart, charactersRead - fragmentStart);
            }
        }

        if (pending.Length > 0)
        {
            var text = pending.ToString();
            PublishServerOutput(new ProcessLogLine(
                text,
                ProcessLogSeverityClassifier.Classify(text, isError)));
        }
    }

    private void PublishDiagnosticLines(string text, bool isError)
    {
        foreach (var line in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries))
        {
            PublishOutput(new ProcessLogLine(
                line,
                ProcessLogSeverityClassifier.Classify(line, isError)));
        }
    }

    private void PublishServerOutput(ProcessLogLine line)
    {
        PublishOutput(line);
    }

    private static async Task DrainOutputAsync(Task? standardOutputTask, Task? standardErrorTask)
    {
        var tasks = new[] { standardOutputTask, standardErrorTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private void PublishOutput(ProcessLogLine line)
    {
        OutputReceived?.Invoke(line);
    }

    private void PublishHealth(LlamaServerHealth health)
    {
        var changed = false;
        lock (_stateLock)
        {
            if (!_lastHealth.Equals(health))
            {
                _lastHealth = health;
                changed = true;
            }
        }

        if (changed)
        {
            HealthChanged?.Invoke(health);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}