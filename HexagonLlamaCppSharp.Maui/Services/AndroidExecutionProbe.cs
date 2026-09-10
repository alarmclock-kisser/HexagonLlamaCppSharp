using Android.OS;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class AndroidExecutionProbe : IExecutionProbe
{
    private const string AndroidShellPath = "/system/bin/sh";
    private const string ProbeScript = "#!/system/bin/sh\nprintf 'app-private-execution-ok\\n'\n";

    private readonly RuntimeLayout _layout;
    private readonly IProcessRunner _processRunner;
    private readonly IFilePermissionService _filePermissionService;

    public AndroidExecutionProbe(
        RuntimeLayout layout,
        IProcessRunner processRunner,
        IFilePermissionService filePermissionService)
    {
        _layout = layout;
        _processRunner = processRunner;
        _filePermissionService = filePermissionService;
    }

    public async Task<ExecutionProbeResult> RunAsync(
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(output);

        progress.Report(new InstallationProgress(0, "Preflight", "Prüfe Android-Shell und privaten Speicher ..."));
        if ((int)Build.VERSION.SdkInt < 35)
        {
            return new ExecutionProbeResult(false, "Android API 35 oder höher ist erforderlich.");
        }

        if (!Build.SupportedAbis.Any(abi => string.Equals(abi, "arm64-v8a", StringComparison.OrdinalIgnoreCase)))
        {
            return new ExecutionProbeResult(false, "Dieses Gerät ist nicht arm64-v8a-kompatibel.");
        }

        _layout.EnsureDirectories();
        var scriptPath = Path.Combine(_layout.ProbeDirectory, "execution-probe.sh");
        await File.WriteAllTextAsync(scriptPath, ProbeScript, cancellationToken);
        _filePermissionService.MakeExecutable(scriptPath);
        output.Report(new ProcessLogLine($"Von /system/bin/sh gelesenes Probe-Skript: {scriptPath}", false));

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessSpec(AndroidShellPath, [scriptPath], _layout.ProbeDirectory),
                output,
                cancellationToken);
        }
        catch (ProcessExecutionException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new ExecutionProbeResult(false, "Ein ausführbares Programm konnte aus dem App-Speicher nicht gestartet werden.");
        }

        if (result.ExitCode != 0)
        {
            return new ExecutionProbeResult(false, $"Das Probe-Skript wurde mit Exit-Code {result.ExitCode} beendet.");
        }

        progress.Report(new InstallationProgress(5, "Preflight", "Android-Shell kann ein Skript aus dem privaten Speicher lesen."));
        output.Report(new ProcessLogLine("Hinweis: Diese Shell-Probe bestätigt nicht, dass native Buildwerkzeuge aus AppData per execve gestartet werden dürfen.", false));
        return new ExecutionProbeResult(true, "Android-Shell-Prüfung erfolgreich.");
    }
}
