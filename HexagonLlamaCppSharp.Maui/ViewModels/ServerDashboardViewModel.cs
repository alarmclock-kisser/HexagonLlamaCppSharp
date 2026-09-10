using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using HexagonLlamaCppSharp.Maui.Models;
using HexagonLlamaCppSharp.Maui.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace HexagonLlamaCppSharp.Maui.ViewModels;

public sealed class ServerDashboardViewModel : INotifyPropertyChanged
{
    private const string PreferencesPrefix = "server-dashboard.";
    private const string ModelPathPreferenceKey = PreferencesPrefix + "model-path";
    private const string MmprojPathPreferenceKey = PreferencesPrefix + "mmproj-path";
    private const string MtpPathPreferenceKey = PreferencesPrefix + "mtp-path";
    private const string PortPreferenceKey = PreferencesPrefix + "port";
    private const string ContextSizePreferenceKey = PreferencesPrefix + "context-size";
    private const string GpuLayersPreferenceKey = PreferencesPrefix + "gpu-layers";
    private const string ThreadsPreferenceKey = PreferencesPrefix + "threads";
    private const string TemperaturePreferenceKey = PreferencesPrefix + "temperature";
    private const string TopPPreferenceKey = PreferencesPrefix + "top-p";
    private const string TopKPreferenceKey = PreferencesPrefix + "top-k";
    private const string RepeatPenaltyPreferenceKey = PreferencesPrefix + "repeat-penalty";
    private const string ParallelPreferenceKey = PreferencesPrefix + "parallel";
    private const string CacheTypeKPreferenceKey = PreferencesPrefix + "cache-type-k";
    private const string CacheTypeVPreferenceKey = PreferencesPrefix + "cache-type-v";
    private const string SpecTypePreferenceKey = PreferencesPrefix + "spec-type";
    private const string SpecDraftNMaxPreferenceKey = PreferencesPrefix + "spec-draft-n-max";
    private const string AdditionalArgsPreferenceKey = PreferencesPrefix + "additional-args";
    private const string EnableWebUiPreferenceKey = PreferencesPrefix + "enable-web-ui";

    private readonly IModelFilePicker _modelFilePicker;
    private readonly ILlamaServerService _serverService;
    private readonly ILogExportService _logExportService;
    private readonly object _logLock = new();
    private CancellationTokenSource? _operationCancellation;
    private ModelFile? _selectedModel;
    private string _persistedModelPath = string.Empty;
    private string _mmprojPath = string.Empty;
    private string _mtpPath = string.Empty;
    private string _portText = string.Empty;
    private string _contextSizeText = "65532";
    private string _gpuLayersText = "0";
    private string _threadsText = "4";
    private string _temperatureText = "0.8";
    private string _topPText = "0.95";
    private string _topKText = "40";
    private string _repeatPenaltyText = "1.1";
    private string _parallelText = "1";
    private string _cacheTypeK = "q8_0";
    private string _cacheTypeV = "q8_0";
    private string _specType = string.Empty;
    private string _specDraftNMaxText = "0";
    private string _additionalArgs = string.Empty;
    private bool _enableWebUi = true;
    private string _status = "Kein Modell ausgewählt.";
    private string _health = "Nicht gestartet";
    private string _serverAddress = string.Empty;
    private string _memoryUsage = "Server-Speicher (RSS): nicht verfügbar";
    private string _consoleText = "Noch keine Serverausgabe.";
    private bool _isBusy;
    private bool _isSavingLog;

