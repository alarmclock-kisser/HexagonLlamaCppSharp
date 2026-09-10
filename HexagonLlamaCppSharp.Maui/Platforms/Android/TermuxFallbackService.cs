using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class TermuxFallbackService : ITermuxFallbackService
{
    private const string TermuxPackageName = "com.termux";
    private const string TermuxRunCommandAction = "com.termux.RUN_COMMAND";
    private const string TermuxRunCommandPath = "com.termux.RUN_COMMAND_PATH";
    private const string TermuxRunCommandArguments = "com.termux.RUN_COMMAND_ARGUMENTS";
    private const string TermuxRunCommandWorkdir = "com.termux.RUN_COMMAND_WORKDIR";
    private const string TermuxRunCommandRunner = "com.termux.RUN_COMMAND_RUNNER";
    private const string TermuxRunCommandLabel = "com.termux.RUN_COMMAND_COMMAND_LABEL";
    private const string TermuxBashPath = "/data/data/com.termux/files/usr/bin/bash";
    private const string TermuxHomePath = "/data/data/com.termux/files/home";
    private static readonly TimeSpan LaunchConfirmationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LaunchPollingInterval = TimeSpan.FromMilliseconds(250);

    private readonly Context _context;

    public TermuxFallbackService()
    {
        _context = global::Android.App.Application.Context;
    }

    public bool IsInstalled()
    {
        try
        {
            _context.PackageManager?.GetPackageInfo(TermuxPackageName, PackageInfoFlags.MetaData);
            return true;
        }
        catch (PackageManager.NameNotFoundException)
        {
            return false;
        }
    }

    public async Task<TermuxFallbackResult> StartBuildAsync(
        string primaryFailureReason,
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryFailureReason);
        ArgumentNullException.ThrowIfNull(nativeAssets);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(output);

        if (!IsInstalled())
        {
            try
            {
                await OpenInstallPageAsync(TermuxStoreSource.PlayStore);
            }
            catch (ActivityNotFoundException exception)
            {
                output.Report(new ProcessLogLine(exception.Message, true));
            }

            return new TermuxFallbackResult(
                false,
                false,
                "Termux ist nicht installiert. Die Installationsseite wurde geöffnet.");
        }

        if (nativeAssets.Count == 0)
        {
            return new TermuxFallbackResult(
                false,
                true,
                "Der Termux-Fallback benötigt die extrahierten Hexagon-Bibliotheken.");
        }

        if (!HasSharedStorageAccess())
        {
            try
            {
                OpenSharedStorageSettings();
            }
            catch (ActivityNotFoundException exception)
            {
                output.Report(new ProcessLogLine(exception.Message, true));
                return new TermuxFallbackResult(
                    false,
                    true,
                    "Die Android-Einstellungen für den öffentlichen Speicher konnten nicht geöffnet werden.");
            }

            return new TermuxFallbackResult(
                false,
                true,
                "Für den Termux-Fallback muss der öffentliche Download-Workspace in den Android-Einstellungen freigegeben werden.");
        }

        try
        {
            var workspace = await PrepareWorkspaceAsync(nativeAssets, primaryFailureReason, cancellationToken);
            var intent = CreateRunCommandIntent(workspace.ScriptPath);
            _context.StartForegroundService(intent);

            output.Report(new ProcessLogLine("Termux-Aufruf wird bestätigt ...", false));
            if (!await WaitForScriptStartAsync(workspace.StartedPath, cancellationToken))
            {
                const string message = "Termux hat den externen RUN_COMMAND-Aufruf nicht angenommen. " +
                    "Bitte in Termux allow-external-apps aktivieren und danach den Fallback erneut starten.";
                output.Report(new ProcessLogLine(message, true));
                output.Report(new ProcessLogLine(
                    "Termux-Befehl: mkdir -p ~/.termux; printf 'allow-external-apps=true\\n' >> ~/.termux/termux.properties; termux-reload-settings",
                    false));
                return new TermuxFallbackResult(false, true, message);
            }

            output.Report(new ProcessLogLine("Termux-Fallback wurde gestartet.", false));
            progress.Report(new InstallationProgress(72, "Termux-Fallback", "Termux führt den Build aus ..."));
            await ForwardLogUntilCompleteAsync(workspace, progress, output, cancellationToken);

            var exitCode = await ReadExitCodeAsync(workspace.ExitCodePath, cancellationToken);
            if (exitCode != 0)
            {
                return new TermuxFallbackResult(false, true, $"Der Termux-Fallback wurde mit Exit-Code {exitCode} beendet.");
            }

            progress.Report(new InstallationProgress(100, "Termux-Fallback", "Termux-Build erfolgreich abgeschlossen."));
            return new TermuxFallbackResult(true, true, "Termux-Fallback erfolgreich abgeschlossen.");
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (ActivityNotFoundException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new TermuxFallbackResult(false, true, "Die Termux-Ausführungsschnittstelle konnte nicht geöffnet werden.");
        }
        catch (Java.Lang.SecurityException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new TermuxFallbackResult(false, true, "Termux verweigert externe RUN_COMMAND-Aufrufe. Bitte allow-external-apps und die RUN_COMMAND-Berechtigung aktivieren.");
        }
        catch (IOException exception)
        {
            output.Report(new ProcessLogLine(exception.Message, true));
            return new TermuxFallbackResult(false, true, "Der gemeinsame Termux-Arbeitsbereich konnte nicht eingerichtet werden.");
        }
    }

    public Task OpenInstallPageAsync(TermuxStoreSource source)
    {
        var uri = source switch
        {
            TermuxStoreSource.PlayStore => "market://details?id=com.termux",
            TermuxStoreSource.Fdroid => "https://f-droid.org/packages/com.termux/",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unbekannte Termux-Quelle.")
        };

        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri));
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            _context.StartActivity(intent);
        }
        catch (ActivityNotFoundException) when (source == TermuxStoreSource.PlayStore)
        {
            var fallbackIntent = new Intent(
                Intent.ActionView,
                global::Android.Net.Uri.Parse("https://play.google.com/store/apps/details?id=com.termux"));
            fallbackIntent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(fallbackIntent);
        }

        return Task.CompletedTask;
    }

    private async Task<TermuxWorkspace> PrepareWorkspaceAsync(
        IReadOnlyList<ExtractedNativeAsset> nativeAssets,
        string primaryFailureReason,
        CancellationToken cancellationToken)
    {
        var sharedStorageDirectory = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
            ?? throw new IOException("Android hat keinen öffentlichen Speicherpfad bereitgestellt.");
        var workspaceDirectory = Path.Combine(
            sharedStorageDirectory,
            "Download",
            "HexagonLlamaCppSharp",
            "llama-build");
        var nativeDirectory = Path.Combine(workspaceDirectory, "native-libraries");
        Directory.CreateDirectory(nativeDirectory);

        foreach (var asset in nativeAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(nativeDirectory, asset.FileName);
            File.Copy(asset.Path, destination, overwrite: true);
        }

        var logPath = Path.Combine(workspaceDirectory, "termux-build.log");
        var exitCodePath = Path.Combine(workspaceDirectory, "termux-build.exit-code");
        var startedPath = Path.Combine(workspaceDirectory, "termux-build.started");
        var scriptPath = Path.Combine(workspaceDirectory, "termux-build.sh");
        File.Delete(logPath);
        File.Delete(exitCodePath);
        File.Delete(startedPath);
        await File.WriteAllTextAsync(
            scriptPath,
            CreateBuildScript(workspaceDirectory, nativeDirectory, logPath, exitCodePath, startedPath, primaryFailureReason),
            cancellationToken);
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);

        return new TermuxWorkspace(workspaceDirectory, scriptPath, logPath, exitCodePath, startedPath);
    }

    private static async Task<bool> WaitForScriptStartAsync(
        string startedPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + LaunchConfirmationTimeout;
        while (!File.Exists(startedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(LaunchPollingInterval, cancellationToken);
        }

        return true;
    }

    private bool HasSharedStorageAccess()
    {
        return Build.VERSION.SdkInt < BuildVersionCodes.R || global::Android.OS.Environment.IsExternalStorageManager;
    }

    private void OpenSharedStorageSettings()
    {
        var intent = new Intent(
            Settings.ActionManageAppAllFilesAccessPermission,
            global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(intent);
    }

    private static Intent CreateRunCommandIntent(string scriptPath)
    {
        var intent = new Intent(TermuxRunCommandAction);
        intent.SetPackage(TermuxPackageName);
        intent.PutExtra(TermuxRunCommandPath, TermuxBashPath);
        intent.PutExtra(TermuxRunCommandArguments, new[] { scriptPath });
        intent.PutExtra(TermuxRunCommandWorkdir, TermuxHomePath);
        intent.PutExtra(TermuxRunCommandRunner, "app-shell");
        intent.PutExtra(TermuxRunCommandLabel, "Hexagon llama.cpp Fallback Build");
        return intent;
    }

    private static async Task ForwardLogUntilCompleteAsync(
        TermuxWorkspace workspace,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        var lastLength = 0;
        while (!File.Exists(workspace.ExitCodePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(workspace.LogPath))
            {
                var content = await File.ReadAllTextAsync(workspace.LogPath, cancellationToken);
                if (content.Length > lastLength)
                {
                    foreach (var line in content[lastLength..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        output.Report(new ProcessLogLine(line.TrimEnd('\r'), false));
                    }

                    lastLength = content.Length;
                }
            }

            progress.Report(new InstallationProgress(72, "Termux-Fallback", "Termux-Build läuft ..."));
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        if (File.Exists(workspace.LogPath))
        {
            var content = await File.ReadAllTextAsync(workspace.LogPath, cancellationToken);
            if (content.Length > lastLength)
            {
                foreach (var line in content[lastLength..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    output.Report(new ProcessLogLine(line.TrimEnd('\r'), false));
                }
            }
        }
    }

    private static async Task<int> ReadExitCodeAsync(string exitCodePath, CancellationToken cancellationToken)
    {
        var value = await File.ReadAllTextAsync(exitCodePath, cancellationToken);
        return int.TryParse(value.Trim(), out var exitCode)
            ? exitCode
            : throw new IOException("Termux hat keinen gültigen Exit-Code geschrieben.");
    }

    private static string CreateBuildScript(
        string workspaceDirectory,
        string nativeDirectory,
        string logPath,
        string exitCodePath,
        string startedPath,
        string primaryFailureReason)
    {
        var script = string.Join(
            '\n',
            "#!/data/data/com.termux/files/usr/bin/bash",
            "set -u",
            "set -o pipefail",
            $"LOG={Quote(logPath)}",
            $"EXIT_CODE={Quote(exitCodePath)}",
            $"STARTED={Quote(startedPath)}",
            $"NATIVE_DIR={Quote(nativeDirectory)}",
            $"WORKSPACE={Quote(workspaceDirectory)}",
            $"PRIMARY_FAILURE={Quote(primaryFailureReason)}",
            "mkdir -p \"$WORKSPACE\"",
            "exec > >(tee -a \"$LOG\") 2>&1",
            "printf 'started\\n' > \"$STARTED\"",
            "status=0",
            "printf 'Primary-In-App-Pfad fehlgeschlagen: %s\\n' \"$PRIMARY_FAILURE\"",
            "printf 'Termux-Fallback gestartet.\\n'",
            "if [ ! -d \"$HOME/storage/shared\" ]; then",
            "  printf 'Bitte zuerst in Termux einmal termux-setup-storage ausführen und den Android-Dialog bestätigen.\\n'",
            "  status=42",
            "fi",
            "if [ \"$status\" -eq 0 ]; then pkg update && pkg upgrade -y || status=$?; fi",
            "if [ \"$status\" -eq 0 ]; then pkg install git cmake clang make ccache -y || status=$?; fi",
            "if [ \"$status\" -eq 0 ]; then",
            "  cd \"$HOME\"",
            "  if [ -d llama.cpp-src/.git ]; then git -C llama.cpp-src pull --ff-only || status=$?; else git clone https://github.com/ggerganov/llama.cpp llama.cpp-src || status=$?; fi",
            "fi",
            "if [ \"$status\" -eq 0 ]; then cmake -S \"$HOME/llama.cpp-src\" -B \"$HOME/llama.cpp-src/build\" -DGGML_HEXAGON=OFF -DCMAKE_BUILD_TYPE=Release || status=$?; fi",
            "if [ \"$status\" -eq 0 ]; then cmake --build \"$HOME/llama.cpp-src/build\" --parallel \"$(nproc)\" || status=$?; fi",
            "if [ \"$status\" -eq 0 ]; then cp \"$NATIVE_DIR\"/*.so \"$HOME/llama.cpp-src/build/bin/\" || status=$?; fi",
            "if [ \"$status\" -eq 0 ]; then chmod +x \"$HOME/llama.cpp-src/build/bin/llama-server\" || status=$?; fi",
            "printf '%s\\n' \"$status\" > \"$EXIT_CODE\"",
            "exit \"$status\"");
        return script;
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private sealed record TermuxWorkspace(
        string Directory,
        string ScriptPath,
        string LogPath,
        string ExitCodePath,
        string StartedPath);
}
