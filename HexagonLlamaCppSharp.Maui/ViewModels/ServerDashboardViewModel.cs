using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using HexagonLlamaCppSharp.Maui.Models;
using HexagonLlamaCppSharp.Maui.Services;

namespace HexagonLlamaCppSharp.Maui.ViewModels;

public sealed class ServerDashboardViewModel : INotifyPropertyChanged
{
    private readonly IModelFilePicker _modelFilePicker;
    private readonly ILlamaServerService _serverService;
    private readonly object _logLock = new();
    private CancellationTokenSource? _operationCancellation;
    private ModelFile? _selectedModel;
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
    private string _cacheTypeK = "q4_0";
    private string _cacheTypeV = "q4_0";
    private string _specType = string.Empty;
    private string _specDraftNMaxText = "0";
    private string _status = "Kein Modell ausgewählt.";
    private string _health = "Nicht gestartet";
    private string _serverAddress = string.Empty;
    private bool _isBusy;

    public ServerDashboardViewModel(
        IModelFilePicker modelFilePicker,
        ILlamaServerService serverService)
    {
        _modelFilePicker = modelFilePicker;
        _serverService = serverService;
        _serverService.OutputReceived += OnOutputReceived;
        _serverService.HealthChanged += OnHealthChanged;

        PickModelCommand = new Command(async () => await PickModelAsync());
        PickMmprojCommand = new Command(async () => await PickAuxiliaryModelAsync(isMmproj: true));
        PickMtpCommand = new Command(async () => await PickAuxiliaryModelAsync(isMmproj: false));
        StartServerCommand = new Command(async () => await StartServerAsync(), CanStartServer);
        StopServerCommand = new Command(async () => await StopServerAsync(), CanStopServer);
        RefreshHealthCommand = new Command(async () => await RefreshHealthAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> LogLines { get; } = [];

    public ICommand PickModelCommand { get; }

    public ICommand PickMmprojCommand { get; }

    public ICommand PickMtpCommand { get; }

    public ICommand StartServerCommand { get; }

    public ICommand StopServerCommand { get; }

    public ICommand RefreshHealthCommand { get; }

    public string SelectedModelName => _selectedModel?.FileName ?? "Kein Modell ausgewählt";

    public string SelectedModelPath => _selectedModel?.Path ?? string.Empty;

    public string SelectedModelSize => _selectedModel is null ? string.Empty : FormatBytes(_selectedModel.Length);

    public string MmprojPath
    {
        get => _mmprojPath;
        set => SetProperty(ref _mmprojPath, value);
    }

    public string MtpPath
    {
        get => _mtpPath;
        set => SetProperty(ref _mtpPath, value);
    }

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value);
    }

    public string ContextSizeText
    {
        get => _contextSizeText;
        set => SetProperty(ref _contextSizeText, value);
    }

    public string GpuLayersText
    {
        get => _gpuLayersText;
        set => SetProperty(ref _gpuLayersText, value);
    }

    public string ThreadsText
    {
        get => _threadsText;
        set => SetProperty(ref _threadsText, value);
    }

    public string TemperatureText
    {
        get => _temperatureText;
        set => SetProperty(ref _temperatureText, value);
    }

    public string TopPText
    {
        get => _topPText;
        set => SetProperty(ref _topPText, value);
    }

    public string TopKText
    {
        get => _topKText;
        set => SetProperty(ref _topKText, value);
    }

    public string RepeatPenaltyText
    {
        get => _repeatPenaltyText;
        set => SetProperty(ref _repeatPenaltyText, value);
    }

    public string ParallelText
    {
        get => _parallelText;
        set => SetProperty(ref _parallelText, value);
    }

    public string CacheTypeK
    {
        get => _cacheTypeK;
        set => SetProperty(ref _cacheTypeK, value);
    }

    public string CacheTypeV
    {
        get => _cacheTypeV;
        set => SetProperty(ref _cacheTypeV, value);
    }

    public string SpecType
    {
        get => _specType;
        set => SetProperty(ref _specType, value);
    }

    public string SpecDraftNMaxText
    {
        get => _specDraftNMaxText;
        set => SetProperty(ref _specDraftNMaxText, value);
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
        private set => SetProperty(ref _serverAddress, value);
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

    private bool CanStartServer() => !IsBusy && _selectedModel is not null && !_serverService.IsRunning;

    private bool CanStopServer() => !IsBusy && _serverService.IsRunning;

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
            OnPropertyChanged(nameof(SelectedModelName));
            OnPropertyChanged(nameof(SelectedModelPath));
            OnPropertyChanged(nameof(SelectedModelSize));
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

        if (!TryBuildOptions(out var options, out var error))
        {
            Status = error;
            AppendLog(new ProcessLogLine(error, true));
            return;
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
            specDraftNMax);
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

    private void OnOutputReceived(ProcessLogLine line)
    {
        AppendLog(line);
    }

    private void OnHealthChanged(LlamaServerHealth health)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Health = health.Message;
            if (!_serverService.IsRunning)
            {
                ServerAddress = string.Empty;
            }

            ChangeCommandState();
        });
    }

    private void AppendLog(ProcessLogLine line)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (_logLock)
            {
                if (LogLines.Count >= 2000)
                {
                    LogLines.RemoveAt(0);
                }

                LogLines.Add(line.IsError ? $"[ERR] {line.Text}" : line.Text);
            }
        });
    }

    private void ChangeCommandState()
    {
        ((Command)StartServerCommand).ChangeCanExecute();
        ((Command)StopServerCommand).ChangeCanExecute();
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