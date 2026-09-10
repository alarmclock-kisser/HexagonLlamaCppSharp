using System.ComponentModel;
using System.Diagnostics;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessSpec specification,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(specification.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(specification.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = specification.FileName,
            WorkingDirectory = specification.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in specification.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (specification.EnvironmentVariables is not null)
        {
            foreach (var variable in specification.EnvironmentVariables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ProcessExecutionException(
                    specification.FileName,
                    new InvalidOperationException("Process.Start returned false."));
            }
        }
        catch (Win32Exception exception)
        {
            throw new ProcessExecutionException(specification.FileName, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProcessExecutionException(specification.FileName, exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new ProcessExecutionException(specification.FileName, exception);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var standardOutputTask = ForwardOutputAsync(process.StandardOutput, false, output, cancellationToken);
            var standardErrorTask = ForwardOutputAsync(process.StandardError, true, output, cancellationToken);
            var processTask = process.WaitForExitAsync(cancellationToken);

            await Task.WhenAll(standardOutputTask, standardErrorTask, processTask);
            return new ProcessResult(process.ExitCode, Stopwatch.GetElapsedTime(startTimestamp));
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
    }

    private static async Task ForwardOutputAsync(
        StreamReader reader,
        bool isError,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            output.Report(new ProcessLogLine(line, isError));
        }
    }
}