    public ServerDashboardViewModel(
        IModelFilePicker modelFilePicker,
        ILlamaServerService serverService,
        ILogExportService logExportService)
    {
        _modelFilePicker = modelFilePicker;
        _serverService = serverService;
        _logExportService = logExportService;
        LoadPersistedSettings();
        _serverService.OutputReceived += OnOutputReceived;
        _serverService.HealthChanged += OnHealthChanged;

        PickModelCommand = new Command(async () => await PickModelAsync());
        PickMmprojCommand = new Command(async () => await PickAuxiliaryModelAsync(isMmproj: true));
        PickMtpCommand = new Command(async () => await PickAuxiliaryModelAsync(isMmproj: false));
        StartServerCommand = new Command(async () => await StartServerAsync(), CanStartServer);
        StopServerCommand = new Command(async () => await StopServerAsync(), CanStopServer);
        RefreshHealthCommand = new Command(async () => await RefreshHealthAsync());
        OpenWebUiCommand = new Command(async () => await OpenWebUiAsync(), CanOpenWebUiCommand);
        SaveLogCommand = new Command(async () => await SaveLogAsync(), () => CanSaveLog);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> LogLines { get; } = [];

    public string ConsoleText
    {
        get => _consoleText;
        private set => SetProperty(ref _consoleText, value);
    }

    public ICommand PickModelCommand { get; }

    public ICommand PickMmprojCommand { get; }

    public ICommand PickMtpCommand { get; }

    public ICommand StartServerCommand { get; }

    public ICommand StopServerCommand { get; }

    public ICommand RefreshHealthCommand { get; }

    public ICommand OpenWebUiCommand { get; }

    public ICommand SaveLogCommand { get; }

    public string SelectedModelName => _selectedModel?.FileName ??
        (string.IsNullOrWhiteSpace(_persistedModelPath)
            ? "Kein Modell ausgewählt"
            : Path.GetFileName(_persistedModelPath));

    public string SelectedModelPath => _selectedModel?.Path ?? _persistedModelPath;

    public string SelectedModelSize => _selectedModel is null ? string.Empty : FormatBytes(_selectedModel.Length);

    public string ModelMemorySummary => _selectedModel is null
        ? "GGUF-Datei: nicht ausgewählt"
        : $"GGUF-Datei: {FormatBytes(_selectedModel.Length)} (Originalpfad, keine Kopie)";

    public string MmprojPath
    {
        get => _mmprojPath;
        set => SetPersistedProperty(ref _mmprojPath, value, MmprojPathPreferenceKey, nameof(MmprojPath));
    }

    public string MtpPath
    {
        get => _mtpPath;
        set => SetPersistedProperty(ref _mtpPath, value, MtpPathPreferenceKey, nameof(MtpPath));
    }

    public string PortText
    {
        get => _portText;
        set => SetPersistedProperty(ref _portText, value, PortPreferenceKey, nameof(PortText));
    }

    public string ContextSizeText
    {
        get => _contextSizeText;
        set => SetPersistedProperty(ref _contextSizeText, value, ContextSizePreferenceKey, nameof(ContextSizeText));
    }

    public string GpuLayersText
    {
        get => _gpuLayersText;
        set => SetPersistedProperty(ref _gpuLayersText, value, GpuLayersPreferenceKey, nameof(GpuLayersText));
    }

    public string ThreadsText
    {
        get => _threadsText;
        set => SetPersistedProperty(ref _threadsText, value, ThreadsPreferenceKey, nameof(ThreadsText));
    }

    public string TemperatureText
    {
        get => _temperatureText;
        set => SetPersistedProperty(ref _temperatureText, value, TemperaturePreferenceKey, nameof(TemperatureText));
    }

    public string TopPText
    {
        get => _topPText;
        set => SetPersistedProperty(ref _topPText, value, TopPPreferenceKey, nameof(TopPText));
    }

    public string TopKText
    {
        get => _topKText;
        set => SetPersistedProperty(ref _topKText, value, TopKPreferenceKey, nameof(TopKText));
    }

    public string RepeatPenaltyText
    {
        get => _repeatPenaltyText;
        set => SetPersistedProperty(ref _repeatPenaltyText, value, RepeatPenaltyPreferenceKey, nameof(RepeatPenaltyText));
    }

    public string ParallelText
    {
        get => _parallelText;
        set => SetPersistedProperty(ref _parallelText, value, ParallelPreferenceKey, nameof(ParallelText));
    }

    public string CacheTypeK
    {
        get => _cacheTypeK;
        set => SetPersistedProperty(ref _cacheTypeK, value, CacheTypeKPreferenceKey, nameof(CacheTypeK));
    }

    public string CacheTypeV
    {
        get => _cacheTypeV;
        set => SetPersistedProperty(ref _cacheTypeV, value, CacheTypeVPreferenceKey, nameof(CacheTypeV));
    }

    public string SpecType
    {
        get => _specType;
        set => SetPersistedProperty(ref _specType, value, SpecTypePreferenceKey, nameof(SpecType));
    }

    public string SpecDraftNMaxText
    {
        get => _specDraftNMaxText;
        set => SetPersistedProperty(ref _specDraftNMaxText, value, SpecDraftNMaxPreferenceKey, nameof(SpecDraftNMaxText));
    }

    public string AdditionalArgs
    {
        get => _additionalArgs;
        set => SetPersistedProperty(ref _additionalArgs, value, AdditionalArgsPreferenceKey, nameof(AdditionalArgs));
    }

    public bool EnableWebUi
    {
        get => _enableWebUi;
        set
        {
            if (SetProperty(ref _enableWebUi, value))
            {
                Preferences.Default.Set(EnableWebUiPreferenceKey, value);
                OnPropertyChanged(nameof(CanOpenWebUi));
                ChangeCommandState();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Health
    {
        get => _health;
        private set => SetProperty(ref _health, value);
    }

    public string ServerAddress
    {
        get => _serverAddress;
        private set
        {
            if (SetProperty(ref _serverAddress, value))
            {
                OnPropertyChanged(nameof(CanOpenWebUi));
                ChangeCommandState();
            }
        }
    }

    public bool CanOpenWebUi =>
        _serverService.IsRunning &&
        EnableWebUi &&
        Uri.TryCreate(ServerAddress, UriKind.Absolute, out var address) &&
        (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

    public string MemoryUsage
    {
        get => _memoryUsage;
        private set => SetProperty(ref _memoryUsage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ChangeCommandState();
            }
        }
    }

    public bool IsSavingLog
    {
        get => _isSavingLog;
        private set
        {
            if (SetProperty(ref _isSavingLog, value))
            {
                OnPropertyChanged(nameof(CanSaveLog));
                ((Command)SaveLogCommand).ChangeCanExecute();
            }
        }
    }

    public bool CanSaveLog => !IsSavingLog;

    private bool CanStartServer() => !IsBusy && _selectedModel is not null && !_serverService.IsRunning;

    private bool CanStopServer() => !IsBusy && _serverService.IsRunning;

    private bool CanOpenWebUiCommand() => CanOpenWebUi;

    private async Task PickModelAsync()
    {
        try
        {
            var model = await _modelFilePicker.PickAsync();
            if (model is null)
            {
                return;
            }

            _selectedModel = model;
            _persistedModelPath = model.Path;
            Preferences.Default.Set(ModelPathPreferenceKey, model.Path);
            OnPropertyChanged(nameof(SelectedModelName));
            OnPropertyChanged(nameof(SelectedModelPath));
            OnPropertyChanged(nameof(SelectedModelSize));
            OnPropertyChanged(nameof(ModelMemorySummary));
            Status = $"Modell ausgewählt: {model.FileName} ({FormatBytes(model.Length)}).";
            AppendLog(new ProcessLogLine($"GGUF wird am Originalpfad verwendet: {model.Path}", false));
            ChangeCommandState();
        }
        catch (IOException exception)
        {
            Status = exception.Message;
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
    }

    private async Task SaveLogAsync()
    {
        if (IsSavingLog)
        {
            return;
        }

        IsSavingLog = true;
        try
        {
            var lines = await MainThread.InvokeOnMainThreadAsync(() =>
            {
                lock (_logLock)
                {
                    return LogLines.ToArray();
                }
            });
            if (lines.Length == 0)
            {
                lines = [ConsoleText];
            }

            var destination = await _logExportService.SaveAsync(
                lines,
                CancellationToken.None,
                "hexagon-server");
            Status = $"Server-Log gespeichert: {destination}";
            AppendLog(new ProcessLogLine($"Server-Log gespeichert: {destination}", false));
        }
        catch (OperationCanceledException)
        {
            Status = "Server-Log-Speicherung abgebrochen.";
        }
        catch (IOException exception)
        {
            Status = "Der Server-Log konnte nicht gespeichert werden.";
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        catch (UnauthorizedAccessException exception)
        {
            Status = "Der Zugriff auf Downloads wurde verweigert.";
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Status = "Der Server-Log konnte nicht gespeichert werden.";
            AppendLog(new ProcessLogLine($"Server-Log-Export fehlgeschlagen ({exception.GetType().Name}): {exception.Message}", true));
        }
        finally
        {
            IsSavingLog = false;
        }
    }

    private async Task PickAuxiliaryModelAsync(bool isMmproj)
    {
        try
        {
            var model = await _modelFilePicker.PickAsync();
            if (model is null)
            {
                return;
            }

            if (isMmproj)
            {
                MmprojPath = model.Path;
            }
            else
            {
                MtpPath = model.Path;
            }

            AppendLog(new ProcessLogLine($"Zusatzmodell wird am Originalpfad verwendet: {model.Path}", false));
        }
        catch (IOException exception)
        {
            Status = exception.Message;
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
    }

    private async Task StartServerAsync()
    {
        if (_selectedModel is null || IsBusy)
        {
            return;
        }

        PersistCurrentSettings();
        if (!TryBuildOptions(out var options, out var error))
        {
            Status = error;
            AppendLog(new ProcessLogLine(error, true));
            return;
        }

        if (options.GpuLayers > 0 &&
            (IsQ4CacheType(options.CacheTypeK) || IsQ4CacheType(options.CacheTypeV)))
        {
            var cacheTypeK = NormalizeHexagonCacheType(options.CacheTypeK);
            var cacheTypeV = NormalizeHexagonCacheType(options.CacheTypeV);
            CacheTypeK = cacheTypeK ?? "q8_0";
            CacheTypeV = cacheTypeV ?? "q8_0";
            options = options with
            {
                CacheTypeK = CacheTypeK,
                CacheTypeV = CacheTypeV
            };
            PersistCurrentSettings();
            AppendLog(new ProcessLogLine(
                "Für Hexagon-Flash-Attention wird der inkompatible q4_0-KV-Cache auf q8_0 umgestellt; KV-Offload bleibt aktiv.",
                ProcessLogSeverity.Warning));
        }

        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        Status = "llama-server wird gestartet und lädt das Modell ...";
        try
        {
            var result = await _serverService.StartServerAsync(options, _operationCancellation.Token);
            Status = result.Message;
            if (result.BaseAddress is not null)
            {
                ServerAddress = result.BaseAddress.ToString();
            }

            AppendLog(new ProcessLogLine(result.Message, !result.Succeeded));
        }
        catch (OperationCanceledException)
        {
            Status = "Serverstart abgebrochen.";
            AppendLog(new ProcessLogLine(Status, true));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or CryptographicException or ProcessExecutionException)
        {
            Status = exception.Message;
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        finally
        {
            IsBusy = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            ChangeCommandState();
        }
    }

    private async Task StopServerAsync()
    {
        if (IsBusy || !_serverService.IsRunning)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _serverService.StopServerAsync(CancellationToken.None);
            Status = "llama-server wurde beendet.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Status = exception.Message;
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        finally
        {
            IsBusy = false;
            ChangeCommandState();
        }
    }

    private async Task RefreshHealthAsync()
    {
        try
        {
            var health = await _serverService.CheckHealthAsync(CancellationToken.None);
            OnHealthChanged(health);
        }
        catch (InvalidOperationException exception)
        {
            Health = exception.Message;
        }
    }

    private async Task OpenWebUiAsync()
    {
        if (!CanOpenWebUi || !Uri.TryCreate(ServerAddress, UriKind.Absolute, out var address))
        {
            Status = "Die WebUI ist erst verfügbar, wenn der Server läuft und die WebUI aktiviert ist.";
            return;
        }

        try
        {
            var opened = await Browser.Default.OpenAsync(address, BrowserLaunchMode.SystemPreferred);
            if (opened)
            {
                Status = $"WebUI geöffnet: {address}";
                return;
            }

            Status = "Der Standardbrowser konnte die llama-WebUI nicht öffnen.";
            AppendLog(new ProcessLogLine(Status, ProcessLogSeverity.Warning));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Java.Lang.Exception)
        {
            Status = "Der Standardbrowser konnte die llama-WebUI nicht öffnen.";
            AppendLog(new ProcessLogLine(
                $"WebUI konnte nicht geöffnet werden ({exception.GetType().Name}): {exception.Message}",
                ProcessLogSeverity.Error));
        }
    }

    private bool TryBuildOptions(out LlamaServerOptions options, out string error)
    {
        options = default!;
        error = string.Empty;
        if (!TryParseOptionalInt(PortText, out var port, out error, "Port") ||
            !TryParseInt(ContextSizeText, out var contextSize, out error, "Context Window") ||
            !TryParseInt(GpuLayersText, out var gpuLayers, out error, "Hexagon/GPU-Layer") ||
            !TryParseInt(ThreadsText, out var threads, out error, "Threads") ||
            !TryParseFloat(TemperatureText, out var temperature, out error, "Temperatur") ||
            !TryParseFloat(TopPText, out var topP, out error, "Top-P") ||
            !TryParseInt(TopKText, out var topK, out error, "Top-K") ||
            !TryParseFloat(RepeatPenaltyText, out var repeatPenalty, out error, "Repeat Penalty") ||
            !TryParseInt(ParallelText, out var parallel, out error, "Parallel") ||
            !TryParseInt(SpecDraftNMaxText, out var specDraftNMax, out error, "Speculative Draft N-Max"))
        {
            return false;
        }

        options = new LlamaServerOptions(
            _selectedModel!.Path,
            NullIfWhiteSpace(MmprojPath),
            NullIfWhiteSpace(MtpPath),
            port,
            contextSize,
            gpuLayers,
            threads,
            temperature,
            topP,
            topK,
            repeatPenalty,
            parallel,
            NullIfWhiteSpace(CacheTypeK),
            NullIfWhiteSpace(CacheTypeV),
            NullIfWhiteSpace(SpecType),
            specDraftNMax,
            NullIfWhiteSpace(AdditionalArgs),
            EnableWebUi);
        return true;
    }

    private static bool TryParseInt(string text, out int value, out string error, string name)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"{name} muss eine ganze Zahl sein.";
        return false;
    }

    private static bool TryParseOptionalInt(string text, out int? value, out string error, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            error = string.Empty;
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            error = string.Empty;
            return true;
        }

        value = null;
        error = $"{name} muss eine ganze Zahl sein.";
        return false;
    }

    private static bool TryParseFloat(string text, out float value, out string error, string name)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"{name} muss eine Zahl sein.";
        return false;
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsQ4CacheType(string? value) =>
        string.Equals(value, "q4_0", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeHexagonCacheType(string? value) =>
        IsQ4CacheType(value) ? "q8_0" : value;

    private static int TryParsePersistedPositiveInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    private void OnOutputReceived(ProcessLogLine line)
    {
        AppendLog(line);
    }

    private void OnHealthChanged(LlamaServerHealth health)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Health = health.Message;
            MemoryUsage = health.MemoryBytes is long bytes
                ? $"Prozess-RSS: {FormatBytes(bytes)} (nur residente CPU-Seiten; HTP/DSP-Speicher nicht enthalten)"
                : "Prozess-RSS: nicht verfügbar (HTP/DSP-Speicher wird separat verwaltet)";
            if (!_serverService.IsRunning)
            {
                ServerAddress = string.Empty;
            }

            OnPropertyChanged(nameof(CanOpenWebUi));
            ChangeCommandState();
        });
    }

    private void AppendLog(ProcessLogLine line)
    {
        void AddLine()
        {
            lock (_logLock)
            {
                if (LogLines.Count >= 2000)
                {
                    LogLines.RemoveAt(0);
                }

                var renderedLine = FormatLogLine(line);
                LogLines.Add(renderedLine);
                ConsoleText = string.Join(Environment.NewLine, LogLines);
                OnPropertyChanged(nameof(CanSaveLog));
                ((Command)SaveLogCommand).ChangeCanExecute();
            }
        }

        if (MainThread.IsMainThread)
        {
            AddLine();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(AddLine);
        }
    }

    private static string FormatLogLine(ProcessLogLine line)
    {
        var prefix = line.Severity switch
        {
            ProcessLogSeverity.Warning => "[WARN] ",
            ProcessLogSeverity.Error => "[ERR] ",
            ProcessLogSeverity.StandardError => "[STDERR] ",
            _ => string.Empty
        };

        return prefix + line.Text;
    }

    private void ChangeCommandState()
    {
        ((Command)StartServerCommand).ChangeCanExecute();
        ((Command)StopServerCommand).ChangeCanExecute();
        ((Command)OpenWebUiCommand).ChangeCanExecute();
        ((Command)SaveLogCommand).ChangeCanExecute();
    }

    private void LoadPersistedSettings()
    {
        _mmprojPath = Preferences.Default.Get(MmprojPathPreferenceKey, string.Empty);
        _mtpPath = Preferences.Default.Get(MtpPathPreferenceKey, string.Empty);
        _portText = Preferences.Default.Get(PortPreferenceKey, string.Empty);
        _contextSizeText = Preferences.Default.Get(ContextSizePreferenceKey, "65532");
        _gpuLayersText = Preferences.Default.Get(GpuLayersPreferenceKey, "0");
        _threadsText = Preferences.Default.Get(ThreadsPreferenceKey, "4");
        _temperatureText = Preferences.Default.Get(TemperaturePreferenceKey, "0.8");
        _topPText = Preferences.Default.Get(TopPPreferenceKey, "0.95");
        _topKText = Preferences.Default.Get(TopKPreferenceKey, "40");
        _repeatPenaltyText = Preferences.Default.Get(RepeatPenaltyPreferenceKey, "1.1");
        _parallelText = Preferences.Default.Get(ParallelPreferenceKey, "1");
        _cacheTypeK = Preferences.Default.Get(CacheTypeKPreferenceKey, "q8_0");
        _cacheTypeV = Preferences.Default.Get(CacheTypeVPreferenceKey, "q8_0");
        if (TryParsePersistedPositiveInt(_gpuLayersText) > 0)
        {
            _cacheTypeK = NormalizeHexagonCacheType(_cacheTypeK) ?? "q8_0";
            _cacheTypeV = NormalizeHexagonCacheType(_cacheTypeV) ?? "q8_0";
            Preferences.Default.Set(CacheTypeKPreferenceKey, _cacheTypeK);
            Preferences.Default.Set(CacheTypeVPreferenceKey, _cacheTypeV);
        }
        _specType = Preferences.Default.Get(SpecTypePreferenceKey, string.Empty);
        _specDraftNMaxText = Preferences.Default.Get(SpecDraftNMaxPreferenceKey, "0");
        _additionalArgs = Preferences.Default.Get(AdditionalArgsPreferenceKey, string.Empty);
        _enableWebUi = Preferences.Default.Get(EnableWebUiPreferenceKey, true);

        _persistedModelPath = Preferences.Default.Get(ModelPathPreferenceKey, string.Empty);
        _selectedModel = TryRestoreModel(_persistedModelPath);
        if (_selectedModel is not null)
        {
            _status = $"Gespeichertes Modell: {_selectedModel.FileName}.";
        }
        else if (!string.IsNullOrWhiteSpace(_persistedModelPath))
        {
            _status = $"Gespeicherter Modellpfad ist derzeit nicht zugreifbar: {_persistedModelPath}";
        }
    }

    private void PersistCurrentSettings()
    {
        Preferences.Default.Set(ModelPathPreferenceKey, _persistedModelPath);
        Preferences.Default.Set(MmprojPathPreferenceKey, MmprojPath);
        Preferences.Default.Set(MtpPathPreferenceKey, MtpPath);
        Preferences.Default.Set(PortPreferenceKey, PortText);
        Preferences.Default.Set(ContextSizePreferenceKey, ContextSizeText);
        Preferences.Default.Set(GpuLayersPreferenceKey, GpuLayersText);
        Preferences.Default.Set(ThreadsPreferenceKey, ThreadsText);
        Preferences.Default.Set(TemperaturePreferenceKey, TemperatureText);
        Preferences.Default.Set(TopPPreferenceKey, TopPText);
        Preferences.Default.Set(TopKPreferenceKey, TopKText);
        Preferences.Default.Set(RepeatPenaltyPreferenceKey, RepeatPenaltyText);
        Preferences.Default.Set(ParallelPreferenceKey, ParallelText);
        Preferences.Default.Set(CacheTypeKPreferenceKey, CacheTypeK);
        Preferences.Default.Set(CacheTypeVPreferenceKey, CacheTypeV);
        Preferences.Default.Set(SpecTypePreferenceKey, SpecType);
        Preferences.Default.Set(SpecDraftNMaxPreferenceKey, SpecDraftNMaxText);
        Preferences.Default.Set(AdditionalArgsPreferenceKey, AdditionalArgs);
        Preferences.Default.Set(EnableWebUiPreferenceKey, EnableWebUi);
    }

    private static ModelFile? TryRestoreModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(path);
            return new ModelFile(path, fileInfo.Name, fileInfo.Length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool SetPersistedProperty(
        ref string storage,
        string value,
        string preferenceKey,
        string propertyName)
    {
        if (!SetProperty(ref storage, value, propertyName))
        {
            return false;
        }

        Preferences.Default.Set(preferenceKey, value);
        return true;
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

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}