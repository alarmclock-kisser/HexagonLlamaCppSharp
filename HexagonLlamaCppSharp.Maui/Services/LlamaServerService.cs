using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class LlamaServerService : ILlamaServerService, IAsyncDisposable
{
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
    private CancellationTokenSource? _healthCancellation;
    private Task? _healthTask;
    private bool _isRunning;
    private int? _port;
    private Uri? _baseAddress;
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
            var port = options.Port ?? FindAvailablePort();
            var baseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
            var process = CreateProcess(serverPath, options, port);
            var exitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += OnProcessExited;
            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnErrorDataReceived;

            lock (_stateLock)
            {
                _process = process;
                _exitCompletion = exitCompletion;
                _port = port;
                _baseAddress = baseAddress;
                _lastHealth = new LlamaServerHealth(false, null, "llama-server wird gestartet.");
            }

            try
            {
                if (!process.Start())
                {
                    throw new ProcessExecutionException(serverPath, new InvalidOperationException("Process.Start returned false."));
                }

                process.EnableRaisingEvents = true;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                CleanupProcess(process);
                throw new ProcessExecutionException(serverPath, exception);
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
            CancellationTokenSource? healthCancellation;
            lock (_stateLock)
            {
                process = _process;
                healthTask = _healthTask;
                healthCancellation = _healthCancellation;
            }

            if (process is null)
            {
                return;
            }

            healthCancellation?.Cancel();
            if (!process.HasExited)
            {
                PublishOutput(new ProcessLogLine("Stoppe llama-server ...", false));
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

    private Process CreateProcess(string serverPath, LlamaServerOptions options, int port)
    {
        var binDirectory = Path.GetDirectoryName(serverPath)
            ?? throw new IOException("Das llama-server-Verzeichnis konnte nicht ermittelt werden.");
        _filePermissionService.MakeExecutable(serverPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            WorkingDirectory = binDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in BuildArguments(options, port))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var existingPath = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[] { binDirectory, existingPath }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var existingLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        startInfo.Environment["LD_LIBRARY_PATH"] = string.Join(
            Path.PathSeparator,
            new[] { binDirectory, existingLibraryPath }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new Process { StartInfo = startInfo };
    }

    private async Task CleanupExitedProcessAsync()
    {
        Process? process;
        Task? healthTask;
        CancellationTokenSource? healthCancellation;
        lock (_stateLock)
        {
            process = _process;
            healthTask = _healthTask;
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

        CleanupProcess(process);
        healthCancellation?.Dispose();
    }

    private static IReadOnlyList<string> BuildArguments(LlamaServerOptions options, int port)
    {
        var arguments = new List<string>
        {
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
            "--parallel", options.Parallel.ToString(CultureInfo.InvariantCulture)
        };

        AddOptionalArgument(arguments, "--mmproj", options.MmprojPath);
        AddOptionalArgument(arguments, "-md", options.MtpPath);
        AddOptionalArgument(arguments, "--ctk", options.CacheTypeK);
        AddOptionalArgument(arguments, "--ctv", options.CacheTypeV);
        AddOptionalArgument(arguments, "--spec-type", options.SpecType);
        if (options.SpecDraftNMax > 0)
        {
            arguments.Add("--spec-draft-n-max");
            arguments.Add(options.SpecDraftNMax.ToString(CultureInfo.InvariantCulture));
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
                return new LlamaServerHealth(false, null, "llama-server wurde während des Starts beendet.");
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
            return response.IsSuccessStatusCode
                ? new LlamaServerHealth(true, statusCode, "Server ist gesund.")
                : new LlamaServerHealth(false, statusCode, $"Health-Endpunkt meldet HTTP {statusCode}.");
        }
        catch (HttpRequestException exception)
        {
            return new LlamaServerHealth(false, null, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlamaServerHealth(false, null, "Health-Prüfung ist abgelaufen.");
        }
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

    private void OnOutputDataReceived(object? sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            PublishOutput(new ProcessLogLine(args.Data, false));
        }
    }

    private void OnErrorDataReceived(object? sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            PublishOutput(new ProcessLogLine(args.Data, true));
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process process)
        {
            return;
        }

        var exitCode = process.ExitCode;
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _exitCompletion?.TrySetResult(exitCode);
        PublishOutput(new ProcessLogLine($"llama-server beendet (Exit-Code {exitCode}).", exitCode != 0));
        PublishHealth(new LlamaServerHealth(false, null, $"llama-server wurde beendet (Exit-Code {exitCode})."));
    }

    private void CleanupProcess(Process process)
    {
        process.Exited -= OnProcessExited;
        process.OutputDataReceived -= OnOutputDataReceived;
        process.ErrorDataReceived -= OnErrorDataReceived;
        process.Dispose();
        lock (_stateLock)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _exitCompletion = null;
                _healthTask = null;
                _healthCancellation = null;
                _isRunning = false;
                _port = null;
                _baseAddress = null;
            }
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