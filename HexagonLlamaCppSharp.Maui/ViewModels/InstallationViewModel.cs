using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HexagonLlamaCppSharp.Maui.Models;
using HexagonLlamaCppSharp.Maui.Services;

namespace HexagonLlamaCppSharp.Maui.ViewModels;

public sealed class InstallationViewModel : INotifyPropertyChanged
{
    private readonly IExecutionProbe _executionProbe;
    private readonly IEmbeddedNativeAssetStore _nativeAssetStore;
    private readonly RuntimeLayout _layout;
    private readonly IToolchainManifestProvider _manifestProvider;
    private readonly IToolchainArchiveTransferService _archiveTransferService;
    private readonly IInstallationDialogService _dialogService;
    private readonly ISharedStorageAccessService _sharedStorageAccessService;
    private readonly ILogExportService _logExportService;
    private readonly IInAppToolchainBootstrapper _inAppToolchainBootstrapper;
    private readonly ILlamaBuildInstaller _llamaBuildInstaller;
    private readonly ITermuxFallbackService _termuxFallbackService;
    private CancellationTokenSource? _installationCancellation;
    private IReadOnlyList<ExtractedNativeAsset> _extractedNativeAssets = [];
    private string _status = "Bereit für die Android-Ausführbarkeitsprüfung.";
    private string _terminalText = string.Empty;
    private int _progressPercent;
    private bool _isBusy;
    private bool _showTermuxActions;
    private bool _showTermuxInstallOptions;
    private bool _archiveBackupOffered;
    private bool _isSavingLog;

    public InstallationViewModel(
        IExecutionProbe executionProbe,
        IEmbeddedNativeAssetStore nativeAssetStore,
        RuntimeLayout layout,
        IToolchainManifestProvider manifestProvider,
        IToolchainArchiveTransferService archiveTransferService,
        IInstallationDialogService dialogService,
        ISharedStorageAccessService sharedStorageAccessService,
        ILogExportService logExportService,
        IInAppToolchainBootstrapper inAppToolchainBootstrapper,
        ILlamaBuildInstaller llamaBuildInstaller,
        ITermuxFallbackService termuxFallbackService)
    {
        _executionProbe = executionProbe;
        _nativeAssetStore = nativeAssetStore;
        _layout = layout;
        _manifestProvider = manifestProvider;
        _archiveTransferService = archiveTransferService;
        _dialogService = dialogService;
        _sharedStorageAccessService = sharedStorageAccessService;
        _logExportService = logExportService;
        _inAppToolchainBootstrapper = inAppToolchainBootstrapper;
        _llamaBuildInstaller = llamaBuildInstaller;
        _termuxFallbackService = termuxFallbackService;
        RunPreflightCommand = new Command(async () => await RunPreflightAsync());
        RunTermuxFallbackCommand = new Command(async () => await RunTermuxFallbackAsync());
        OpenTermuxPlayStoreCommand = new Command(async () => await _termuxFallbackService.OpenInstallPageAsync(TermuxStoreSource.PlayStore));
        OpenTermuxFdroidCommand = new Command(async () => await _termuxFallbackService.OpenInstallPageAsync(TermuxStoreSource.Fdroid));
        CancelCommand = new Command(Cancel, () => IsBusy);
        SaveLogCommand = new Command(async () => await SaveLogAsync(), () => CanSaveLog);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> TerminalLines { get; } = [];

    public string TerminalText
    {
        get => _terminalText;
        private set => SetProperty(ref _terminalText, value);
    }

    public ICommand RunPreflightCommand { get; }

    public ICommand RunTermuxFallbackCommand { get; }

    public ICommand OpenTermuxPlayStoreCommand { get; }

    public ICommand OpenTermuxFdroidCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SaveLogCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (SetProperty(ref _progressPercent, value))
            {
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public double ProgressFraction => ProgressPercent / 100d;

    public string ProgressText => $"{ProgressPercent}%";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
                ((Command)CancelCommand).ChangeCanExecute();
            }
        }
    }

    public bool CanStart => !IsBusy;

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

    public bool CanSaveLog => !IsSavingLog && TerminalLines.Count > 0;

    public bool ShowTermuxActions
    {
        get => _showTermuxActions;
        private set => SetProperty(ref _showTermuxActions, value);
    }

    public bool ShowTermuxInstallOptions
    {
        get => _showTermuxInstallOptions;
        private set => SetProperty(ref _showTermuxInstallOptions, value);
    }

    public async Task RunPreflightAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _installationCancellation = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        Status = "Preflight wird ausgeführt ...";
        ShowTermuxActions = false;
        ShowTermuxInstallOptions = false;
        _archiveBackupOffered = false;
        _extractedNativeAssets = [];
        TerminalLines.Clear();
        TerminalText = string.Empty;
        AppendLog(new ProcessLogLine("=== Hexagon-Laufzeit: Preflight ===", false));

        var progress = new Progress<InstallationProgress>(UpdateProgress);
        var output = new Progress<ProcessLogLine>(AppendLog);

        try
        {
            await OfferLocalArchiveChoiceAsync(progress, output, _installationCancellation.Token);

            var probeResult = await _executionProbe.RunAsync(progress, output, _installationCancellation.Token);
            AppendLog(new ProcessLogLine(probeResult.Message, !probeResult.Succeeded));

            try
            {
                _extractedNativeAssets = await _nativeAssetStore.ExtractAsync(progress, _installationCancellation.Token);
                AppendLog(new ProcessLogLine($"{_extractedNativeAssets.Count} native Bibliotheken extrahiert.", false));
            }
            catch (IOException exception)
            {
                await TryHandlePrimaryFailureAsync(
                    $"Native Bibliotheken konnten nicht extrahiert werden: {exception.Message}",
                    _installationCancellation.Token);
                return;
            }

            if (!probeResult.Succeeded)
            {
                await TryHandlePrimaryFailureAsync(probeResult.Message, _installationCancellation.Token);
                return;
            }

            var toolchainResult = await _inAppToolchainBootstrapper.InitializeAsync(
                progress,
                output,
                _installationCancellation.Token);
            AppendLog(new ProcessLogLine(toolchainResult.Message, !toolchainResult.Succeeded));
            if (!toolchainResult.Succeeded)
            {
                await TryHandlePrimaryFailureAsync(toolchainResult.Message, _installationCancellation.Token);
                return;
            }

            var buildResult = await _llamaBuildInstaller.BuildAsync(
                toolchainResult,
                _extractedNativeAssets,
                progress,
                output,
                _installationCancellation.Token);
            AppendLog(new ProcessLogLine(buildResult.Message, !buildResult.Succeeded));
            if (!buildResult.Succeeded)
            {
                await TryHandlePrimaryFailureAsync(buildResult.Message, _installationCancellation.Token);
                return;
            }

            Status = "In-App-Installation abgeschlossen. llama-server ist bereit.";
        }
        catch (OperationCanceledException)
        {
            Status = "Preflight abgebrochen.";
            AppendLog(new ProcessLogLine("Vorgang wurde abgebrochen.", true));
        }
        catch (ProcessExecutionException exception)
        {
            await TryHandlePrimaryFailureAsync(exception.Message, _installationCancellation.Token);
        }
        catch (IOException exception)
        {
            await TryHandlePrimaryFailureAsync($"Dateisystemfehler während des Primärpfads: {exception.Message}", _installationCancellation.Token);
        }
        catch (UnauthorizedAccessException exception)
        {
            await TryHandlePrimaryFailureAsync($"Dateizugriff wurde verweigert: {exception.Message}", _installationCancellation.Token);
        }
        catch (PlatformNotSupportedException exception)
        {
            await TryHandlePrimaryFailureAsync($"Die erforderliche Dateiberechtigungsfunktion wird nicht unterstützt: {exception.Message}", _installationCancellation.Token);
        }
        catch (Exception exception)
        {
            Status = "Preflight fehlgeschlagen.";
            AppendLog(new ProcessLogLine($"Unerwarteter Preflight-Fehler ({exception.GetType().Name}): {exception.Message}", true));
        }
        finally
        {
            IsBusy = false;
            _installationCancellation.Dispose();
            _installationCancellation = null;
        }
    }

    public async Task RunTermuxFallbackAsync()
    {
        if (IsBusy || _extractedNativeAssets.Count == 0)
        {
            return;
        }

        _installationCancellation = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await StartTermuxFallbackAsync(
                "Manueller Start des Termux-Fallbacks.",
                _installationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Termux-Fallback abgebrochen.";
            AppendLog(new ProcessLogLine("Vorgang wurde abgebrochen.", true));
        }
        catch (Exception exception)
        {
            Status = "Termux-Fallback fehlgeschlagen.";
            AppendLog(new ProcessLogLine($"Unerwarteter Termux-Fehler ({exception.GetType().Name}): {exception.Message}", true));
        }
        finally
        {
            IsBusy = false;
            _installationCancellation.Dispose();
            _installationCancellation = null;
        }
    }

    public void Cancel()
    {
        _installationCancellation?.Cancel();
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
            var lines = await MainThread.InvokeOnMainThreadAsync(() => TerminalLines.ToArray());
            var destination = await _logExportService.SaveAsync(lines, CancellationToken.None);
            Status = $"Log gespeichert: {destination}";
            AppendLog(new ProcessLogLine($"Log gespeichert: {destination}", false));
        }
        catch (OperationCanceledException)
        {
            Status = "Log-Speicherung abgebrochen.";
        }
        catch (IOException exception)
        {
            Status = "Die Logdatei konnte nicht gespeichert werden.";
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        catch (UnauthorizedAccessException exception)
        {
            Status = "Der Zugriff auf Downloads wurde verweigert.";
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Status = "Die Logdatei konnte nicht gespeichert werden.";
            AppendLog(new ProcessLogLine($"Log-Export fehlgeschlagen ({exception.GetType().Name}): {exception.Message}", true));
        }
        finally
        {
            IsSavingLog = false;
        }
    }

    private async Task StartTermuxFallbackAsync(string reason, CancellationToken cancellationToken)
    {
        ShowTermuxActions = true;
        ShowTermuxInstallOptions = !_termuxFallbackService.IsInstalled();
        AppendLog(new ProcessLogLine($"In-App-Pfad nicht verfügbar: {reason}", true));

        var progress = new Progress<InstallationProgress>(UpdateProgress);
        var output = new Progress<ProcessLogLine>(AppendLog);
        var fallbackResult = await _termuxFallbackService.StartBuildAsync(
            reason,
            _extractedNativeAssets,
            progress,
            output,
            cancellationToken);
        AppendLog(new ProcessLogLine(fallbackResult.Message, !fallbackResult.Succeeded));
        Status = fallbackResult.Message;
        ShowTermuxInstallOptions = !fallbackResult.TermuxInstalled;
    }

    private async Task HandlePrimaryFailureAsync(string reason, CancellationToken cancellationToken)
    {
        await OfferArchiveBackupAsync(cancellationToken);
        ShowTermuxActions = false;
        ShowTermuxInstallOptions = false;
        Status = $"In-App-Installation fehlgeschlagen: {reason}";
        AppendLog(new ProcessLogLine($"In-App-Installation nicht abgeschlossen: {reason}", true));
    }

    private async Task TryHandlePrimaryFailureAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await HandlePrimaryFailureAsync(reason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "Die Fehlerbehandlung wurde abgebrochen.";
            AppendLog(new ProcessLogLine("Die Fehlerbehandlung wurde abgebrochen.", true));
        }
        catch (IOException exception)
        {
            Status = "Die In-App-Installation konnte nicht abgeschlossen werden.";
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        catch (UnauthorizedAccessException exception)
        {
            Status = "Der In-App-Installation wurde der Speicherzugriff verweigert.";
            AppendLog(new ProcessLogLine(exception.Message, true));
        }
        catch (Exception exception)
        {
            Status = "Die In-App-Installation konnte nicht abgeschlossen werden.";
            AppendLog(new ProcessLogLine($"Fehler beim Behandeln des Installationsfehlers ({exception.GetType().Name}): {exception.Message}", true));
        }
    }

    private async Task OfferLocalArchiveChoiceAsync(
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        progress.Report(new InstallationProgress(1, "Toolchain-Archive", "Prüfe vorhandene Toolchain-Archive ..."));
        output.Report(new ProcessLogLine("Prüfe lokale NDK/SDK-Archive und Downloads ...", false));
        if (File.Exists(_layout.ToolchainStatePath))
        {
            output.Report(new ProcessLogLine("Eine installierte Toolchain-Version ist bereits vermerkt.", false));
            return;
        }

        var manifest = await _manifestProvider.LoadAsync(cancellationToken);
        if (manifest is null)
        {
            output.Report(new ProcessLogLine("Kein Toolchain-Manifest im APK gefunden.", true));
            return;
        }

        output.Report(new ProcessLogLine($"Toolchain-Manifest {manifest.Version} geladen.", false));
        if (await _archiveTransferService.HasCompleteAppArchivesAsync(manifest, cancellationToken))
        {
            output.Report(new ProcessLogLine("Verifizierte Toolchain-Archive sind bereits im privaten App-Speicher vorhanden.", false));
            return;
        }

        if (!await RequestDownloadAccessAsync(cancellationToken))
        {
            return;
        }

        if (!await _archiveTransferService.HasAnyDownloadArchivesAsync(manifest, cancellationToken))
        {
            output.Report(new ProcessLogLine("Keine verifizierten Toolchain-Archive in Downloads gefunden; Online-Download bleibt aktiviert.", false));
            return;
        }

        var useLocalArchives = await _dialogService.ConfirmAsync(
            "Toolchain-Archive",
            "Sollen vorhandene NDK/SDK-TAR-XZ-Dateien aus dem Downloads-Ordner verwendet werden? Bei Nein werden die verifizierten Archive online von GitHub geladen.",
            "Downloads wählen",
            "Online laden");
        if (!useLocalArchives)
        {
            output.Report(new ProcessLogLine("Toolchain-Archive werden online von GitHub geladen.", false));
            return;
        }

        try
        {
            progress.Report(new InstallationProgress(2, "Toolchain-Archive", "Lokale NDK/SDK-Archive werden gesucht ..."));
            var imported = await _archiveTransferService.ImportFromDownloadsAsync(
                manifest,
                progress,
                output,
                cancellationToken);
            if (!imported)
            {
                output.Report(new ProcessLogLine("Keine lokalen Toolchain-Archive übernommen; der Online-Download bleibt aktiviert.", false));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            output.Report(new ProcessLogLine($"Lokale Toolchain-Archive konnten nicht verwendet werden: {exception.Message}", true));
        }
    }

    private async Task<bool> RequestDownloadAccessAsync(CancellationToken cancellationToken)
    {
        Status = "Speicherfreigabe prüfen: In Android gegebenenfalls Zugriff auf alle Dateien erlauben und zur App zurückkehren.";
        try
        {
            var granted = await _sharedStorageAccessService.RequestAccessAsync(cancellationToken);
            AppendLog(new ProcessLogLine(
                granted
                    ? "Zugriff auf Downloads ist freigegeben."
                    : "Warnung: Speicherfreigabe nicht erteilt; die automatische Downloads-Prüfung wird übersprungen. Online-Download bleibt aktiviert.",
                !granted));
            return granted;
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or InvalidOperationException)
        {
            AppendLog(new ProcessLogLine($"Speicherfreigabe konnte nicht geöffnet werden: {exception.Message}. Online-Download bleibt aktiviert.", true));
            return false;
        }
    }

    private async Task OfferArchiveBackupAsync(CancellationToken cancellationToken)
    {
        if (_archiveBackupOffered)
        {
            return;
        }

        _archiveBackupOffered = true;
        var manifest = await _manifestProvider.LoadAsync(cancellationToken);
        if (manifest is null || !await _archiveTransferService.HasCompleteAppArchivesAsync(manifest, cancellationToken))
        {
            return;
        }

        if (await _archiveTransferService.HasCompleteDownloadArchivesAsync(manifest, cancellationToken))
        {
            AppendLog(new ProcessLogLine("Die verifizierten Toolchain-Archive liegen bereits vollständig in Downloads; keine erneute Sicherung nötig.", false));
            return;
        }

        var shouldSave = await _dialogService.ConfirmAsync(
            "Toolchain sichern",
            "Der Installationsschritt ist fehlgeschlagen. Die beiden verifizierten NDK/SDK-TAR-XZ-Dateien können jetzt im Downloads-Ordner gesichert werden, damit eine spätere Neuinstallation sie wiederverwenden kann.",
            "OK",
            "Nein");
        if (!shouldSave)
        {
            AppendLog(new ProcessLogLine("Sicherung der Toolchain-Archive wurde übersprungen.", false));
            return;
        }

        try
        {
            var progress = new Progress<InstallationProgress>(UpdateProgress);
            var output = new Progress<ProcessLogLine>(AppendLog);
            await _archiveTransferService.ExportToDownloadsAsync(manifest, progress, output, cancellationToken);
            AppendLog(new ProcessLogLine("Die verifizierten Toolchain-Archive wurden im Downloads-Ordner gesichert.", false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendLog(new ProcessLogLine($"Die Toolchain-Archive konnten nicht gesichert werden: {exception.Message}", true));
        }
    }

    private void UpdateProgress(InstallationProgress progress)
    {
        void ApplyProgress()
        {
            ProgressPercent = Math.Clamp(progress.Percent, 0, 100);
            Status = progress.Message;
        }

        if (MainThread.IsMainThread)
        {
            ApplyProgress();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(ApplyProgress);
        }
    }

    private void AppendLog(ProcessLogLine line)
    {
        void AddLine()
        {
            if (TerminalLines.Count >= 2000)
            {
                TerminalLines.RemoveAt(0);
            }

            TerminalLines.Add(line.IsError ? $"[ERR] {line.Text}" : line.Text);
            TerminalText = string.Join(Environment.NewLine, TerminalLines);
            OnPropertyChanged(nameof(CanSaveLog));
            ((Command)SaveLogCommand).ChangeCanExecute();
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
